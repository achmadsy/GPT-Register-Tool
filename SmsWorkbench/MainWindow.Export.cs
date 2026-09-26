// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

namespace SmsWorkbench
{
    public partial class MainWindow
    {
        // Account import/export, scan result and export JSON helpers.
        //
        // CLI argument construction is delegated to BackendCommandPlanner;
        // backend JSON business interpretation is delegated to
        // BackendResultInterpreter.

        private async void ImportExistingSessions_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select existing account session JSON files",
                Filter = "Session JSON (*.json)|*.json",
                Multiselect = true
            };
            if (picker.ShowDialog() != true) return;
            var plan = BackendCommandPlanner.CreateLocalSessionImport(picker.FileNames);
            try
            {
                await RunBackendWithResultAsync(plan.TaskName, plan.Arguments.ToList());
                RefreshPools();
                ShowThemedInfoDialog("Import complete", "Existing account sessions were imported locally. Select an account to inspect it; importing does not register it again. Check task output for imported/skipped counts.");
            }
            catch (Exception ex)
            {
                ShowThemedInfoDialog("Import failed", "No account was imported. Check selected JSON files. " + SensitiveDataSanitizer.Redact(ex.Message));
            }
        }

        private void ImportPaidCpa_Click(object sender, RoutedEventArgs e)
        {
            string target = ShowImportTargetDialog("Send local accounts to CPA/SUB2API");
            if (target.Length == 0) return;

            if (MessageBox.Show("This sends selected local account sessions to an external CPA or SUB2API service. Continue?", "Send accounts externally", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var selected = SelectedRowsOrCurrent()
                .Where(IsImportableAccountRow)
                .Where(r => !string.IsNullOrWhiteSpace(r.Identifier))
                .GroupBy(r => r.Identifier.Trim().ToLowerInvariant())
                .Select(g => g.First())
                .ToList();
            var rows = selected.Count > 0
                ? selected
                : allRows.Where(IsImportableAccountRow)
                    .Where(r => !string.IsNullOrWhiteSpace(r.Identifier))
                    .GroupBy(r => r.Identifier.Trim().ToLowerInvariant())
                    .Select(g => g.First())
                    .ToList();

            if (rows.Count == 0)
            {
                MessageBox.Show("No importable accounts found. Register accounts first and obtain an access_token/session.", "Send accounts externally", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var plan = BackendCommandPlanner.CreateAccountImport(
                target,
                rows.Select(r => r.Identifier.Trim()).ToList());
            RunBackend(plan.TaskName, plan.Arguments.ToList());
        }

        private async void ExportAccounts_Click(object sender, RoutedEventArgs e)
        {
            string format = ShowExportFormatDialog();
            if (format.Length == 0) return;

            var rows = ExportCandidateRows();
            if (format.Equals("txt", StringComparison.OrdinalIgnoreCase))
            {
                await ExportAccountsTxtAsync(rows).ConfigureAwait(true);
                return;
            }
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                ExportAccountsJson(rows);
                return;
            }
            ExportAccountsConvertedJson(rows, format);
        }

        private List<PoolRow> ExportCandidateRows()
        {
            var rows = SelectedRowsOrCurrent();
            if (rows.Count == 0)
            {
                rows = allRows.Where(FilterRow).ToList();
            }
            if (rows.Count == 0)
            {
                rows = allRows.ToList();
            }
            return rows;
        }

        private async Task ExportAccountsTxtAsync(List<PoolRow> rows)
        {
            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int skipped = 0;
            foreach (PoolRow row in rows)
            {
                string? line = await TryBuildAccountExportLineAsync(row).ConfigureAwait(true);
                if (line is null)
                {
                    skipped++;
                }
                else if (seen.Add(line))
                {
                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                ShowThemedInfoDialog("Export", "No exportable account records found. Only mailbox records with email, password, client ID, and refresh token are supported; CFWorker records or ones missing password/refresh token are skipped.");
                return;
            }

            string outputDir = Path.Combine(rootDir, "runtime");
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, "account-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));
            Log("One-click export wrote " + lines.Count + " account(s), skipped " + skipped + ": " + outputPath);
            ShowExportCompleteDialog(outputPath, lines.Count, skipped, "TXT", "email----password----client-ID----refresh-token");
        }

        private void ExportAccountsJson(List<PoolRow> rows)
            => RunUiTask(() => ExportAccountsJsonAsync(rows, _lifetimeCts.Token));

        private async Task ExportAccountsJsonAsync(List<PoolRow> rows, CancellationToken ct = default)
        {
            var collected = await CollectAccountExportJsonAsync(rows);
            if (collected.Items.Count == 0)
            {
                ShowThemedInfoDialog("Export", "No JSON account records found. An account needs a generated session/auth_session or an SQLite record.");
                return;
            }

            string outputDir = Path.Combine(rootDir, "runtime", "account_json");
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, "account-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            object payload = collected.Items.Count == 1 ? collected.Items[0] : collected.Items;
            var options = new JsonSerializerOptions { WriteIndented = true };
            await Task.Run(() => File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, options), new UTF8Encoding(false)));
            Log("One-click JSON export wrote " + collected.Items.Count + " account(s), skipped " + collected.Skipped + ": " + outputPath);
            ShowExportCompleteDialog(outputPath, collected.Items.Count, collected.Skipped, "JSON", "Raw account session JSON; RT fields are preserved and left empty when absent");
        }

        private sealed record CollectedAccountExport(List<Dictionary<string, object>> Items, int Skipped);

        private async Task<CollectedAccountExport> CollectAccountExportJsonAsync(List<PoolRow> rows, CancellationToken ct = default)
        {
            var items = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            foreach (PoolRow row in rows)
            {
                Dictionary<string, object>? item = await BuildAccountExportJsonAsync(row);
                if (item != null)
                {
                    string key = JsonExportDedupKey(item, row);
                    if (seen.Add(key))
                    {
                        items.Add(item);
                    }
                }
                else
                {
                    skipped++;
                }
            }
            return new CollectedAccountExport(items, skipped);
        }

        private void ExportAccountsConvertedJson(List<PoolRow> rows, string format)
            => RunUiTask(() => ExportAccountsConvertedJsonAsync(rows, format, _lifetimeCts.Token));

        private async Task ExportAccountsConvertedJsonAsync(List<PoolRow> rows, string format, CancellationToken ct = default)
        {
            var collected = await CollectAccountExportJsonAsync(rows);
            if (collected.Items.Count == 0)
            {
                ShowThemedInfoDialog("Export", "No account sessions to convert. An account needs access_token/session/auth_session or an SQLite record.");
                return;
            }
            List<Dictionary<string, object>> items = collected.Items;
            int skipped = collected.Skipped;

            string normalized = (format ?? "cpa").Trim().ToLowerInvariant();
            string outputDir = Path.Combine(rootDir, "runtime", "account_json");
            Directory.CreateDirectory(outputDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string sourcePath = Path.Combine(Path.GetTempPath(), "account_export_source_" + stamp + ".json");
            string outputPath = Path.Combine(outputDir, "account-" + normalized + "-" + stamp + ".json");
            object payload = items.Count == 1 ? items[0] : items;
            var options = new JsonSerializerOptions { WriteIndented = true };
            await Task.Run(() => File.WriteAllText(sourcePath, JsonSerializer.Serialize(payload, options), new UTF8Encoding(false)));

            try
            {
                var plan = BackendCommandPlanner.CreateSessionConversion(sourcePath, normalized, outputPath);
                await RunBackendWithResultAsync(plan.TaskName, plan.Arguments.ToList());
            }
            catch (Exception ex)
            {
                Log("Account format conversion failed: " + ex.Message);
                ShowThemedInfoDialog("Export", "Account format conversion failed: " + ex.Message);
                return;
            }
            finally
            {
                try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                ShowThemedInfoDialog("Export", "Format conversion produced no output file. Check the log below for the converter result.");
                return;
            }

            Log("One-click converted export wrote " + items.Count + " account(s), skipped " + skipped + ", format=" + normalized + ": " + outputPath);
            ShowExportCompleteDialog(outputPath, items.Count, skipped, ExportFormatLabel(normalized), ExportFormatDescription(normalized));
        }

        private string ShowExportFormatDialog()
        {
            string selected = "";
            var dialog = new Window
            {
                Title = "Export Accounts",
                Owner = this,
                Width = 560,
                MinWidth = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            header.Children.Add(new TextBlock
            {
                Text = "Choose export format",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain")
            });
            header.Children.Add(new TextBlock
            {
                Text = "TXT keeps the raw mailbox line format; raw JSON keeps the session; other formats run session_converter.py to produce CPA/Sub2API/Cockpit/9router/Codex/AxonHub/Codex-Manager output.",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("TextSub")
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var combo = new ComboBox { SelectedIndex = 2, Margin = new Thickness(0, 0, 0, 16) };
            combo.Items.Add(new ComboBoxItem { Content = "TXT - email----password----client-ID----refresh-token", Tag = "txt" });
            combo.Items.Add(new ComboBoxItem { Content = "Raw JSON - session/auth_session", Tag = "json" });
            combo.Items.Add(new ComboBoxItem { Content = "CPA JSON", Tag = "cpa" });
            combo.Items.Add(new ComboBoxItem { Content = "Sub2API JSON", Tag = "sub2api" });
            combo.Items.Add(new ComboBoxItem { Content = "Cockpit JSON", Tag = "cockpit" });
            combo.Items.Add(new ComboBoxItem { Content = "9router JSON", Tag = "9router" });
            combo.Items.Add(new ComboBoxItem { Content = "Codex auth.json", Tag = "codex" });
            combo.Items.Add(new ComboBoxItem { Content = "AxonHub JSON", Tag = "axonhub" });
            combo.Items.Add(new ComboBoxItem { Content = "Codex-Manager JSON", Tag = "codexmanager" });
            Grid.SetRow(combo, 1);
            root.Children.Add(combo);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var exportButton = new Button
            {
                Content = "Export",
                Width = 88,
                Style = (Style)FindResource("PrimaryButton")
            };
            exportButton.Click += (_, __) =>
            {
                selected = ((combo.SelectedItem as ComboBoxItem)?.Tag as string) ?? "cpa";
                dialog.Close();
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 76,
                Margin = new Thickness(8, 0, 0, 0)
            };
            cancelButton.Click += (_, __) => dialog.Close();
            actions.Children.Add(exportButton);
            actions.Children.Add(cancelButton);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
            return selected;
        }

        private string ExportFormatLabel(string format)
        {
            string value = (format ?? "").Trim().ToLowerInvariant();
            if (value == "sub2api") return "SUB2API JSON";
            if (value == "cockpit") return "Cockpit JSON";
            if (value == "9router") return "9router JSON";
            if (value == "codex") return "Codex auth.json";
            if (value == "axonhub") return "AxonHub JSON";
            if (value == "codexmanager") return "Codex-Manager JSON";
            if (value == "json") return "Raw JSON";
            if (value == "txt") return "TXT";
            return "CPA JSON";
        }

        private string ExportFormatDescription(string format)
        {
            string value = (format ?? "").Trim().ToLowerInvariant();
            if (value == "sub2api") return "Sub2API accounts document produced by session_converter.py";
            if (value == "cockpit") return "Cockpit/Codex import structure produced by session_converter.py";
            if (value == "9router") return "9router provider structure produced by session_converter.py";
            if (value == "codex") return "Codex auth.json structure produced by session_converter.py";
            if (value == "axonhub") return "AxonHub structure produced by session_converter.py; a placeholder is written when RT is missing";
            if (value == "codexmanager") return "Codex-Manager structure produced by session_converter.py";
            return "CPA JSON produced by session_converter.py; a compatible field is synthesized when id_token is missing";
        }

        private void ShowExportCompleteDialog(string outputPath, int exportedCount, int skippedCount, string formatLabel, string formatDescription)
        {
            var dialog = new Window
            {
                Title = "Export Accounts",
                Owner = this,
                Width = 520,
                MinWidth = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            header.Children.Add(new TextBlock
            {
                Text = "Export complete",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain")
            });
            header.Children.Add(new TextBlock
            {
                Text = "Generated the account " + formatLabel + " file: " + formatDescription,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("TextSub")
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var summary = new Border
            {
                Background = (Brush)FindResource("PanelBg"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 16)
            };
            var summaryStack = new StackPanel();
            summaryStack.Children.Add(new TextBlock
            {
                Text = "Exported: " + exportedCount + "    Skipped: " + skippedCount,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain")
            });
            summaryStack.Children.Add(new TextBlock
            {
                Text = outputPath,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (Brush)FindResource("TextSub")
            });
            summary.Child = summaryStack;
            Grid.SetRow(summary, 1);
            root.Children.Add(summary);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var openDirButton = new Button
            {
                Content = "Open folder",
                Width = 92,
                Style = (Style)FindResource("PrimaryButton")
            };
            openDirButton.Click += (_, __) =>
            {
                string directory = Path.GetDirectoryName(outputPath) ?? Path.Combine(rootDir, "runtime");
                OpenPath(directory);
                dialog.Close();
            };
            var closeButton = new Button
            {
                Content = "Close",
                Width = 76,
                Margin = new Thickness(8, 0, 0, 0)
            };
            closeButton.Click += (_, __) => dialog.Close();
            actions.Children.Add(openDirButton);
            actions.Children.Add(closeButton);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private void ShowAccountScanResultDialog(string backendOutput, string title = "Account check")
        {
            var summary = BackendResultInterpreter.TryExtractScanSummary(backendOutput);
            if (summary == null)
            {
                ShowThemedInfoDialog(title, title + " finished, but no result summary could be parsed. Check the log below for details.");
                return;
            }

            var results = new List<Dictionary<string, object>>();
            if (summary.TryGetValue("results", out object? rawResults) && rawResults is List<object> items)
            {
                foreach (object item in items)
                {
                    if (item is Dictionary<string, object> map)
                    {
                        results.Add(map);
                    }
                }
            }

            bool directProbe = results.Any(r => BackendJson.TryGetMap(r, "probe", out _));
            // Promotion rows carry their own badge; without this the panel would
            // show "AT valid / HTTP 200" and hide the actual promotion answer.
            bool isPromotion = BackendResultInterpreter.IsPromotionRows(results);
            var rtRows = directProbe ? new List<Dictionary<string, object>>() : results.Where(r => BackendJson.GetBool(r, "has_rt")).ToList();
            var noRtRows = directProbe ? results : results.Where(r => !BackendJson.GetBool(r, "has_rt")).ToList();

            var dialog = new Window
            {
                Title = title + " Results",
                Owner = this,
                Width = 740,
                MinWidth = 740,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 760,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            header.Children.Add(new TextBlock
            {
                Text = title + " complete",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain")
            });
            header.Children.Add(new TextBlock
            {
                Text = isPromotion
                    ? BackendResultInterpreter.PromotionSummary(results)
                    : directProbe
                    ? FormatDirectProbeSummary(results, summary)
                    : "Total: " + BackendJson.GetString(summary, "total")
                        + "    Alive: " + BackendJson.GetString(summary, "alive")
                        + "    Deactivated: " + BackendJson.GetString(summary, "account_deactivated")
                        + "    401/AT invalid: " + BackendJson.GetString(summary, "at_invalid")
                        + "    Phone verification: " + BackendJson.GetString(summary, "secondary_phone_verification_required")
                        + "    Failed: " + BackendJson.GetString(summary, "failed"),
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("TextSub")
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new StackPanel();
            if (noRtRows.Count > 0)
            {
                AddScanResultSection(
                    body,
                    isPromotion ? title + " results" : (directProbe ? "AT probe results" : "No-phone-verification results"),
                    noRtRows);
            }
            if (rtRows.Count > 0)
            {
                AddScanResultSection(body, "Phone-verified results", rtRows);
            }
            if (body.Children.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "No " + title + " details to display.",
                    Foreground = (Brush)FindResource("TextSub")
                });
            }

            var scroll = new ScrollViewer
            {
                Content = body,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var ok = new Button { Content = "Close", Width = 82, Style = (Style)FindResource("PrimaryButton") };
            ok.Click += (_, __) => dialog.Close();
            actions.Children.Add(ok);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private string FormatDirectProbeSummary(
            List<Dictionary<string, object>> results,
            Dictionary<string, object> summary)
        {
            results ??= new List<Dictionary<string, object>>();
            summary ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int directDeactivated = results.Count(BackendResultInterpreter.IsProbeDeactivated);
            int directOk = results.Count(row =>
                !BackendResultInterpreter.IsProbeDeactivated(row)
                && BackendResultInterpreter.IsProbeSucceeded(row));
            int direct401 = results.Count(row =>
                !BackendResultInterpreter.IsProbeDeactivated(row)
                && BackendResultInterpreter.IsProbeReturned401(row));
            int directFailed = Math.Max(0, results.Count - directOk - direct401 - directDeactivated);
            int.TryParse(BackendJson.GetString(summary, "relogin_attempted"), out int reloginAttempted);
            string directSummary = "Total: " + results.Count
                + "    AT valid: " + directOk
                + "    AT invalid: " + direct401
                + "    Deactivated: " + directDeactivated
                + "    Other failures: " + directFailed;
            if (reloginAttempted > 0)
            {
                directSummary += "    Re-login OK: " + BackendJson.GetString(summary, "relogin_success")
                    + "    Re-login failed: " + BackendJson.GetString(summary, "relogin_failed")
                    + "    Confirmed deactivated: " + BackendJson.GetString(summary, "relogin_account_deactivated");
            }
            return directSummary;
        }

        private void AddScanResultSection(StackPanel parent, string title, List<Dictionary<string, object>> rows)
        {
            parent.Children.Add(new TextBlock
            {
                Text = title + " (" + rows.Count + ")",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain"),
                Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 12, 0, 8)
            });

            var card = new Border
            {
                Background = (Brush)FindResource("PanelBg"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var stack = new StackPanel();
            foreach (Dictionary<string, object> row in rows)
            {
                string email = BackendJson.GetString(row, "email");
                string status;
                string error;
                if (BackendJson.TryGetMap(row, "probe", out var probe))
                {
                    status = BackendResultInterpreter.ResultRowStatus(row);
                    if (BackendJson.TryGetMap(row, "relogin", out var relogin)
                        && !BackendJson.GetBool(relogin, "ok"))
                    {
                        error = BackendJson.GetString(relogin, "error");
                    }
                    else
                    {
                        error = BackendJson.GetString(probe, "error");
                    }
                }
                else
                {
                    status = BackendResultInterpreter.ScanStatusLabel(BackendJson.GetString(row, "scan_status"));
                    error = BackendResultInterpreter.ScanResultError(row);
                }
                string line = error.Length > 0 ? email + "  ·  " + status + "  ·  " + error : email + "  ·  " + status;
                stack.Children.Add(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Margin = new Thickness(0, 0, 0, 6),
                    Foreground = (Brush)FindResource("TextSub")
                });
            }
            card.Child = stack;
            parent.Children.Add(card);
        }

        /// Returns null when the row has no exportable account data (no session
        /// file, or an empty/unusable payload) - the caller skips it.
        private async Task<Dictionary<string, object>?> BuildAccountExportJsonAsync(PoolRow? row, CancellationToken ct = default)
        {
            if (row == null) return null;
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!await TryLoadAccountDataForRowAsync(row, data) || data.Count == 0)
            {
                return null;
            }

            Dictionary<string, object> source = data;
            if (BackendJson.TryGetMap(data, "auth_session", out var authSession) && authSession.Count > 0)
            {
                source = authSession;
            }

            if (CloneExportJsonValue(source) is not Dictionary<string, object> clean || clean.Count == 0)
            {
                return null;
            }

            EnsureJsonExportEmail(clean, row);
            EnsureJsonExportRefreshToken(clean, data);

            return clean;
        }

        private async Task<bool> TryLoadAccountDataForRowAsync(PoolRow row, Dictionary<string, object> data, CancellationToken ct = default)
        {
            if (row == null) return false;

            string source = (row.SourcePath ?? "").Trim();
            if (!source.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) return false;
            try
            {
                JsonElement account = await desktopRead.ReadAccountExportAsync(
                    OnlyDigits(row.RawLine), row.Identifier);
                foreach (KeyValuePair<string, object> item in BackendJson.ElementToDictionary(account))
                {
                    data[item.Key] = item.Value;
                }
                return data.Count > 0;
            }
            catch (Exception ex)
            {
                Log("Account export backend read failed: " + SensitiveDataSanitizer.Redact(row.Identifier) + " " + SensitiveDataSanitizer.Redact(ex.Message));
                return false;
            }
        }

        private object CloneExportJsonValue(object value)
        {
            if (value is Dictionary<string, object> map)
            {
                var clean = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in map)
                {
                    clean[pair.Key] = CloneExportJsonValue(pair.Value);
                }
                return clean;
            }
            if (value is List<object> list)
            {
                return list.Select(CloneExportJsonValue).ToList();
            }
            return value;
        }

        private void EnsureJsonExportEmail(Dictionary<string, object> item, PoolRow row)
        {
            string email = (row?.Identifier ?? "").Trim();
            if (email.Length == 0) return;
            if (!BackendJson.TryGetMap(item, "user", out var user))
            {
                user = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                item["user"] = user;
            }
            if (BackendJson.GetString(user, "email").Length == 0)
            {
                user["email"] = email;
            }
        }

        private void EnsureJsonExportRefreshToken(Dictionary<string, object> item, Dictionary<string, object> sourceData)
        {
            string rt = FirstJsonString(
                BackendJson.GetString(sourceData, "oauth_refresh_token"),
                BackendJson.GetString(sourceData, "refresh_token"),
                BackendJson.NestedString(sourceData, "codex_session", "refresh_token"),
                BackendJson.NestedString(sourceData, "token", "refresh_token"),
                BackendJson.NestedString(sourceData, "credentials", "refresh_token")
            );
            item["refresh_token"] = rt;
            if (BackendJson.GetString(item, "oauth_refresh_token").Length == 0 && rt.Length > 0)
            {
                item["oauth_refresh_token"] = rt;
            }
        }

        private string FirstJsonString(params string[] values)
        {
            foreach (string value in values)
            {
                string text = (value ?? "").Trim();
                if (text.Length > 0) return text;
            }
            return "";
        }

        private string JsonExportDedupKey(Dictionary<string, object> item, PoolRow row)
        {
            if (BackendJson.TryGetMap(item, "user", out var user))
            {
                string userEmail = BackendJson.GetString(user, "email").Trim();
                if (userEmail.Length > 0) return userEmail.ToLowerInvariant();
            }
            string email = BackendJson.GetString(item, "email").Trim();
            if (email.Length > 0) return email.ToLowerInvariant();
            email = (row?.Identifier ?? "").Trim();
            if (email.Length > 0) return email.ToLowerInvariant();
            return JsonSerializer.Serialize(item);
        }

        /// Returns the export line, or null when the row cannot be exported.
        /// Async because the last-resort path asks the backend for the mailbox
        /// credential line over IPC.
        private async Task<string?> TryBuildAccountExportLineAsync(PoolRow row)
        {
            if (row == null) return null;

            string source = await FindMailboxLineForRowAsync(row).ConfigureAwait(true);
            if (source.Length == 0 && !string.IsNullOrWhiteSpace(row.RawLine))
            {
                source = row.RawLine;
            }

            if (!MailboxCredentialLineParser.TryParseMailboxExportParts(
                    source,
                    () => !string.IsNullOrWhiteSpace(row.ClientId) ? row.ClientId.Trim() : DefaultMailboxClientId(),
                    out string email, out string password, out string clientId, out string refreshToken))
            {
                return null;
            }

            if (email.Length == 0 || password.Length == 0 || clientId.Length == 0 || refreshToken.Length == 0)
            {
                return null;
            }

            return email + "----" + password + "----" + clientId + "----" + refreshToken;
        }

        // TryParseMailboxExportParts / LooksMicrosoftClientId migrated to
        // SmsWorkbench.Contracts.MailboxCredentialLineParser (placement rule 8:
        // parsing stays window-independent and xunit-testable).

        private string DefaultMailboxClientId()
        {
            string configured = settingsService.GetString("email_registration.oauth_client_id").Trim();
            return configured.Length > 0 ? configured : "9e5f94bc-e8a4-4e73-b8be-63364c29d753";
        }

        private string ShowImportTargetDialog(string title)
        {
            string selected = "";
            var dialog = new Window
            {
                Title = title,
                Owner = this,
                Width = 360,
                Height = 190,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("AppBg")
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Choose import target",
                Foreground = (System.Windows.Media.Brush)FindResource("TextMain"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            var combo = new ComboBox { SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 18) };
            combo.Items.Add(new ComboBoxItem { Content = "CPA", Tag = "cpa" });
            combo.Items.Add(new ComboBoxItem { Content = "SUB2API", Tag = "sub2api" });
            Grid.SetRow(combo, 1);
            root.Children.Add(combo);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "OK", Width = 76, Style = (Style)FindResource("PrimaryButton") };
            ok.Click += (_, __) =>
            {
                selected = ((combo.SelectedItem as ComboBoxItem)?.Tag as string) ?? "cpa";
                dialog.Close();
            };
            var cancel = new Button { Content = "Cancel", Width = 76, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (_, __) =>
            {
                selected = "";
                dialog.Close();
            };
            actions.Children.Add(ok);
            actions.Children.Add(cancel);
            Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            dialog.Content = root;
            dialog.ShowDialog();
            return selected;
        }
    }
}
