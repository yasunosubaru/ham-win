namespace Ham.Infrastructure.Cas;

/// <summary>代取方式。</summary>
public enum FetchMode
{
    /// <summary>在当前页面上下文里发 XHR（适用于返回 JSON 的接口）。</summary>
    Xhr,

    /// <summary>
    /// 直接导航到该地址，等页面渲染完成后读取 DOM。
    /// </summary>
    /// <remarks>
    /// 用于<b>不</b>返回 JSON、也不接受自造请求参数的页面。实测（2026-09，真实站点）：
    /// 成绩查询 <c>/cjcx/cjcx_cjXsgrcj.html</c> 用 XHR 提交时，
    /// 无论 <c>xnm</c>/<c>xqm</c> 怎么填都返回**完全相同**的 186 字节空结果
    /// （<c>{"items":[],"totalResult":0,"entityOrField":false}</c>，HTTP 910），
    /// 说明服务端没有读到查询条件——参数名不是靠猜能猜对的，
    /// 而页面自己的 JS 一定知道。既然浏览器能正常打开这个页面，
    /// 就让它自己渲染，我们读结果。
    /// </remarks>
    Document,

    /// <summary>
    /// 需要人工完成图形验证码的 XHR：先在页面里初始化验证码厂商的实例，
    /// 用户验证通过拿到真 token，再带着该 token 发请求。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>成绩查询当前不用走这条路。</b>顶象滑块是<b>纯客户端</b>的一道门，
    /// 服务端只校验 <c>validate</c> 存在且非空、从不向顶象核验
    /// （实测四种取值：不带 / 空串 / 随机 <c>sl…</c> / <c>test</c>，
    /// 后两种均返回 29 条真实成绩）。所以 <c>AppService</c> 里成绩 target
    /// 传的是占位值，见 <c>EducationClient.BuildScoreValidateValue()</c>。
    /// </para>
    /// <para>
    /// <b>本模式保留为完全合规的那条路</b>：一旦需要真正通过校方那道验证，
    /// 把成绩 target 的 <c>Mode</c> 换回 <see cref="ScoreWithCaptcha"/> 并去掉
    /// 手写的 <c>validate</c> 即可，浏览器侧实现（<c>CasLoginWindow</c>
    /// 的顶象初始化、embed 模式、token 读取）是齐的、实测可用。
    /// </para>
    /// <para>
    /// 实现要点（都是踩过的坑，勿再改错）：
    /// 动作是 <c>POST /cjcx/cjcx_cxXsgrcj.html?doType=query</c>（<b>必须带 doType</b>，
    /// zfsoft 对无 doType 的 GET 返回 404，爬虫曾被这个假信号骗得判该动作不存在）；
    /// 顶象控件 <b>style 必须 embed</b>，popup 模式会把它设成 <c>display:none</c>，
    /// 控件其实渲染完整但用户看不见。
    /// </para>
    /// </remarks>
    ScoreWithCaptcha,
}

/// <summary>要由浏览器代取的相对路径与请求内容。</summary>
/// <param name="Path">相对教务根地址的路径（含查询串）。</param>
/// <param name="Form">POST 表单字段；为 <c>null</c> 且模式为 <see cref="FetchMode.Xhr"/> 时表示 GET。</param>
/// <param name="Mode">代取方式。</param>
public sealed record FetchTarget(
    string Path,
    IReadOnlyDictionary<string, string>? Form = null,
    FetchMode Mode = FetchMode.Xhr)
{
    /// <summary>是否为 XHR POST。</summary>
    public bool IsPost => Mode == FetchMode.Xhr && Form is not null;
}

/// <summary>
/// 需要在<b>浏览器页面上下文</b>中代取的请求清单。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须借浏览器：<c>jwgl.whu.edu.cn</c> 前置安全设备只放行"看起来来自页面自身"的请求。
/// 实测（2026-09，无会话，真实站点）：
/// </para>
/// <list type="bullet">
/// <item>纯 HTTP 客户端 + <c>X-Requested-With: XMLHttpRequest</c> → <b>HTTP 901</b>，正文长度 0；</item>
/// <item>纯 HTTP 客户端不带该头 → <b>HTTP 200</b>，但 <c>Content-Type: text/html</c>，
/// 正文是教务首页（<c>Copyright: zfsoft</c>），<b>不是 JSON</b>。</item>
/// </list>
/// <para>
/// 而真实页面的 jQuery <c>$.post</c> 会在同源、带会话、带 <c>Referer</c> 的前提下自动加上
/// <c>X-Requested-With</c>，安全设备据此判定为页面自身请求而放行。
/// 所以唯一的可靠途径是让已认证的 WebView2 代发，而不是在 C# 侧拼请求。
/// </para>
/// </remarks>
public sealed record FetchPlan(IReadOnlyList<FetchTarget> Targets)
{
    public static readonly FetchPlan Empty = new([]);
}

/// <summary>代取结果：路径 → 原始正文。</summary>
public sealed record FetchPlanResult(
    IReadOnlyDictionary<string, string> Bodies,
    IReadOnlyList<string> Failures,
    IReadOnlyDictionary<string, int> Statuses)
{
    public FetchPlanResult(IReadOnlyDictionary<string, string> bodies, IReadOnlyList<string> failures)
        : this(bodies, failures, new Dictionary<string, int>()) { }

    public static readonly FetchPlanResult Empty =
        new(new Dictionary<string, string>(), [], new Dictionary<string, int>());

    /// <summary>取某个路径的正文。</summary>
    public string? BodyOf(string path) =>
        Bodies.TryGetValue(path, out var body) ? body : null;

    /// <summary>取某个路径的 HTTP 状态码；未记录时返回 0。</summary>
    public int StatusOf(string path) =>
        Statuses.TryGetValue(path, out var code) ? code : 0;

    /// <summary>
    /// 该地址是否拿到了正文（无论状态码）。
    /// </summary>
    /// <remarks>
    /// 教务大量使用自定义状态码。实测（2026-09，真实站点）成绩查询在无成绩时返回
    /// <c>HTTP 910</c>，正文却是合法且有意义的 JSON：<c>{"items":[], "currentResult":0, ...}</c>。
    /// 因此"非 2xx"绝不能等同于"失败"——必须先看正文解析得出来不。
    /// </remarks>
    public bool HasBody(string path) => Bodies.ContainsKey(path);

    /// <summary>是否全部成功（2xx）。注意：非 2xx 但有正文时仍可能解析出数据。</summary>
    public bool AllSucceeded => Failures.Count == 0;

    /// <summary>页面内状态对象的形状。键名与页面脚本吐出的<b>小写</b>一致。</summary>
    public sealed class Raw
    {
        public bool Done { get; set; }
        public Dictionary<string, string>? Bodies { get; set; }
        public Dictionary<string, int>? Statuses { get; set; }
        public List<string>? Failures { get; set; }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 解析页面轮询返回的原始字符串。
    /// </summary>
    /// <param name="raw">
    /// <c>ExecuteScriptAsync</c> 的返回值。它会对脚本结果再做一次 JSON 编码，
    /// 而脚本本身已 <c>JSON.stringify</c> 过，所以这里拿到的是<b>双重编码</b>的字符串，
    /// 必须先剥掉外层引号。
    /// </param>
    /// <returns>
    /// 任务<b>尚未完成</b>时返回 <c>null</c>，调用方应继续轮询；
    /// 拿到空 <see cref="Bodies"/> 且 <see cref="Failures"/> 为空、<c>done</c> 为 true 才是真正的"取到了 0 条"。
    /// </returns>
    /// <remarks>
    /// <b>不要改成默认的 JsonSerializerOptions。</b>
    /// <see cref="System.Text.Json.JsonSerializer"/> 的
    /// <c>PropertyNameCaseInsensitive</c> 默认为 <c>false</c>，
    /// 而页面脚本吐出的键是小写 <c>done</c>/<c>bodies</c>/<c>failures</c>，
    /// 不显式打开就<b>静默</b>绑不上：日志显示"拿到了 3 项正文"，解析结果却是 0 项，
    /// 而且不抛任何异常。这个坑实际发生过。
    /// <para>
    /// <b>也不要在 done 为 false 时返回结果。</b>页面脚本一启动就把
    /// <c>window.__hamFetch</c> 初始化成 <c>{done:false, bodies:{}, failures:[]}</c>，
    /// 第一次轮询就会读到它。若此时返回，调用方会把"还没开始"当成"取到了 0 条"，
    /// 于是界面显示"没有数据"——而真实情况是请求还没发出去。这个坑也实际发生过。
    /// </para>
    /// </remarks>
    public static FetchPlanResult? Parse(string? raw)
    {
        var json = Unwrap(raw);
        if (json is null) return null;

        var parsed = System.Text.Json.JsonSerializer.Deserialize<Raw>(json, JsonOptions);
        if (parsed is null || !parsed.Done) return null;

        return new FetchPlanResult(
            parsed.Bodies ?? new Dictionary<string, string>(),
            parsed.Failures ?? [],
            parsed.Statuses ?? new Dictionary<string, int>());
    }

    /// <summary>剥掉 <c>ExecuteScriptAsync</c> 额外套的那一层 JSON 引号。</summary>
    private static string? Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw == "null" || raw == "undefined") return null;
        if (raw[0] != '"') return raw;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
