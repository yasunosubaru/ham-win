using Ham.Core.Models;
using Xunit;

namespace Ham.Tests;

public class ScoreCalculatorTests
{
    private static ScoreRecord S(string name, double credit, double score,
        string type = "专业必修", string college = "计算机学院")
        => new() { Name = name, CourseId = name, Credit = credit, Score = score, CourseType = type, CourseCollege = college };

    [Fact]
    public void WeightedAverageIsCreditWeighted()
    {
        var records = new[] { S("A", 4, 92), S("B", 2, 76) };
        // (4*92 + 2*76) / 6 = 520/6
        Assert.Equal(520.0 / 6.0, ScoreCalculator.WeightedAverage(records), 6);
    }

    [Fact]
    public void GpaUsesScaleBands()
    {
        var records = new[] { S("A", 3, 95), S("B", 3, 85) };
        // 标准 4.0 九档: 95→4.0, 85→3.7 → (3*4.0 + 3*3.7)/6 = 23.1/6 = 3.85
        // （旧的四档表给 85→3.0，算出 3.5；那是错的，见 StandardScaleBands 的说明）
        Assert.Equal(3.85, ScoreCalculator.Gpa(records, GpaScale.Standard4_0), 6);

        // 同一份成绩在四档口径下确实才是 3.5——两张表并存，所以这里钉住差异
        Assert.Equal(3.5, ScoreCalculator.Gpa(records, GpaScale.Coarse4Tier), 6);
    }

    [Fact]
    public void DisabledRecordsAreExcluded()
    {
        var records = new[]
        {
            S("A", 3, 95),
            S("B", 3, 60) with { IsEnabled = false },
        };
        Assert.Equal(95.0, ScoreCalculator.WeightedAverage(records), 6);
        Assert.Equal(1, records.Count(r => r.IsEnabled));
    }

    [Fact]
    public void FailingCourseCountsTowardCreditButZeroesPoints()
    {
        var records = new[] { S("A", 3, 95), S("Fail", 2, 45) };
        // 绩点：3*4.0 + 2*0 = 12 / 总学分 5
        Assert.Equal(12.0 / 5.0, ScoreCalculator.Gpa(records, GpaScale.Standard4_0), 6);
        // 及格学分只算 3
        Assert.Equal(3.0, ScoreCalculator.EarnedCredit(records), 6);
    }

    [Fact]
    public void EmptyInputYieldsZeroNotNaN()
    {
        Assert.Equal(0.0, ScoreCalculator.WeightedAverage([]));
        Assert.Equal(0.0, ScoreCalculator.Gpa([], GpaScale.Standard4_0));
    }

    [Fact]
    public void BySemesterGroupsAndOrders()
    {
        var records = new[]
        {
            S("A", 3, 90) with { Year = 2025, SemesterNumber = 2 },
            S("B", 3, 80) with { Year = 2025, SemesterNumber = 1 },
            S("C", 2, 70) with { Year = 2026, SemesterNumber = 1 },
        };
        var result = ScoreCalculator.BySemester(records, GpaScale.Standard4_0);
        Assert.Equal(3, result.Count);
        Assert.Equal(new Semester(2025, 1), result[0].Semester);
        Assert.Equal(new Semester(2026, 1), result[2].Semester);
    }

    [Fact]
    public void DistributionBucketsByTenPoints()
    {
        var records = new[] { S("A", 1, 95), S("B", 1, 92), S("C", 1, 65) };
        var buckets = ScoreCalculator.Distribution(records);
        Assert.Equal(2, buckets[9].Count);   // 90-99
        Assert.Equal(1, buckets[6].Count);   // 60-69
    }

    /// <summary>
    /// 默认口径是通行的<b>九档</b> 4.0 制。
    /// </summary>
    /// <remarks>
    /// 这组用例以前断言的是 90/80/70/60 <b>四档</b>（89→3.0、79→2.0、69→1.0），
    /// 而那个四档表当时被命名为「标准 4.0 制」并设为默认。
    /// 名字、文档与实现三者对不上，导致同一份 27 门真实成绩在两个 UI 上
    /// 分别显示 3.222 与 3.68。现已改正，期望值随之更新。
    /// 四档口径的行为改由 <see cref="CoarseScaleBands"/> 单独钉住。
    /// </remarks>
    [Theory]
    [InlineData(95, 4.0)]
    [InlineData(90, 4.0)]
    [InlineData(89, 3.7)]
    [InlineData(85, 3.7)]
    [InlineData(84, 3.3)]
    [InlineData(82, 3.3)]
    [InlineData(80, 3.0)]
    [InlineData(78, 3.0)]
    [InlineData(76, 2.7)]
    [InlineData(75, 2.7)]
    [InlineData(73, 2.3)]
    [InlineData(72, 2.3)]
    [InlineData(70, 2.0)]
    [InlineData(68, 2.0)]
    [InlineData(66, 1.5)]
    [InlineData(64, 1.5)]
    [InlineData(62, 1.0)]
    [InlineData(60, 1.0)]
    [InlineData(59, 0.0)]
    public void StandardScaleBands(double score, double expected)
        => Assert.Equal(expected, GpaScale.Standard4_0.ToGradePoint(score), 6);

    /// <summary>四档粗放口径：仍可选，但不再是默认，且名字已与标准表区分开。</summary>
    [Theory]
    [InlineData(95, 4.0)]
    [InlineData(90, 4.0)]
    [InlineData(89, 3.0)]
    [InlineData(80, 3.0)]
    [InlineData(79, 2.0)]
    [InlineData(70, 2.0)]
    [InlineData(69, 1.0)]
    [InlineData(60, 1.0)]
    [InlineData(59, 0.0)]
    public void CoarseScaleBands(double score, double expected)
        => Assert.Equal(expected, GpaScale.Coarse4Tier.ToGradePoint(score), 6);

    /// <summary>
    /// 口径解析必须单一来源：名字对不上就回落默认，且历史英文值要显式兼容。
    /// </summary>
    /// <remarks>
    /// 早期 <c>AppSettings.GpaScaleName</c> 的默认值写成英文 <c>"Standard 4.0"</c>，
    /// 与预设名「标准 4.0 制」永远匹配不上，只是碰巧回落到同一张表才没暴露。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Standard 4.0")]          // 历史遗留值
    [InlineData("根本不存在的口径")]
    public void ResolveFallsBackToStandardScale(string? name)
        => Assert.Same(GpaScale.Standard4_0, GpaScale.Resolve(name));

    [Fact]
    public void ResolveHonoursAPresetName()
        => Assert.Same(GpaScale.Whu4_0, GpaScale.Resolve("武汉大学 4.0 制"));

    [Fact]
    public void DefaultScaleNameActuallyMatchesItsPreset()
    {
        // 这个断言是为了让"默认值必须是某个真实预设的名字"变成编译期之外也守得住的约束
        var settings = new Ham.Infrastructure.Storage.AppSettings();
        Assert.Same(GpaScale.Standard4_0, GpaScale.Resolve(settings.GpaScaleName));
    }

    [Fact]
    public void LinearFormulaScale()
    {
        // (95-60)/10 = 3.5
        Assert.Equal(3.5, GpaScale.Linear4_0.ToGradePoint(95), 6);
        Assert.Equal(0.0, GpaScale.Linear4_0.ToGradePoint(55), 6);
    }

    [Fact]
    public void RankCountsHowManyAveragesAreHigher()
    {
        var mine = new[] { S("A", 3, 80) };
        var cohort = new List<double> { 90, 85, 79, 75, 70 };
        var (rank, average) = ScoreCalculator.EstimateRank(mine, cohort);
        Assert.Equal(3, rank);          // 只有 90 和 85 高于 80
        Assert.Equal(80.0, average, 6);
    }
}

public class ComprehensiveScoreCalculatorTests
{
    private const string Me = "计算机学院";
    private const string Other = "数学与统计学院";

    private static ScoreRecord S(string name, double credit, double score,
        string type, string college) => new()
    {
        Name = name,
        CourseId = name,
        Credit = credit,
        Score = score,
        CourseType = type,
        CourseCollege = college,
    };

    [Fact]
    public void NewF2TakesTopEightB2Courses()
    {
        var records = new List<ScoreRecord>
        {
            S("MATH", 5, 90, "公共基础必修", Me),
        };

        // 12 门跨学院专业选修，分数 70..81
        for (var i = 0; i < 12; i++)
        {
            records.Add(S($"E{i}", 2, 70 + i, "专业选修", Other));
        }

        var result = ComprehensiveScoreCalculator.Calculate(
            records, Me, ComprehensiveScoreMethod.NewF2);

        // B2 选 8 门 → 分数最高的 8 门 = 74..81，均值 77.5
        // F2 = 90 + 77.5*0.002 = 90.155
        Assert.Equal(90 + 77.5 * 0.002, result.Value, 4);
        Assert.Equal(9, result.CourseCount);   // 1 门 B1 + 8 门 B2
    }

    [Fact]
    public void LegacyF2UsesWeightedBlend()
    {
        var records = new[]
        {
            S("B1A", 4, 90, "公共基础必修", Me),
            S("B1B", 4, 80, "公共基础必修", Me),
            S("B2A", 2, 100, "专业选修", Other),
            S("B2B", 2, 60, "专业选修", Other),
        };

        // B1 均值 = (4*90+4*80)/8 = 85 ; B2 均值 = (2*100+2*60)/4 = 80
        // F2 = 85*0.98 + 80*0.02 = 84.9
        var result = ComprehensiveScoreCalculator.Calculate(
            records, Me, ComprehensiveScoreMethod.LegacyF2);

        Assert.Equal(84.9, result.Value, 4);
    }

    [Fact]
    public void LegacyF2DoesNotCapB2AtEight()
    {
        var records = new List<ScoreRecord> { S("B1", 4, 90, "公共基础必修", Me) };
        for (var i = 0; i < 10; i++) records.Add(S($"E{i}", 1, 100, "专业选修", Other));

        var result = ComprehensiveScoreCalculator.Calculate(
            records, Me, ComprehensiveScoreMethod.LegacyF2);

        // 旧版：B1 均值 90，B2 均值 100 → 90*0.98 + 100*0.02 = 90.2
        Assert.Equal(11, result.CourseCount);
        Assert.Equal(90.2, result.Value, 4);
    }

    [Fact]
    public void ExplicitB2SelectionOverridesAutoPick()
    {
        var records = new[]
        {
            S("B1", 4, 90, "公共基础必修", Me),
            S("E1", 2, 95, "专业选修", Other),
            S("E2", 2, 60, "专业选修", Other),
        };

        var explicitIds = new HashSet<string> { "E2" };
        var result = ComprehensiveScoreCalculator.Calculate(
            records, Me, ComprehensiveScoreMethod.NewF2, explicitIds);

        // 只选 E2(60 分) → F2 = 90 + 60*0.002
        Assert.Equal(90 + 60 * 0.002, result.Value, 4);
    }

    [Fact]
    public void CoursesOutsideB1AndB2AreIgnored()
    {
        var records = new[]
        {
            S("B1", 4, 90, "公共基础必修", Me),
            S("Same", 3, 99, "专业选修", Me),          // 本学院选修 → 既非 B1 也非 B2
        };

        var result = ComprehensiveScoreCalculator.Calculate(
            records, Me, ComprehensiveScoreMethod.NewF2);

        Assert.Equal(90.0, result.Value, 4);
        Assert.Equal(1, result.CourseCount);
    }

    [Fact]
    public void DefaultMethodMatchesDocumentedColleges()
    {
        Assert.Equal(ComprehensiveScoreMethod.NewF2,
            ComprehensiveScoreCalculator.DefaultMethodForCollege("计算机学院"));
        Assert.Equal(ComprehensiveScoreMethod.LegacyF2,
            ComprehensiveScoreCalculator.DefaultMethodForCollege("资源与环境科学学院"));
    }

    [Fact]
    public void NoRecordsYieldsZero()
    {
        var result = ComprehensiveScoreCalculator.Calculate(
            [], Me, ComprehensiveScoreMethod.NewF2);
        Assert.Equal(0.0, result.Value, 4);
        Assert.Equal(0, result.CourseCount);
    }
}
