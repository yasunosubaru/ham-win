using Ham.Infrastructure.Education;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 成绩字段契约的回归测试。
/// </summary>
/// <remarks>
/// 这里的字段名<b>不是猜的</b>，是从教务页面自己的
/// <c>/js/comp/jwglxt/cjgl/cjcx/cxDgXscj.js</c>（<b>免凭据可下载</b>）里
/// <c>getGridColModel()</c> 原样抽出的 40 个列名中挑出来的。
/// <para>
/// 为什么值得专门锁：字段名一旦猜错，解析器会「安静地返回 0 条」，
/// 没有异常、没有日志，症状与「真的没有成绩」完全一样。
/// 把手编字段名的测试换成真实字段名，才能在写错的第一时间就红。
/// </para>
/// </remarks>
public class ScoreFieldContractTests
{
    /// <summary>
    /// 一条真实形态的成绩行，字段名取自 <c>getGridColModel()</c>。
    /// </summary>
    private const string RealisticScoreResponse =
        """
        {"items":[
          {"xmblbz":"必修","xmcjbz":"学位课程","wlkkxq":"","ck":"0",
           "xh":"2025310100001","xm":"张三","xnmmc":"2025-2026","xqmmc":"第一学期",
           "kch":"1","kcmc":"大学物理(A)","kcxzmc":"公共基础必修","xf":"4.0",
           "cj":"88","cjbz":"正常考试","jd":"1","ksxz":"正常考试",
           "qmcj":"88","sycj":"","qzcj":"","pscj":"90","cjsfzf":"1","sfxwkc":"",
           "kccjpm":"","kkbmmc":"物理科学与技术学院","jysmc":"张三","kcbj":"1",
           "kclbmc":"主讲","kcgsmc":"闭卷考试","jxbmc":"PHY1101","jsxm":"张三",
           "khfsmc":"","xsbjmc":"","xfjd":"1.0","sfjfmc":"是","jycj":"","jybkcj":"",
           "ksxzdm":"11","bfzcj":"88","xnm":"2025","xqm":"3"},
          {"xmblbz":"选修","xmcjbz":"","wlkkxq":"","ck":"0",
           "xh":"2025310100001","xm":"张三","xnmmc":"2025-2026","xqmmc":"第一学期",
           "kch":"1","kcmc":"中国近现代史纲要","kcxzmc":"公共基础必修","xf":"2.0",
           "cj":"缺考","cjbz":"","jd":"1","ksxz":"",
           "qmcj":"","sycj":"","qzcj":"","pscj":"","cjsfzf":"","sfxwkc":"",
           "kccjpm":"","kkbmmc":"历史学院","jysmc":"李四","kcbj":"1",
           "kclbmc":"主讲","kcgsmc":"开卷","jxbmc":"HIS1102","jsxm":"李四",
           "khfsmc":"","xsbjmc":"","xfjd":"1.0","sfjfmc":"是","jycj":"","jybkcj":"",
           "ksxzdm":"","bfzcj":"缺考","xnm":"2025","xqm":"3"}
        ]}
        """;

    [Fact]
    public void ParsesRowUsingTheAuthenticColumnNames()
    {
        var result = EducationParser.ParseScores(RealisticScoreResponse);

        Assert.NotNull(result);
        // 第二门是「缺考」，按既有行为被丢弃（与 ham-rn 一致）
        var one = Assert.Single(result!.Scores);

        Assert.Equal("大学物理(A)", one.Name);
        Assert.Equal("PHY1101", one.CourseId);
        Assert.Equal(4.0, one.Credit, 3);
        Assert.Equal(88.0, one.Score, 3);
        Assert.Equal("张三", one.Instructor);
        Assert.Equal("物理科学与技术学院", one.CourseCollege);
        Assert.Equal("公共基础必修", one.CourseType);
    }

    [Fact]
    public void PicksUpStudentIdentityFromTheSameRow()
    {
        var result = EducationParser.ParseScores(RealisticScoreResponse);

        Assert.NotNull(result);
        Assert.NotNull(result!.UserInfo);
        Assert.Equal("2025310100001", result.UserInfo!.StudentId);
        Assert.Equal("张三", result.UserInfo.Name);
    }

    /// <summary>
    /// 非数值成绩（缺考/缓考）被丢弃，但<b>不</b>让整份结果变成 0 条而看不出原因。
    /// </summary>
    [Fact]
    public void NonNumericScoreIsDroppedWithoutBreakingTheRest()
    {
        var result = EducationParser.ParseScores(RealisticScoreResponse);

        Assert.NotNull(result);
        Assert.Single(result!.Scores);
        Assert.DoesNotContain(result.Scores, s => s.Name.Contains("中国近现代史"));
    }

    // ── 判据：看首个字符，不看 HTTP 状态 ──

    [Fact]
    public void TwoHundredWithHtmlLoginPageIsNotMistakenForData()
    {
        // 这个主机大量失败路径是 200 + HTML。只看状态码会把它当数据，
        // 结果是「0 条成绩」且毫无线索。
        const string loginHtml = "<!DOCTYPE html><html><head><title>用户登录</title></head>"
                                 + "<body>本科教务管理系统 请先登录</body></html>";

        Assert.False(EducationParser.LooksLikeJson(loginHtml));
        Assert.Null(EducationParser.ParseScores(loginHtml));
    }

    [Fact]
    public void TwoHundredWithHomepageHtmlIsAlsoRejected()
    {
        const string home = "<html><body>教学管理信息服务平台 — 欢迎</body></html>";

        Assert.False(EducationParser.LooksLikeJson(home));
        Assert.Null(EducationParser.ParseScores(home));
    }

    [Fact]
    public void TheServersFailMessageIsNotJsonEither()
    {
        // 打过教师版动作 cxDgXscj 时收到的就是这个
        const string fail = "{'state':'fail','message':'please try it again later!'}";

        // 单引号不是合法 JSON —— 但首字符是 {，所以判据放行，交给真正的 JSON 解析去拒。
        // 这里断言的是「不会在这里被静默当成空结果」，而是明确解析失败。
        Assert.Null(EducationParser.ParseScores(fail));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrMissingBodyIsRejected(string? body)
    {
        Assert.False(EducationParser.LooksLikeJson(body));
        Assert.Null(EducationParser.ParseScores(body));
    }

    [Fact]
    public void LeadingWhitespaceAndBomStillCountAsJson()
    {
        Assert.True(EducationParser.LooksLikeJson("  {\"items\":[]}"));
        Assert.True(EducationParser.LooksLikeJson("﻿{\"items\":[]}"));
        Assert.True(EducationParser.LooksLikeJson("[{\"bfzcj\":\"88\"}]"));
    }

    /// <summary>空结果与「拿不到」必须能区分开，否则验证码/会话问题永远查不出来。</summary>
    [Fact]
    public void EmptyItemsIsDistinguishableFromUnparseable()
    {
        var empty = EducationParser.ParseScores("{\"items\":[]}");
        Assert.NotNull(empty);
        Assert.Empty(empty!.Scores);

        Assert.Null(EducationParser.ParseScores("<html>请先登录</html>"));
    }
}
