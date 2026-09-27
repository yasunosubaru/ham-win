namespace Ham.Infrastructure.Rating.Models;

/// <summary>
/// 课程给分统计项。
/// </summary>
/// <remarks>
/// 对应开放平台 <c>CourseScoreItem</c>：一门课 / 一位授课人的给分分布聚合。
/// </remarks>
public sealed record CourseRating
{
    public string Id { get; init; } = string.Empty;
    public required string CourseName { get; init; }
    public string Instructor { get; init; } = string.Empty;

    /// <summary>平均分。</summary>
    public double Average { get; init; }

    /// <summary>参与统计的人数。</summary>
    public int Total { get; init; }

    /// <summary>分数段分布，含展示色值。</summary>
    public IReadOnlyList<RatingRange> Ranges { get; init; } = [];

    /// <summary>预先格式化的一行文本，避免在 XAML 里拼装（也更易做本地化与单测）。</summary>
    public string AverageText => $"{Average:F1}";

    public string TotalText => $"{Total} 人评价";

    public string SummaryText => string.IsNullOrWhiteSpace(Instructor)
        ? $"{CourseName} · 平均 {AverageText} · {TotalText}"
        : $"{CourseName} · {Instructor} · 平均 {AverageText} · {TotalText}";
}

/// <summary>一个分数段。</summary>
public sealed record RatingRange
{
    public required int From { get; init; }
    public required int To { get; init; }
    public required int Total { get; init; }
    public string Color { get; init; } = "#A5B9F3";

    public int Width => To - From + 1;

    public string Label => From == To ? $"{From}" : $"{From}-{To}";
}

/// <summary>搜索命中项（对应 <c>SearchCourseHitItem</c>）。</summary>
public sealed record SearchHit
{
    /// <summary>命中类型：1=课程名，2=授课人。</summary>
    public int Type { get; init; }

    public required string Value { get; init; }
    public string Highlighted { get; init; } = string.Empty;

    public string TypeName => Type switch
    {
        1 => "课程",
        2 => "教师",
        _ => "未知",
    };
}

/// <summary>我发布的课程评价。</summary>
public sealed record CourseReview
{
    public required string CourseName { get; init; }
    public string Instructor { get; init; } = string.Empty;
    public int Rating { get; init; }
    public string Comment { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Anonymous { get; init; } = true;
}

/// <summary>"想上"标记。</summary>
public sealed record CourseWish
{
    public required string CourseId { get; init; }
    public required string CourseName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
