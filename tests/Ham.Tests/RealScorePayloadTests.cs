using Ham.Infrastructure.Education;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 用**线上真实响应**验证成绩解析。
/// </summary>
/// <remarks>
/// 这份载荷的**字段名与结构**是 2026-09-27 实测
/// <c>POST /cjcx/cjcx_cxXsgrcj.html?doType=query</c> 返回的真实内容，逐字段照抄，
/// <b>不是手编的示意数据</b>。
/// <para>
/// 手编 JSON 的教训已经吃过两次：字段名会把 <c>stopID</c> 写成 <c>stopId</c>、
/// 会把 <c>startStopName</c> 当成首站名。用真实载荷才抓得到。
/// </para>
/// <para>
/// <b>已脱敏：</b>字段名、字段顺序、取值形态（学期码 <c>xqm=3/12</c>、
/// 退课课的 <c>cj="W"</c> + <c>bfzcj="0"</c>、学分、课程号）全部照抄原样，
/// 这些才是解析器真正依赖的东西；而<b>能定位到具体个人的值一律替换为占位</b>——
/// 学号、姓名、班号、班级名、专业、学院、任课教师姓名。
/// 断言里的期望值同步替换，测试覆盖的逻辑一行没少。
/// </para>
/// </remarks>
public class RealScorePayloadTests
{
    /// <summary>真实响应节选：保留全部与解析相关的字段，含一门中期退课的课。</summary>
    private const string RealResponse = """
        {"currentPage":1,"currentResult":0,"entityOrField":false,"items":[
        {"bfzcj":"77","bh":"20250001","bj":"示例班","cj":"77","cjbdczr":"张伟",
         "cjsfzf":"否","czr":"张伟","jgmc":"示例学院","jsxm":"张伟",
         "jxbmc":"(2025-2026-1)-1100740011001-47","kch":"1100740011001","kcmc":"形势与政策1",
         "kcywmc":"Situation and Policy（1）","kcxzdm":"0000","kcxzmc":"公共基础必修",
         "kkbmmc":"形势与政策教育中心","ksxzdm":"01","sfxwkc":"否","xf":"0.5","xh":"2025310100001",
         "xm":"张三","xnm":"2025","xnmmc":"2025-2026","xqm":"3","xqmmc":"1",
         "xsbjmc":"研究生选课","xslb":"普通本科","xqmmc2":"","zymc":"示例专业"},

        {"bfzcj":"0","bh":"20250001","bj":"示例班","cj":"W","cjbz":"中期退课",
         "cjbdczr":"李娜","cjsfzf":"否","czr":"李娜","jgmc":"示例学院",
         "jsxm":"李娜","jxbmc":"(2025-2026-1)-1100820011060-0211","kch":"1100820011060",
         "kcmc":"素质体育1","kcxzdm":"0000","kcxzmc":"公共基础必修","kkbmmc":"体育部",
         "ksxzdm":"01","sfxwkc":"否","xf":"1.0","xh":"2025310100001","xm":"张三",
         "xnm":"2025","xnmmc":"2025-2026","xqm":"3","xqmmc":"1","zymc":"示例专业"},

        {"bfzcj":"92","bh":"20250001","bj":"示例班","cj":"92","czr":"王芳",
         "jgmc":"示例学院","jsxm":"王芳",
         "jxbmc":"(2025-2026-2)-1100820011061-91","kch":"1100820011061","kcmc":"素质体育2",
         "kcxzdm":"0000","kcxzmc":"公共基础必修","kkbmmc":"体育部","ksxzdm":"01",
         "sfxwkc":"否","xf":"1.0","xh":"2025310100001","xm":"张三","xnm":"2025",
         "xnm2":"","xqm":"12","xqmmc":"2","zymc":"示例专业"},

        {"bfzcj":"82","bh":"20250001","cj":"82","czr":"刘洋",
         "jsxm":"刘洋","jxbmc":"(2025-2026-1)-1100850011004-03","kch":"1100850011004",
         "kcmc":"高等数学A1","kcxzmc":"公共基础必修","kkbmmc":"公共数学教学",
         "ksxzdm":"01","xf":"6.0","xh":"2025310100001","xm":"张三",
         "xnm":"2025","xqm":"3","xqmmc":"1","zymc":"示例专业"},

        {"bfzcj":"83","bh":"20250001","cj":"83","czr":"陈静",
         "jsxm":"陈静","jxbmc":"(2025-2026-2)-1100860011001-08","kch":"1100860011001",
         "kcmc":"大学物理A（上）","kcxzmc":"公共基础必修","kkbmmc":"公共物理教学",
         "ksxzdm":"01","xf":"4.0","xh":"2025310100001","xm":"张三",
         "xnm":"2025","xqm":"12","xqmmc":"2","zymc":"示例专业"}
        ],"totalResult":"29"}
        """;

    [Fact]
    public void ParsesRealResponse()
    {
        var result = EducationParser.ParseScores(RealResponse);

        Assert.NotNull(result);
        // 5 门里「素质体育1」是中期退课（cj="W"），应当被排除
        Assert.Equal(4, result!.Scores.Count);
    }

    [Fact]
    public void ExcludesWithdrawnCourseInsteadOfCountingItAsZero()
    {
        var result = EducationParser.ParseScores(RealResponse);
        Assert.NotNull(result);

        // 这门课 cj="W"、cjbz="中期退课"、bfzcj="0"。
        // 若只按 bfzcj 判，它会以 0 分进入均分和绩点——那是错的，它根本没考。
        Assert.DoesNotContain(result!.Scores, s => s.Name == "素质体育1");
        Assert.DoesNotContain(result.Scores, s => s.Score == 0);
    }

    [Fact]
    public void MapsFieldsFromTheRealColumnNames()
    {
        var result = EducationParser.ParseScores(RealResponse);
        Assert.NotNull(result);

        var math = result!.Scores.Single(s => s.Name == "高等数学A1");
        Assert.Equal(82, math.Score);
        Assert.Equal(6.0, math.Credit);
        Assert.Equal("刘洋", math.Instructor);
        Assert.Equal("公共数学教学", math.CourseCollege);
        Assert.Equal("公共基础必修", math.CourseType);
        Assert.Equal(2025, math.Year);
        Assert.Equal(1, math.SemesterNumber);      // xqm=3 → 第一学期
    }

    [Fact]
    public void DecodesInternalSemesterCodes()
    {
        var result = EducationParser.ParseScores(RealResponse);
        Assert.NotNull(result);

        // 真实响应里 xqm=3 表示第一学期、xqm=12 表示第二学期
        var s1 = result!.Scores.Where(s => s.Name == "高等数学A1").Single();
        var s2 = result.Scores.Where(s => s.Name == "大学物理A（上）").Single();

        Assert.Equal(1, s1.SemesterNumber);
        Assert.Equal(2, s2.SemesterNumber);
    }

    [Fact]
    public void ReadsStudentIdentityFromTheRealRow()
    {
        var result = EducationParser.ParseScores(RealResponse);

        Assert.NotNull(result);
        Assert.NotNull(result!.UserInfo);
        Assert.Equal("2025310100001", result.UserInfo!.StudentId);
        Assert.Equal("张三", result.UserInfo.Name);
        Assert.Equal("示例学院", result.UserInfo.College);
        Assert.Equal("示例专业", result.UserInfo.Major);
    }

    [Fact]
    public void ComputesGpaFromRealData()
    {
        var result = EducationParser.ParseScores(RealResponse);
        Assert.NotNull(result);

        var scale = Ham.Core.Models.GpaScale.Whu4_0;
        // 4 门课全部计入：77/0.5、92/1.0、82/6.0、83/4.0
        var avg = Ham.Core.Models.ScoreCalculator.WeightedAverage(result!.Scores);
        Assert.InRange(avg, 80, 86);

        var gpa = Ham.Core.Models.ScoreCalculator.Gpa(result.Scores, scale);
        Assert.InRange(gpa, 3.0, 3.9);
    }
}
