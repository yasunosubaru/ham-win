namespace Ham.Core.Models;

/// <summary>
/// 百分制 → 绩点的换算规则。
/// </summary>
/// <remarks>
/// 各校换算表不同且会随培养方案调整，因此本应用不硬编码唯一口径：
/// 内置多套常见预设供选择，同时支持线性公式，用户也可在设置中自定义分段表。
/// </remarks>
public sealed record GpaScale
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>分段表：按 <see cref="MinScore"/> 降序匹配首个满足 score &gt;= MinScore 的区间。</summary>
    public required IReadOnlyList<GpaBand> Bands { get; init; }

    /// <summary>可选的线性公式模式。为 null 时使用 <see cref="Bands"/>。</summary>
    public GpaFormula? Formula { get; init; }

    public static GpaScale Whu4_0 { get; } = new()
    {
        Name = "武汉大学 4.0 制",
        Description = "细分九档，60 分及格线计 1.5 绩点。",
        Bands =
        [
            new GpaBand(90, 4.0), new GpaBand(85, 3.8), new GpaBand(82, 3.5),
            new GpaBand(78, 3.3), new GpaBand(75, 3.0), new GpaBand(72, 2.7),
            new GpaBand(68, 2.3), new GpaBand(64, 2.0), new GpaBand(60, 1.5),
            new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    public static GpaScale Peking4_0 { get; } = new()
    {
        Name = "北大 4.0 制",
        Description = "十档细分，60 分计 1.0 绩点。",
        Bands =
        [
            new GpaBand(90, 4.0), new GpaBand(85, 3.7), new GpaBand(82, 3.3),
            new GpaBand(78, 3.0), new GpaBand(75, 2.7), new GpaBand(72, 2.3),
            new GpaBand(69, 2.0), new GpaBand(66, 1.7), new GpaBand(63, 1.3),
            new GpaBand(60, 1.0), new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    public static GpaScale Standard4_0 { get; } = new()
    {
        Name = "标准 4.0 制",
        Description = "90/80/70/60 四档，最通用的口径。",
        Bands =
        [
            new GpaBand(90, 4.0), new GpaBand(80, 3.0), new GpaBand(70, 2.0),
            new GpaBand(60, 1.0), new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    public static GpaScale Cqu4_3 { get; } = new()
    {
        Name = "4.3 制",
        Description = "满分 4.3，95 分起 4.3。",
        Bands =
        [
            new GpaBand(95, 4.3), new GpaBand(90, 4.0), new GpaBand(85, 3.7),
            new GpaBand(82, 3.3), new GpaBand(78, 3.0), new GpaBand(75, 2.7),
            new GpaBand(72, 2.3), new GpaBand(68, 2.0), new GpaBand(64, 1.3),
            new GpaBand(60, 1.0), new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    public static GpaScale Linear4_0 { get; } = new()
    {
        Name = "线性公式 4.0 制",
        Description = "绩点 = (百分制 − 60) / 10，60 分及格线计 0 绩点。",
        Bands = [new GpaBand(double.NegativeInfinity, 0.0)],
        Formula = new GpaFormula(Offset: 60, Divisor: 10, PassScore: 60),
    };

    public static GpaScale Zju4_0 { get; } = new()
    {
        Name = "浙大 4.0 制",
        Description = "85 分以上满分，60-84 每 1 分 0.1 绩点。",
        Bands =
        [
            new GpaBand(85, 4.0),
            new GpaBand(80, 3.9), new GpaBand(75, 3.4), new GpaBand(70, 2.9),
            new GpaBand(65, 2.4), new GpaBand(60, 1.9),
            new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    public static IReadOnlyList<GpaScale> Presets { get; } =
        [Standard4_0, Whu4_0, Peking4_0, Cqu4_3, Zju4_0, Linear4_0];

    /// <summary>把百分制成绩换算为绩点。</summary>
    public double ToGradePoint(double score)
    {
        if (Formula is { } formula)
        {
            if (score < formula.PassScore) return 0.0;
            return (score - formula.Offset) / formula.Divisor;
        }

        foreach (var band in Bands)
        {
            if (score >= band.MinScore) return band.Points;
        }

        return 0.0;
    }

    /// <summary>从 JSON 文本还原自定义分段表；失败返回 null。</summary>
    public static GpaScale? TryParseCustom(string? name, string bandsJson)
    {
        if (string.IsNullOrWhiteSpace(bandsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bandsJson);
            var bands = new List<GpaBand>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var min = element.GetProperty("min").GetDouble();
                var points = element.GetProperty("points").GetDouble();
                bands.Add(new GpaBand(min, points));
            }

            if (bands.Count == 0) return null;
            bands.Sort((a, b) => b.MinScore.CompareTo(a.MinScore));
            return new GpaScale
            {
                Name = string.IsNullOrWhiteSpace(name) ? "自定义" : name,
                Description = "用户自定义分段表。",
                Bands = bands,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>一个绩点区间。</summary>
/// <param name="MinScore">分数下界（含）。</param>
/// <param name="Points">该区间对应的绩点。</param>
public readonly record struct GpaBand(double MinScore, double Points);

/// <summary>线性绩点公式参数。</summary>
/// <param name="Offset">减数。</param>
/// <param name="Divisor">除数。</param>
/// <param name="PassScore">及格线，低于此分数绩点为 0。</param>
public readonly record struct GpaFormula(double Offset, double Divisor, double PassScore);
