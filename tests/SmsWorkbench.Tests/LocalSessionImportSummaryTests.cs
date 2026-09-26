using SmsWorkbench;

namespace SmsWorkbench.Tests;

public sealed class LocalSessionImportSummaryTests
{
    [Fact]
    public void FormatsCountsReasonsAndStoragePaths()
    {
        string payload = """
            {
              "ok": true,
              "imported": 1,
              "skipped": 2,
              "results": [
                {"file": "File 1", "status": "imported"},
                {"file": "File 2", "status": "skipped", "reason": "Account already exists"},
                {"file": "File 3", "status": "skipped", "reason": "Missing valid account email"}
              ],
              "session_dir": "C:\\app\\sessions",
              "database_path": "C:\\app\\runtime\\accounts.sqlite3"
            }
            """;

        var summary = LocalSessionImportSummary.Format(payload);

        Assert.NotNull(summary);
        Assert.True(summary.Ok);
        Assert.Equal(1, summary.Imported);
        Assert.Equal(2, summary.Skipped);
        Assert.Contains("Imported 1 account session(s), skipped 2.", summary.Message);
        Assert.Contains("File 1: imported", summary.Message);
        Assert.Contains("File 2: Account already exists", summary.Message);
        Assert.Contains("File 3: Missing valid account email", summary.Message);
        Assert.Contains("Saved under: C:\\app\\sessions", summary.Message);
        Assert.Contains("Indexed in: C:\\app\\runtime\\accounts.sqlite3", summary.Message);
        Assert.Contains("duplicates of an existing email are skipped", summary.Message);
    }

    [Fact]
    public void AllSkippedShowsContractInsteadOfDuplicateNote()
    {
        string payload = """
            {
              "ok": false,
              "imported": 0,
              "skipped": 1,
              "results": [
                {"file": "File 1", "status": "skipped", "reason": "Expected a single session JSON object, not an array; select one account per file"}
              ]
            }
            """;

        var summary = LocalSessionImportSummary.Format(payload);

        Assert.NotNull(summary);
        Assert.False(summary.Ok);
        Assert.Equal(0, summary.Imported);
        Assert.Contains("No account was imported; 1 file(s) skipped.", summary.Message);
        Assert.Contains("not an array", summary.Message);
        Assert.Contains("access_token/accessToken", summary.Message);
        Assert.DoesNotContain("duplicates of an existing email", summary.Message);
    }

    [Fact]
    public void UnknownReasonIsRedactedNotEchoed()
    {
        // The sanitizer's log rules mask account emails; an unexpected reason
        // containing one must not survive into the dialog.
        string payload = """
            {
              "ok": false,
              "imported": 0,
              "skipped": 1,
              "results": [
                {"file": "File 1", "status": "skipped", "reason": "boom user@example.com leaked"}
              ]
            }
            """;

        var summary = LocalSessionImportSummary.Format(payload);

        Assert.NotNull(summary);
        Assert.DoesNotContain("user@example.com", summary.Message);
    }

    [Fact]
    public void ReturnsNullForNonImportPayloads()
    {
        Assert.Null(LocalSessionImportSummary.Format(""));
        Assert.Null(LocalSessionImportSummary.Format("not json"));
        Assert.Null(LocalSessionImportSummary.Format("""{"ok": true}"""));
        Assert.Null(LocalSessionImportSummary.Format("[1, 2]"));
    }
}
