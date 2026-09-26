// Opted into nullable reference checking file-by-file - see the note in
// PaymentBatchService.cs for why the project-wide switch stays `annotations`.
#nullable enable

using System.Linq.Expressions;
using Expr = System.Linq.Expressions.Expression;

namespace SmsWorkbench
{
    public sealed class StatusSeverityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = (value as string ?? "").Trim();
            if (status.Length == 0) return "neutral";
            if (RegistrationStatusPresentation.IsPartial(status)) return "warn";

            if (Has(status, "失败", "失效", "掉号", "异常", "无RT", "缺失", "未获取", "K12未切换", "K12已退出")
                || Has(status, "failed", "invalid", "deactivated", "missing", "absent", "no rt", "k12 exited", "not switched"))
                return "danger";

            if (PromotionStatusPresentation.IsTrialEligible(status)
                || status.Contains('✅') || Has(status, "完成", "已注册", "已获取", "已导入", "K12已进入", "PM已创建", "已设置")
                || Has(status, "completed", "registered", "imported", "present", "k12 joined", "pm created"))
                return "success";

            if (Has(status, "待", "缺", "K12已申请", "旧token", "OTP")
                || Has(status, "pending", "k12 requested", "legacy rt"))
                return "warn";

            if (Has(status, "已保存", "待刷新", "未知") || Has(status, "saved", "refresh", "unknown"))
                return "info";

            return "neutral";
        }

        private static bool Has(string status, params string[] keywords) =>
            keywords.Any(keyword => status.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public static class PromotionStatusPresentation
    {
        // Machine promotion state emitted by the Python backend
        // (sms_tool/promotion_states.py). The Chinese label is display copy:
        // filtering and sorting key off the code so a label reword cannot
        // change behaviour. Rows recorded before the field existed fall back
        // to the legacy substring rule.
        public const string TrialEligibleState = "trial_eligible";

        public static bool IsTrialEligible(string status, string state = null)
        {
            string machine = (state ?? "").Trim();
            if (machine.Length > 0) return machine == TrialEligibleState;
            return IsTrialEligibleLabel(status);
        }

        public static bool IsTrialEligibleLabel(string status)
        {
            string value = (status ?? "").Trim();
            if (value.Length == 0) return false;
            return value.Contains("可试用", StringComparison.OrdinalIgnoreCase)
                && value.Contains("plus", StringComparison.OrdinalIgnoreCase);
        }

        public static int SortRank(string status, string state = null)
        {
            if (IsTrialEligible(status, state)) return 0;
            return string.IsNullOrWhiteSpace(status) ? 2 : 1;
        }
    }

    public static class AccountGridOrdering
    {
        // Compiled property getters: sorting several hundred rows used to do a
        // TypeDescriptor property lookup (reflection) per row per keystroke.
        private static readonly Dictionary<string, Func<PoolRow, object>> PropertyGetters = BuildGetters();

        private static Dictionary<string, Func<PoolRow, object>> BuildGetters()
        {
            var getters = new Dictionary<string, Func<PoolRow, object>>(StringComparer.Ordinal);
            ParameterExpression parameter = Expr.Parameter(typeof(PoolRow), "row");
            foreach (System.Reflection.PropertyInfo property in typeof(PoolRow).GetProperties())
            {
                if (!property.CanRead || property.GetMethod == null) continue;
                System.Linq.Expressions.Expression body = Expr.Convert(
                    Expr.Property(parameter, property), typeof(object));
                getters[property.Name] = Expr.Lambda<Func<PoolRow, object>>(body, parameter).Compile();
            }
            return getters;
        }

        public static IEnumerable<PoolRow> Apply(
            IEnumerable<PoolRow> rows,
            string sortMember,
            ListSortDirection? direction)
        {
            if (rows == null) return Enumerable.Empty<PoolRow>();
            string member = (sortMember ?? "").Trim();
            if (member.Length == 0 || direction == null) return rows;

            Func<PoolRow, AccountSortValue> selector = row => SortValue(row, member);
            return direction == ListSortDirection.Descending
                ? rows.OrderByDescending(selector)
                : rows.OrderBy(selector);
        }

        private static AccountSortValue SortValue(PoolRow row, string member)
        {
            if (member.Equals(nameof(PoolRow.PromotionStatus), StringComparison.Ordinal))
            {
                string promotion = row?.PromotionStatus ?? "";
                return new AccountSortValue(PromotionStatusPresentation.SortRank(promotion, row?.PromotionState), promotion);
            }

            if (PropertyGetters.TryGetValue(member, out Func<PoolRow, object>? getter))
            {
                object? value = row == null ? null : getter(row);
                return new AccountSortValue(value == null ? 1 : 0, Convert.ToString(value, CultureInfo.CurrentCulture) ?? "");
            }
            return new AccountSortValue(1, "");
        }

        private readonly record struct AccountSortValue(int Rank, string Text) : IComparable<AccountSortValue>
        {
            public int CompareTo(AccountSortValue other)
            {
                int rank = Rank.CompareTo(other.Rank);
                return rank != 0
                    ? rank
                    : StringComparer.CurrentCultureIgnoreCase.Compare(Text, other.Text);
            }
        }
    }
}
