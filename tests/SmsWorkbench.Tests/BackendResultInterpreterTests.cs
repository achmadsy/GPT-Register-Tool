using System.Text.Json;
using SmsWorkbench;

namespace SmsWorkbench.Tests;

public sealed class BackendResultInterpreterTests
{
    // ── Scan summary extraction ─────────────────────────────────────────

    [Fact]
    public void TryExtractScanSummary_ReturnsNullForEmptyOutput()
    {
        Assert.Null(BackendResultInterpreter.TryExtractScanSummary(""));
        Assert.Null(BackendResultInterpreter.TryExtractScanSummary("   "));
        Assert.Null(BackendResultInterpreter.TryExtractScanSummary("plain text without JSON"));
    }

    [Fact]
    public void TryExtractScanSummary_FindsLastJsonWithResultsAndTotal()
    {
        string output = """
            [info] processing...
            {"results": [], "total": 5, "alive": 3, "account_deactivated": 1}
            """;
        var summary = BackendResultInterpreter.TryExtractScanSummary(output);
        Assert.NotNull(summary);
        Assert.Equal("5", BackendJson.GetString(summary, "total"));
        Assert.Equal("3", BackendJson.GetString(summary, "alive"));
    }

    [Fact]
    public void TryExtractScanSummary_ReadsResultsFromV2EnvelopePayload()
    {
        // Desktop mode wraps the backend result in an IPC v2 envelope, so
        // "results"/"total" live in the payload, not at the last `{...}` root.
        string output = """
            [info] scanning accounts...
            @@SMSWORKBENCH_V2@@{"schema":"smsworkbench.ipc.v2","version":2,"type":"progress","payload":{"stage":"scan"}}
            @@SMSWORKBENCH_V2@@{"schema":"smsworkbench.ipc.v2","version":2,"type":"result","payload":{"results":[{"email":"a@example.com","status":"alive"}],"total":5,"alive":3,"account_deactivated":1}}
            """;
        var summary = BackendResultInterpreter.TryExtractScanSummary(output);
        Assert.NotNull(summary);
        Assert.Equal("5", BackendJson.GetString(summary, "total"));
        Assert.Equal("3", BackendJson.GetString(summary, "alive"));
    }

    [Fact]
    public void TryExtractScanSummary_FallsBackToRawScanWithoutEnvelope()
    {
        // Same data emitted bare (CLI mode) must keep working.
        string output = """
            [info] scanning accounts...
            {"results": [{"email": "a@example.com", "status": "alive"}], "total": 5, "alive": 3, "account_deactivated": 1}
            """;
        var summary = BackendResultInterpreter.TryExtractScanSummary(output);
        Assert.NotNull(summary);
        Assert.Equal("5", BackendJson.GetString(summary, "total"));
        Assert.Equal("3", BackendJson.GetString(summary, "alive"));
    }

    [Fact]
    public void TryExtractScanSummary_ReadsResultsFromPromotionEnvelopePayload()
    {
        // The promotion check emits the same rows/total shape through the same envelope, so
        // the promotion check must resolve to a summary too.
        string output = """
            @@SMSWORKBENCH_V2@@{"schema":"smsworkbench.ipc.v2","version":2,"type":"result","payload":{"ok":true,"total":2,"success":2,"failed":0,"results":[{"email":"a@example.com","promotion_status":"eligible"},{"email":"b@example.com","promotion_status":"not_eligible"}]}}
            """;
        var summary = BackendResultInterpreter.TryExtractScanSummary(output);
        Assert.NotNull(summary);
        Assert.Equal("2", BackendJson.GetString(summary, "total"));
    }

    [Fact]
    public void TryExtractScanSummary_SurvivesMalformedEnvelopeJson()
    {
        // A truncated envelope must not throw out of the interpreter; it falls
        // back to the raw-text scan and simply reports no summary.
        string output = """
            @@SMSWORKBENCH_V2@@{"schema":"smsworkbench.ipc.v2","version":2,"type":"result","pay
            """;
        Assert.Null(BackendResultInterpreter.TryExtractScanSummary(output));
    }

    [Fact]
    public void TryExtractScanSummary_IgnoresJsonWithoutResults()
    {
        string output = """
            {"some": "data"}
            """;
        Assert.Null(BackendResultInterpreter.TryExtractScanSummary(output));
    }

    // ── Promotion-aware row rendering ───────────────────────────────────

    private static Dictionary<string, object> Row(params (string Key, object Value)[] pairs)
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object value) in pairs) row[key] = value;
        return row;
    }

    [Fact]
    public void IsPromotionRows_DetectsThePromotionBadge()
    {
        Assert.True(BackendResultInterpreter.IsPromotionRows(new[]
        {
            Row(("email", "a@example.com"), ("promotion_status", "Free·无优惠")),
        }));
    }

    [Fact]
    public void IsPromotionRows_ReturnsFalseForLivenessRows()
    {
        Assert.False(BackendResultInterpreter.IsPromotionRows(new[]
        {
            Row(("email", "a@example.com"), ("probe", Row(("ok", true)))),
        }));
    }

    [Fact]
    public void IsPromotionRows_HandlesEmptyAndNull()
    {
        Assert.False(BackendResultInterpreter.IsPromotionRows(new List<Dictionary<string, object>>()));
        Assert.False(BackendResultInterpreter.IsPromotionRows(null!));
    }

    [Fact]
    public void ResultRowStatus_LeadsWithThePromotionBadge()
    {
        // Without this the panel showed "AT valid / HTTP 200" and hid the answer.
        var row = Row(
            ("email", "a@example.com"),
            ("ok", true),
            ("promotion_status", "可试用Plus-50%×3month"),
            ("probe", Row(("ok", true), ("status_code", "200"))));
        Assert.Equal("可试用Plus-50%×3month", BackendResultInterpreter.ResultRowStatus(row));
    }

    [Fact]
    public void ResultRowStatus_PrefersThePreComposedPromotionDisplay()
    {
        // The payment-rail badge is appended by Python
        // (promotion_states.promotion_status_with_eligibility) and arrives as
        // `promotion_display`; the C# side must not re-derive the separator.
        var row = Row(
            ("email", "a@example.com"),
            ("ok", true),
            ("promotion_status", "可试用Plus-100%"),
            ("promotion_display", "可试用Plus-100% · card/upi/momo"),
            ("probe", Row(("ok", true), ("status_code", "200"))));
        Assert.Equal("可试用Plus-100% · card/upi/momo", BackendResultInterpreter.ResultRowStatus(row));
    }

    [Fact]
    public void ResultRowStatus_FallsBackToTheBareLabelWithoutPromotionDisplay()
    {
        // Rows written before the eligibility probe existed carry no
        // `promotion_display` and must keep rendering exactly as before.
        var row = Row(
            ("email", "a@example.com"),
            ("ok", true),
            ("promotion_status", "可试用Plus-100%"),
            ("probe", Row(("ok", true), ("status_code", "200"))));
        Assert.Equal("可试用Plus-100%", BackendResultInterpreter.ResultRowStatus(row));
    }

    [Fact]
    public void IsPromotionRows_DetectsAPromotionDisplayOnlyRow()
    {
        Assert.True(BackendResultInterpreter.IsPromotionRows(new[]
        {
            Row(("email", "a@example.com"), ("promotion_display", "Free·无优惠 · card")),
        }));
    }

    [Fact]
    public void ResultRowStatus_KeepsProbeLabellingForLiveness()
    {
        var alive = Row(("probe", Row(("ok", true), ("status_code", "200"))));
        Assert.Equal("AT valid / HTTP 200", BackendResultInterpreter.ResultRowStatus(alive));

        var dead = Row(("probe", Row(("status", "account_deactivated"))));
        Assert.Equal("Account deactivated", BackendResultInterpreter.ResultRowStatus(dead));
    }

    [Fact]
    public void ResultRowStatus_FallsBackToScanStatusWithoutProbe()
    {
        Assert.Equal("Normal", BackendResultInterpreter.ResultRowStatus(Row(("scan_status", "alive"))));
    }

    [Fact]
    public void PromotionSummary_GroupsBadgesAndCounts()
    {
        var rows = new[]
        {
            Row(("email", "a@example.com"), ("ok", true), ("promotion_status", "可试用Plus-50%"),
                ("probe", Row(("ok", true)))),
            Row(("email", "b@example.com"), ("ok", true), ("promotion_status", "Free·无优惠"),
                ("probe", Row(("ok", true)))),
            Row(("email", "c@example.com"), ("ok", false), ("promotion_status", "AT失效"),
                ("probe", Row(("ok", false), ("status_code", "401")))),
        };
        string summary = BackendResultInterpreter.PromotionSummary(rows);

        Assert.Contains("Total: 3", summary);
        Assert.Contains("Probe succeeded: 2", summary);
        Assert.Contains("AT invalid: 1", summary);
        Assert.Contains("可试用Plus-50%: 1", summary);
        Assert.Contains("Free·无优惠: 1", summary);
    }

    [Fact]
    public void PromotionSummary_HandlesEmpty()
    {
        Assert.Contains("Total: 0", BackendResultInterpreter.PromotionSummary(new List<Dictionary<string, object>>()));
    }

    // ── Result-dialog gate ──────────────────────────────────────────────

    [Theory]
    [InlineData("Account check (3)", true)]
    [InlineData("Account check", true)]
    [InlineData("Promotion check (12)", true)]
    [InlineData("Promotion check", true)]
    [InlineData("Batch email change (4)", false)]
    [InlineData("Phone registration (2)", false)]
    [InlineData("", false)]
    public void IsAccountScanResultTask_CoversLivenessAndPromotion(string taskName, bool expected)
    {
        Assert.Equal(expected, BackendResultInterpreter.IsAccountScanResultTask(taskName));
    }

    [Theory]
    [InlineData("Account check (3)", "Account check")]
    [InlineData("Promotion check (12)", "Promotion check")]
    [InlineData("Phone registration (2)", "Account check")]
    public void AccountScanResultTitle_MatchesTaskFamily(string taskName, string expected)
    {
        Assert.Equal(expected, BackendResultInterpreter.AccountScanResultTitle(taskName));
    }

    [Fact]
    public void IsAccountScanResultTask_AcceptsNullTaskName()
    {
        Assert.False(BackendResultInterpreter.IsAccountScanResultTask(null!));
    }

    // ── Deactivation detection ──────────────────────────────────────────

    [Fact]
    public void IsProbeDeactivated_DetectsDeactivatedInRow()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "account_deactivated"
        };
        Assert.True(BackendResultInterpreter.IsProbeDeactivated(row));
    }

    [Fact]
    public void IsProbeDeactivated_DetectsDeactivatedInProbe()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["probe"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = "deactivated"
            }
        };
        Assert.True(BackendResultInterpreter.IsProbeDeactivated(row));
    }

    [Fact]
    public void IsProbeDeactivated_DetectsDeactivatedInRelogin()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["relogin"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["error"] = "account has been deactivated"
            }
        };
        Assert.True(BackendResultInterpreter.IsProbeDeactivated(row));
    }

    [Fact]
    public void IsProbeDeactivated_ReturnsFalseForAliveRow()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "alive"
        };
        Assert.False(BackendResultInterpreter.IsProbeDeactivated(row));
    }

    [Fact]
    public void IsProbeDeactivated_ReturnsFalseForNull()
    {
        Assert.False(BackendResultInterpreter.IsProbeDeactivated(null!));
    }

    // ── Probe status labels ─────────────────────────────────────────────

    [Fact]
    public void ProbeStatusLabel_ReturnsDeactivated()
    {
        var probe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "account_deactivated"
        };
        Assert.Equal("Account deactivated", BackendResultInterpreter.ProbeStatusLabel(probe));
    }

    [Fact]
    public void ProbeStatusLabel_Returns401()
    {
        var probe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status_code"] = "401"
        };
        Assert.Equal("AT invalid / HTTP 401", BackendResultInterpreter.ProbeStatusLabel(probe));
    }

    [Fact]
    public void ProbeStatusLabel_ReturnsOkWithStatusCode()
    {
        var probe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = true,
            ["status_code"] = "200"
        };
        Assert.Equal("AT valid / HTTP 200", BackendResultInterpreter.ProbeStatusLabel(probe));
    }

    [Fact]
    public void ProbeStatusLabel_ReturnsOkWithoutStatusCode()
    {
        var probe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = true
        };
        Assert.Equal("AT valid", BackendResultInterpreter.ProbeStatusLabel(probe));
    }

    [Fact]
    public void ProbeStatusLabel_ReturnsFailedWithStatusCode()
    {
        var probe = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status_code"] = "500"
        };
        Assert.Equal("Check failed / HTTP 500", BackendResultInterpreter.ProbeStatusLabel(probe));
    }

    // ── IsProbeSucceeded / IsProbeReturned401 ───────────────────────────

    [Fact]
    public void IsProbeSucceeded_ReturnsTrueWhenOk()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["probe"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ok"] = true
            }
        };
        Assert.True(BackendResultInterpreter.IsProbeSucceeded(row));
    }

    [Fact]
    public void IsProbeSucceeded_ReturnsFalseWhenMissing()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Assert.False(BackendResultInterpreter.IsProbeSucceeded(row));
    }

    [Fact]
    public void IsProbeReturned401_ReturnsTrueForStatusCode401()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["probe"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status_code"] = "401"
            }
        };
        Assert.True(BackendResultInterpreter.IsProbeReturned401(row));
    }

    [Fact]
    public void IsProbeReturned401_ReturnsFalseWithoutProbe()
    {
        Assert.False(BackendResultInterpreter.IsProbeReturned401(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
    }

    // ── ScanResultError ─────────────────────────────────────────────────

    [Fact]
    public void ScanResultError_FindsOauthError()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["oauth"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["error"] = "token expired"
            }
        };
        Assert.Equal("token expired", BackendResultInterpreter.ScanResultError(row));
    }

    [Fact]
    public void ScanResultError_FindsRefreshError()
    {
        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["refresh"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["error"] = "network error"
            }
        };
        Assert.Equal("network error", BackendResultInterpreter.ScanResultError(row));
    }

    [Fact]
    public void ScanResultError_ReturnsEmptyWhenNoError()
    {
        Assert.Equal("", BackendResultInterpreter.ScanResultError(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
    }

    // ── ScanStatusLabel ─────────────────────────────────────────────────

    [Theory]
    [InlineData("alive", "Normal")]
    [InlineData("alive_probe_inconclusive", "RT valid / OAuth deep probe inconclusive")]
    [InlineData("account_deactivated", "Deactivated")]
    [InlineData("secondary_phone_verification_required", "Phone verification")]
    [InlineData("phone_verification_required", "Phone verified")]
    [InlineData("scan_failed", "Scan failed")]
    [InlineData("network_failed", "Network failed")]
    [InlineData("mailbox_failed", "Mailbox link failed")]
    [InlineData("auth_state_failed", "Session invalid")]
    [InlineData("rate_limited", "Rate limited")]
    [InlineData("relogin_failed", "Re-login failed")]
    [InlineData("unknown_status", "unknown_status")]
    [InlineData("", "Unknown")]
    public void ScanStatusLabel_ReturnsCorrectLabel(string input, string expected)
    {
        Assert.Equal(expected, BackendResultInterpreter.ScanStatusLabel(input));
    }

    // ── Proxy test results ──────────────────────────────────────────────

    [Fact]
    public void ParseProxyTestResult_ParsesFullResult()
    {
        string json = """
            {
              "ok": true,
              "stages": {
                "checkout": { "ip": "1.2.3.4", "country_code": "US", "expected_country": "US", "error": "" },
                "approve": { "ip": "5.6.7.8", "country_code": "TR", "expected_country": "TR", "error": "" },
                "update": { "ip": "9.10.11.12", "country_code": "TR", "expected_country": "TR", "error": "timeout" }
              }
            }
            """;
        var result = BackendResultInterpreter.ParseProxyTestResult(json);

        Assert.True(result.AllOk);
        Assert.Equal(3, result.Stages.Count);
        Assert.Equal("checkout", result.Stages[0].Stage);
        Assert.Equal("1.2.3.4", result.Stages[0].Ip);
        Assert.Equal("TR", result.Stages[2].ExpectedCountry);
        Assert.Equal("timeout", result.Stages[2].Error);
    }

    [Fact]
    public void ParseProxyTestResult_HandlesNonJson()
    {
        var result = BackendResultInterpreter.ParseProxyTestResult("not json");
        Assert.False(result.AllOk);
        Assert.Empty(result.Stages);
    }

    [Fact]
    public void ParseProxyTestResult_HandlesEmptyStages()
    {
        var result = BackendResultInterpreter.ParseProxyTestResult("{\"ok\": true}");
        Assert.True(result.AllOk);
        Assert.Empty(result.Stages);
    }

    // ── BackendExecutionResult ──────────────────────────────────────────

    [Fact]
    public void Interpret_ReturnsTimedOut()
    {
        var result = new BackendCommandResult(0, "", "", null, TimedOut: true);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.False(interpreted.IsSuccess);
        Assert.Equal("timed_out", interpreted.State);
        Assert.Contains("timed out", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_ReturnsFailedWithStandardError()
    {
        var result = new BackendCommandResult(1, "", "something went wrong", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.False(interpreted.IsSuccess);
        Assert.Equal("failed", interpreted.State);
        Assert.Contains("something went wrong", interpreted.DisplayText);
        Assert.Contains("arguments", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_ReturnsFailedWithNonZeroExitCode()
    {
        var result = new BackendCommandResult(-1, "error output", "", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.False(interpreted.IsSuccess);
        Assert.Equal("failed", interpreted.State);
    }

    [Fact]
    public void Interpret_ExitTwoSurfacesPreconditionCategory()
    {
        var result = new BackendCommandResult(2, "", "no mailbox account was found", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.False(interpreted.IsSuccess);
        Assert.Equal("failed", interpreted.State);
        Assert.Contains("pre-check", interpreted.DisplayText);
        Assert.Contains("no mailbox account was found", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_ExitThreeSurfacesRuntimeCategory()
    {
        var result = new BackendCommandResult(3, "", "extraction failed", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.False(interpreted.IsSuccess);
        Assert.Equal("failed", interpreted.State);
        Assert.Contains("runtime", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_ExitZeroWithStderrRemainsSuccess()
    {
        // Progress/diagnostics on stderr are normal for successful backend
        // runs (e.g. --view-inbox redirects progress output to stderr).
        var payload = JsonDocument.Parse("{\"ok\": true}").RootElement;
        var result = new BackendCommandResult(0, "", "[*] fetching messages", payload, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.True(interpreted.IsSuccess);
        Assert.Equal("completed", interpreted.State);
    }

    [Fact]
    public void Interpret_ExitZeroPlainOutputWithStderrRemainsSuccess()
    {
        var result = new BackendCommandResult(0, "plain output", "diagnostic line", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.True(interpreted.IsSuccess);
        Assert.Equal("plain output", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_ExitZeroWithoutOutputFallsBackToStderrText()
    {
        var result = new BackendCommandResult(0, "", "only diagnostics", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.True(interpreted.IsSuccess);
        Assert.Equal("only diagnostics", interpreted.DisplayText);
    }

    [Fact]
    public void Interpret_NonZeroExitCodeRetainsStructuredPayload()
    {
        var payload = JsonDocument.Parse(
            "{\"ok\": false, \"decision_text\": \"account is not eligible\"}").RootElement;
        var result = new BackendCommandResult(3, payload.GetRawText(), "", payload, false);

        BackendExecutionResult interpreted = BackendResultInterpreter.Interpret(result, "payment");

        Assert.False(interpreted.IsSuccess);
        Assert.True(interpreted.Payload.HasValue);
        Assert.Equal("account is not eligible", interpreted.Payload.Value.GetProperty("decision_text").GetString());
    }

    [Fact]
    public void Interpret_ReturnsPayloadJson()
    {
        var payload = JsonDocument.Parse("{\"ok\": true, \"url\": \"https://example.com\"}").RootElement;
        var result = new BackendCommandResult(0, "", "", payload, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.True(interpreted.IsSuccess);
        Assert.Equal("completed", interpreted.State);
        Assert.NotNull(interpreted.Payload);
        Assert.True(interpreted.Payload.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean());
    }

    [Fact]
    public void Interpret_ReturnsStandardOutputWhenNoPayload()
    {
        var result = new BackendCommandResult(0, "plain output", "", null, false);
        var interpreted = BackendResultInterpreter.Interpret(result, "test");

        Assert.True(interpreted.IsSuccess);
        Assert.Equal("plain output", interpreted.DisplayText);
    }

    [Fact]
    public void BatchSummaryLabelIncludesPartialTimeouts()
    {
        using var document = JsonDocument.Parse("{\"total\":20,\"success\":17,\"failed\":3,\"timed_out\":2}");
        Assert.Equal("Completed 17/20, failed 3, timed out 2", BackendResultInterpreter.BatchSummaryLabel(document.RootElement));
    }

    [Fact]
    public void BatchSummaryLabelIncludesLiveness401AndMailboxAuthFailures()
    {
        using var liveness = JsonDocument.Parse("{\"total\":5,\"success\":3,\"failed\":2,\"liveness_401\":2}");
        Assert.Equal("Completed 3/5, failed 2, 401 2", BackendResultInterpreter.BatchSummaryLabel(liveness.RootElement));

        using var mailbox = JsonDocument.Parse("{\"total\":5,\"success\":2,\"failed\":3,\"mailbox_auth_invalid\":3}");
        Assert.Equal("Completed 2/5, failed 3, mailbox auth failed 3", BackendResultInterpreter.BatchSummaryLabel(mailbox.RootElement));
    }

    [Fact]
    public void Cancelled_ReturnsCancelledState()
    {
        var cancelled = BackendResultInterpreter.Cancelled("test");
        Assert.False(cancelled.IsSuccess);
        Assert.Equal("cancelled", cancelled.State);
        Assert.Contains("Cancelled", cancelled.DisplayText);
    }

    [Fact]
    public void StartupFailed_ReturnsFailedState()
    {
        var failed = BackendResultInterpreter.StartupFailed("test", "cannot find python");
        Assert.False(failed.IsSuccess);
        Assert.Equal("failed", failed.State);
        Assert.Contains("cannot find python", failed.DisplayText);
    }
}
