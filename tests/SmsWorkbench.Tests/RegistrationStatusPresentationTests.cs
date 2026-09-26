using System.Globalization;
using SmsWorkbench;
using Xunit;

namespace SmsWorkbench.Tests;

public class RegistrationStatusPresentationTests
{
    [Theory]
    [InlineData("partial_registered")]
    [InlineData("PARTIAL_REGISTERED")]
    [InlineData("半注册")]
    public void PartialRegistrationIsNotAnUnusedMailbox(string state)
    {
        Assert.True(RegistrationStatusPresentation.IsPartial(state));
        Assert.Equal("Partially registered", RegistrationStatusPresentation.MailboxStatus(state, "Can receive mail"));
        Assert.True(RegistrationStatusPresentation.NeedsAttention(new PoolRow { RegistrationStatus = state }));
    }

    [Fact]
    public void OrdinaryMailboxAndRegisteredAccountKeepTheirStatus()
    {
        Assert.Equal("Can receive mail", RegistrationStatusPresentation.MailboxStatus("unknown", "Can receive mail"));
        Assert.False(RegistrationStatusPresentation.IsPartial("registered"));
        Assert.Equal("Registered", AccountStatusInterpreter.DisplayAccountStatus("registered", "", "AT", "", "", "", ""));
    }

    [Fact]
    public void PartialRegistrationHasWarningSeverityAndExplicitAccountLabel()
    {
        Assert.Equal("warn", new StatusSeverityConverter().Convert("Partially registered", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("Partially registered", AccountStatusInterpreter.DisplayAccountStatus("partial_registered", "", "", "user_already_exists", "", "", ""));
    }

    [Fact]
    public void PartialRegistrationEventHasAnOperatorLine()
    {
        var progress = new BackendProgressEvent("registration", "run", "abc123", "", "registration_status_changed", "running", "半注册");
        Assert.Equal("Registration · abc123 · Partially registered", BackendLogPresenter.ProgressEventLine(progress));
    }
}
