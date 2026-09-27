using System.Globalization;
using System.Text.RegularExpressions;
using Ham.Core.Models;

namespace Ham.Infrastructure.Education;

/// <summary>
/// 从教务「成绩查询」页面<b>渲染后的 DOM</b>中解析成绩。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：成绩查询接口 <c>/cjcx/cjcx_cjXsgrcj.html</c> 用自造参数发 XHR 是行不通的。
/// 实测（2026-09，真实站点，在校生账号）：
/// <c>xnm</c>/<c>xqm</c> 遍历 6 种组合（2025/2026 × 3/12/16），
/// 返回的响应<b>完全相同</b>，均为 186 字节：
/// </para>
/// <code>
/// HTTP 910 {"currentPage":1,"currentResult":0,"entityOrField":false,"items":[],
///           "limit":15,"totalResult":0,...}
/// </code>
/// <para>
/// <c>entityOrField:false</c> 与参数无关，说明服务端压根没读到查询条件。
/// 参数名靠猜是猜不出来的，而页面自己的 JS 一定知道 —— 既然浏览器能正常打开该页面并
/// 渲染出成绩表，就让它渲染，我们读结果。
/// </para>
/// <para>
/// 解析策略刻意宽松：正方系统的表头文案会随版本变，因此不假定列序，而是<b>按表头文字</b>
/// 定位列，再逐行取值。认不出的表头一律忽略，绝不猜。
/// </para>
/// </remarks>
public static partial class EducationScorePageParser
{
    /// <summary>从页面文本/HTML 中解析成绩。解析不到任何一行时返回 <c>null</c>。</summary>
    public static IReadOnlyList<ScoreRecord>? Parse(string? document)
    {
        if (string.IsNullOrWhiteSpace(document)) return null;

        var tables = TableRegex().Matches(document);
        foreach (Match table in tables)
        {
            var rows = RowRegex().Matches(table.Value);
            if (rows.Count < 2) continue;

            var header = SplitCells(rows[0].Value);
            var map = MapColumns(header);
            if (!map.ContainsKey(Column.Course) || !map.ContainsKey(Column.Score)) continue;

            var result = new List<ScoreRecord>();
            foreach (var row in rows.Skip(1))
            {
                var cells = SplitCells(row.Value);
                if (cells.Count < header.Count) continue;

                var name = Cell(cells, map, Column.Course);
                var scoreText = Cell(cells, map, Column.Score);
                if (name.Length == 0) continue;

                var credit = ParseDouble(Cell(cells, map, Column.Credit));
                if (!TryParseScore(scoreText, out var score)) continue;   // 缺考/缓考等一律跳过

                result.Add(new ScoreRecord
                {
                    Name = name,
                    Score = score,
                    Credit = credit,
                    Instructor = Cell(cells, map, Column.Instructor),
                    CourseType = Cell(cells, map, Column.Category),
                    CourseCollege = Cell(cells, map, Column.College),
                    Year = ParseYear(Cell(cells, map, Column.Year)),
                    SemesterNumber = ParseSemester(Cell(cells, map, Column.Semester)),
                });
            }

            if (result.Count > 0) return result;
        }

        return null;
    }

    private enum Column
    {
        Course, Score, Credit, Instructor, Category, College, Year, Semester,
    }

    /// <summary>按表头文字定位列。认不出的表头不猜，直接忽略。</summary>
    private static Dictionary<Column, int> MapColumns(IReadOnlyList<string> header)
    {
        var map = new Dictionary<Column, int>();

        for (var i = 0; i < header.Count; i++)
        {
            var h = header[i];

            if (!map.ContainsKey(Column.Course) && Mentions(h, "课程")) map[Column.Course] = i;
            // 「成绩」必须与「课程性质/成绩类型」区分：优先匹配完全等于「成绩」的列
            if (!map.ContainsKey(Column.Score) && (h == "成绩" || h == "分数" || h == "总评成绩")) map[Column.Score] = i;
            if (!map.ContainsKey(Column.Credit) && Mentions(h, "学分")) map[Column.Credit] = i;
            if (!map.ContainsKey(Column.Instructor) && Mentions(h, "教师") && Mentions(h, "任课")) map[Column.Instructor] = i;
            if (!map.ContainsKey(Column.Category) && Mentions(h, "课程性质")) map[Column.Category] = i;
            if (!map.ContainsKey(Column.College) && Mentions(h, "开课院系")) map[Column.College] = i;
            if (!map.ContainsKey(Column.Year) && Mentions(h, "学年")) map[Column.Year] = i;
            if (!map.ContainsKey(Column.Semester) && Mentions(h, "学期")) map[Column.Semester] = i;
        }

        // 教师列常常就叫「教师」或「任课教师」
        if (!map.ContainsKey(Column.Instructor))
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (Mentions(header[i], "教师")) { map[Column.Instructor] = i; break; }
            }
        }

        return map;
    }

    private static bool Mentions(string header, string keyword)
        => header.Contains(keyword, StringComparison.Ordinal);

    private static string Cell(IReadOnlyList<string> cells, Dictionary<Column, int> map, Column column)
        => map.TryGetValue(column, out var i) && i < cells.Count ? cells[i] : string.Empty;

    private static IReadOnlyList<string> SplitCells(string rowHtml)
        => CellRegex().Matches(rowHtml)
            .Select(m => System.Net.WebUtility.HtmlDecode(
                TagRegex().Replace(m.Value, " ")).Trim())
            .Where(s => s.Length > 0 || true)
            .ToList();

    private static double ParseDouble(string s)
    {
        var digits = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;
    }

    private static int ParseYear(string s)
    {
        var m = Regex.Match(s, @"(20\d{2})");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>把「1」/「第一学期」/「秋季」等映射为内部学期号 1/2/3。</summary>
    private static int ParseSemester(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return 0;
        if (t == "1") return 1;
        if (t == "2") return 2;
        if (t.Contains("秋", StringComparison.Ordinal)) return 1;
        if (t.Contains("春", StringComparison.Ordinal)) return 2;
        return 0;
    }

    /// <summary>
    /// 解析分数。只接受纯数值或百分制；「缺考」「缓考」「免修」等一律返回 false。
    /// </summary>
    private static bool TryParseScore(string text, out double score)
    {
        score = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;

        // 等级制（A/B+/优秀/合格…）无法换算成百分制 GPA，如实跳过而不是编一个数
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            return false;

        return score is >= 0 and <= 100;
    }

    [GeneratedRegex("<table[^>]*>.*?</table>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRegex();

    [GeneratedRegex("<tr[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex("<t[hd][^>]*>.*?</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
