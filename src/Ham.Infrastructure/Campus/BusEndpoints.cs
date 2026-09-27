namespace Ham.Infrastructure.Campus;

/// <summary>
/// 校巴（<c>bus.whu.edu.cn</c>）接口地址与常量。
/// </summary>
/// <remarks>
/// 契约来源：读取线上 SPA 的 <c>app.js</c>（<c>/mobile/static/js/app.*.js</c>）后
/// 从中还原，不是猜的，也不是抄文档。
/// <para><b>最重要的两点：</b></para>
/// <list type="number">
/// <item>
/// <b>不需要登录。</b><c>/mobile/</c> 会 302 到统一身份认证，但
/// <c>/mobile/index.html</c> 直接返回 200，<b>不在 CAS 保护范围内</b>。
/// 打开它，SPA 会自己带签名把数据全拉下来。
/// 这意味着校巴是唯一一个既能用真实数据、又完全不碰学生凭据的功能。
/// </item>
/// <item>
/// <b>不要用 HttpClient 直接打接口。</b> 路径是
/// <c>base64(RSA签名(时间戳 + "/" + 真实路径)).replace("/", "*")</c>，
/// 而私钥字面量在前端是<b>故意截断的</b>，要靠 8 个 base64 片段才拼得全。
/// 与其在 .NET 里重实现这套加密，正确做法是让 WebView2 打开页面、由页面自己签名，
/// 我们只旁路记录它的 XHR 响应——与课表/成绩已验证的做法同源。
/// </item>
/// </list>
/// </remarks>
public static class BusEndpoints
{
    public const string Host = "bus.whu.edu.cn";
    public const string BaseUrl = "https://bus.whu.edu.cn";

    /// <summary>
    /// SPA 入口。**必须用这个带 index.html 的路径**，
    /// 换成 <c>/mobile/</c> 会被 CAS 拦下。
    /// </summary>
    public const string PageUrl = "https://bus.whu.edu.cn/mobile/index.html";

    /// <summary>接口根。</summary>
    public const string ApiBase = "https://bus.whu.edu.cn/interface/whubus/weben";

    /// <summary>租户/机构前缀，线上固定为 10486。</summary>
    public const string Tenant = "10486";

    /// <summary>签名之前的真实路径模板。</summary>
    public static class Raw
    {
        /// <summary>线路详情（站序、票价、首末班）。</summary>
        public static string Line(string lineId) => $"{Tenant}/line/{lineId}";

        /// <summary>行驶轨迹折线。</summary>
        public static string LinePath(string lineId) => $"{Tenant}/linePath/{lineId}";

        /// <summary>车辆实时位置。</summary>
        public static string Bus(string lineId) => $"{Tenant}/line/{lineId}/bus";
    }

    /// <summary>成功标志：外壳 JSON 里 <c>resultCode</c> 为字符串 "1"。</summary>
    public const string SuccessCode = "1";

    /// <summary>
    /// 实测线上存在的线路。
    /// </summary>
    /// <remarks>
    /// 前端 <c>app.js</c> 里写死的 <c>lineList</c> 是
    /// <c>[{name:"1号线",id:"123456"}, {name:"2号线",id:"123456"}, {name:"5号线",id:"123456"}]</c>
    /// ——三个 id 都是同一个占位值 <c>123456</c>，**不能直接采信**。
    /// 下面这三个是抓真实接口响应时观察到的 id。
    /// <para>
    /// 列表里缺哪条线路、补了什么新线路，取决于武大实际开行情况，
    /// 所以这只是<b>已知可用</b>的集合，不是权威名录。
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<KnownLine> Lines =
    [
        new("10486-50-0", "1号线", "50"),
        new("10486-55-0", "2号线", "55"),
        new("10486-56-0", "5号线", "56"),
    ];

    /// <summary>一条已知可用的线路标识。</summary>
    /// <param name="Id">调接口用的 lineId。</param>
    /// <param name="Name">对外名称。</param>
    /// <param name="Number">内部线路号。</param>
    public sealed record KnownLine(string Id, string Name, string Number);
}
