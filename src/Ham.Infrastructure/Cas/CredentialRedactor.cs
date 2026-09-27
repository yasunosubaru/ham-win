namespace Ham.Infrastructure.Cas;

/// <summary>
/// 日志脱敏器：把凭据从任何要写入日志的文本里抹掉。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它，而不是"注意不要打印密码"。</b>
/// 登录流程里密码有两个可能漏进日志的通道，都不是显式打印：
/// </para>
/// <list type="number">
/// <item><b>异常消息。</b>预填脚本的源码里含明文密码，而
/// <c>Log.Write</c> 会把 <c>ex.ToString()</c>（含堆栈）整段写进文件。
/// 只要 WebView2 或 JSON 反序列化在异常里回显了脚本文本，密码就落盘了。
/// 这种情况无法靠代码审查杜绝——异常内容不受我们控制。</item>
/// <item><b>URL。</b>少数 SSO 会把票据放进 query 或 fragment，
/// 而流程里有多处记录当前地址。</item>
/// </list>
/// <para>
/// 与其依赖每一处调用点都小心，不如在这条链的<b>出口</b>统一过滤：
/// 无论上游写了什么，落盘前一律替换。这样新增日志点也不会漏。
/// </para>
/// <para>
/// <b>注意这不能替代其它防护。</b>脚本一旦注入，密码就存在于教务主机的
/// <c>window.__hamPass</c> 上（见 <see cref="EducationSession.BuildPrefillScript"/>），
/// 那是浏览器沙箱内的事，日志脱敏管不到。
/// </para>
/// </remarks>
public sealed class CredentialRedactor
{
    /// <summary>替换后的占位符。</summary>
    public const string Mask = "***";

    private readonly string[] _secrets;

    /// <summary>
    /// 用需要隐藏的值构造。
    /// </summary>
    /// <param name="secrets">
    /// 逐个传入密码、学号等。会自动忽略空值与过短的片段——
    /// 太短的值（例如一位数字）做全局替换会把日志搅得一团糟。
    /// </param>
    public CredentialRedactor(params string?[] secrets)
    {
        _secrets = secrets
            .Where(s => !string.IsNullOrEmpty(s))
            // 编译器无法从 Where 里推断出非空，这里显式收窄一次。
            .Select(s => s!)
            .Where(s => s.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>是否没有任何需要隐藏的内容（此时脱敏是零成本的直通）。</summary>
    public bool IsEmpty => _secrets.Length == 0;

    /// <summary>把文本里的所有凭据替换成占位符。</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text) || _secrets.Length == 0) return text ?? string.Empty;

        var s = text;
        foreach (var secret in _secrets) s = s.Replace(secret, Mask, StringComparison.Ordinal);
        return s;
    }

    /// <summary>把异常整体（含消息与堆栈）脱敏。</summary>
    /// <remarks>
    /// 只脱敏 <see cref="Exception.Message"/> 是不够的：堆栈里也可能出现
    /// 带着脚本文本的参数值，而 <c>ex.ToString()</c> 会把两者都输出。
    /// </remarks>
    public string Scrub(Exception? ex)
        => ex is null ? string.Empty : Scrub(ex.ToString());
}
