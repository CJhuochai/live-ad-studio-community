using System.Text;
using System.Text.RegularExpressions;
using LiveAdDesk;

namespace LiveAdStudio;

sealed record PlanFields(string? PlanId, string? Goal, decimal? Budget, decimal? Target);
sealed record PlanPreview(PlanFields Fields, string[] Review);

static class ScreenshotForm
{
    const string IdLabel = @"计划(?:I|\])D";
    const string BudgetLabel = @"日预算(?:\(元\))?";
    const string RoiLabel = @"(?:全域)?支付RO(?:I|\])目标";
    const string CostLabel = @"(?:全域)?成单人数成本目标(?:\(元\))?|获取1个成单用户的目标出价(?:\(元\))?";
    const string Money = @"(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]+)?(?:万|千)?(?:元)?";
    const string Number = @"[0-9]+(?:\.[0-9]+)?";
    const string Id = @"[0-9]{5,40}";

    static string[] Lines(string text) => text.Normalize(NormalizationForm.FormKC)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => Regex.Replace(line, @"\s+", ""))
        .Where(line => !line.Contains("->") && !line.Contains("→"))
        .ToArray();

    static IEnumerable<string> Values(string[] lines, string label, string value)
    {
        string prefix = $"(?:{label})[:：]?[¥￥]?";
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in Regex.Matches(lines[i], "(?<!建议)(?<!推荐)" + prefix + $"(?<value>{value})(?=$|[；;，、|]|直播间|全域|编辑|日预算|计划ID)", RegexOptions.IgnoreCase))
                yield return match.Groups["value"].Value;
            if (i + 1 < lines.Length && Regex.IsMatch(lines[i], "^" + prefix + "$", RegexOptions.IgnoreCase)
                && Regex.IsMatch(lines[i + 1], "^" + value + "$"))
                yield return lines[i + 1];
        }
    }

    public static PlanPreview ReadPlan(IReadOnlyList<string> texts)
    {
        var pages = texts.Select(Lines).ToArray();
        var lines = pages.SelectMany(page => page).ToArray();
        var ids = pages.SelectMany(page => Values(page, IdLabel, Id)).Distinct().ToArray();
        if (ids.Length > 1) throw new InvalidDataException("截图包含不同的计划ID，请重新选择同一计划的截图。");

        var review = new List<string>();
        if (pages.Length > 1 && pages.Any(page => !Values(page, IdLabel, Id).Any()))
            review.Add("有截图未识别到计划ID，请确认两张图来自同一计划。");

        decimal? Amount(string label, string pattern, string title)
        {
            // Read each page independently so a label at the bottom cannot borrow a value from the next image.
            var numbers = pages.SelectMany(page => Values(page, label, pattern))
                .Select(ExcelImport.Number).Distinct().ToArray();
            if (numbers.Length > 1) review.Add(title + "有多个值，请对照原图填写。");
            return numbers.Length == 1 ? numbers[0] : null;
        }
        var budget = Amount(BudgetLabel, Money, "日预算");
        var roi = Amount(RoiLabel, Number, "目标ROI");
        var cost = Amount(CostLabel, Money, "目标成单人数成本");
        bool amountGoal = lines.Any(line => Regex.IsMatch(line, RoiLabel, RegexOptions.IgnoreCase) || line == "提升成交金额");
        bool peopleGoal = lines.Any(line => Regex.IsMatch(line, CostLabel) || line == "提升成单人数");
        if (amountGoal && peopleGoal) review.Add("截图出现两种优化目标，请核对当前选中项后填写。");
        string? goal = amountGoal == peopleGoal ? null : amountGoal ? "成交金额" : "成单人数";
        return new(new(ids.SingleOrDefault(), goal, budget, goal == "成交金额" ? roi : goal == "成单人数" ? cost : null), review.ToArray());
    }

    public static void CheckSnapshotPlan(string text, string expectedPlanId)
    {
        var ids = Values(Lines(text), IdLabel, Id).Distinct().ToArray();
        if (ids.Length > 1 || (ids.Length == 1 && expectedPlanId.Length > 0 && ids[0] != expectedPlanId))
            throw new InvalidDataException("截图的计划ID与当前场次不一致，请选择对应场次。");
    }

    public static void ApplyPlan(Room room, PlanFields values)
    {
        if (values.PlanId is not null && !Regex.IsMatch(values.PlanId, @"\A[0-9]{1,40}\z"))
            throw new InvalidDataException("计划ID只能填写数字。");
        if (values.Goal is not (null or "成交金额" or "成单人数") ||
            values.Budget < 0 || values.Target < 0 || (values.Target.HasValue && values.Goal is null))
            throw new InvalidDataException("填写目标值时，请同时核对优化目标。预算和目标值不可为负。");
        if (values == new PlanFields(null, null, null, null))
            throw new InvalidDataException("请至少确认一个计划字段。");

        var planId = values.PlanId ?? room.Business.PlanId;
        DecisionCommands.EnsurePlanChangeAllowed(room, planId);
        if (planId != room.Business.PlanId)
        {
            if (room.Business.PlanId.Length > 0)
            {
                room.Finance.Budget = null;
                room.Finance.Target = null;
            }
            room.Business.PlanId = planId;
            room.Facts = new();
        }
        if (values.Goal is not null && room.Finance.Goal != values.Goal)
        {
            room.Finance.Goal = values.Goal;
            room.Finance.Target = null;
        }
        if (values.Budget.HasValue) room.Finance.Budget = values.Budget;
        if (values.Target.HasValue) room.Finance.Target = values.Target;
    }
}
