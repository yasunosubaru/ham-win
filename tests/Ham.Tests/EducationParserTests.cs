using System.Text;
using Ham.Core.Models;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Education;
using Xunit;

namespace Ham.Tests;

public class EducationParserTests
{
    [Fact]
    public void StripsIdeographicSpacesBeforeParsing()
    {
        // U+3000 全角空格，不剔除则 JSON.parse 失败
        var raw = "{\u3000\u3000\"a\": 1\u3000}";
        var normalized = EducationParser.Normalize(raw);
        Assert.DoesNotContain('　', normalized);
    }

    [Fact]
    public void ParsesRealShapedCourseResponse()
    {
        var raw = """
        {
        　"xsxx": { "XH": "202312345678", "XH_ID": "u-99" },
        　"kbList": [
        　　{ "kcmc": "高等数学(上)", "jxbmc": "MATH101", "xqj": "2", "cdmc": "教三-201",
        　　  "xm": "张老师", "zcmc": "主讲", "kcxz": "公共基础必修", "xf": "5.0",
        　　  "jcs": "1-2", "zcd": "1-17周" },
        　　{ "kcmc": "大学英语", "jxbmc": "ENGL101", "xqj": "7", "cdmc": "外语楼-101",
        　　  "xm": "李老师", "zcmc": "主讲", "kcxz": "公共基础必修", "xf": "3.0",
        　　  "jcs": "3-4", "zcd": "1-17周(单)" }
        　]
        }
        """;

        var result = EducationParser.ParseCourses(raw, 2026, 1);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Courses.Count);
        Assert.Equal("202312345678", result.StudentId);

        var math = result.Courses[0];
        Assert.Equal("高等数学(上)", math.Name);
        Assert.Equal("MATH101", math.CourseId);
        Assert.Equal(2, math.Weekday);           // xqj=2 → 周二（内部 0=周日…6=周六）
        Assert.Equal(1, math.ClassFrom);
        Assert.Equal(2, math.ClassTo);
        Assert.Equal(1, math.WeekFrom);
        Assert.Equal(17, math.WeekTo);
        Assert.Equal(5.0, math.Credit);
        Assert.Equal("教三-201", math.Location);
        Assert.Equal(CourseColorAssignerIndex(math.CourseId), math.Color);

        // xqj=7 是周日，必须归一化到 0
        var english = result.Courses[1];
        Assert.Equal(0, english.Weekday);
        Assert.Equal(WeekParity.Odd, WeekRuleParser.Parse(english.RawWeekText!).Parity);
    }

    private static string CourseColorAssignerIndex(string id) => Core.Models.CourseColorAssigner.ForCourseId(id);

    [Fact]
    public void NormalizesWeekdaySevenToZero()
    {
        var raw = """
        { "xsxx": {"XH":"1"}, "kbList": [
          { "kcmc":"A", "jxbmc":"A1", "xqj":"7", "jcs":"1-2", "zcd":"1-9周" },
          { "kcmc":"B", "jxbmc":"B1", "xqj":"1", "jcs":"1-2", "zcd":"1-9周" }
        ] }
        """;
        var result = EducationParser.ParseCourses(raw, 2026, 1);
        Assert.Equal(0, result!.Courses[0].Weekday);
        Assert.Equal(1, result.Courses[1].Weekday);
    }

    [Theory]
    [InlineData("5-6", 5, 6)]
    [InlineData("1-2", 1, 2)]
    [InlineData("6-5", 5, 6)]     // 倒置
    [InlineData("3-3", 3, 3)]
    [InlineData("abc", -1, -1)]
    [InlineData("5", -1, -1)]     // 不含连字符时整体保持 -1
    [InlineData("", -1, -1)]
    [InlineData(null, -1, -1)]
    public void ParsesSessionRanges(string? raw, int expectedFrom, int expectedTo)
    {
        var (from, to) = EducationParser.ParseSessions(raw);
        Assert.Equal(expectedFrom, from);
        Assert.Equal(expectedTo, to);
    }

    [Fact]
    public void CollectsUnparsableWeekCoursesInsteadOfDroppingSilently()
    {
        var raw = """
        { "xsxx": {"XH":"1"}, "kbList": [
          { "kcmc":"正常课", "jxbmc":"OK1", "xqj":"1", "jcs":"1-2", "zcd":"1-9周" },
          { "kcmc":"周次异常课", "jxbmc":"BAD1", "xqj":"3", "jcs":"1-2", "zcd":"待定" }
        ] }
        """;
        var result = EducationParser.ParseCourses(raw, 2026, 1);
        Assert.Equal(2, result!.Courses.Count);
        Assert.Contains("周次异常课", result.IgnoredCourseNames);
        Assert.DoesNotContain(result.Slots, s => s.Weekday == 3);   // 异常课不产生课表格
    }

    [Fact]
    public void ParsesRealShapedScoreResponse()
    {
        var raw = """
        { "items": [
          { "jgmc":"计算机学院", "zymc":"计算机科学与技术", "xm":"张三", "xh":"202312345678",
            "xnm":"2025", "kcmc":"高等数学(上)", "jsxm":"张老师", "jxbmc":"MATH101",
            "xf":"5.0", "kcxzmc":"公共基础必修", "bfzcj":"92", "kkbmmc":"数学与统计学院", "xqm":"16" },
          { "jgmc":"计算机学院", "zymc":"计算机科学与技术", "xm":"张三", "xh":"202312345678",
            "xnm":"2025", "kcmc":"缺考课程", "jsxm":"王老师", "jxbmc":"P/F101",
            "xf":"2.0", "kcxzmc":"专业必修", "bfzcj":"缺考", "kkbmmc":"计算机学院", "xqm":"16" }
        ] }
        """;

        var result = EducationParser.ParseScores(raw);
        Assert.NotNull(result);

        // 非数值成绩（缺考）被丢弃
        Assert.Single(result!.Scores);
        var s = result.Scores[0];
        Assert.Equal(2025, s.Year);
        Assert.Equal(3, s.SemesterNumber);      // xqm=16 → 学期 3
        Assert.Equal("高等数学(上)", s.Name);
        Assert.Equal(5.0, s.Credit);
        Assert.Equal(92, s.Score);
        Assert.Equal("公共基础必修", s.CourseType);
        Assert.Equal("数学与统计学院", s.CourseCollege);
        Assert.True(s.IsEnabled);

        Assert.NotNull(result.UserInfo);
        Assert.Equal("202312345678", result.UserInfo!.StudentId);
        Assert.Equal("计算机学院", result.UserInfo.College);
        Assert.Equal("计算机科学与技术", result.UserInfo.Major);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(12, 2)]
    [InlineData(16, 3)]
    [InlineData(99, 1)]   // 无法识别时回落 1
    public void MapsInternalSemesterCodes(int internalCode, int expected)
        => Assert.Equal(expected, Core.Models.SemesterCode.FromInternal(internalCode));

    [Fact]
    public void ParsesUserInfoHtmlPage()
    {
        var html = """
        <div class="media">
          <img class="media-object" src="/t.jpg?xh_id=202312345678&amp;t=1">
          <div class="media-body">
            <h4 class="media-heading">张三 学生</h4>
            <p>计算机学院 2023 </p>
          </div>
        </div>
        """;

        var info = EducationClient.ParseUserInfoHtml(html);
        Assert.NotNull(info);
        Assert.Equal("202312345678", info!.StudentId);
        Assert.Equal("张三", info.Name);
        Assert.Equal("计算机学院", info.College);
    }

    [Fact]
    public void MalformedJsonReturnsNullInsteadOfThrowing()
    {
        Assert.Null(EducationParser.ParseCourses("not json at all", 2026, 1));
        Assert.Null(EducationParser.ParseScores("<html>error</html>"));
    }

    [Fact]
    public void EmptyArraysAreHandled()
    {
        var result = EducationParser.ParseCourses("""{ "xsxx": {"XH":"1"}, "kbList": [] }""", 2026, 1);
        Assert.NotNull(result);
        Assert.Empty(result!.Courses);
    }

    [Fact]
    public void ValidateTokenLooksLikeOfficialFormat()
    {
        var token = EducationClient.BuildValidateToken();
        Assert.StartsWith("sl", token);
        Assert.True(token.Length > 10);
    }
}

public class CasLogicTests
{
    [Theory]
    // 本机实测（2026-09）武大当前部署的真实成功落地页：
    // /authserver/mobile/auth?appId=… → /authserver/login?service=<编码后的 mobile/callback>
    // → 登录成功后 302 到 callback 并带上 ticket=ST-…
    [InlineData("https://cas.whu.edu.cn/authserver/mobile/callback?appId=985180443&ticket=ST-1234-abcXYZ", "ST-1234-abcXYZ")]
    [InlineData("https://cas.whu.edu.cn/authserver/mobile/callback?appId=985180443&login_type=mobileLogin&ticket=ST-9", "ST-9")]
    // 旧版 mobile_token 流程仍需兼容
    [InlineData("https://cas.whu.edu.cn/authserver/mobile/default.html?mobile_token=abc123", "abc123")]
    public void DetectsLoginSuccessAndExtractsTicket(string url, string expected)
    {
        Assert.True(CasClient.IsLoginSuccess(url, out var ticket));
        Assert.Equal(expected, ticket);
    }

    [Fact]
    public void DecodesPercentEncodedUrlBeforeChecking()
    {
        // service 参数整体编码，票据参数混在编码串里
        var url = "https://cas.whu.edu.cn/authserver/mobile/callback"
                  + "%3FappId%3D985180443%26ticket%3DST-99";
        Assert.True(CasClient.IsLoginSuccess(url, out var ticket));
        Assert.Equal("ST-99", ticket);
    }

    [Fact]
    public void DetectsRealObservedMobileSuccessUrl()
    {
        // 2026-09-26 实网观测到的真实移动端成功页（脱敏后保留结构）。
        // 关键点：mobile_token 在 URL **片段**（#）里，不在查询串里。
        // 曾因正则锚定 [?&] 而匹配不到，只能靠更早的裸 Contains 侥幸命中。
        const string real =
            "https://cas.whu.edu.cn/authserver/mobile/default.html"
            + "#mobile_token=UM9hYldxpSsvAET2+xCHmgzPC3R2aaUS/uTdnpuHiuMSlz2vwoCMbvtf0ezZ9z8davc"
            + "BYWUQnl2mmdQ7AxK9a9/sFxr7xBCScKgizO1i3vFoW5YFOW+e8A==";

        Assert.True(CasClient.IsLoginSuccess(real, out var token));

        // 令牌是 Base64，含 / + =，必须完整取到且不能按 URL 组件解码
        Assert.StartsWith("UM9hYldxpSsvAET2+xCHmgz", token, StringComparison.Ordinal);
        Assert.EndsWith("e8A==", token, StringComparison.Ordinal);
        Assert.DoesNotContain("%", token, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotOverDecodeIntoFalsePositive()
    {
        // 双重编码：解码一层后是 %26ticket%253D…，仍不是真正的票据分隔。
        // 刻意只解一层，因此不得凭空判定为成功。
        var url = "https://cas.whu.edu.cn/authserver/login%3Fservice%3Dhttps%253A%252F%252F"
                  + "cas.whu.edu.cn%252Fauthserver%252Fmobile%252Fcallback%253FappId%253D985180443"
                  + "%2526ticket%253Dst-99";
        Assert.False(CasClient.IsLoginSuccess(url, out _));
    }

    [Theory]
    // 登录页本身会重定向到的地址 —— 注意它含 service= 但**没有** ticket，
    // 历史上正是把它误判为"已成功"（或用 default.html 判据导致永远等不到），引发无限等待。
    [InlineData("https://cas.whu.edu.cn/authserver/mobile/auth?appId=985180443")]
    [InlineData("https://cas.whu.edu.cn/authserver/login?service=x")]
    [InlineData("https://cas.whu.edu.cn/authserver/login?service=https%3A%2F%2Fcas.whu.edu.cn%2Fauthserver%2Fmobile%2Fcallback%3FappId%3D985180443&login_type=mobileLogin")]
    [InlineData("https://cas.whu.edu.cn/authserver/mobile/default.html")]  // 无票据，判为未完成
    [InlineData("")]
    [InlineData(null)]
    public void NonSuccessUrlsAreRejected(string? url)
        => Assert.False(CasClient.IsLoginSuccess(url, out _));

    [Theory]
    [InlineData("2023123456789", true)]   // 13 位
    [InlineData("20231234", true)]        // 8 位
    [InlineData("2023123", false)]        // 7 位
    [InlineData("20231234567890", false)] // 14 位
    public void StudentIdLengthValidation(string id, bool expected)
        => Assert.Equal(expected, CasClient.IsLikelyAcceptedStudentIdLength(id));

    [Fact]
    public void SsoUrlUsesPreEncodedServiceExactlyOnce()
    {
        var url = CasEndpoints.BuildSsoLoginUrl();
        Assert.StartsWith("https://cas.whu.edu.cn/authserver/login?service=", url);
        // service 已编码一次，不应出现二次编码留下的 %25
        Assert.DoesNotContain("%25", url);
        Assert.Contains(CasEndpoints.EducationServiceEncoded, url);
    }

    [Fact]
    public void ExtractsEducationErrorMessage()
    {
        var html = "<script>var dlktsxx=\"您提供的用户名或者密码有误\";</script>";
        Assert.Equal("您提供的用户名或者密码有误", CasClient.ExtractErrorMessage(html));
    }

    [Fact]
    public void MissingErrorMessageYieldsNull()
        => Assert.Null(CasClient.ExtractErrorMessage("<html>no marker</html>"));

    [Theory]
    // IsWhuHost 只做 host 白名单判定；是否含 ReAuth 由调用方另行判断。
    [InlineData("https://evil.example.com/ReAuth?x=1", false)]
    [InlineData("https://jwgl.whu.edu.cn/ReAuth?x=1", true)]
    [InlineData("https://cas.whu.edu.cn/a/ReAuth", true)]
    [InlineData("https://notwhu.edu.cn/ReAuth", false)]
    [InlineData("not a url", false)]
    public void ReAuthHostAllowlistBlocksForeignHosts(string url, bool expected)
        => Assert.Equal(expected, EducationClient.IsWhuHost(url));
}
