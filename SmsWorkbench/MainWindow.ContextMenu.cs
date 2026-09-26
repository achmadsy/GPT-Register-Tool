// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

namespace SmsWorkbench
{
    public partial class MainWindow
    {
        // ── Search clear button ──

        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchText = "";
            UpdateSearchClearVisibility();
        }

        /// <summary>
        /// Toggle the visibility of the search clear (×) button based on
        /// whether the search text is non-empty. Called from the SearchText
        /// setter and from the clear button click handler.
        /// </summary>
        private void UpdateSearchClearVisibility()
        {
            if (SearchClearButton != null)
            {
                SearchClearButton.Visibility = string.IsNullOrEmpty(SearchText)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        // ── DataGrid context menu handlers ──

        private void CtxViewDetail_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row)
                ShowAccountDetail(row);
        }

        private void CtxViewInbox_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row)
                ShowInboxDialog(row);
        }

        private void CtxCopyEmail_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row && !string.IsNullOrWhiteSpace(row.Identifier))
            {
                try
                {
                    Clipboard.SetText(row.Identifier);
                    NotifyInfo("Email copied: " + row.Identifier);
                }
                catch (Exception ex)
                {
                    Log("Copy email failed: " + ex.Message);
                }
            }
        }

        private void CtxCopyAccessToken_Click(object sender, RoutedEventArgs e)
            => RunUiTask(() => CtxCopyAccessTokenAsync());

        private async Task CtxCopyAccessTokenAsync(CancellationToken ct = default)
        {
            if (AccountGrid?.SelectedItem is not PoolRow row)
            {
                NotifyWarning("Select an account first.");
                return;
            }
            string accessToken = await ResolveAccountAccessTokenAsync(row);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                NotifyWarning("Selected account has no AT to copy.");
                return;
            }
            try
            {
                Clipboard.SetText(accessToken);
                NotifyInfo("AT copied.");
            }
            catch (Exception ex)
            {
                Log("Copy AT failed: " + ex.Message);
            }
        }

        private void CtxCopyPayPal_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row && !string.IsNullOrWhiteSpace(row.PayPalUrl))
            {
                CopyPayPalUrl(row.PayPalUrl, row.Identifier);
            }
            else
            {
                NotifyWarning("Selected row has no payment link.");
            }
        }

        private void CtxOpenPayPal_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row && !string.IsNullOrWhiteSpace(row.PayPalUrl))
            {
                OpenPayPalUrl(row.PayPalUrl, row.Identifier);
            }
            else
            {
                NotifyWarning("Selected row has no payment link.");
            }
        }

        private void CtxOpenSource_Click(object sender, RoutedEventArgs e)
        {
            if (AccountGrid?.SelectedItem is PoolRow row)
                OpenAccountJson(row);
        }

        private void CtxCheckAccountAlive_Click(object sender, RoutedEventArgs e)
            => RunUiTask(() => CtxCheckAccountAliveAsync());

        private async Task CtxCheckAccountAliveAsync(CancellationToken ct = default)
        {
            if (AccountGrid?.SelectedItem is not PoolRow row || string.IsNullOrWhiteSpace(row.Identifier))
            {
                NotifyWarning("Select an account first.");
                return;
            }
            await CheckAccountAliveAsync(row);
        }

        private void CtxBatchProtocolPayment_Click(object sender, RoutedEventArgs e)
        {
            BatchProtocolPayment_Click(sender, e);
        }

        private void CtxChangeEmail_Click(object sender, RoutedEventArgs e)
            => RunUiTask(() => CtxChangeEmailAsync());

        private void ChangeEmail_Click(object sender, RoutedEventArgs e)
            => RunUiTask(() => CtxChangeEmailAsync());

        private async Task CtxChangeEmailAsync(CancellationToken ct = default)
        {
            var rows = SelectedRowsOrCurrent().Where(row => row != null && !string.IsNullOrWhiteSpace(row.Identifier)).ToList();
            if (rows.Count == 0)
            {
                NotifyWarning("Select an account to change email first.");
                return;
            }
            var options = ChangeEmailDialogService.Show(
                this,
                rows.Count,
                DefaultWorkerCount(),
                GetConfiguredSmailrDomain(),
                GetConfiguredCfWorkerDomain());
            if (options is null) return;
            var plan = BackendCommandPlanner.CreateChangeEmail(
                rows.Select(row => row.Identifier).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                options.Provider,
                options.MailboxFile,
                options.Workers,
                options.SmailrDomain,
                options.CfworkerDomain,
                GetRegistrationProxyPool(),
                rootDir);
            string json;
            try { json = await RunBackendWithResultAsync(plan.TaskName, plan.Arguments.ToList(), plan.TimeoutMilliseconds ?? 900000); }
            finally { foreach (string path in plan.TempFiles) TryDeleteFile(path); }
            try
            {
                using var doc = JsonDocument.Parse(json);
                bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                await DialogFactory.ShowInfoAsync(this, "Change Email", ok ? "Email change completed." : "Email change partially failed. Check the task result.");
                RefreshPools();
            }
            catch
            {
                await DialogFactory.ShowInfoAsync(this, "Change Email", "No valid result received. Check the run log.");
            }
        }

        private async Task CheckAccountAliveAsync(PoolRow row, CancellationToken ct = default)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Identifier))
            {
                NotifyWarning("Select an account first.");
                return;
            }

            if (!row.HasAccessToken)
            {
                await DialogFactory.ShowInfoAsync(this, "Account Check", "This account has no Access Token and cannot be checked. Sign in first to obtain an AT.");
                return;
            }

            try
            {
                Log($"Checking account: {row.Identifier}");
                var args = new List<string> { "--quota-usage", "--email", row.Identifier, "--refresh-timeout", "45" };
                AddRegistrationProxy(args);
                string json = await RunBackendWithResultAsync("Account check", args, 120000, ct);

                if (string.IsNullOrWhiteSpace(json))
                {
                    await DialogFactory.ShowInfoAsync(this, "Account Check", "Account check failed: no valid response received.");
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                {
                    string detail = FormatAccountLivenessDetail(root);
                    await DialogFactory.ShowInfoAsync(this, $"Account Check: {row.Identifier}", detail);
                    Log($"Account check succeeded: {row.Identifier} → AT valid");
                    RefreshPools();
                }
                else
                {
                    string error = root.TryGetProperty("error", out var errEl) ? SensitiveDataSanitizer.Redact(errEl.GetString() ?? "Unknown error") : "Unknown error";
                    string status = root.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                    string failureClass = root.TryGetProperty("failure_class", out var fcEl) ? fcEl.GetString() ?? "" : "";
                    string msg = $"Account check failed: {error}";
                    if (failureClass.Length > 0)
                        msg += $"\nFailure class: {failureClass}";
                    if (status == "token_invalid")
                        msg += "\n\nThe endpoint returned HTTP 401; the current Access Token has expired.";
                    await DialogFactory.ShowInfoAsync(this, $"Account Check: {row.Identifier}", msg);
                    Log($"Account check failed: {row.Identifier} → {error} failure_class={failureClass}");
                }
            }
            catch (Exception ex)
            {
                Log($"Account check error: {ex.Message}");
                    await DialogFactory.ShowInfoAsync(this, "Account Check", $"Account check error: {SensitiveDataSanitizer.Redact(ex.Message)}");
            }
        }

        private static string FormatAccountLivenessDetail(JsonElement root)
        {
            var sb = new StringBuilder();
            string statusCode = root.TryGetProperty("status_code", out var codeEl) ? codeEl.ToString() : "";
            sb.AppendLine("Status: AT valid");
            sb.AppendLine("Endpoint: HTTP " + (string.IsNullOrWhiteSpace(statusCode) ? "200" : statusCode));
            return sb.ToString().TrimEnd();
        }
    }
}
