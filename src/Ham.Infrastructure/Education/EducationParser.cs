using System.Text.Json;
using System.Text.Json.Serialization;
using Ham.Core.Models;

namespace Ham.Infrastructure.Education;

// ── 教务 JSON 契约 ────────────────────────────────────────────────────────────
// 注意：以下 DTO 对应教务响应体。字段名保留原始拼音/大写，不做重命名，
// 以便与官方页面/抓包结果逐字段对照。

internal sealed class CourseResponse
{
    [JsonPropertyName("xsxx")] public StudentInfo? Student { get; set; }
    [JsonPropertyName("kbList")] public List<CourseKb>? KbList { get; set; }
}

internal sealed class StudentInfo
{
    [JsonPropertyName("XH")] public string? StudentId { get; set; }
    [JsonPropertyName("XH_ID")] public string? StudentIdAlt { get; set; }
}

internal sealed class CourseKb
{
    [JsonPropertyName("kcmc")] public string? Name { get; set; }
    [JsonPropertyName("jxbmc")] public string? CourseId { get; set; }
    [JsonPropertyName("xqj")] public string? Weekday { get; set; }
    [JsonPropertyName("cdmc")] public string? Location { get; set; }
    [JsonPropertyName("xm")] public string? Instructor { get; set; }
    [JsonPropertyName("zcmc")] public string? InstructorType { get; set; }
    [JsonPropertyName("kcxz")] public string? CourseType { get; set; }
    [JsonPropertyName("xf")] public string? Credit { get; set; }
    [JsonPropertyName("jcs")] public string? ClassSessions { get; set; }
    [JsonPropertyName("zcd")] public string? WeekText { get; set; }
}

internal sealed class ScoreResponse
{
    [JsonPropertyName("items")] public List<ScoreItem>? Items { get; set; }
}

internal sealed class ScoreItem
{
    [JsonPropertyName("jgmc")] public string? College { get; set; }
    [JsonPropertyName("zymc")] public string? Major { get; set; }
    [JsonPropertyName("xm")] public string? Name { get; set; }
    [JsonPropertyName("xh")] public string? StudentId { get; set; }
    [JsonPropertyName("xnm")] public string? Year { get; set; }
    [JsonPropertyName("kcmc")] public string? CourseName { get; set; }
    [JsonPropertyName("jsxm")] public string? Instructor { get; set; }
    [JsonPropertyName("jxbmc")] public string? CourseId { get; set; }
    [JsonPropertyName("xf")] public string? Credit { get; set; }
    [JsonPropertyName("kcxzmc")] public string? CourseType { get; set; }
    [JsonPropertyName("bfzcj")] public string? Score { get; set; }
    [JsonPropertyName("kkbmmc")] public string? CourseCollege { get; set; }
    [JsonPropertyName("xqm")] public string? Semester { get; set; }

    /// <summary>
    /// 原始成绩代码（源字段 <c>cj</c>）。
    /// </summary>
    /// <remarks>
    /// <b>必须有它才能正确处理非正常成绩。</b>实测真实响应里，
    /// 中期退课的课是 <c>cj="W"</c> 而 <c>bfzcj="0"</c>——
    /// 只看 <c>bfzcj</c> 会把它当成「考了 0 分」计入均分和绩点，
    /// 那是错的：那门课根本没考。判据是 <c>cj</c> 能不能解析成数字。
    /// </remarks>
    [JsonPropertyName("cj")] public string? RawScore { get; set; }

    /// <summary>成绩备注，如「中期退课」（源字段 <c>cjbz</c>）。</summary>
    [JsonPropertyName("cjbz")] public string? ScoreNote { get; set; }

    /// <summary>课程英文名（源字段 <c>kcywmc</c>）。</summary>
    [JsonPropertyName("kcywmc")] public string? CourseNameEn { get; set; }
}

/// <summary>学生基本信息。</summary>
public sealed record EducationUserInfo(string StudentId, string Name, string College, string Major);

/// <summary>一次教务查询的完整结果。</summary>
public sealed record CourseFetchResult(
    IReadOnlyList<Course> Courses,
    IReadOnlyList<CourseSlot> Slots,
    IReadOnlyList<string> IgnoredCourseNames,
    string? StudentId);

public sealed record ScoreFetchResult(
    IReadOnlyList<ScoreRecord> Scores,
    EducationUserInfo? UserInfo);

/// <summary>
/// 教务响应解析器。
/// </summary>
/// <remarks>
/// 与 ham-rn <c>business/education/*/parser.ts</c> 行为对齐。抽成纯函数以便离线单测。
/// </remarks>
public static class EducationParser
{
    /// <summary>
    /// 教务用<b>全角空格 U+3000</b>填充 JSON 缩进，直接 JSON.parse 会失败，必须先剔除。
    /// 这是复刻该接口最容易踩的坑。
    /// </summary>
    public static string Normalize(string raw)
    {
        var text = raw.TrimStart('﻿').Trim();
        return text.Replace('　', ' ');
    }

    public static T? ParseJson<T>(string raw) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Normalize(raw), JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>解析课表响应。</summary>
    public static CourseFetchResult? ParseCourses(string raw, int year, int semester)
    {
        var response = ParseJson<CourseResponse>(raw);
        if (response?.KbList is null) return null;

        var courses = new List<Course>();
        var slots = new List<CourseSlot>();
        var ignored = new List<string>();

        foreach (var kb in response.KbList)
        {
            var courseId = kb.CourseId?.Trim() ?? string.Empty;
            var name = kb.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;

            // 教务 xqj 约定 7=周日，内部统一为 0=周日。
            var weekdayRaw = int.TryParse(kb.Weekday, out var wd) ? wd : 0;
            var weekday = weekdayRaw == 7 ? 0 : weekdayRaw;
            if (weekday is < 0 or > 6) continue;

            var (classFrom, classTo) = ParseSessions(kb.ClassSessions);
            var weekRule = WeekRuleParser.Parse(kb.WeekText);

            // 周次完全解析不出来的课程保留，但标记后由调用方过滤，避免静默丢课。
            if (weekRule.IsEmpty)
            {
                ignored.Add(name);
            }

            var course = new Course
            {
                Name = name,
                CourseId = courseId,
                Instructor = kb.Instructor?.Trim() ?? string.Empty,
                InstructorType = kb.InstructorType?.Trim() ?? string.Empty,
                WeekFrom = weekRule.WeekFrom,
                WeekTo = weekRule.WeekTo,
                ClassFrom = classFrom,
                ClassTo = classTo,
                Weekday = weekday,
                CourseType = kb.CourseType?.Trim() ?? string.Empty,
                Credit = double.TryParse(kb.Credit, out var c) ? c : 0,
                Location = kb.Location?.Trim() ?? string.Empty,
                Color = CourseColorAssigner.ForCourseId(courseId),
                Year = year,
                SemesterNumber = semester,
                RawWeekText = kb.WeekText,
                Weeks = weekRule.Weeks,
            };

            courses.Add(course);

            foreach (var week in weekRule.Weeks)
            {
                slots.Add(new CourseSlot
                {
                    Week = week,
                    Weekday = weekday,
                    ClassFrom = classFrom,
                    ClassTo = classTo,
                    Color = course.Color,
                });
            }
        }

        var studentId = response.Student?.StudentId ?? response.Student?.StudentIdAlt;
        return new CourseFetchResult(courses, slots, ignored, studentId);
    }

    /// <summary>
    /// 解析节次字段 <c>jcs</c>。形如 <c>"5-6"</c>；不含连字符时整体保持 -1，
    /// 而不是误当成单节处理。
    /// </summary>
    public static (int From, int To) ParseSessions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (-1, -1);
        var text = raw.Trim();

        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return (-1, -1);

        if (!int.TryParse(parts[0], out var from) || !int.TryParse(parts[1], out var to))
            return (-1, -1);

        if (from > to) (from, to) = (to, from);
        return (from, to);
    }

    /// <summary>
    /// 判断正文是否像接口返回的 JSON。
    /// </summary>
    /// <remarks>
    /// <b>不能只看 HTTP 状态码。</b>jwgl.whu.edu.cn 上大量失败路径返回
    /// <c>200</c> + <c>text/html</c> 的登录页或首页——会话失效、被安全设备拦截、
    /// 验证码没过，都会长这样。只看状态码会把 HTML 当成数据去解析，
    /// 得到「0 条成绩」这种无法定位的结论。
    /// <para>
    /// 判据取自 iOS 端交接：<b>看首个字符</b>，是 <c>{</c> 或 <c>[</c> 才当作 JSON。
    /// 请求前会带一个前导空白（BOM 之类），所以先 Trim。
    /// </para>
    /// </remarks>
    public static bool LooksLikeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var c = raw.TrimStart().TrimStart('﻿').TrimStart()[0];
        return c is '{' or '[';
    }

    /// <summary>解析成绩响应。</summary>
    /// <remarks>
    /// 正文不像 JSON 时直接返回 <c>null</c>，让上层把
    /// 「拿不到」和「真的是 0 条」区分开——这两者混为一谈会让
    /// 验证码失败、会话失效等问题一直查不出来。
    /// </remarks>
    public static ScoreFetchResult? ParseScores(string? raw)
    {
        if (!LooksLikeJson(raw)) return null;

        var response = ParseJson<ScoreResponse>(raw!);
        if (response?.Items is null) return null;

        var scores = new List<ScoreRecord>();
        EducationUserInfo? userInfo = null;

        foreach (var item in response.Items)
        {
            // userInfo 取第一条学号非空的记录。
            if (userInfo is null && !string.IsNullOrEmpty(item.StudentId))
            {
                userInfo = new EducationUserInfo(
                    item.StudentId!.Trim(),
                    item.Name?.Trim() ?? string.Empty,
                    item.College?.Trim() ?? string.Empty,
                    item.Major?.Trim() ?? string.Empty);
            }

            if (!int.TryParse(item.Year, out var year) || year <= 0) continue;
            if (!double.TryParse(item.Credit, out var credit)) continue;

            // 非数值成绩（"缺考"/"缓考"/"W"等）在此被丢弃，与 ham-rn 行为一致。
            //
            // 但**光看 bfzcj 不够**。实测真实响应里中期退课的课是：
            //     cj = "W"   cjbz = "中期退课"   bfzcj = "0"
            // 只按 bfzcj 判，它会被当成「考了 0 分」算进均分与绩点，
            // 而那门课根本没考。**用 cj 能否解析成数字来判**，才正确。
            if (int.TryParse(item.RawScore, out var score)
                && int.TryParse(item.Score, out var bfz))
            {
                // 两者都在时以 cj 为准；bfzcj 只在 cj 缺失时兜底。
                score = bfz;
            }
            else if (item.RawScore is { Length: > 0 })
            {
                continue;   // cj 存在但不是数字 → 非正常成绩，丢弃
            }
            else if (!int.TryParse(item.Score, out score))
            {
                continue;   // 两者都没有可解析的数值
            }

            var semesterCode = int.TryParse(item.Semester, out var sc) ? sc : 3;

            scores.Add(new ScoreRecord
            {
                Year = year,
                SemesterNumber = SemesterCode.FromInternal(semesterCode),
                Name = item.CourseName?.Trim() ?? string.Empty,
                CourseId = item.CourseId?.Trim() ?? string.Empty,
                Instructor = item.Instructor?.Trim() ?? string.Empty,
                Credit = credit,
                CourseType = item.CourseType?.Trim() ?? string.Empty,
                Score = score,
                CourseCollege = item.CourseCollege?.Trim() ?? string.Empty,
                IsEnabled = true,
            });
        }

        return new ScoreFetchResult(scores, userInfo);
    }
}
