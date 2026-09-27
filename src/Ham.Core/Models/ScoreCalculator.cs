namespace Ham.Core.Models;

/// <summary>一次统计计算的汇总结果。</summary>
/// <param name="Value">主结果值（GPA 或综测 F2）。</param>
/// <param name="TotalCredit">参与计算的总学分。</param>
/// <param name="CourseCount">参与计算的课程门数。</param>
/// <param name="SelectedCourseIds">参与计算的课程唯一键，界面据此高亮。</param>
public sealed record ScoreSummary(
    double Value,
    double TotalCredit,
    int CourseCount,
    IReadOnlyList<string> SelectedCourseIds);

/// <summary>按学期分组的统计结果。</summary>
public sealed record SemesterScoreSummary(
    Semester Semester,
    double Gpa,
    double WeightedAverage,
    double TotalCredit,
    int CourseCount)
{
    public string SemesterText => $"{Semester.Year} 学年第 {Semester.SemesterNumber} 学期";

    public string GpaText => $"GPA {Gpa:F2}";

    public string AverageText => $"均分 {WeightedAverage:F1}";
}

/// <summary>
/// 成绩统计：学分、加权平均分、GPA。
/// </summary>
public static class ScoreCalculator
{
    /// <summary>计算学分加权的平均分（百分制）。</summary>
    public static double WeightedAverage(IEnumerable<ScoreRecord> records)
    {
        double weighted = 0, credit = 0;
        foreach (var r in records)
        {
            if (!r.IsEnabled) continue;
            weighted += r.Credit * r.Score;
            credit += r.Credit;
        }

        return credit > 0 ? weighted / credit : 0.0;
    }

    /// <summary>计算平均学分绩点。</summary>
    public static double Gpa(IEnumerable<ScoreRecord> records, GpaScale scale)
    {
        double weightedPoints = 0, credit = 0;
        foreach (var r in records)
        {
            if (!r.IsEnabled) continue;
            weightedPoints += r.Credit * scale.ToGradePoint(r.Score);
            credit += r.Credit;
        }

        return credit > 0 ? weightedPoints / credit : 0.0;
    }

    /// <summary>累计获得学分（只累加及格以上的课程）。</summary>
    public static double EarnedCredit(IEnumerable<ScoreRecord> records, double passScore = 60.0)
        => records.Where(r => r.IsEnabled && r.Score >= passScore).Sum(r => r.Credit);

    /// <summary>按学期升序统计各学期的 GPA 与加权平均分。</summary>
    public static IReadOnlyList<SemesterScoreSummary> BySemester(IEnumerable<ScoreRecord> records, GpaScale scale)
        => records
            .GroupBy(r => r.Semester)
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.SemesterNumber)
            .Select(g => new SemesterScoreSummary(
                g.Key,
                Gpa(g, scale),
                WeightedAverage(g),
                g.Where(r => r.IsEnabled).Sum(r => r.Credit),
                g.Count(r => r.IsEnabled)))
            .ToList();

    /// <summary>成绩分数分布直方图（按 10 分一档）。</summary>
    public static IReadOnlyList<ScoreBucket> Distribution(IEnumerable<ScoreRecord> records)
    {
        var buckets = new ScoreBucket[11];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new ScoreBucket(i * 10, Math.Min(i * 10 + 9, 100), 0);
        }

        foreach (var r in records)
        {
            if (!r.IsEnabled) continue;
            var index = Math.Clamp((int)Math.Floor(r.Score / 10), 0, 10);
            buckets[index] = buckets[index] with { Count = buckets[index].Count + 1 };
        }

        return buckets;
    }

    /// <summary>专业/班级排名估算：按加权平均分从高到低排序后取名次。</summary>
    public static (int Rank, double Average) EstimateRank(
        IReadOnlyList<ScoreRecord> mine,
        IReadOnlyList<double> cohortAverages)
    {
        var average = WeightedAverage(mine);
        var rank = 1 + cohortAverages.Count(a => a > average);
        return (rank, average);
    }
}

/// <summary>分数分布的一个档位。</summary>
public readonly record struct ScoreBucket(int From, int To, int Count);

/// <summary>综测（F2）计算方式。</summary>
public enum ComprehensiveScoreMethod
{
    /// <summary>新版：F2 = B1 + B2 × 0.002，B2 最多 8 门，取最高。</summary>
    NewF2,

    /// <summary>旧版：F2 = B1 × 0.98 + B2 × 0.02。</summary>
    LegacyF2,

    /// <summary>使用自定义 JavaScript 脚本计算。</summary>
    Script,
}

/// <summary>
/// 综测成绩（F2）计算器。
/// </summary>
/// <remarks>
/// 移植自 ham 官方文档 <c>guide/handbook/score</c>：
/// <list type="bullet">
/// <item>B1 = 必修、通识课程；B2 = 选修、跨专业课程。</item>
/// <item>新版 <c>F2 = B1 + B2 × 0.002</c>，B2 最多取 8 门，按 B2 高者优先。</item>
/// <item>旧版 <c>F2 = B1 × 0.98 + B2 × 0.02</c>。</item>
/// </list>
/// </remarks>
public static class ComprehensiveScoreCalculator
{
    /// <summary>新版 F2 中 B2 最多选取的课程数。</summary>
    public const int MaxB2CourseCount = 8;

    public static ComprehensiveScoreMethod DefaultMethodForCollege(string? college) => college switch
    {
        "计算机学院" => ComprehensiveScoreMethod.NewF2,
        "资源与环境科学学院" => ComprehensiveScoreMethod.LegacyF2,
        _ => ComprehensiveScoreMethod.NewF2,
    };

    /// <summary>按指定方式计算综测成绩。</summary>
    /// <param name="records">全部成绩。</param>
    /// <param name="userCollege">用户所在学院，决定 B1/B2 归类。</param>
    /// <param name="method">计算方式。</param>
    /// <param name="b2CourseIds">
    /// 用户手动指定的 B2 课程键集合。为 null 时按方式自动选取（新版取最高 8 门）。
    /// </param>
    public static ScoreSummary Calculate(
        IReadOnlyList<ScoreRecord> records,
        string? userCollege,
        ComprehensiveScoreMethod method,
        IReadOnlySet<string>? b2CourseIds = null)
    {
        var active = records.Where(r => r.IsEnabled).ToList();

        var b1 = active.Where(r =>
            CourseTypeClassifier.IsPrimaryCourse(r.CourseType, userCollege, r.CourseCollege)).ToList();

        var b2Pool = active.Where(r =>
            CourseTypeClassifier.IsOtherCollegeMajorCourse(r.CourseType, userCollege, r.CourseCollege)).ToList();

        var b2 = SelectB2(b2Pool, b2CourseIds, method);

        var b1Average = ScoreCalculator.WeightedAverage(b1);
        var b2Average = ScoreCalculator.WeightedAverage(b2);

        var value = method switch
        {
            ComprehensiveScoreMethod.LegacyF2 => b1Average * 0.98 + b2Average * 0.02,
            _ => b1Average + b2Average * 0.002,
        };

        var selected = b1.Concat(b2)
            .Select(CourseKey)
            .ToList();

        return new ScoreSummary(
            Math.Round(value, 4),
            b1.Sum(r => r.Credit) + b2.Sum(r => r.Credit),
            b1.Count + b2.Count,
            selected);
    }

    /// <summary>计算 B2 部分的平均值（界面展示用）。</summary>
    public static double B2Average(
        IReadOnlyList<ScoreRecord> records,
        string? userCollege,
        ComprehensiveScoreMethod method,
        IReadOnlySet<string>? b2CourseIds = null)
    {
        var b2Pool = records
            .Where(r => r.IsEnabled)
            .Where(r => CourseTypeClassifier.IsOtherCollegeMajorCourse(r.CourseType, userCollege, r.CourseCollege))
            .ToList();

        return ScoreCalculator.WeightedAverage(SelectB2(b2Pool, b2CourseIds, method));
    }

    private static List<ScoreRecord> SelectB2(
        List<ScoreRecord> pool,
        IReadOnlySet<string>? explicitIds,
        ComprehensiveScoreMethod method)
    {
        if (explicitIds is not null)
        {
            return pool.Where(r => explicitIds.Contains(CourseKey(r))).ToList();
        }

        if (method == ComprehensiveScoreMethod.NewF2)
        {
            // 高 B2 优先：先按成绩降序，再按学分降序，保证选取稳定。
            return pool
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Credit)
                .ThenBy(r => CourseKey(r), StringComparer.Ordinal)
                .Take(MaxB2CourseCount)
                .ToList();
        }

        return pool;
    }

    /// <summary>课程唯一键。课头号可能为空时退化用 "名称@学期"。</summary>
    public static string CourseKey(ScoreRecord record)
        => string.IsNullOrWhiteSpace(record.CourseId)
            ? $"{record.Name}@{record.Year}-{record.SemesterNumber}"
            : record.CourseId;
}
