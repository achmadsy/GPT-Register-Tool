// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

using System.Text.Json;

namespace SmsWorkbench
{
    public partial class MainWindow
    {
        // Backend process, task list, deletion and cancellation actions.
        //
        // CLI argument construction is delegated to BackendCommandPlanner;
        // backend JSON business interpretation is delegated to
        // BackendResultInterpreter.

        private bool doctorProbeStarted;

        // Long-running backend tasks (registration / payment batches) share one
        // timeout budget; keep it a named constant instead of inline math.
        private const int BackendTaskTimeoutMs = 12 * 60 * 60 * 1000;
        private const int BackendTaskTimeoutSeconds = 12 * 60 * 60;
        private DateTime lastHotPersistenceRefreshUtc = DateTime.MinValue;

        /// <summary>
        /// One-shot background environment probe (`python -m sms_tool --doctor --json`)
        /// run straight through the backend client so the single-active-task
        /// invariant is untouched. Surfaces missing interpreter/dependencies
        /// with fix hints instead of letting them surface as per-task failures.
        /// </summary>
        internal async Task RunStartupDoctorProbeAsync(CancellationToken ct = default)
        {
            if (doctorProbeStarted)
                return;
            doctorProbeStarted = true;
            try
            {
                var command = BackendCommand.Create("doctor", new[] { "--doctor", "--json" }, 90 * 1000);
                BackendCommandResult result = await backendClient.RunAsync(command).ConfigureAwait(true);
                if (!result.Payload.HasValue)
                {
                    Log("[doctor] Environment check returned no structured result (exit code " + result.ExitCode + ")");
                    return;
                }
                var fails = new List<string>();
                int warned = 0;
                foreach (JsonElement check in result.Payload.Value.GetProperty("checks").EnumerateArray())
                {
                    // GetString() returns null for a JSON `null` value, not only
                    // for a missing property - `?? ""` keeps that null out of
                    // the interpolated failure line below.
                    string status = check.TryGetProperty("status", out JsonElement statusElement) ? statusElement.GetString() ?? "" : "";
                    string name = check.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
                    string hint = check.TryGetProperty("hint", out JsonElement hintElement) ? hintElement.GetString() ?? "" : "";
                    if (status == "fail")
                        fails.Add(string.IsNullOrEmpty(hint) ? name : $"{name}: {hint}");
                    else if (status == "warn")
                        warned++;
                }
                if (fails.Count == 0)
                {
                    Log($"[doctor] Environment check passed{(warned > 0 ? $" ({warned} warnings; see Settings and proxy configuration)" : "")}");
                    return;
                }
                var detail = string.Join("\n  - ", fails);
                Log("[doctor] Environment check found " + fails.Count + " missing dependencies");
                MessageBox.Show(
                    this,
                    $"Environment check found {fails.Count} required dependencies missing:\n  - {detail}\n\n" +
                    "Run: python -m pip install -r requirements.txt -c constraints.txt\n" +
                    "Or run python chatgpt_phone_reg.py --doctor for the full report.",
                    "Environment Check",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Log("[doctor] Environment check failed: " + ex.Message);
                MessageBox.Show(
                    this,
                    SensitiveDataSanitizer.Redact(ex.Message) + "\n\nThe desktop app requires Python 3.10+ and packages from requirements.txt/constraints.txt." +
                    "\nAfter installation, configure the interpreter under Settings → Data & Files → Runtime, then restart the app.",
                    "Could not start Python backend",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // RerunFailed_Click / RebuildSqlite_Click removed (2026-09-02, round 6):
        // dead event handlers -- no XAML element subscribes to either. They were
        // also the only `async void` in this file. The BackendCommandPlanner
        // builders they called (CreateRerunFailedRegistration /
        // CreateRebuildSqlite) are kept: the Contracts side is unit-tested and is
        // the piece to wire up if these actions ever return to the UI.

        private void AccountGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (object item in e.AddedItems)
            {
                if (item is PoolRow row) row.IsChecked = true;
            }
        }

        private void AccountDetail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PoolRow row)
            {
                ShowAccountDetail(row);
            }
        }

        private void RunBackend(string taskName, List<string> args)
            => RunUiTask(() => RunBackendAsync(taskName, args, ct: _lifetimeCts.Token));

        private void RunAccountBatchBackend(string taskName, List<string> args, string domain, int total, int? timeoutMs = null)
            => RunUiTask(() => RunBackendAsync(taskName, args, domain, total, timeoutMs, ct: _lifetimeCts.Token));

        private async Task RunBackendAsync(string taskName, List<string> args, string progressDomain = "", int progressTotal = 0, int? timeoutMs = null, CancellationToken ct = default)
        {
            if (backendTasks.IsRunning)
            {
                MessageBox.Show("A batch is already running. Cancel it or wait for it to finish.", "Running", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string safeArgs = FormatBackendArgsForDisplay(args);
            var task = new TaskRow { Name = "Batch " + taskSeq++, Task = taskName, Status = "Running", Info = safeArgs };
            Tasks.Add(task);
            ScrollTaskGridToBottom();
            DateTime started = DateTime.Now;
            AccountBatchProgressTracker? accountProgress = string.IsNullOrWhiteSpace(progressDomain)
                ? null
                : new AccountBatchProgressTracker(progressDomain, progressTotal);
            AccountBatchProgressDialog? progressDialog = accountProgress == null
                ? null
                : new AccountBatchProgressDialog(this, taskName, progressTotal, () => backendTasks.Cancel());
            progressDialog?.Show();

            var backendOutput = new StringBuilder();
            object backendOutputLock = new object();
            void CaptureBackendLine(string line)
            {
                lock (backendOutputLock)
                {
                    backendOutput.AppendLine(line);
                }
            }

            // Machine consumers read the raw stream (backendOutput above); the
            // panel gets the folded operator story instead of envelopes, JSON
            // blocks and banner bars.
            var logFolder = new BackendLogFolder();
            string commandId = Guid.NewGuid().ToString("N");
            var progress = new Progress<BackendOutputLine>(line =>
            {
                if (BackendProgressEventParser.TryParse(line.Text, out BackendProgressEvent? progressEvent))
                {
                    LogBackendProgress(taskName, progressEvent);
                    if (progressEvent.Domain == "registration" && progressEvent.Stage == "registration_status_changed")
                        RefreshPoolsThrottled(preserveView: true);
                    // The backend drops cooling/quarantined/dead-end mailboxes
                    // before attempting them; the event carries the masked list
                    // so the grid can refresh those rows out of their stale
                    // pre-batch state instead of leaving the operator to find
                    // the skip in the backend log.
                    if (progressEvent.Domain == "registration" && progressEvent.Stage == "mailboxes_skipped")
                        RefreshPoolsThrottled(preserveView: true);
                    if (accountProgress != null
                        && string.Equals(progressEvent.Domain, accountProgress.Domain, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update only returns true for a terminal per-account
                        // event, i.e. one account really finished and was
                        // persisted backend-side. That is the moment its grid
                        // row goes stale, so reload it instead of waiting for
                        // the whole batch -- this is what the scan commands
                        // were missing versus registration's "Saved session:"
                        // marker.
                        if (accountProgress.Update(progressEvent))
                            RefreshPoolsThrottled(preserveView: true);
                        progressDialog?.Update(
                            accountProgress.Completed,
                            accountProgress.Total,
                            progressEvent.AccountRef,
                            progressEvent.Detail);
                    }
                    task.Info = progressEvent.Detail.Length > 0
                        ? $"{progressEvent.Stage}: {progressEvent.Detail}"
                        : progressEvent.Stage;
                    // The event itself never reaches the panel (we return
                    // below); emit its operator-facing stage line here so the
                    // scan commands read as staged output instead of a wall of
                    // raw backend lines.
                    string? stageLine = BackendLogPresenter.ProgressEventLine(progressEvent);
                    if (stageLine != null)
                        UiLog(stageLine);
                    return;
                }
                CaptureBackendLine(line.Text);
                foreach (string display in logFolder.Feed(line.Text))
                    UiLog(display);
                RefreshPoolsAfterHotPersistence(line.Text);
            });
            try
            {
                logger?.Information("Starting backend task={TaskName} command_id={CommandId} args={Args}", taskName, commandId, safeArgs);
                Log(BackendLogPresenter.TaskStartLine(taskName));
                StatusText = taskName + " running";
                BackendCommandResult result = await backendTasks.RunAsync(
                    BackendCommand.Create(
                        taskName,
                        args,
                        timeoutMs ?? BackendTaskTimeoutMs,
                        new Dictionary<string, string>
                        {
                            ["SMSWORKBENCH_EVENTS"] = "1",
                            ["SMS_TOOL_COMMAND_ID"] = commandId,
                            ["SMS_TOOL_TASK_NAME"] = taskName,
                            ["SMS_TOOL_EVENT_SOURCE"] = "wpf",
                        }),
                    progress);

                // Use BackendResultInterpreter to normalize the outcome
                BackendExecutionResult interpreted = BackendResultInterpreter.Interpret(
                    result, taskName, (timeoutMs ?? BackendTaskTimeoutMs) / 1000);

                // A killed Python process may not emit its terminal envelope.
                // Recover the rows already persisted by the liveness workers so
                // the operator still gets a useful partial result dialog.
                if (result.TimedOut && taskName.StartsWith("Account check", StringComparison.OrdinalIgnoreCase))
                {
                    string snapshot = TryReadLatestLivenessSnapshot();
                    if (snapshot.Length > 0)
                        CaptureBackendLine(snapshot);
                }

                task.Status = interpreted.IsSuccess ? "Completed" : "Failed";
                string batchSummary = BackendResultInterpreter.BatchSummaryLabel(interpreted.Payload);
                if (batchSummary.Length > 0)
                    task.Info = batchSummary;
                task.Cost = ((int)(DateTime.Now - started).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                task.DoneAt = SafeTime(DateTime.Now);
                StatusText = taskName + " finished";
                RefreshPools();
                ScrollTaskGridToBottom();
                if (BackendResultInterpreter.IsAccountScanResultTask(taskName))
                {
                    string output;
                    lock (backendOutputLock)
                    {
                        output = backendOutput.ToString();
                    }
                    ShowAccountScanResultDialog(output, BackendResultInterpreter.AccountScanResultTitle(taskName));
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = "Cancelled";
                task.DoneAt = SafeTime(DateTime.Now);
                StatusText = taskName + " cancelled";
            }
            catch (BackendTaskAlreadyRunningException)
            {
                task.Status = "Not started";
                task.DoneAt = SafeTime(DateTime.Now);
                StatusText = taskName + " not started";
                MessageBox.Show("A batch is already running. Cancel it or wait for it to finish.", "Running", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                task.Status = "Start failed";
                Log("Start failed: " + ex.Message);
            }
            finally
            {
                progressDialog?.Close();
            }
        }

        private string TryReadLatestLivenessSnapshot()
        {
            try
            {
                string directory = Path.Combine(rootDir, "runtime", "account_liveness_batches");
                if (!Directory.Exists(directory)) return "";
                // Prefer a terminal snapshot: partial snapshots are written
                // while the batch runs (and after a hard kill) and may carry
                // unfinished rows. A stale non-terminal snapshot (older than
                // 10 minutes) is crash residue from a dead batch, not an
                // answer to this task's question.
                string? latest = Directory.GetFiles(directory, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(latest)) return "";
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(latest));
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("results", out _) || !root.TryGetProperty("total", out _))
                    return "";
                bool terminal = root.TryGetProperty("terminal", out JsonElement terminalEl)
                    && terminalEl.ValueKind == JsonValueKind.True;
                if (!terminal && File.GetLastWriteTimeUtc(latest) < DateTime.UtcNow.AddMinutes(-10))
                    return "";
                return root.GetRawText();
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> RunBackendWithResultAsync(string taskName, List<string> args, int timeoutMs = 120000, CancellationToken ct = default)
        {
            string commandId = Guid.NewGuid().ToString("N");
            logger?.Information("Starting backend task={TaskName} command_id={CommandId} args={Args}", taskName, commandId, FormatBackendArgsForDisplay(args));
            Log(BackendLogPresenter.TaskStartLine(taskName));
            var logFolder = new BackendLogFolder();
            var progress = new Progress<BackendOutputLine>(line =>
            {
                if (BackendProgressEventParser.TryParse(line.Text, out BackendProgressEvent? progressEvent))
                {
                    string? stageLine = BackendLogPresenter.ProgressEventLine(progressEvent);
                    if (stageLine != null)
                        UiLog(stageLine);
                    return;
                }
                foreach (string display in logFolder.Feed(line.Text))
                    UiLog(display);
            });
            return await backendTasks.RunForResultAsync(
                BackendCommand.Create(
                    taskName,
                    args,
                    timeoutMs,
                    new Dictionary<string, string>
                    {
                        ["SMSWORKBENCH_EVENTS"] = "1",
                        ["SMS_TOOL_COMMAND_ID"] = commandId,
                        ["SMS_TOOL_TASK_NAME"] = taskName,
                        ["SMS_TOOL_EVENT_SOURCE"] = "wpf",
                    }),
                progress,
                ct);
        }

        private void LogBackendProgress(string taskName, BackendProgressEvent progressEvent)
        {
            logger?.Information(
                "Backend progress task={TaskName} command_id={CommandId} account_ref={AccountRef} stage={Stage} status={Status} failure_class={FailureClass} detail={Detail}",
                taskName,
                progressEvent.CommandId,
                progressEvent.AccountRef,
                progressEvent.Stage,
                progressEvent.Status,
                progressEvent.FailureClass,
                SensitiveDataSanitizer.Redact(progressEvent.Detail));
        }

        private static string FormatBackendArgsForDisplay(List<string> args)
        {
            return SensitiveDataSanitizer.RedactArguments(args);
        }

        private void RefreshPoolsAfterHotPersistence(string line)
        {
            if (string.IsNullOrWhiteSpace(line)
                || !line.Contains(BackendTextMarkers.SavedSession, StringComparison.OrdinalIgnoreCase))
                return;

            // Registration adds rows, so jumping to page one is the useful
            // behaviour there. Mid-batch scan refreshes must not do that.
            RefreshPoolsThrottled(preserveView: false);
        }

        /// <summary>
        /// Rate-limited pool reload shared by both hot-refresh signals: the
        /// "Saved session:" text marker (registration) and the terminal
        /// per-account IPC event (scan / promotion). A backend can finish
        /// several accounts inside 750ms; one reload per burst is enough.
        /// <para>
        /// A reload is not free: <c>read_accounts</c> took ~1.3s to build an
        /// 862-row response when this was measured, so the window is only a
        /// floor. <c>RefreshPoolsAsync</c> also drops a reload that arrives
        /// while another is still in flight, which is what actually paces a
        /// batch that completes accounts faster than the read can keep up.
        /// </para>
        /// </summary>
        private void RefreshPoolsThrottled(bool preserveView)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastHotPersistenceRefreshUtc).TotalMilliseconds < 750)
                return;
            lastHotPersistenceRefreshUtc = now;
            if (preserveView)
                RefreshPoolsPreservingView();
            else
                RefreshPools();
        }

        private void TaskGrid_Loaded(object sender, RoutedEventArgs e) => ScrollTaskGridToBottom();

        private void ScrollTaskGridToBottom()
        {
            if (TaskGrid == null || Tasks.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                object last = Tasks[Tasks.Count - 1];
                TaskGrid.SelectedItem = last;
                TaskGrid.ScrollIntoView(last);
            }), DispatcherPriority.Background);
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
            => RunUiTask(() => DeleteSelectedAsync());

        private async Task DeleteSelectedAsync(CancellationToken ct = default)
        {
            var selected = SelectedEmailRowsOrNotify("delete");
            if (selected.Count == 0) return;
            if (!await ShowDeleteConfirmDialog(selected.Count)) return;
            BackendCommandPlan? plan = null;
            try
            {
                plan = BackendCommandPlanner.CreateBatchDeleteAccounts(
                    selected.Select(row => NormalizeEmailKey(row.Identifier)).ToArray(),
                    workers: Math.Min(8, Math.Max(1, selected.Count)));
                BackendCommandResult backend = await backendTasks.RunAsync(
                    BackendCommand.Create(plan.TaskName, plan.Arguments.ToList(), plan.TimeoutMilliseconds ?? 120000));
                int failed = CountBatchDeleteFailures(backend, selected.Count);
                if (failed > 0)
                {
                    await DialogFactory.ShowInfoAsync(
                        this,
                        "Delete incomplete",
                        failed + " record(s) could not be fully deleted. Check the run log.");
                }
            }
            catch (Exception ex)
            {
                Log("Batch delete failed: " + SensitiveDataSanitizer.Redact(ex.Message));
                await DialogFactory.ShowInfoAsync(this, "Delete failed", "Batch delete incomplete. Check the run log.");
            }
            finally
            {
                if (plan != null)
                {
                    foreach (string path in plan.TempFiles)
                        TryDeleteFile(path);
                }
                RefreshPools();
            }
        }

        private static int CountBatchDeleteFailures(BackendCommandResult backend, int expected)
        {
            if (backend.ExitCode != 0 || !backend.Payload.HasValue)
                return expected;
            JsonElement payload = backend.Payload.Value;
            if (payload.TryGetProperty("failed", out JsonElement failed) && failed.ValueKind == JsonValueKind.Number)
                return Math.Max(0, failed.GetInt32());
            return payload.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                ? 0
                : expected;
        }

        private async Task<bool> ShowDeleteConfirmDialog(int count, CancellationToken ct = default)
        {
            return await DialogFactory.ShowConfirmAsync(
                this,
                "Delete selected " + count + " record(s)?",
                "This also removes matching local mailbox-pool rows, SQLite records, and session files. This cannot be undone.",
                "Delete",
                isDanger: true);
        }

        private bool TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Log("File deletion failed: " + SensitiveDataSanitizer.Redact(path) + " " + SensitiveDataSanitizer.Redact(ex.Message));
                return false;
            }
        }

        private void CancelBatch_Click(object sender, RoutedEventArgs e)
        {
            if (!backendTasks.IsRunning)
            {
                Log("No batch is currently running.");
                return;
            }
            try
            {
                if (backendTasks.Cancel())
                    Log("Current batch cancelled.");
            }
            catch (Exception ex)
            {
                Log("Cancel failed: " + ex.Message);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPools();

        private void Settings_Click(object sender, RoutedEventArgs e) => ShowConfigDialog();
    }
}
