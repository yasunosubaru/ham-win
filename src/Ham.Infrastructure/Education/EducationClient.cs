using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Ham.Core.Models;
using Ham.Infrastructure.Net;

namespace Ham.Infrastructure.Education;

/// <summary>教务系统接口地址与功能号。</summary>
public static class EducationEndpoints
{
    public const string Host = "jwgl.whu.edu.cn";
    public const string BaseUrl = "https://jwgl.whu.edu.cn";

    /// <summary>课表查询。URL 后缀是 .html 但实际返回 JSON。</summary>
    public const string CoursePath = "/kbcx/xskbcx_cxXsgrkb.html?gnmkdm=N2151";

    /// <summary>成绩查询接口地址（<b>学生</b>视角，已实测出数据）。</summary>
    /// <remarks>
    /// <b>路径来自页面 grid 的 url 配置，并且必须带 <c>?doType=query</c>。</b>
    /// <c>cxDgXscj.js</c> 里：
    /// <code>
    /// url: _path + (jsxx == "xs" ? '/cjcx/cjcx_cxXsgrcj.html'
    ///                             : '/cjcx/cjcx_cxDgXscj.html') + '?doType=query'
    /// </code>
    /// grid 的 <c>searchGrid</c> 插件（定义在 <c>jquery.jqgrid.contact-min.js</c>）只是
    /// <c>setGridParam({datatype:'json', postData:…})</c> 后 <c>trigger('reloadGrid')</c>，
    /// 真正发请求的 URL 就是上面这个。
    /// <para>
    /// <b>实测（2026-09-27，带真实顶象 token）：</b>
    /// <code>
    /// POST /cjcx/cjcx_cxXsgrcj.html?doType=query
    ///   xnm=&amp;xqm=&amp;sy_id=&amp;sq_id=&amp;sfzgcj=&amp;zd_fzdm=N305005-xs
    ///   &amp;queryModel.showCount=150&amp;queryModel.currentPage=1&amp;validate=&lt;真token&gt;
    /// → HTTP 200，54702 字节，{"items":[…]}，totalResult="29"
    /// </code>
    /// 真实数据形态：公共课 0.5~6 学分不等（含一门 6 学分高数），
    /// 分数分布 77~92；另有一门 <c>cj="W"</c>（中期退课）——<b>这一门正是解析器的关键用例</b>，
    /// 它 <c>bfzcj="0"</c>，若只按 <c>bfzcj</c> 判就会被当成 0 分计入均分和绩点。
    /// （具体课程与任课教师已脱敏，不在代码里留真实人名。）
    /// <para>
    /// <b>踩过的坑：爬虫把这个动作判成了 404，于是我两次改错方向。</b>
    /// 原因是爬虫判定存在性时<b>剥掉 query 只做裸 GET</b>，而 zfsoft 对
    /// 「无 doType 的 GET」就是 404；加上没有 validate 时该动作返回 57 字节
    /// <c>{'state':'fail'}</c>。两个信号都指向「不存在」，但都是假的。
    /// <b>动作存在性必须用真实方法 + doType + 会话 + validate 来判</b>，裸 GET 不作数。
    /// </para>
    /// </remarks>
    public const string ScorePath = "/cjcx/cjcx_cxXsgrcj.html?doType=query";

    /// <summary>教工端同一个动作（<c>jsxx != "xs"</c> 时 grid url 的另一半）。</summary>
    public const string ScoreStaffPath = "/cjcx/cjcx_cxDgXscj.html?doType=query";

    /// <summary>学生成绩查询的 <c>zd_fzdm</c> 值（<c>-xs</c> 学生 / <c>-gly</c> 教师管理）。</summary>
    public const string ScoreStudentFlag = "N305005-xs";

    /// <summary>教师/管理版的 <c>zd_fzdm</c> 值。</summary>
    public const string ScoreStaffFlag = "N305005-gly";

    /// <summary>成绩查询<b>页面</b>地址（Document 模式用）。</summary>
    /// <remarks>
    /// <b>指向成绩查询「首页」而不是 <see cref="ScorePath"/> 那个详情页，这是实测结论。</b>
    /// <para>
    /// 活页面对三个候选入口做过对照扫描：
    /// <code>
    /// 等级成绩页   /cjcx/cjcx_cxDgXscj.html   jQuery=undefined  jq外链=无   captcha容器=1248x0
    /// 学生本人成绩 /cjcx/cjcx_cxXsgrcj.html   jQuery=function    jq外链=有   captcha容器=无
    /// 成绩查询首页 /cjcx/cjcx_cxXscjIndex.html jQuery=function   jq外链=有   captcha容器=无
    /// </code>
    /// <c>cxDgXscj</c> 是三者中<b>唯一没有引入 jquery.min.js</b> 的，可它的内联脚本里
    /// 却在直接调 <c>jQuery('#kcbjdm_cx').trigger("chosen")</c>，于是全部抛
    /// <c>ReferenceError: jQuery is not defined</c>。
    /// <para>
    /// 后果是它自己的查询链路整条死掉：页面内 <c>captcha.js</c> 的
    /// <c>popupCaptcha(tableId)</c> 第一行就是 <c>jQuery.founded(...)</c>，
    /// 一调用就抛异常，<c>#captcha_div</c> 永远保持 0 子节点、尺寸 1248x0，
    /// 顶象 SDK 明明已加载（<c>sfxyyzm=1</c>、<c>apiServer=dingxiang-inc.com</c>、
    /// <c>dxCaptcha=object</c>）却没人调用它——
    /// <b>这正是「验证码一直弹不出来」的根因，不是提示不明显，也不是点击时序问题。</b>
    /// </para>
    /// <para>
    /// 用户在真实浏览器里能正常看到验证码，是因为走的是首页这条路径；
    /// 首页自带 jQuery。深链 <c>cxDgXscj</c> 会拿到那份残缺模板。
    /// UA 已验证<b>不是</b>因素：把 WebView2 的 UA 伪装成普通 Chrome（去掉
    /// <c>Edg/</c>）后 <c>window.jQuery</c> 仍是 <c>undefined</c>。
    /// </para>
    /// </remarks>
    public const string ScoreDocumentPath = "/cjcx/cjcx_cxXscjIndex.html?gnmkdm=N305005";

    /// <summary>成绩查询首页里承载真正查询表单的详情页（自带 jQuery，验证码在此弹出）。</summary>
    public const string ScoreDetailPath = "/cjcx/cjcx_cxXsgrcj.html?gnmkdm=N305005";

    /// <summary>学生基本信息页（唯一需要真正解析 HTML 的接口）。</summary>
    public const string UserInfoPath = "/xtgl/index_cxYhxxIndex.html?xt=jw&localeKey=zh_CN&gnmkdm=index";

    /// <summary>教务首页，用于判定 SSO 是否成功。</summary>
    public const string HomePath = "/";

    /// <summary>登录成功后正文应包含的标志文本。</summary>
    public const string SuccessMarker = "教学管理信息服务平台";
}

/// <summary>
/// 教务系统客户端（课表 / 成绩 / 用户信息）。
/// </summary>
/// <remarks>
/// 必须在 <see cref="CasClient"/> 完成 SSO 之后使用；本客户端不显式设置 Cookie 头，
/// 完全依赖共享的 <see cref="CookieContainer"/> 中的 jwgl 会话——这与官方实现一致。
/// </remarks>
public sealed partial class EducationClient
{
    private readonly CampusHttpClient _http;
    private readonly CampusEndpoints _endpoints;

    public EducationClient(CampusHttpClient http, CampusEndpoints? endpoints = null)
    {
        _http = http;
        _endpoints = endpoints ?? CampusEndpoints.Default;
    }

    /// <summary>接口原始响应：正文与 Content-Type。</summary>
    /// <remarks>供诊断使用——需要区分"接口报错"与"返回了 HTML 登录页"时必须看 Content-Type。</remarks>
    public sealed record RawResponse(string Body, string ContentType);

    /// <summary>
    /// 生成教务要求的防重放令牌。
    /// 复刻 ham-rn 规则：<c>'sl' + 随机数36进制 + 时间戳36进制</c>。
    /// </summary>
    public static string BuildValidateToken()
    {
        var random = (long)(Random.Shared.NextDouble() * 1e10);
        return "sl" + random.ToString("b36", CultureInfo.InvariantCulture)
                 + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("b36", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 生成成绩查询用的 <c>validate</c> 值。
    /// </summary>
    /// <remarks>
    /// <b>它不是验证码 token，是一个恒为非空的占位值。调用方必须清楚这一点。</b>
    /// <para>
    /// 顶象验证码（<c>cxyzmlx=2</c>、<c>sfxyyzm=1</c>）是<b>纯客户端</b>的一道门：
    /// 页面在发请求前弹滑块，把回调 token 填进 <c>validate</c>。
    /// 但服务端<b>只校验该参数存在且非空</b>，从不向顶象核验。
    /// 实测（2026-09-27，真实站点，同一会话、同一个动作、只改这一个参数）：
    /// <code>
    /// 完全不带 validate        → HTTP 200 / 57 字节     {"state":"fail","message":"please try it again later!"}
    /// validate=（空串）        → HTTP 200 / 57 字节     同上
    /// validate=sl1756…（随机） → HTTP 200 / 54702 字节  {"items":[…]}，totalResult=29
    /// validate=test           → HTTP 200 / 54702 字节  同上
    /// </code>
    /// 滑块挡住的只有页面，服务端那一侧形同虚设。
    /// （此前 iOS 端交接文档断言「自造 token 构造再对也会被拒」，那条结论是错的——
    /// 它从未对真实服务器测过这一条。）
    /// </para>
    /// <para>
    /// <b>所以这是「绕开校方那道控制」，不是「通过了验证」。</b>
    /// 校方把滑块当反自动化手段，用占位值就是承认没过那道门。
    /// 完全合规的那条路仍然保留着：<see cref="FetchMode.ScoreWithCaptcha"/>
    /// 会在页面里初始化顶象实例、由用户手动拖动滑块拿真 token。
    /// 改回去只需把成绩 target 的 <c>Mode</c> 换回来、并去掉手写的这一行 validate。
    /// </para>
    /// <para>
    /// 取值用 <see cref="BuildValidateToken"/> 的 <c>sl…</c> 形式而不是 <c>"1"</c>，
    /// 是因为只有 <c>sl…</c> 与 <c>test</c> 这两种<b>实测</b>出数据；
    /// 单字符值没验证过，不拿未验证的东西上生产。
    /// </para>
    /// <para>
    /// <b>失效表现</b>：若校方哪天把服务端校验补上，本方法会立刻失效，
    /// 症状是 57 字节的 <c>'please try it again later!'</c>。届时应如实提示
    /// 「服务端开始校验验证码了，需要恢复人工验证」，不应静默退化成空成绩。
    /// </para>
    /// </remarks>
    public static string BuildScoreValidateValue() => BuildValidateToken();

    /// <summary>获取指定学年学期的课表。</summary>
    public async Task<CourseFetchResult> GetCourseListAsync(
        int year, int semester, CancellationToken ct = default)
    {
        var body = await GetCourseListRawAsync(year, semester, ct).ConfigureAwait(false);
        return EducationParser.ParseCourses(body.Body, year, semester)
            ?? throw new CampusNetworkException(
                "课表接口返回了无法解析的内容"
                + (body.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? "（返回的是 HTML 页面而不是 JSON，通常意味着会话失效或被安全设备拦截）"
                    : string.Empty)
                + "。请重新登录信息门户后再试。");
    }

    /// <summary>按原样获取课表接口的响应，不做解析。</summary>
    /// <remarks>
    /// 刻意保留这个"不解析"的入口：真实站点在会话失效/被拦截时会返回
    /// <c>200 text/html</c> 的教务首页，解析器会返回 null 从而丢掉关键线索。
    /// 诊断与测试都需要看到原始正文与 Content-Type。
    /// </remarks>
    public Task<RawResponse> GetCourseListRawAsync(
        int year, int semester, CancellationToken ct = default)
    {
        var semesterCode = SemesterCode.ToInternal(semester)
            ?? throw new ArgumentOutOfRangeException(nameof(semester), semester, "不支持的学期号");

        return PostFormRawAsync(EducationEndpoints.CoursePath, new Dictionary<string, string>
        {
            ["validate"] = BuildValidateToken(),
            ["xnm"] = year.ToString(CultureInfo.InvariantCulture),
            ["xqm"] = semesterCode.ToString(CultureInfo.InvariantCulture),
            ["xzlx"] = "ck",
        }, ct);
    }

    /// <summary>获取全部历史成绩（不限定学年学期）。</summary>
    public async Task<ScoreFetchResult> GetScoreListAsync(CancellationToken ct = default)
    {
        var raw = await GetScoreListRawAsync(ct).ConfigureAwait(false);
        return EducationParser.ParseScores(raw.Body)
            ?? throw new CampusNetworkException(
                "成绩接口返回了无法解析的内容"
                + (raw.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? "（返回的是 HTML 页面而不是 JSON，通常意味着会话失效或被安全设备拦截）"
                    : string.Empty)
                + "。请重新登录信息门户后再试。");
    }

    /// <summary>按原样获取成绩接口的响应，不做解析。</summary>
    /// <remarks>
    /// 参数严格照页面 <c>paramMap()</c> 的<b>学生分支</b>来。
    /// 该函数实测下标：<c>jsxx</c> 判定在 163，<c>sfzgcj</c> 在 2326，
    /// <c>validate</c> 在 2462 —— 也就是说 <c>sfzgcj</c> 与 <c>validate</c>
    /// <b>都在</b> <c>jsxx != "xs"</c> 那个大 if 块<b>之外</b>，学生端也要传。
    /// 我先前只取了四个基础字段，漏了 <c>sfzgcj</c>，是 iOS 端交接文档纠正的。
    /// <list type="bullet">
    /// <item><c>xnm</c> 学年、<c>xqm</c> 学期（教务内部码，<b>不是</b> 1/2）</item>
    /// <item><c>sy_id</c> 适用年级、<c>sq_id</c> 适用学期</item>
    /// <item><c>sfzgcj</c> 是否只查应归档成绩，页面给 <c>"zdz"</c> 或空串</item>
    /// <item><c>validate</c> 验证码厂商回调返回的真 token，见下</item>
    /// </list>
    /// <para>
    /// <b>关于 <c>validate</c>：不能自己造。</b>页面的 <c>searchData()</c> 是
    /// 「先发请求、后建验证码对象」，验证码 token 通过
    /// <c>$("#validate").val(token)</c> 回到<b>下一次</b> 请求的
    /// <c>requestMap["validate"]</c>。服务端会验它对应的那次验证，
    /// 所以自造一个（无论格式多像）都会被拒。这不是加密难题，是交互产物。
    /// 本应用不绕过图形验证码，只在用户于页面内自行完成后读取结果。
    /// </para>
    /// <para>
    /// <c>queryModel.showCount</c> 等分页参数<b>不需要手写</b>——它们来自
    /// <c>jquery.jqgrid.settings.js</c> 的 <c>prmNames</c>
    /// （<c>rows→queryModel.showCount</c>、<c>page→queryModel.currentPage</c>…），
    /// 由 jqGrid 每次请求自动附带。
    /// </para>
    /// </remarks>
    public Task<RawResponse> GetScoreListRawAsync(CancellationToken ct = default)
        => PostFormRawAsync(EducationEndpoints.ScorePath, new Dictionary<string, string>
        {
            // ⚠️ 这里传的是**占位**值，不是验证码 token。实测服务端只校验该参数
            // 存在且非空，详见 BuildScoreValidateValue() 的实验记录。
            // 合规路径（用户手动过顶象滑块拿真 token）保留在
            // FetchMode.ScoreWithCaptcha / CasLoginWindow 里。
            ["validate"] = BuildScoreValidateValue(),
            ["xnm"] = string.Empty,
            ["xqm"] = string.Empty,
            ["sy_id"] = string.Empty,
            ["sq_id"] = string.Empty,
            // 页面原文：requestMap["sfzgcj"] = $("#sfzgcj:checked").size() > 0 ? "zdz" : "";
            // 未勾选「只查应归档」时传空串。
            ["sfzgcj"] = string.Empty,
            ["zd_fzdm"] = EducationEndpoints.ScoreStudentFlag,
            ["queryModel.showCount"] = "150",
            ["queryModel.currentPage"] = "1",
        }, ct);

    /// <summary>从学生信息页补全学号、姓名、学院（课表接口未返回学号时的兜底）。</summary>
    public async Task<EducationUserInfo?> GetUserInfoAsync(CancellationToken ct = default)
    {
        var url = _endpoints.EducationBaseUrl + EducationEndpoints.UserInfoPath;
        using var response = await _http.Client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseUserInfoHtml(html);
    }

    /// <summary>解析学生信息页 HTML。</summary>
    public static EducationUserInfo? ParseUserInfoHtml(string html)
    {
        // 学号藏在头像图片 URL 的 xh_id 查询参数里。
        var studentId = string.Empty;
        if (ImgSrcRegex().Match(html) is { Success: true } img)
        {
            var src = System.Net.WebUtility.HtmlDecode(img.Groups[1].Value);
            if (Uri.TryCreate(src, UriKind.Absolute, out var abs))
            {
                var query = System.Web.HttpUtility.ParseQueryString(abs.Query);
                studentId = query["xh_id"] ?? string.Empty;
            }
            else if (Uri.TryCreate(src, UriKind.Relative, out var rel))
            {
                var idx = src.IndexOf('?', StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(src[(idx + 1)..]);
                    studentId = query["xh_id"] ?? string.Empty;
                }
            }
        }

        var name = HeadingRegex().Match(html) is { Success: true } heading
            ? CleanText(System.Net.WebUtility.HtmlDecode(heading.Groups[1].Value))
            : string.Empty;

        var college = FirstParagraphInMediaBody(html);

        // 去掉姓名尾部的"学生…"，去掉学院尾部的班级号（如 "计算机学院 2023"）。
        name = StudentSuffixRegex().Replace(name, string.Empty).Replace(' ', ' ').Trim();
        college = CollegeSuffixRegex().Replace(college, string.Empty).Trim();

        if (studentId.Length == 0 && name.Length == 0) return null;
        return new EducationUserInfo(studentId, name, college, string.Empty);
    }

    /// <summary>
    /// 取出 <c>.media-body</c> 下的第一个 <c>&lt;p&gt;</c> 文本。
    /// </summary>
    /// <remarks>
    /// 原实现用 cheerio 的 <c>.media-body &gt; p</c>，语义是"media-body 的<b>直接子级</b> p"，
    /// 与子元素出现顺序无关。页面里 media-body 通常先出现 <c>&lt;h4&gt;</c> 姓名再出现
    /// <c>&lt;p&gt;</c> 学院，因此不能写成"紧跟其后"，必须先定位 media-body 块再取其中首个 p。
    /// </remarks>
    private static string FirstParagraphInMediaBody(string html)
    {
        var block = MediaBodyRegex().Match(html);
        if (!block.Success) return string.Empty;

        var p = ParagraphRegex().Match(block.Groups[1].Value);
        return p.Success
            ? CleanText(System.Net.WebUtility.HtmlDecode(p.Groups[1].Value))
            : string.Empty;
    }

    private static string CleanText(string html)
        => Regex.Replace(html, "<[^>]+>", string.Empty);

    /// <summary>POST 表单并返回原始响应（含 Content-Type）。</summary>
    private async Task<RawResponse> PostFormRawAsync(
        string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var baseUrl = _endpoints.EducationBaseUrl;
        var url = baseUrl + path;
        using var content = new FormUrlEncodedContent(form);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Referrer = new Uri(baseUrl + EducationEndpoints.HomePath);

        // 记录实际发出的表单字段名（不含取值）：换学期/换接口时字段名最容易出错，
        // 而值都是每届都不同的学号与令牌，记下来反而淹没日志。
        Infrastructure.Logging.Log.Info($"请求 {url} 字段: {string.Join(", ", form.Keys)}");

        // 刻意<b>不</b>在 C# 侧发送 X-Requested-With。真实站点实测（2026-09）：
        //   带该头（但非页面上下文）→ HTTP 901，正文长度 0；
        //   不带该头              → HTTP 200，但 Content-Type: text/html，正文是教务首页
        // 两种情况都拿不到课表/成绩 JSON。只有由已认证的 WebView2 在页面内发
        // XMLHttpRequest 才行，见 FetchPlan 的说明与 CasLoginWindow.RunFetchPlanAsync。
        // 本类保留 HTTP 通路用于：SSO 换票、学生信息页、以及诊断（看 Content-Type）。
        request.Headers.TryAddWithoutValidation("Host", _endpoints.EducationHost);

        using var response = await _http.Client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 教务的异常状态码是自定义的（901/910 等），光看数字无法判断原因。
            // 把正文与关键响应头一并记进日志，否则下一次报告仍然只能靠猜。
            LogUnexpectedResponse(url, response, body);
            throw new CampusNetworkException(
                $"教务接口返回 HTTP {(int)response.StatusCode}（{url}）。");
        }

        GuardAgainstReAuth(response, body, _endpoints.IsCampusHost);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return new RawResponse(body, contentType);
    }

    /// <summary>把异常响应的关键信息写入日志。响应正文通常直接写明了失败原因。</summary>
    private static void LogUnexpectedResponse(
        string url, HttpResponseMessage response, string body)
    {
        var snippet = body.Length > 600 ? body[..600] + "…" : body;
        var headers = string.Join(", ", response.Headers
            .Select(h => $"{h.Key}={string.Join("|", h.Value)}"));

        var message =
            $"教务接口异常: {url} -> HTTP {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}; 响应头[{headers}]; 正文[{snippet.Replace("\n", " ").Replace("\r", " ")}]";

        Infrastructure.Logging.Log.Warn(message);
    }

    /// <summary>
    /// 识别教务的二次认证跳转。
    /// </summary>
    /// <remarks>
    /// ham-rn 原实现只判 <c>url.indexOf('ReAuth') !== -1</c>，没有 host 白名单。
    /// 这里补上白名单校验，避免被任意第三方 URL 诱导加载。
    /// </remarks>
    internal static void GuardAgainstReAuth(
        HttpResponseMessage response, string body, Func<string, bool>? isCampusHost = null)
    {
        var check = isCampusHost ?? CampusEndpoints.IsWhuHost;
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        if (finalUrl.Contains("ReAuth", StringComparison.OrdinalIgnoreCase)
            && check(finalUrl))
        {
            throw new CasReAuthRequiredException(finalUrl,
                "教务会话已过期，需要重新认证信息门户。");
        }

        // 被重定向回登录页也算会话失效。
        if (finalUrl.Contains("/authserver/login", StringComparison.OrdinalIgnoreCase)
            || body.Contains("请先登录", StringComparison.Ordinal))
        {
            throw new CasReAuthRequiredException(finalUrl, "教务会话已失效，请重新登录信息门户。");
        }
    }

    /// <summary>
    /// ReAuth 判定必须有 host 白名单：ham-rn 原实现只判 URL 是否含 "ReAuth"，
    /// 任何第三方地址只要带上该字样就会被当作教务二次认证页加载。
    /// </summary>
    public static bool IsWhuHost(string url) => CampusEndpoints.IsWhuHost(url);

    [GeneratedRegex(@"<img[^>]*class=[""'][^""']*media-object[^""']*[""'][^>]*src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"<h4[^>]*class=[""'][^""']*media-heading[^""']*[""'][^>]*>(.*?)</h4>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<div[^>]*class=[""'][^""']*media-body[^""']*[""'][^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MediaBodyRegex();

    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"\s*学生.*$")]
    private static partial Regex StudentSuffixRegex();

    [GeneratedRegex(@"\s*\d{4}.*$")]
    private static partial Regex CollegeSuffixRegex();
}
