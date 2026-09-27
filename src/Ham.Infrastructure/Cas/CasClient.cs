using System.Net;
using System.Text.RegularExpressions;
using Ham.Core.Models;
using Ham.Infrastructure.Education;
using Ham.Infrastructure.Net;

namespace Ham.Infrastructure.Cas;

/// <summary>超星 CAS 移动端登录相关地址常量。</summary>
public static class CasEndpoints
{
    public const string Host = "cas.whu.edu.cn";

    /// <summary>移动端登录页。appId 为武大超星 CAS 应用标识。</summary>
    public const string MobileLoginUrl = "https://cas.whu.edu.cn/authserver/mobile/auth?appId=985180443";

    /// <summary>
    /// 旧版移动端成功落地页路径，URL 中携带 <c>mobile_token</c>。
    /// </summary>
    /// <remarks>
    /// <b>本机实测（2026-09）在武大当前部署的登录页中并不存在</b>（既无 <c>default.html</c>
    /// 也无 <c>mobile_token</c>）。保留仅为兼容历史部署与旧版断言，<b>不可作为登录成功判据</b>。
    /// 现行判据见 <see cref="CasClient.IsLoginSuccess"/>。
    /// </remarks>
    public const string MobileSuccessMarker = "/mobile/default.html";

    /// <summary>标准 CAS 票据的查询参数名，登录成功后随 callback 一起出现。</summary>
    public const string TicketParam = "ticket";

    /// <summary>教务 SSO 服务地址。ham-rn 中该值<b>已预先 URL 编码一次</b>，不可重复编码。</summary>
    public const string EducationServiceEncoded = "https%3A%2F%2Fjwgl.whu.edu.cn%2Fsso%2Fjznewsixlogin";

    public const string EducationServiceDecoded = "https://jwgl.whu.edu.cn/sso/jznewsixlogin";

    /// <summary>构造 SSO 登录地址。</summary>
    public static string BuildSsoLoginUrl() =>
        $"https://{Host}/authserver/login?service={EducationServiceEncoded}";

    /// <summary>隐私政策地址（登录页需外链打开）。</summary>
    public const string PrivacyPolicyUrl = "https://homewh.chaoxing.com/agree/privacyPolicy?appId=1000028";
}

/// <summary>登录结果：Cookie 集合 + 由浏览器代取到的原始正文。</summary>
public sealed record CasLoginOutcome(
    IReadOnlyList<CasCookie> Cookies,
    FetchPlanResult Payloads)
{
    public static readonly CasLoginOutcome Empty = new([], FetchPlanResult.Empty);
}

/// <summary>从浏览器内核取回的一条 Cookie，附带它所属的主机名。</summary>
/// <remarks>
/// 必须带主机名：桌面端 SSO 流程会同时在 <c>cas.whu.edu.cn</c>（CASTGC/JSESSIONID）
/// 与 <c>jwgl.whu.edu.cn</c>（教务会话）两侧留下 Cookie，而 <see cref="CookieContainer"/>
/// 以域名为键——不区分域名就会把教务会话种到 CAS 域下，SSO 随即失效。
/// </remarks>
public sealed record CasCookie(string Host, string Name, string Value);

/// <summary>
/// 信息门户（CAS）认证客户端。
/// </summary>
/// <remarks>
/// 登录动作本身需要执行远端页面的 JavaScript（超星 CAS 的口令加密逻辑在页面内，
/// 仓库中无法获得密钥），因此由宿主用 WebView2 承载；本类负责：
/// <list type="number">
/// <item>定义登录地址与成功判定规则（纯逻辑，可单测）；</item>
/// <item>把 WebView2 中取得的 Cookie 迁移进 <see cref="CampusHttpClient"/>；</item>
/// <item>完成到教务系统的 SSO 会话换取，并识别二次认证。</item>
/// </list>
/// </remarks>
public sealed partial class CasClient
{
    private readonly CampusHttpClient _http;
    private readonly CampusEndpoints _endpoints;

    public CasClient(CampusHttpClient http, CampusEndpoints? endpoints = null)
    {
        _http = http;
        _endpoints = endpoints ?? CampusEndpoints.Default;
    }

    /// <summary>
    /// 判定某个 URL 是否代表 CAS 登录已完成，并从中取出票据。
    /// </summary>
    /// <param name="url">WebView 的当前地址（可能含百分号编码）。</param>
    /// <param name="ticket">标准 CAS 票据 <c>ticket=ST-…</c>，若不存在则退回 <c>mobile_token</c>。</param>
    /// <remarks>
    /// <b>本机实测（2026-09）武大 CAS 有两套互不兼容的登录流程：</b>
    /// <list type="number">
    /// <item>
    /// <b>移动端</b>（<c>/authserver/mobile/auth?appId=985180443</c>）→ 表单
    /// <c>&lt;form id="pwdFromId" action="/authserver/login"&gt;</c> 整页 POST → 302 到
    /// <c>/authserver/mobile/default.html#mobile_token=…</c>。
    /// <para>
    /// <b>实测证明这条路走不通</b>：成功后浏览器里只有
    /// <c>happyVoyage</c>、<c>MULTIFACTOR_BROWSER_FINGERPRINT</c>、
    /// <c>…CookieLocaleResolver.LOCALE</c> 三条 Cookie，
    /// <b>没有 CASTGC、也没有 JSESSIONID</b>，因此无法换取教务系统的 SSO 会话。
    /// <c>happyVoyage</c> 是超星移动端 App 的标记——这条流程是为 App 设计的，不是给第三方客户端用的。
    /// </para>
    /// <para>
    /// 另注意 <c>mobile_token</c> 出现在 URL <b>片段（<c>#</c>）</b>里而非查询串，
    /// 锚定 <c>[?&amp;]</c> 的正则匹配不到，必须把 <c>#</c> 一并算作分隔符。
    /// 该值是 Base64，含 <c>/</c>、<c>+</c>、<c>=</c>，不能按 URL 组件解码。
    /// </para>
    /// </item>
    /// <item>
    /// <b>桌面端</b>（<c>/authserver/login?service=&lt;编码后的教务 SSO 地址&gt;</c>）→ 同一张
    /// <c>pwdFromId</c> 表单 → 成功后种下 CASTGC 并 302 到该 service，URL 带 <c>ticket=ST-…</c>。
    /// 这条流程才能真正拿到教务会话，<b>本项目改用它</b>。
    /// </para>
    /// </item>
    /// </list>
    /// <para>
    /// 最可靠的判据是 <b>是否已落到教务主机</b>（或教务会话 Cookie 是否出现），
    /// 见 <c>CasLoginWindow</c>；本方法仅作快速路径。
    /// </para>
    /// <para>
    /// <b>只解一层百分号编码</b>：先匹配原始 URL，再匹配 <c>Uri.UnescapeDataString</c> 后的结果
    /// （ham-rn 的 <c>decodeURIComponent</c> 同样只解一层）。刻意<b>不做</b>多轮解码——
    /// 多轮会把 <c>service</c> 参数里层层嵌套的内容也拉平，反而可能凭空造出
    /// <c>ticket=</c> 造成误判。
    /// </para>
    /// </remarks>
    public static bool IsLoginSuccess(string? url, out string ticket)
    {
        ticket = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (TryMatchTicket(url, out ticket)) return true;

        // 单层解码后再试一次，覆盖票据参数被百分号编码的情形。
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(url);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return decoded != url && TryMatchTicket(decoded, out ticket);
    }

    private static bool TryMatchTicket(string url, out string ticket)
    {
        ticket = string.Empty;

        // 标准 CAS：?ticket=ST-xxx
        var ticketMatch = TicketRegex().Match(url);
        if (ticketMatch.Success)
        {
            ticket = ticketMatch.Groups[1].Value;
            return ticket.Length > 0;
        }

        // 旧版移动端流程：?mobile_token=xxx（保留以兼容历史部署）
        var tokenMatch = MobileTokenRegex().Match(url);
        if (tokenMatch.Success)
        {
            ticket = tokenMatch.Groups[1].Value;
            return ticket.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// 判断学号长度是否落在超星 CAS 移动端页面常见的接受范围（13 位或 8 位）。
    /// </summary>
    /// <remarks>
    /// <b>只能作为提示，不能作为拦截条件。</b>ham-rn 把该检查写进注入脚本并直接阻断提交，
    /// 但那是<b>客户端</b>行为而非服务端权威规则；武大本科常见 12 位学号。
    /// 一旦误拦合法学号，用户将完全无法登录，因此本项目只提示不阻断，
    /// 最终以服务端是否接受为准。
    /// </remarks>
    public static bool IsLikelyAcceptedStudentIdLength(string studentId)
        => studentId.Length is 13 or 8;

    /// <summary>
    /// 把从 WebView2 取到的 Cookie 迁移进 HTTP 客户端的 jar。
    /// </summary>
    /// <param name="cookies">每条 Cookie 自带所属主机名。</param>
    public void SeedCookies(IEnumerable<CasCookie> cookies)
    {
        var list = cookies
            .Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Host))
            .Select(c => new Cookie(c.Name, c.Value, "/", c.Host))
            .ToList();

        if (list.Count > 0) _http.SeedCookies(list);
    }

    /// <summary>生成供 WebView 注入的本地化脚本（对齐 ham-rn 的 CasMobileLoginView 行为）。</summary>
    public static string BuildLocalizationScript() => """
        (function () {
          if (window.__hamWrapped) { return; }
          window.__hamWrapped = true;

          // 超星 CAS 的表单 id 拼写为 pwdFromId（少一个 m），必须按原样匹配。
          function patch() {
            var form = document.getElementById('pwdFromId');
            if (!form) { return false; }

            var user = document.getElementById('username');
            var pass = document.getElementById('password');
            var submit = document.getElementById('login_submit');

            if (user && pass) {
              // 学号长度：13 位或 8 位最常见。
              // 刻意只提示、不阻断：官方实现是客户端硬拦截，但服务端才是权威；
              // 误拦合法学号会导致用户彻底无法登录。
              var warned = false;
              user.addEventListener('input', function () {
                var v = (user.value || '').trim();
                if (v.length !== 0 && v.length !== 13 && v.length !== 8) {
                  if (!warned) {
                    warned = true;
                    if (window.alertBox) {
                      window.alertBox('提示：学号通常为 13 位或 8 位，当前为 ' + v.length
                        + ' 位。若提交失败请确认学号是否正确。');
                    }
                  }
                } else {
                  warned = false;
                }
              }, true);

              pass.addEventListener('input', function () {
                try {
                  window.ReactNativeWebView.postMessage(JSON.stringify({
                    type: 'postMessage',
                    data: { username: user.value, password: pass.value, type: 'passwordChange' }
                  }));
                } catch (e) { /* 宿主未注入时忽略 */ }
              }, true);
            }

            ['.social-aut-login', '.combine_options_footer', '.ge-wrapper-footer, .wjmm',
             '#retrievePassPwdId, #retrievePassId'].forEach(function (sel) {
              document.querySelectorAll(sel).forEach(function (el) { el.remove(); });
            });

            var lang = document.getElementById('languages') || document.querySelector('.language-wrap');
            if (lang) { lang.style.display = 'none'; }

            var header = document.querySelector('header');
            if (header) { header.textContent = '武汉大学信息门户'; }

            var remember = document.querySelector('#myRememberMe .change-color');
            if (remember) { remember.textContent = '记住我'; }

            // 覆写提示文案，使其对用户可读。
            if (typeof window.showTips === 'function') {
              var original = window.showTips;
              window.showTips = function (msg) {
                var text = String(msg || '');
                if (text.indexOf('请先阅读并同意隐私协议') >= 0) { return original.call(this, '请先阅读并同意隐私协议'); }
                if (text.indexOf('该帐号已经过期') >= 0) { return original.call(this, '该账号已过期，请前往信息门户重置密码'); }
                if (text.indexOf('您提供的用户名或者密码有误') >= 0) { return original.call(this, '你提供的用户名或密码有误'); }
                return original.apply(this, arguments);
              };
            }
            return true;
          }

          var timer = setInterval(function () {
            if (patch()) { clearInterval(timer); }
          }, 200);
          setTimeout(function () { clearInterval(timer); }, 2000);
        })();
        """;

    /// <summary>
    /// 用 CAS Cookie 换取教务系统会话。
    /// </summary>
    /// <exception cref="CasReAuthRequiredException">教务要求二次认证。</exception>
    /// <exception cref="CampusNetworkException">Cookie 无效或网络不可达。</exception>
    public async Task LoginToEducationAsync(CancellationToken ct = default)
    {
        var url = _endpoints.BuildSsoLoginUrl();
        using var response = await _http.Client.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        if (finalUrl.Contains("ReAuth", StringComparison.OrdinalIgnoreCase)
            && _endpoints.IsCampusHost(finalUrl))
        {
            throw new CasReAuthRequiredException(finalUrl, "信息门户会话已过期，需要重新认证。");
        }

        if (body.Contains(EducationEndpoints.SuccessMarker, StringComparison.Ordinal))
            return;

        // 与 ham-rn 一致：从 HTML 中抽取 dlktsxx 作为可读错误原因。
        var reason = ExtractErrorMessage(body)
            ?? "信息门户登录失败，请前往「我的 → 信息门户设置」重新登录。";

        throw new CampusNetworkException(reason);
    }

    /// <summary>从教务错误页抽取 <c>var dlktsxx="…";</c> 中的提示文本。</summary>
    public static string? ExtractErrorMessage(string html)
    {
        if (DlktRegex().Match(html) is { Success: true } match)
        {
            var value = match.Groups[1].Value.Trim();
            if (value.Length > 0) return WebUtility.HtmlDecode(value);
        }

        return null;
    }

    [GeneratedRegex(@"[?&#]ticket=([^&\s""'#]+)")]
    private static partial Regex TicketRegex();

    [GeneratedRegex(@"[?&#]mobile_token=([^&\s""'#]+)")]
    private static partial Regex MobileTokenRegex();

    [GeneratedRegex(@"var\s+dlktsxx\s*=\s*[""'](.*?)[""']\s*;")]
    private static partial Regex DlktRegex();
}
