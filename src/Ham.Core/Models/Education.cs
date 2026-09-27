namespace Ham.Core.Models;

/// <summary>
/// 学期标识。教务系统内部学期码与业务学期码的映射关系见 <see cref="SemesterCode"/>。
/// </summary>
public sealed record Semester(int Year, int SemesterNumber)
{
    public override string ToString() => $"{Year}-{SemesterNumber}";

    /// <summary>用于界面显示的简短名称，例如 "2026 秋季"。</summary>
    public string DisplayName => $"{Year} 学年第 {SemesterNumber} 学期";
}

/// <summary>
/// 教务系统（正方教务）内部学期码映射。
/// </summary>
/// <remarks>
/// ham-rn 源码实测：内部 <c>1→3</c>、<c>2→12</c>、<c>3→16</c>（请求方向），
/// 响应方向 <c>3→1</c>、<c>12→2</c>、<c>16→3</c>，其余一律回落到 1。
/// </remarks>
public static class SemesterCode
{
    private static readonly IReadOnlyDictionary<int, int> BusinessToInternal =
        new Dictionary<int, int> { [1] = 3, [2] = 12, [3] = 16 };

    private static readonly IReadOnlyDictionary<int, int> InternalToBusiness =
        new Dictionary<int, int> { [3] = 1, [12] = 2, [16] = 3 };

    /// <summary>业务学期号（1/2/3）转教务内部学期码；其余返回 null。</summary>
    public static int? ToInternal(int semesterNumber)
        => BusinessToInternal.TryGetValue(semesterNumber, out var code) ? code : null;

    /// <summary>教务内部学期码转业务学期号；无法识别时按 ham-rn 行为回落到 1。</summary>
    public static int FromInternal(int internalCode)
        => InternalToBusiness.TryGetValue(internalCode, out var value) ? value : 1;
}

/// <summary>
/// 课程实体。对应教务 <c>kbcx/xskbcx_cxXsgrkb.html</c> 响应 <c>kbList</c> 中的单条记录。
/// </summary>
public sealed record Course
{
    /// <summary>课程名称（源字段 <c>kcmc</c>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>课头号（源字段 <c>jxbmc</c>），同时是配色与"同一门课"判定的依据。</summary>
    public string CourseId { get; init; } = string.Empty;

    /// <summary>教师姓名（源字段 <c>xm</c>）。</summary>
    public string Instructor { get; init; } = string.Empty;

    /// <summary>教师类型（源字段 <c>zcmc</c>）。</summary>
    public string InstructorType { get; init; } = string.Empty;

    /// <summary>连续周次起点；周次不连续时为 -1。</summary>
    public int WeekFrom { get; init; } = -1;

    /// <summary>连续周次终点；周次不连续时为 -1。</summary>
    public int WeekTo { get; init; } = -1;

    /// <summary>起始节（源字段 <c>jcs</c> 的 "5-6" 取首）。</summary>
    public int ClassFrom { get; init; } = -1;

    /// <summary>结束节。</summary>
    public int ClassTo { get; init; } = -1;

    /// <summary>
    /// 星期几，0=周日 … 6=周六（与 <see cref="System.DayOfWeek"/> 一致）。
    /// 教务的 <c>xqj</c> 约定 7=周日，复刻时已归一化到 0。
    /// </summary>
    public int Weekday { get; init; }

    /// <summary>课程性质（源字段 <c>kcxz</c>）。</summary>
    public string CourseType { get; init; } = string.Empty;

    /// <summary>学分（源字段 <c>xf</c>）。</summary>
    public double Credit { get; init; }

    /// <summary>上课地点（源字段 <c>cdmc</c>）。</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>由课头号确定性推导的显示颜色，形如 <c>#RRGGBB</c>。</summary>
    public string Color { get; init; } = CourseColorAssigner.Palette[0];

    /// <summary>学年（仅导入时已知，手工添加的课程为 null）。</summary>
    public int? Year { get; init; }

    /// <summary>学期号（仅导入时已知）。</summary>
    public int? SemesterNumber { get; init; }

    /// <summary>周次原始文本，仅在解析阶段保留，跨边界前会被清除。</summary>
    public string? RawWeekText { get; init; }

    /// <summary>本课程实际占用的周次集合。</summary>
    public IReadOnlyList<int> Weeks { get; init; } = [];

    /// <summary>
    /// 判断指定教学周 / 星期是否需要上课（忽略具体节次）。
    /// </summary>
    /// <remarks>
    /// 冲突检测与课表网格只关心"这一格有没有课"，因此不能走 <see cref="OccursIn"/>：
    /// 那里会对节次做范围校验，传入 0 之类的哨兵值会恒定判定为不匹配。
    /// </remarks>
    public bool OccursOn(int week, int weekday)
    {
        if (Weekday != weekday) return false;
        if (Weeks.Count > 0) return Weeks.Contains(week);
        return week >= WeekFrom && week <= WeekTo;
    }

    /// <summary>判断指定教学周 / 星期 / 节次是否需要上课。</summary>
    public bool OccursIn(int week, int weekday, int classPeriod)
    {
        if (!OccursOn(week, weekday)) return false;
        return classPeriod >= ClassFrom && classPeriod <= ClassTo;
    }

    /// <summary>返回去除解析期临时字段的副本。</summary>
    public Course WithoutParsingArtifacts() => this with { RawWeekText = null };
}

/// <summary>课表中的一格。</summary>
public sealed record CourseSlot
{
    public int Week { get; init; }
    public int Weekday { get; init; }
    public int ClassFrom { get; init; }
    public int ClassTo { get; init; }
    public string Color { get; init; } = string.Empty;

    public bool Overlaps(int weekday, int from, int to)
        => Weekday == weekday && from <= ClassTo && to >= ClassFrom;
}

/// <summary>成绩实体。对应教务 <c>cjcx/cjcx_cjXsgrcj.html</c> 响应 <c>items</c> 中的单条记录。</summary>
public sealed record ScoreRecord
{
    public int Year { get; init; }
    public int SemesterNumber { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>课头号（源字段 <c>jxbmc</c>）。</summary>
    public string CourseId { get; init; } = string.Empty;

    public string Instructor { get; init; } = string.Empty;
    public double Credit { get; init; }

    /// <summary>课程类别（源字段 <c>kcxzmc</c>），例如 "公共基础必修"。</summary>
    public string CourseType { get; init; } = string.Empty;

    /// <summary>百分制成绩（源字段 <c>bfzcj</c>）。非数值（如"缺考"）会在解析阶段被丢弃。</summary>
    public double Score { get; init; }

    /// <summary>开课学院（源字段 <c>kkbmmc</c>）。</summary>
    public string CourseCollege { get; init; } = string.Empty;

    /// <summary>是否参与 GPA / 综测计算。用户可在设置中逐门关闭。</summary>
    public bool IsEnabled { get; init; } = true;

    public Semester Semester => new(Year, SemesterNumber);
}
