namespace Ham.Core.Models;

/// <summary>日程重复频率。</summary>
public enum RecurrenceFrequency
{
    None,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// 日程重复规则。支持按天 / 按周 / 按月重复，并可设置重复结束日期。
/// </summary>
public sealed record RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; init; } = RecurrenceFrequency.None;

    /// <summary>
    /// 按周重复时的星期集合（0=周日）。为空则沿用起始日的星期。
    /// </summary>
    /// <remarks>
    /// 类型必须是 <see cref="IReadOnlyList{T}"/> 而<b>不能</b>是 <c>IReadOnlySet&lt;T&gt;</c>：
    /// System.Text.Json 能为 <c>IReadOnlyList&lt;T&gt;</c> 实例化 <c>List&lt;T&gt;</c>，
    /// 但 <c>IReadOnlySet&lt;T&gt;</c> 是抽象接口，反序列化时抛
    /// <see cref="NotSupportedException"/>，导致读取数据文件时整个应用启动失败、
    /// 窗口永不出现。去重语义由 <see cref="WeeklyDaysDistinct"/> 提供。
    /// </remarks>
    public IReadOnlyList<int> WeeklyDays { get; init; } = [];

    /// <summary>去重后的星期集合，供高频查询使用。</summary>
    public IReadOnlySet<int> WeeklyDaysDistinct => WeeklyDays.ToHashSet();

    /// <summary>按周重复时的间隔周数，1 表示每周。</summary>
    public int WeekInterval { get; init; } = 1;

    /// <summary>按月重复时是按"第几个星期几"还是"固定日期"。</summary>
    public bool MonthlyByDayOfWeek { get; init; }

    /// <summary>重复结束日期（含）。null 表示一直重复。</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>重复次数上限。null 表示不限。</summary>
    public int? MaxOccurrences { get; init; }

    public bool IsRepeating => Frequency != RecurrenceFrequency.None;

    /// <summary>
    /// 依据规则展开某一段日期区间内的所有发生日。
    /// </summary>
    public IReadOnlyList<DateOnly> Expand(DateOnly start, DateOnly rangeEnd, int maxCount = 512)
    {
        if (!IsRepeating) return [start];

        // 按月重复无法逐日推进（逐日推进会永远停在起始月），因此单独按月迭代。
        if (Frequency == RecurrenceFrequency.Monthly)
        {
            return ExpandMonthly(start, rangeEnd, maxCount);
        }

        var result = new List<DateOnly>();
        var cursor = start;
        var day = (int)DayOfWeekOf(start);
        var emitted = 0;

        while (cursor <= rangeEnd && result.Count < maxCount)
        {
            if (EndDate is { } end && cursor > end) break;
            if (MaxOccurrences is { } cap && emitted >= cap) break;

            var occurs = Frequency switch
            {
                RecurrenceFrequency.Daily => true,
                RecurrenceFrequency.Weekly =>
                    (WeeklyDays.Count > 0
                        ? WeeklyDaysDistinct.Contains((int)DayOfWeekOf(cursor))
                        : (int)DayOfWeekOf(cursor) == day)
                    && WeeksBetween(start, cursor) % Math.Max(WeekInterval, 1) == 0,
                _ => false,
            };

            if (occurs)
            {
                result.Add(cursor);
                emitted++;
            }

            cursor = cursor.AddDays(1);
        }

        return result;
    }

    private List<DateOnly> ExpandMonthly(DateOnly start, DateOnly rangeEnd, int maxCount)
    {
        var result = new List<DateOnly>();
        var anchorWeekday = (int)DayOfWeekOf(start);
        var anchorOrdinal = (start.Day - 1) / 7 + 1;   // 起始日是当月第几个星期几

        var month = new DateOnly(start.Year, start.Month, 1);
        var lastMonth = new DateOnly(rangeEnd.Year, rangeEnd.Month, 1);

        while (month <= lastMonth && result.Count < maxCount)
        {
            if (MaxOccurrences is { } cap && result.Count >= cap) break;

            DateOnly? candidate = MonthlyByDayOfWeek
                ? NthWeekdayOfMonth(month.Year, month.Month, anchorWeekday, anchorOrdinal)
                : FixedDayOfMonth(month.Year, month.Month, start.Day);

            if (candidate is { } d
                && d >= start
                && d <= rangeEnd
                && (EndDate is null || d <= EndDate))
            {
                result.Add(d);
            }

            month = month.AddMonths(1);
        }

        return result;
    }

    /// <summary>取某年某月"第 ordinal 个星期 weekday"对应的日期；不存在返回 null。</summary>
    private static DateOnly? NthWeekdayOfMonth(int year, int month, int weekday, int ordinal)
    {
        var first = new DateOnly(year, month, 1);
        var offset = (weekday - (int)DayOfWeekOf(first) + 7) % 7;
        var day = 1 + offset + (ordinal - 1) * 7;
        if (day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    /// <summary>取某年某月的固定日期；该月天数不足时返回 null（31 号不自动进位）。</summary>
    private static DateOnly? FixedDayOfMonth(int year, int month, int day)
    {
        if (day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    private static DayOfWeek DayOfWeekOf(DateOnly date) => (DayOfWeek)date.DayOfWeek;

    private static int WeeksBetween(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber;
        return days < 0 ? 0 : days / 7;
    }
}

/// <summary>日程分组，用于分类管理。</summary>
public sealed record ScheduleGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Icon { get; init; } = "📅";
    public int Order { get; init; }
}

/// <summary>
/// 一条日程事项。
/// </summary>
public sealed record ScheduleItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    public string Location { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    public required DateTime Start { get; init; }

    /// <summary>结束时间。与开始时间相隔不得超过 24 小时。</summary>
    public DateTime? End { get; init; }

    public string? GroupId { get; init; }

    /// <summary>提前提醒的分钟数；null 表示不提醒。</summary>
    public int? ReminderMinutes { get; init; }

    public RecurrenceRule Recurrence { get; init; } = new();

    /// <summary>关联的课头号；日程卡片会显示对应课程名。</summary>
    public string? LinkedCourseId { get; init; }

    public string? LinkedCourseName { get; init; }

    public DateTime? ActualStart { get; init; }
    public DateTime? ActualEnd { get; init; }

    public bool IsOccurrenceOverride { get; init; }

    /// <summary>
    /// 校验并规范化日程输入。返回 null 表示合法，否则为错误原因。
    /// </summary>
    public static string? Validate(string title, DateTime start, DateTime? end)
    {
        if (string.IsNullOrWhiteSpace(title)) return "名称不能为空";
        if (end is { } e)
        {
            if (e < start) return "结束时间不能早于开始时间";
            if (e - start > TimeSpan.FromHours(24)) return "开始与结束时间相隔不能超过 24 小时";
        }

        return null;
    }

    /// <summary>提醒时间点；未设置提醒时返回 null。</summary>
    public DateTime? ReminderAt => ReminderMinutes is { } m ? ActualStart?.AddMinutes(-m) ?? Start.AddMinutes(-m) : null;
}

/// <summary>
/// 日程管理器：负责把带重复规则的日程展开为时间线上的一次次发生。
/// </summary>
public sealed class ScheduleTimeline
{
    private readonly IReadOnlyList<ScheduleItem> _items;

    public ScheduleTimeline(IReadOnlyList<ScheduleItem> items) => _items = items;

    /// <summary>
    /// 取距离 <paramref name="now"/> 最近的、尚未结束的日程（即首页"最近日程"卡片）。
    /// </summary>
    public ScheduleOccurrence? NearestUpcoming(DateTime now, TimeSpan? horizon = null)
    {
        var span = horizon ?? TimeSpan.FromDays(365);
        var from = now.AddHours(-1);

        return Occurrences(from, now.Add(span))
            .Where(o => o.End is null || o.End > now)
            .OrderBy(o => o.Start)
            .FirstOrDefault();
    }

    /// <summary>展开区间内的所有发生。</summary>
    public IReadOnlyList<ScheduleOccurrence> Occurrences(DateTime from, DateTime to)
    {
        var result = new List<ScheduleOccurrence>();
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        foreach (var item in _items)
        {
            if (item.IsOccurrenceOverride)
            {
                if (item.ActualStart is { } s && s >= from && s <= to)
                    result.Add(new ScheduleOccurrence(item, s, item.ActualEnd));
                continue;
            }

            foreach (var date in item.Recurrence.Expand(DateOnly.FromDateTime(item.Start), toDate))
            {
                var start = date.ToDateTime(TimeOnly.MinValue).Add(item.Start.TimeOfDay);
                if (start < from || start > to) continue;

                DateTime? end = item.End is { } e
                    ? date.ToDateTime(TimeOnly.MinValue).Add(e.TimeOfDay)
                    : null;

                // 跨日结束的日程：结束时刻早于开始时刻时按"次日"处理。
                if (end is not null && end < start) end = start + (item.End!.Value - item.Start);

                result.Add(new ScheduleOccurrence(item, start, end));
            }
        }

        return result.OrderBy(o => o.Start).ToList();
    }
}

/// <summary>日程的一次具体发生。</summary>
public sealed record ScheduleOccurrence(ScheduleItem Item, DateTime Start, DateTime? End)
{
    public string Title => Item.Title;
    public string Location => Item.Location;
    public TimeSpan? CountdownTo(DateTime now) => Start - now;
}
