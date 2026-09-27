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

    /// <summary>
    /// 默认口径：国内通行的九档 4.0 制。
    /// </summary>
    /// <remarks>
    /// <b>这里曾经是一张错表，必须记住这件事。</b>
    /// 本预设曾被实现成 90/80/70/60 <b>四档</b>（4.0 / 3.0 / 2.0 / 1.0），
    /// 名字叫「标准 4.0 制」、README 也写「默认使用标准 4.0 制」，
    /// 但它根本不是标准表——同一份 27 门真实成绩，它算出 GPA 3.222，
    /// 而正确的九档表算出 3.555。名字、文档、实现三者对不上，
    /// 又因为它同时是默认值，错误一路传播到两个 UI 的界面上。
    /// <para>
    /// 现改为通行的九档分段。四档表没有删除，它以
    /// <see cref="Coarse4Tier"/> 的名义保留，供确实需要该口径时选用。
    /// </para>
    /// </remarks>
    public static GpaScale Standard4_0 { get; } = new()
    {
        Name = "标准 4.0 制",
        Description = "九档细分，60 分计 1.0 绩点，90 分及以上计 4.0。",
        Bands =
        [
            new GpaBand(90, 4.0), new GpaBand(85, 3.7), new GpaBand(82, 3.3),
            new GpaBand(78, 3.0), new GpaBand(75, 2.7), new GpaBand(72, 2.3),
            new GpaBand(68, 2.0), new GpaBand(64, 1.5), new GpaBand(60, 1.0),
            new GpaBand(double.NegativeInfinity, 0.0),
        ],
    };

    /// <summary>
    /// 粗放四档：90/80/70/60 → 4.0/3.0/2.0/1.0。
    /// </summary>
    /// <remarks>
    /// 这不是国内通行的标准表，只是早年常见的简化口径。
    /// 曾被误命名为「标准 4.0 制」并设为默认值，现已更名，
    /// 仅在用户明确选择时使用。
    /// </remarks>
    public static GpaScale Coarse4Tier { get; } = new()
    {
        Name = "四档 4.0 制（90/80/70/60）",
        Description = "四档粗放口径，非通行标准，仅供对照。",
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
        [Standard4_0, Whu4_0, Peking4_0, Cqu4_3, Zju4_0, Linear4_0, Coarse4Tier];

    /// <summary>
    /// 按名字解析绩点口径，供 WPF 与 WinUI 3 <b>共用同一份逻辑</b>。
    /// </summary>
    /// <remarks>
    /// 之所以要抽出来：两个 UI 曾各自选表——WinUI 3 硬编码 <c>Whu4_0</c>，
    /// WPF 走设置项，于是同一份 27 门课两边分别显示 GPA 3.68 与 3.222。
    /// 界面上同时出现两个 GPA，比算错更糟。口径解析必须是单一事实来源。
    /// <para>
    /// 名字匹配不上时回落 <see cref="Standard4_0"/>；有自定义分段表时优先用自定义。
    /// </para>
    /// </remarks>
    public static GpaScale Resolve(string? presetName, string? customBandsJson = null)
    {
        var custom = TryParseCustom("自定义", customBandsJson);
        if (custom is not null) return custom;

        // 历史值兼容：早期 DataStore 存的是英文 "Standard 4.0"，
        // 而预设名是中文「标准 4.0 制」，这个名字永远匹配不上。
        // 当时只是因为回落分支恰好也是 Standard4_0 才没出事。
        // 显式映射，别再依赖巧合。
        if (presetName is "Standard 4.0") return Standard4_0;

        return Presets.FirstOrDefault(s => s.Name == presetName) ?? Standard4_0;
    }

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
    public static GpaScale? TryParseCustom(string? name, string? bandsJson)
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
