#nullable enable

namespace SmsWorkbench;

public static class RegistrationStatusPresentation
{
    public const string PartialRegistered = "partial_registered";
    public const string PartialLabel = "Partially registered";

    // Legacy backend label; kept as a recognised synonym so rows recorded
    // before the English relabel still classify correctly.
    public const string LegacyPartialLabel = "半注册";

    public static bool IsPartial(string? state) =>
        string.Equals(state, PartialRegistered, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, PartialLabel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, LegacyPartialLabel, StringComparison.Ordinal);

    public static string MailboxStatus(string? state, string fallback) =>
        IsPartial(state) ? PartialLabel : state == "registered" ? "Registered" : fallback;

    public static bool NeedsAttention(PoolRow row) =>
        IsPartial(row.RegistrationStatus) || IsPartial(row.Status)
        || ContainsAny(row.Status, "待", "缺", "失败", "pending", "missing", "failed", "partially");

    private static bool ContainsAny(string value, params string[] keywords) =>
        keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
