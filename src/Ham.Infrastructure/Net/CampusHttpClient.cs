using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Ham.Infrastructure.Net;

/// <summary>
/// 校园网络请求客户端工厂。
/// </summary>
/// <remarks>
/// 关键设计：每个客户端实例持有<b>自己的</b> <see cref="CookieContainer"/> 并保持长生命周期。
/// 教务的课表/成绩接口<b>不显式携带 Cookie</b>，完全依赖先前的 SSO 在同一 cookie jar 中
/// 种下的 jwgl.whu.edu.cn 会话；若每次请求新建 HttpClient 会话，会话随即丢失导致 302 跳登录页。
/// WebView2 的 cookie jar 与 HttpClient 的相互独立，因此 CAS 登录后需显式做一次 Cookie 迁移。
/// </remarks>
public sealed class CampusHttpClient : IDisposable
{
    /// <summary>与 ham-rn 一致的桌面浏览器 UA，教务系统对异常 UA 会拒绝服务。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _client;
    private bool _disposed;

    /// <param name="userAgent">请求 UA。为 <c>null</c> 时使用 <see cref="DesktopUserAgent"/>。</param>
    /// <param name="timeoutSeconds">单次请求超时秒数。</param>
    /// <param name="endpoints">接入点配置，供 <see cref="ClearCookies"/> 等使用。</param>
    public CampusHttpClient(
        string? userAgent = null,
        int timeoutSeconds = 45,
        CampusEndpoints? endpoints = null)
    {
        Endpoints = endpoints ?? CampusEndpoints.Default;
        Cookies = new CookieContainer();
        _handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
        };

        _client = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? DesktopUserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        _client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    /// <summary>接入点配置。</summary>
    public CampusEndpoints Endpoints { get; }

    /// <summary>会话 Cookie 罐。跨请求保持，登录态依赖于此。</summary>
    public CookieContainer Cookies { get; }

    public HttpClient Client => _client;

    /// <summary>把 WebView2 中某个域下的 Cookie 迁移进本客户端的 jar。</summary>
    public void SeedCookies(IEnumerable<Cookie> cookies)
    {
        foreach (var cookie in cookies)
        {
            Cookies.Add(cookie);
        }
    }

    /// <summary>从 jar 中取出指定主机的全部 Cookie，串成请求头值（无空格分隔）。</summary>
    public string BuildCookieHeader(string host)
    {
        var values = Cookies.GetCookies(new Uri($"https://{host}/"))
            .Cast<Cookie>()
            .Select(c => $"{c.Name}={c.Value}")
            .ToArray();

        return string.Join("; ", values);
    }

    /// <summary>
    /// 注销会话：把 jar 中校园各系统的 Cookie 全部标记为过期。
    /// </summary>
    /// <param name="endpoints">
    /// 要清理的主机。默认清理 CAS 与教务两个主机。
    /// </param>
    /// <remarks>
    /// 不能直接换一个新的 <see cref="CookieContainer"/>——它已被 <see cref="HttpClientHandler"/>
    /// 持有，替换后 <see cref="Client"/> 仍用旧罐，表现为"退出后仍处于登录态"。
    /// <para>
    /// 必须<b>按主机分别</b>清理：CookieContainer 以域名为键，只标记
    /// <c>whu.edu.cn</c> 下的 Cookie 不会清掉 <c>cas.whu.edu.cn</c> / <c>jwgl.whu.edu.cn</c> 的
    /// 会话，注销后依然保持登录态。
    /// </para>
    /// </remarks>
    public void ClearCookies(CampusEndpoints? endpoints = null)
    {
        var cfg = endpoints ?? Endpoints;
        var seeds = new[] { cfg.CasBaseUrl, cfg.EducationBaseUrl };

        // 用各系统自己的 base URL 作种子，CookieContainer 才能命中对应域下的 Cookie。
        foreach (var baseUrl in seeds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var seed)) continue;

            foreach (var cookie in Cookies.GetCookies(seed).Cast<Cookie>().ToList())
            {
                cookie.Expired = true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _handler.Dispose();
    }
}

/// <summary>统一的网络异常类型，便于 UI 区分"网络不通"与"凭据错误"。</summary>
public sealed class CampusNetworkException : Exception
{
    public CampusNetworkException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>需要重新进行 CAS 认证时抛出，UI 应据此引导用户重新登录信息门户。</summary>
public sealed class CasReAuthRequiredException : Exception
{
    public CasReAuthRequiredException(string reAuthUrl, string message)
        : base(message)
        => ReAuthUrl = reAuthUrl;

    /// <summary>教务返回的二次认证地址，交由 WebView 打开以换发新会话。</summary>
    public string ReAuthUrl { get; }
}
