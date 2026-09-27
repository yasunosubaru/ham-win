namespace Ham.Core.Models;

/// <summary>
/// 学期教学日历：把"第几周第几天"和真实日期互相换算。
/// </summary>
/// <remarks>
/// 教务系统不提供学期起止信息（ham-rn 中该值由宿主原生侧硬编码），
/// 因此本应用要求用户设置开学日期与周数，并据此推导。
/// 第 1 周的第 1 天为开学日期当天所在周的周日（对齐 <see cref="DayOfWeek"/> 约定）。
/// </remarks>
public sealed class SemesterCalendar
{
    /// <summary>武汉大学常见学期周数。</summary>
    public const int DefaultTotalWeeks = 20;

    private readonly DateOnly _firstDayOfWeek1;
    private readonly int _totalWeeks;

    public SemesterCalendar(DateOnly semesterStartDate, Semester semester, int totalWeeks = DefaultTotalWeeks)
    {
        if (semesterStartDate.DayOfWeek != DayOfWeek.Sunday)
        {
            // 归一到该周周日，使第 1 周与 ISO/教务的"周日为首"一致。
            semesterStartDate = semesterStartDate.AddDays(-(int)semesterStartDate.DayOfWeek);
        }

        _firstDayOfWeek1 = semesterStartDate;
        _totalWeeks = Math.Max(1, totalWeeks);
        Semester = semester;
    }

    public Semester Semester { get; }

    public DateOnly FirstDayOfWeek1 => _firstDayOfWeek1;

    public int TotalWeeks => _totalWeeks;

    /// <summary>学期最后一天（含）。</summary>
    public DateOnly LastDay => _firstDayOfWeek1.AddDays(_totalWeeks * 7 - 1);

    /// <summary>取"第 week 周的星期 weekday"对应的日期。超出学期范围返回 null。</summary>
    public DateOnly? DateOf(int week, int weekday)
    {
        if (week < 1 || week > _totalWeeks) return null;
        if (weekday is < 0 or > 6) return null;
        return _firstDayOfWeek1.AddDays((week - 1) * 7 + weekday);
    }

    /// <summary>取指定节次对应的上课时刻。节次按每天 7 节、每节 45 分钟、8:00 起算。</summary>
    public static (DateTime Start, DateTime End) ClassPeriodTimes(DateOnly date, int classFrom, int classTo)
    {
        const int firstClassStartHour = 8;
        const int minutesPerPeriod = 45;
        const int periodsPerDay = 7;

        var startIndex = Math.Max(1, classFrom) - 1;
        var endIndex = Math.Clamp(classTo, 1, periodsPerDay) - 1;

        var start = date.ToDateTime(new TimeOnly(firstClassStartHour, 0)).AddMinutes(startIndex * minutesPerPeriod);
        var end = date.ToDateTime(new TimeOnly(firstClassStartHour, 0)).AddMinutes((endIndex + 1) * minutesPerPeriod);
        return (start, end);
    }

    /// <summary>把日期换算为教学周次；不在学期范围内返回 null。</summary>
    public int? WeekOf(DateOnly date)
    {
        if (date < _firstDayOfWeek1 || date > LastDay) return null;
        return ((date.DayNumber - _firstDayOfWeek1.DayNumber) / 7) + 1;
    }

    /// <summary>当前教学周次；不在学期内返回 null。</summary>
    public int? CurrentWeek(DateTime now) => WeekOf(DateOnly.FromDateTime(now));

    /// <summary>判断今天是否属于该学期的教学周。</summary>
    public bool IsWithinSemester(DateTime now)
    {
        var date = DateOnly.FromDateTime(now);
        return date >= _firstDayOfWeek1 && date <= LastDay;
    }

    /// <summary>生成课表网格：<c>[week][weekday]</c> 索引到当天的课程格。</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<CourseSlot>>> BuildGrid(
        IReadOnlyList<Course> courses,
        int? weeks = null)
    {
        var totalWeeks = Math.Min(weeks ?? _totalWeeks, _totalWeeks);
        var grid = new List<IReadOnlyList<IReadOnlyList<CourseSlot>>>(totalWeeks);

        for (var w = 1; w <= totalWeeks; w++)
        {
            var byDay = new List<IReadOnlyList<CourseSlot>>(7);
            for (var d = 0; d < 7; d++)
            {
                var daySlots = new List<CourseSlot>();
                foreach (var course in courses)
                {
                    if (!course.OccursOn(w, d)) continue;

                    // 一门课在本日只登记一格；节次范围由 ClassFrom/ClassTo 表达。
                    daySlots.Add(new CourseSlot
                    {
                        Week = w,
                        Weekday = d,
                        ClassFrom = course.ClassFrom,
                        ClassTo = course.ClassTo,
                        Color = course.Color,
                    });
                }

                daySlots.Sort((a, b) => a.ClassFrom.CompareTo(b.ClassFrom));
                byDay.Add(daySlots);
            }

            grid.Add(byDay);
        }

        return grid;
    }
}

/// <summary>课程时间冲突。</summary>
/// <param name="Week">发生冲突的周次。</param>
/// <param name="Weekday">星期几。</param>
/// <param name="First">先登记的课程。</param>
/// <param name="Second">与之冲突的课程。</param>
public sealed record CourseConflict(int Week, int Weekday, Course First, Course Second)
{
    public string Describe() =>
        $"第 {Week} 周 星期{ConflictDetector.DayOfWeekLabel(Weekday)} " +
        $"{First.Name}({First.ClassFrom}-{First.ClassTo}节) 与 " +
        $"{Second.Name}({Second.ClassFrom}-{Second.ClassTo}节) 时间冲突";
}

/// <summary>课程冲突检测器。</summary>
public static class ConflictDetector
{
    /// <summary>找出所有课表冲突。</summary>
    public static IReadOnlyList<CourseConflict> Detect(IReadOnlyList<Course> courses, int maxWeeks = 30)
    {
        var conflicts = new List<CourseConflict>();

        for (var w = 1; w <= maxWeeks; w++)
        {
            for (var d = 0; d < 7; d++)
            {
                var onDay = courses.Where(c => c.OccursOn(w, d)).ToList();
                for (var i = 0; i < onDay.Count; i++)
                {
                    for (var j = i + 1; j < onDay.Count; j++)
                    {
                        var a = onDay[i];
                        var b = onDay[j];
                        if (a.ClassFrom <= b.ClassTo && b.ClassFrom <= a.ClassTo)
                        {
                            conflicts.Add(new CourseConflict(w, d, a, b));
                        }
                    }
                }
            }
        }

        return conflicts;
    }

    /// <summary>星期中文标签。</summary>
    public static string DayOfWeekLabel(int weekday) => weekday switch
    {
        0 => "日",
        1 => "一",
        2 => "二",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        _ => "?",
    };

    /// <summary>星期完整标签。</summary>
    public static string DayOfWeekName(int weekday) => weekday switch
    {
        0 => "周日",
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        _ => "未知",
    };
}
