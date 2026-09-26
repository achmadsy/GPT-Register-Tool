// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

using System.Text.Json;

namespace SmsWorkbench
{
    /// <summary>
    /// Formats the <c>--import-local-session</c> backend payload into the
    /// operator-facing import dialog text. Pure and token-safe by construction:
    /// the backend only emits "File N" labels and reasons from a closed
    /// allowlist, and anything unexpected is redacted before it reaches the
    /// dialog. Kept static so the dialog logic is testable without a Window.
    /// </summary>
    public static class LocalSessionImportSummary
    {
        public sealed record ImportSummary(bool Ok, int Imported, int Skipped, string Message);

        /// <summary>
        /// Parse the backend result payload (already unwrapped from the IPC v2
        /// envelope by <see cref="BackendJsonProtocol"/>) and render the dialog
        /// message. Returns null when the payload is not a recognizable import
        /// result — the caller then shows a generic failure dialog.
        /// </summary>
        public static ImportSummary? Format(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return null;
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("imported", out _))
                return null;

            int imported = root.TryGetProperty("imported", out var importedEl) && importedEl.TryGetInt32(out int i) ? i : 0;
            int skipped = root.TryGetProperty("skipped", out var skippedEl) && skippedEl.TryGetInt32(out int s) ? s : 0;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;

            var text = new System.Text.StringBuilder();
            text.AppendLine(imported > 0
                ? $"Imported {imported} account session(s), skipped {skipped}."
                : $"No account was imported; {skipped} file(s) skipped.");

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in results.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string file = JsonText(item, "file");
                    string status = JsonText(item, "status");
                    string reason = JsonText(item, "reason");
                    if (!item.TryGetProperty("reason", out _))
                        reason = status; // imported rows have no reason; show status instead
                    if (file.Length == 0) continue;
                    text.AppendLine($"{file}: {SensitiveDataSanitizer.Redact(reason)}");
                }
            }

            if (root.TryGetProperty("session_dir", out var dirEl) && dirEl.ValueKind == JsonValueKind.String
                && dirEl.GetString() is { Length: > 0 } sessionDir)
                text.AppendLine($"Saved under: {sessionDir}");
            if (root.TryGetProperty("database_path", out var dbEl) && dbEl.ValueKind == JsonValueKind.String
                && dbEl.GetString() is { Length: > 0 } dbPath)
                text.AppendLine($"Indexed in: {dbPath}");

            text.Append(imported > 0
                ? "If a row is still missing: duplicates of an existing email are skipped, and the current search/filter/page can hide it — clear the filter and Refresh."
                : "Each file must contain ONE JSON object with an email (email, user.email, auth_session.email, or auth_session.user.email) and at least one token: access_token/accessToken, oauth_refresh_token/refresh_token, or session_token/sessionToken (auth_session nesting accepted). Arrays and cookie-only files are rejected.");

            return new ImportSummary(ok, imported, skipped, text.ToString().TrimEnd());
        }

        private static string JsonText(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
    }
}
