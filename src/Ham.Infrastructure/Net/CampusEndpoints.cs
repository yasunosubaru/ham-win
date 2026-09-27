namespace Ham.Infrastructure.Net;

/// <summary>
/// 校园各系统的接入点配置。
/// </summary>
/// <remarks>
/// 生产环境使用默认值（<c>cas.whu.edu.cn</c> / <c>jwgl.whu.edu.cn</c>）。
/// 之所以把它抽成可注入的配置而不是散落的常量，有两个原因：
/// <list type="bullet">
/// <item>
/// <b>可测试性</b>：「拿到 CAS 会话之后」的整条链路（票交换 → 教务 SSO → 课表/成绩抓取 → 解析）
/// 此前完全没有验证过，只能靠真实凭据。要覆盖它，就必须能把端点指向本地 mock。
/// </item>
/// <item>
/// <b>host 白名单</b>：<c>ReAuth</c> 二次认证识别依赖「是否属于本校域名」这一判断。
/// mock 的 <c>127.0.0.1</c> 不在校域名下，若白名单写死就会在测试里被误判为二次认证。</item>
/// </list>
/// </remarks>
public sealed record CampusEndpoints
{
    /// <summary>生产环境端点。</summary>
    public static readonly CampusEndpoints Default = new();

    /// <summary>CAS 站点根地址，末尾不带斜杠。</summary>
    public string CasBaseUrl { get; init; } = "https://cas.whu.edu.cn";

    /// <summary>CAS 主机名。用于 Cookie 迁移与页面地址拼装。</summary>
    public string CasHost { get; init; } = "cas.whu.edu.cn";

    /// <summary>教务系统根地址，末尾不带斜杠。</summary>
    public string EducationBaseUrl { get; init; } = "https://jwgl.whu.edu.cn";

    /// <summary>教务主机名。</summary>
    public string EducationHost { get; init; } = "jwgl.whu.edu.cn";

    /// <summary>移动端登录页（相对 <see cref="CasBaseUrl"/>）。</summary>
    public string MobileLoginPath { get; init; } = "/authserver/mobile/auth?appId=985180443";

    /// <summary>教务 SSO 服务地址（<b>未</b>编码的原始形式）。</summary>
    public string EducationService { get; init; } = "https://jwgl.whu.edu.cn/sso/jznewsixlogin";

    /// <summary>判断某个 URL 是否属于校园域名。用于二次认证跳转的白名单校验。</summary>
    public Func<string, bool> IsCampusHost { get; init; } = IsWhuHost;

    /// <summary>移动端登录页的完整地址。</summary>
    public string MobileLoginUrl => CasBaseUrl + MobileLoginPath;

    /// <summary>
    /// 构造"用 CAS 会话换教务会话"的地址。
    /// </summary>
    /// <remarks>
    /// <b><c>service</c> 只允许编码一次。</b>ham-rn 中该值已预先 URL 编码一次，
    /// 若再编码一遍（双重编码）教务会解析出错误的 service 而跳转失败。
    /// </remarks>
    public string BuildSsoLoginUrl()
    {
        // 生产环境沿用 ham-rn 的预编码常量，保证与既有行为逐字节一致。
        var service = EducationService == "https://jwgl.whu.edu.cn/sso/jznewsixlogin"
            ? "https%3A%2F%2Fjwgl.whu.edu.cn%2Fsso%2Fjznewsixlogin"
            : Uri.EscapeDataString(EducationService);

        return $"{CasBaseUrl}/authserver/login?service={service}";
    }

    /// <summary>生产环境下"是否武大域名"的判定。</summary>
    public static bool IsWhuHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host.Equals("whu.edu.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".whu.edu.cn", StringComparison.OrdinalIgnoreCase);
    }
}
