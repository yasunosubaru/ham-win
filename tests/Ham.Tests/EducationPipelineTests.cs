using System.Net;
using System.Text;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Education;
using Ham.Infrastructure.Net;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 本地 mock 的 CAS + 教务服务器，用于端到端验证「拿到 CAS 会话之后」的整条链路。
/// </summary>
/// <remarks>
/// <para>
/// 这段链路此前<b>完全没有被验证过</b>——只跑得通需要真实学号与信息门户密码。
/// 可它恰恰是最容易静默出错的一段：
/// </para>
/// <list type="bullet">
/// <item>CAS Cookie 迁进 <see cref="CookieContainer"/> 时域名/路径是否正确；</item>
/// <item>SSO 地址的 <c>service</c> 是否只编码一次；</item>
/// <item>课表/成绩接口<b>不显式带 Cookie</b>，完全依赖同一 jar 里的教务会话；</item>
/// <item>二次认证与"被重定向回登录页"能否被识别。</item>
/// </list>
/// <para>
/// mock 严格复刻真实系统的关键行为，其中最重要的是：
/// <b>课表/成绩接口在缺少教务会话 Cookie 时返回登录页</b>。
/// 只有这样，测试才能真正证明"会话依赖"是有效契约而不是恰好能跑通。
/// </para>
/// </remarks>
public sealed class MockCampusServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly int _port;

    /// <summary>服务端实际收到的请求日志，断言用。</summary>
    public List<string> Requests { get; } = [];

    /// <summary>标记服务端是否应当接受任何请求（用于负向测试）。</summary>
    public bool EducationSessionRequired { get; set; } = true;

    /// <summary>强制返回的状态码（0 表示不强制）。</summary>
    public int ForcedStatus { get; set; }

    /// <summary>强制返回的正文。</summary>
    public string ForcedBody { get; set; } = string.Empty;

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>供被测代码使用的端点配置。</summary>
    public CampusEndpoints Endpoints => new()
    {
        CasBaseUrl = BaseUrl,
        CasHost = "127.0.0.1",
        EducationBaseUrl = BaseUrl,
        EducationHost = $"127.0.0.1:{_port}",
        EducationService = $"{BaseUrl}/sso/jznewsixlogin",
        IsCampusHost = url => url.Contains("127.0.0.1", StringComparison.Ordinal),
    };

    public MockCampusServer()
    {
        _port = FindFreePort();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private static int FindFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            var query = req.Url?.Query ?? string.Empty;
            var hasCasSession = req.Cookies["CASTGC"] is { Value.Length: > 0 };
            var hasEduSession = req.Cookies["JSESSIONID"] is { Value.Length: > 0 };
            Requests.Add($"{req.HttpMethod} {path}{query} cas={hasCasSession} edu={hasEduSession}");

            // ── 登录表单提交：真实系统是整页 POST 到 /authserver/login ──
            if (path == "/authserver/login" && req.HttpMethod == "POST")
            {
                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                var user = ExtractFormField(body, "username");
                var pass = ExtractFormField(body, "password");

                if (user == "202312345678" && pass == "CorrectHorse")
                {
                    ctx.Response.Headers.Add("Set-Cookie", "CASTGC=TGT-MOCK; path=/");
                    var svc = GetQueryValue(req.Url!, "service") ?? string.Empty;
                    Redirect(ctx, AppendQuery(Uri.UnescapeDataString(svc), "ticket=ST-MOCK-1"));
                    return;
                }

                WriteHtml(ctx, "<html><body>您提供的用户名或者密码有误</body></html>");
                return;
            }

            switch (path)
            {
                // ── CAS 票校验入口（浏览器侧）──
                case "/authserver/mobile/auth":
                case "/authserver/login" when req.HttpMethod == "GET":
                {
                    if (!hasCasSession)
                    {
                        WriteHtml(ctx, "<html><body>请先登录</body></html>");
                        return;
                    }

                    var svc = GetQueryValue(req.Url!, "service");
                    if (string.IsNullOrEmpty(svc)) svc = $"{BaseUrl}/authserver/mobile/callback";

                    var target = Uri.UnescapeDataString(svc);
                    if (target.Contains("jznewsixlogin", StringComparison.Ordinal))
                    {
                        // 教务 SSO：发一张票
                        Redirect(ctx, AppendQuery(target, "ticket=ST-MOCK-1"));
                    }
                    else
                    {
                        Redirect(ctx, AppendQuery(target, "ticket=ST-MOCK-1"));
                    }
                    return;
                }

                // ── 教务 SSO 消费票据，建立教务会话 ──
                case "/sso/jznewsixlogin":
                {
                    var ticket = GetQueryValue(req.Url!, "ticket");
                    if (!hasCasSession || ticket != "ST-MOCK-1")
                    {
                        WriteHtml(ctx, "<html><body>票据无效，请重新登录</body></html>");
                        return;
                    }

                    ctx.Response.Headers.Add("Set-Cookie", "JSESSIONID=EDU-SESSION; path=/");
                    Redirect(ctx, BaseUrl + "/");
                    return;
                }

                // ── 教务首页 ──
                case "/":
                {
                    if (EducationSessionRequired && !hasEduSession)
                    {
                        WriteHtml(ctx, "<html><body>请先登录</body></html>");
                        return;
                    }

                    WriteHtml(ctx, "<html><body>教学管理信息服务平台 — 欢迎</body></html>");
                    return;
                }

                // ── 课表 / 成绩：必须依赖同一 jar 中的教务会话 ──
                // 成绩走学生动作 cxXsgrcj（页面 jqGrid 的 jsxx=="xs" 分支）；
                // cxDgXscj 是教师/管理版，只保留在路由里以防有人又打错回去。
                case "/kbcx/xskbcx_cxXsgrkb.html":
                case "/cjcx/cjcx_cxXsgrcj.html":
                case "/cjcx/cjcx_cxDgXscj.html":
                {
                    string form;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                        form = await reader.ReadToEndAsync().ConfigureAwait(false);

                    Requests.Add($"  form: {form}");

                    if (ForcedStatus != 0)
                    {
                        ctx.Response.StatusCode = ForcedStatus;
                        var bytes = Encoding.UTF8.GetBytes(ForcedBody);
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                        return;
                    }

                    // 复刻真实站点：请求必须"看起来来自页面自身"才返回 JSON。
                    // - 带 X-Requested-With: XMLHttpRequest（页面 jQuery $.post 的行为）→ 200 JSON
                    // - 不带 → 200 text/html 的教务首页（真实站点实测，Copyright: zfsoft）
                    var fromPage = req.Headers["X-Requested-With"] == "XMLHttpRequest";

                    if (!fromPage)
                    {
                        WriteHtml(ctx, EducationHomeHtml);
                        return;
                    }

                    if (EducationSessionRequired && !hasEduSession)
                    {
                        WriteJson(ctx, """{ "error": "请先登录" }""");
                        return;
                    }

                    WriteJson(ctx, path.Contains("kbcx") ? CourseJson : ScoreJson);
                    return;
                }

                // ── 学生信息页 ──
                case "/xtgl/index_cxYhxxIndex.html":
                {
                    if (EducationSessionRequired && !hasEduSession)
                    {
                        WriteHtml(ctx, "<html><body>请先登录</body></html>");
                        return;
                    }

                    WriteHtml(ctx, UserInfoHtml);
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            WriteHtml(ctx, "<html><body>404</body></html>");
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* 客户端已断开 */ }
        }
    }

    /// <summary>真实教务返回的 JSON 以 U+3000 全角空格缩进。</summary>
    /// <summary>教务首页 HTML。真实站点对"不像页面自身发出的请求"返回的就是它。</summary>
    private const string EducationHomeHtml =
        "<!doctype html><html><head><title>教学管理信息服务平台</title>" +
        "<meta name=\"Copyright\" content=\"zfsoft\" /></head><body>教学管理信息服务平台</body></html>";

    private const string CourseJson = """
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

    private const string ScoreJson = """
        {
        　"items": [
        　　{ "jgmc":"计算机学院", "zymc":"计算机科学与技术", "xm":"张三", "xh":"202312345678",
        　　  "xnm":"2025", "kcmc":"高等数学(上)", "jsxm":"张老师", "jxbmc":"MATH101",
        　　  "xf":"5.0", "kcxzmc":"公共基础必修", "bfzcj":"92", "kkbmmc":"数学与统计学院", "xqm":"16" }
        　]
        }
        """;

    private const string UserInfoHtml = """
        <html><body>
          <div class="media">
            <img class="media-object" src="/photo.jsp?xh_id=202312345678&w=120">
            <div class="media-body">
              <h4 class="media-heading">张三 学生</h4>
              <p>计算机学院 2023级</p>
            </div>
          </div>
        </body></html>
        """;

    private static void Redirect(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = location;
        ctx.Response.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static void WriteJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json;charset=UTF-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string AppendQuery(string url, string extra)
        => url.Contains('?', StringComparison.Ordinal) ? url + "&" + extra : url + "?" + extra;

    private static string? GetQueryValue(Uri uri, string key)
        => HttpUtilityShim.ParseQuery(uri.Query).GetValueOrDefault(key);

    private static string ExtractFormField(string body, string name)
    {
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')) == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}

file static class HttpUtilityShim
{
    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }
            result[Uri.UnescapeDataString(pair[..eq].Replace('+', ' '))] =
                Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }
}

public class EducationPipelineTests
{
    /// <summary>
    /// 完整链路：CAS 会话 → 教务 SSO → 课表 → 成绩 → 学生信息。
    /// </summary>
    [Fact]
    public async Task FullPipelineFromCasSessionToParsedData()
    {
        using var server = new MockCampusServer();
        using var http = new CampusHttpClient();

        var cas = new CasClient(http, server.Endpoints);
        var edu = new EducationClient(http, server.Endpoints);

        // 模拟 WebView2 登录成功后导出的 CAS 会话
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);

        // 1) 票交换：CAS 会话 → 教务会话
        await cas.LoginToEducationAsync();

        // 2) 课表与成绩。生产路径由浏览器页面代取，这里用等价的方式模拟
        //    （带上页面同源 XHR 才会带的 X-Requested-With）。
        var payloads = await FetchThroughBrowserAsync(server, http, server.Endpoints);

        var courses = EducationParser.ParseCourses(
            payloads[EducationEndpoints.CoursePath], 2026, 1);
        Assert.NotNull(courses);
        Assert.Equal(2, courses!.Courses.Count);
        Assert.Equal("202312345678", courses.StudentId);
        Assert.Equal(2, courses.Courses[0].Weekday);
        Assert.Equal(0, courses.Courses[1].Weekday);   // xqj=7 → 周日

        var scores = EducationParser.ParseScores(payloads[EducationEndpoints.ScorePath]);
        Assert.NotNull(scores);
        Assert.Single(scores!.Scores);
        Assert.Equal(92.0, scores.Scores[0].Score);
        Assert.Equal(2025, scores.Scores[0].Year);
        Assert.Equal(3, scores.Scores[0].SemesterNumber);  // xqm=16 → 学期 3

        // 3) 学生信息（HTML 页，可直接 GET）
        var info = await edu.GetUserInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("202312345678", info!.StudentId);
        Assert.Equal("张三", info.Name);
        Assert.Equal("计算机学院", info.College);

        // 4) 纯 HTTP 通路即使有会话也拿不到 JSON —— 这是站点行为，不是实现缺陷
        var viaHttp = await edu.GetCourseListRawAsync(2026, 1);
        Assert.Contains("text/html", viaHttp.ContentType, StringComparison.OrdinalIgnoreCase);

        // 全程只用过一次 SSO 换票
        Assert.Single(server.Requests,
            r => r.StartsWith("GET /sso", StringComparison.Ordinal)
                 || r.StartsWith("POST /sso", StringComparison.Ordinal));
    }

    /// <summary>
    /// 模拟 <c>CasLoginWindow.RunFetchPlanAsync</c>：以页面内 XHR 的特征发请求。
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchThroughBrowserAsync(
        MockCampusServer server, CampusHttpClient http, CampusEndpoints endpoints)
    {
        var plan = new FetchPlan(
        [
            new FetchTarget(EducationEndpoints.CoursePath, new Dictionary<string, string>
            {
                ["validate"] = EducationClient.BuildValidateToken(),
                ["xnm"] = "2026",
                ["xqm"] = "3",
                ["xzlx"] = "ck",
            }),
            new FetchTarget(EducationEndpoints.ScorePath, new Dictionary<string, string>
            {
                ["validate"] = EducationClient.BuildValidateToken(),
                ["xnm"] = string.Empty,
                ["xqm"] = string.Empty,
                ["queryModel.showCount"] = "150",
            }),
        ]);

        return await EducationBrowserProbe.FetchAsync(http, endpoints, plan);
    }

    /// <summary>
    /// 反证：课表/成绩接口<b>依赖会话</b>，换一个没有会话的上下文就必然拿不到数据。
    /// </summary>
    [Fact]
    public async Task CourseEndpointDependsOnSharedSession()
    {
        using var server = new MockCampusServer();
        using var session = new CampusHttpClient();
        using var fresh = new CampusHttpClient();

        var cas = new CasClient(session, server.Endpoints);
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);
        await cas.LoginToEducationAsync();

        // 持有会话的上下文能拿到 JSON
        var good = await FetchThroughBrowserAsync(server, session, server.Endpoints);
        Assert.NotNull(EducationParser.ParseCourses(good[EducationEndpoints.CoursePath], 2026, 1));

        // 全新客户端没有教务会话 → 服务端返回「请先登录」，
        // 且必须被识别为"会话失效"而不是静默当成"本学期没课"。
        var ex = await Assert.ThrowsAsync<CasReAuthRequiredException>(
            () => FetchThroughBrowserAsync(server, fresh, server.Endpoints));
        Assert.NotNull(ex);
    }

    /// <summary>没有 CAS 会话时，SSO 换票必须失败，而不是静默"成功"。</summary>
    [Fact]
    public async Task SsoExchangeFailsWithoutCasSession()
    {
        using var server = new MockCampusServer();
        using var http = new CampusHttpClient();

        var cas = new CasClient(http, server.Endpoints);   // 故意不播种 Cookie

        await Assert.ThrowsAsync<CampusNetworkException>(() => cas.LoginToEducationAsync());
    }

    /// <summary>SSO 地址的 service 参数只允许编码一次（双重编码会导致教务解析失败）。</summary>
    [Fact]
    public void SsoUrlEncodesServiceExactlyOnce()
    {
        var prod = CampusEndpoints.Default.BuildSsoLoginUrl();
        Assert.Equal(
            "https://cas.whu.edu.cn/authserver/login?service=https%3A%2F%2Fjwgl.whu.edu.cn%2Fsso%2Fjznewsixlogin",
            prod);

        // 编码后的值解回来必须正好是原地址，不能是双重编码
        var service = prod[(prod.IndexOf("service=", StringComparison.Ordinal) + 8)..];
        Assert.Equal("https://jwgl.whu.edu.cn/sso/jznewsixlogin", Uri.UnescapeDataString(service));
    }

    /// <summary>
    /// 教务的自定义状态码（901/910 等均非 HTTP 标准码）必须连同正文一起进日志。
    /// </summary>
    /// <remarks>
    /// 只记状态码是没用的——数字本身不说明原因，正文才写得清楚。
    /// 这条断言就是防止有人后来把正文日志删掉。
    /// </remarks>
    [Fact]
    public async Task CustomStatusCodeResponseBodyIsLogged()
    {
        using var server = new MockCampusServer { ForcedStatus = 910, ForcedBody = "<html>安全设备拦截</html>" };
        using var http = new CampusHttpClient(endpoints: server.Endpoints);

        var cas = new CasClient(http, server.Endpoints);
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);
        await cas.LoginToEducationAsync();

        var lines = new List<string>();
        var previous = Infrastructure.Logging.Log.Sink;
        Infrastructure.Logging.Log.Sink = l => { lines.Add(l); return true; };
        try
        {
            await Assert.ThrowsAsync<CampusNetworkException>(
                () => new EducationClient(http, server.Endpoints).GetCourseListRawAsync(2026, 1));
        }
        finally
        {
            Infrastructure.Logging.Log.Sink = previous;
        }

        var warn = Assert.Single(lines, l => l.Contains("910", StringComparison.Ordinal));
        Assert.Contains("安全设备拦截", warn, StringComparison.Ordinal);
        Assert.Contains("/kbcx/xskbcx_cxXsgrkb.html", warn, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归：<c>X-Requested-With: XMLHttpRequest</c> 必须<b>由浏览器页面</b>发出，
    /// 不能在 C# 侧手工拼进请求头。
    /// </summary>
    /// <remarks>
    /// 曾两次踩坑：先是无条件发送该头 → 稳定 901 空正文；随后整体删掉该头 →
    /// 拿到 200 但正文是 HTML 首页而不是 JSON。正确做法是让 WebView2 在页面内发
    /// <c>XMLHttpRequest</c>，此时同源 + 会话 + Referer + XHR 标记齐全，安全设备才放行。
    /// </remarks>
    [Fact]
    public async Task DoesNotHandCraftXmlHttpRequestHeader()
    {
        using var server = new MockCampusServer();
        using var http = new CampusHttpClient(endpoints: server.Endpoints);

        var cas = new CasClient(http, server.Endpoints);
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);
        await cas.LoginToEducationAsync();

        var edu = new EducationClient(http, server.Endpoints);

        // 客户端不得自行添加该头
        var raw = await edu.GetCourseListRawAsync(2026, 1);
        Assert.Contains("text/html", raw.ContentType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 反证：<b>纯 HTTP 客户端拿不到课表数据</b>，这不是实现问题而是站点的既定行为。
    /// </summary>
    /// <remarks>
    /// 真实站点实测（2026-09）：不带 <c>X-Requested-With</c> → 200 但
    /// <c>Content-Type: text/html</c>、正文是教务首页（<c>Copyright: zfsoft</c>）；
    /// 带该头（但非页面上下文）→ 901 空正文。
    /// 因此数据必须由已认证的浏览器页面代取，见 <c>FetchPlan</c>。
    /// 本用例把这个事实钉住：一旦有人改回纯 HTTP 拼请求，测试会立刻失败。
    /// </remarks>
    [Fact]
    public async Task PlainHttpClientCannotObtainCourseJson()
    {
        using var server = new MockCampusServer();
        using var http = new CampusHttpClient(endpoints: server.Endpoints);

        var cas = new CasClient(http, server.Endpoints);
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);
        await cas.LoginToEducationAsync();

        // 教育会话已建立，但纯 HTTP 客户端拿到的仍然是 HTML 首页
        var raw = await new EducationClient(http, server.Endpoints)
            .GetCourseListRawAsync(2026, 1);

        Assert.Contains("text/html", raw.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("教学管理信息服务平台", raw.Body, StringComparison.Ordinal);
        Assert.Contains("zfsoft", raw.Body, StringComparison.Ordinal);

        // 解析器必须对它返回 null —— 不能把 HTML 当成空课表蒙混过去
        Assert.Null(EducationParser.ParseCourses(raw.Body, 2026, 1));
    }

    /// <summary>注销后必须真正清掉 CAS 与教务两侧的会话，否则"退出登录"形同虚设。</summary>
    [Fact]
    public async Task LogoutClearsBothCasAndEducationSessions()
    {
        using var server = new MockCampusServer();
        using var http = new CampusHttpClient();

        var cas = new CasClient(http, server.Endpoints);
        cas.SeedCookies([new CasCookie(server.Endpoints.CasHost, "CASTGC", "TGT-MOCK")]);
        await cas.LoginToEducationAsync();

        // 注销前可以取数
        var before = await FetchThroughBrowserAsync(server, http, server.Endpoints);
        Assert.NotNull(EducationParser.ParseCourses(before[EducationEndpoints.CoursePath], 2026, 1));

        http.ClearCookies(server.Endpoints);

        // 注销后必须被判定为会话失效
        await Assert.ThrowsAsync<CasReAuthRequiredException>(
            () => FetchThroughBrowserAsync(server, http, server.Endpoints));
    }
}
