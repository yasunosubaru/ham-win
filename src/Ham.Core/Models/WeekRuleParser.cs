using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ham.Core.Models;

/// <summary>周次奇偶属性。</summary>
public enum WeekParity
{
    /// <summary>每周都上。</summary>
    Normal,

    /// <summary>单周上。</summary>
    Odd,

    /// <summary>双周上。</summary>
    Even,
}

/// <summary>
/// 周次规则的解析结果。
/// </summary>
/// <param name="Weeks">展开后的具体周次集合（已去重、已排序）。</param>
/// <param name="Parity">识别出的奇偶属性。</param>
/// <param name="WeekFrom">连续时的起点，否则 -1。</param>
/// <param name="WeekTo">连续时的终点，否则 -1。</param>
public sealed record WeekRule(IReadOnlyList<int> Weeks, WeekParity Parity, int WeekFrom, int WeekTo)
{
    public bool IsEmpty => Weeks.Count == 0;

    public bool IsContinuous => WeekFrom > 0 && WeekTo >= WeekFrom;

    public bool Contains(int week) => Weeks.Contains(week);
}

/// <summary>
/// 教务周次文本（源字段 <c>zcd</c>）解析器。
/// </summary>
/// <remarks>
/// 移植自 ham-rn <c>business/education/course/parser.ts</c>，逐条对齐其边界行为：
/// <list type="bullet">
/// <item>分段分隔符：<c>, ， 、</c>（半角/全角逗号、顿号）。</item>
/// <item>单段剥离 <c>( ) （ ） 单 双 第 周</c>；<b>必须剥掉"第"</b>，
/// 否则 <c>parseInt("第1")</c> 得 NaN，会静默丢掉整门课。</item>
/// <item>含"单"取奇数周、含"双"取偶数周，否则 <see cref="WeekParity.Normal"/>。</item>
/// <item>倒置范围 <c>8-1周</c> 自动交换为 1-8。</item>
/// <item>单周 + 奇偶矛盾（<c>2周(单)</c>）降级为 <see cref="WeekParity.Normal"/> 并保留该周。</item>
/// <item><see cref="WeekRule.WeekFrom"/>/<see cref="WeekRule.WeekTo"/> 仅在周次完全连续时写入，
/// 否则保持 -1/-1（界面据此提示"周次不连续"）。</item>
/// </list>
/// </remarks>
public static partial class WeekRuleParser
{
    [GeneratedRegex(@"[,，、]")]
    private static partial Regex SegmentSeparatorRegex();

    [GeneratedRegex(@"[()（）单双第周]")]
    private static partial Regex StripRegex();

    /// <summary>解析周次文本。无法解析出任何周次时返回空规则。</summary>
    public static WeekRule Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new WeekRule([], WeekParity.Normal, -1, -1);

        var parity = WeekParity.Normal;
        if (rawText.Contains('单')) parity = WeekParity.Odd;
        else if (rawText.Contains('双')) parity = WeekParity.Even;

        var weeks = new SortedSet<int>();
        var declaredParity = parity;

        foreach (var segment in SegmentSeparatorRegex().Split(rawText))
        {
            var cleaned = StripRegex().Replace(segment, string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            // "第" 已被剥离；若此处仍非纯数字/连字符格式，直接跳过该段。
            if (!int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
                && !RangeRegex().IsMatch(cleaned))
            {
                continue;
            }

            if (RangeRegex().Match(cleaned) is { Success: true } match)
            {
                var from = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var to = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                if (from > to) (from, to) = (to, from);   // 倒置范围自动纠正

                for (var w = from; w <= to; w++)
                {
                    if (MatchesParity(w, declaredParity)) weeks.Add(w);
                }
            }
            else if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var one))
            {
                if (MatchesParity(one, declaredParity)) weeks.Add(one);
            }
        }

        // 单周且与声明的奇偶矛盾时（"2周(单)"）降级为每周，修回该周。
        if (weeks.Count == 0 && declaredParity != WeekParity.Normal && TryExtractFirstNumber(rawText, out var lone))
        {
            parity = WeekParity.Normal;
            weeks.Add(lone);
        }

        if (weeks.Count == 0)
            return new WeekRule([], WeekParity.Normal, -1, -1);

        var list = weeks.ToList();
        var contiguous = list.Count == (list[^1] - list[0] + 1);
        return new WeekRule(list, parity, contiguous ? list[0] : -1, contiguous ? list[^1] : -1);
    }

    /// <summary>把周次规则渲染为规范文本，例如 <c>1-17周(单)</c>。</summary>
    public static string Describe(WeekRule rule)
    {
        if (rule.IsEmpty) return string.Empty;

        var parityText = rule.Parity switch
        {
            WeekParity.Odd => "(单)",
            WeekParity.Even => "(双)",
            _ => string.Empty,
        };

        // 有奇偶属性时，周次集合天然不连续，但应还原为"1-17周(单)"这种区间写法；
        // 只有既无奇偶、又确实断续的集合才逐个罗列。
        if (rule.IsContinuous || rule.Parity != WeekParity.Normal)
        {
            return $"{rule.Weeks[0]}-{rule.Weeks[^1]}周{parityText}";
        }

        return string.Join(",", rule.Weeks) + "周";
    }

    private static bool MatchesParity(int week, WeekParity parity) => parity switch
    {
        WeekParity.Odd => week % 2 == 1,
        WeekParity.Even => week % 2 == 0,
        _ => true,
    };

    private static bool TryExtractFirstNumber(string text, out int value)
    {
        var digits = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch)) digits.Append(ch);
            else if (digits.Length > 0) break;
        }

        return int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"^(\d{1,3})\s*-\s*(\d{1,3})$")]
    private static partial Regex RangeRegex();
}
