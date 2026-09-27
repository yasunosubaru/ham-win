using System.Diagnostics;

using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Logging;
using Ham.Infrastructure.Net;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Ham.Ui;

/// <summary>
/// CAS 登录窗口（WinUI 3）。
/// </summary>
/// <remarks>
/// <para>
/// 流程逻辑<b>不在这里</b>——全部在 <see cref="EducationSession"/> 里，那份实现与 UI 框架无关。
/// 本类只负责三件事：把 WebView2 摆出来、把浏览器能力适配成
/// <see cref="IEducationLoginHost"/>、把状态文案显示出来。
/// </para>
/// <para>
/// 这样切分是有原因的：这段流程里叠了好几层竞态护栏（详见 <see cref="EducationSession"/>
/// 的注释），WPF 与 WinUI 3 各写一份必然分叉，而分叉的那份没人测过。
/// </para>
/// </remarks>
public sealed partial class LoginWindow : Window, IEducationLoginHost
{
    private readonly CampusEndpoints _endpoints;
    private readonly FetchPlan _plan;
    private readonly string? _user;
    private readonly string? _password;

    private EducationSession? _session;
    private CancellationTokenSource? _cts;
    private CredentialRedactor _redactor = new();
    private bool _closing;

    /// <summary>WebView2 的独立数据目录。</summary>
    /// <remarks>
    /// 必须显式指定，不能用默认目录：
    /// <list type="bullet">
    /// <item>unpacked 运行时不允许把用户数据放在 exe 旁边，所以只能放 <c>%LOCALAPPDATA%</c>；</item>
    /// <item>校巴抓取也用 WebView2，两边共用默认 profile 会互相带 cookie；</item>
    /// <item>独立 profile 让 <c>MULTIFACTOR_BROWSER_FINGERPRINT</c> 之类的 Cookie
    /// 跨次运行保持稳定，减少被二次验证拦下的概率。</item>
    /// </list>
    /// </remarks>
    private static string WebUserDataFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ham", "webview");

    public LoginWindow(CampusEndpoints endpoints, FetchPlan plan, string? user, string? password)
    {
        _endpoints = endpoints;
        _plan = plan;
        _user = user;
        _password = password;

        InitializeComponent();
        Root.Background = null;
        TrySetMica();
        TrySetIcon();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Heading);
    }

    private void TrySetMica()
    {
        try { SystemBackdrop = new MicaBackdrop(); }
        catch { /* Windows 10 上没有 Mica，功能不受影响 */ }
    }

    /// <summary>
    /// 窗口图标。
    /// </summary>
    /// <remarks>
    /// unpackaged 没有包身份，<c>ApplicationIcon</c> 只改 exe 资源；
    /// 标题栏与任务栏必须显式走 <c>AppWindow.SetIcon</c>。
    /// 登录窗口同样需要，否则同步时弹出的窗口会是系统默认图标。
    /// </remarks>
    private void TrySetIcon()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ham-win.ico");
            if (File.Exists(icon)) appWindow.SetIcon(icon);
        }
        catch (Exception ex)
        {
            Log.Error("设置登录窗口图标失败", ex);
        }
    }

    string IEducationLoginHost.CurrentUrl
    {
        get
        {
            // 首航之前 Source 为 null；导航过程中偶尔也会短暂为 null。
            // 统一成空串，交给 EducationSession 处理（它对空地址有专门分支）。
            try { return Web.Source?.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    // ExecuteScriptAsync 返回的是 WinRT 的 IAsyncOperation<string>，
    // 必须 await 一次再返回，不能直接把 IAsyncOperation 当 Task 递出去。
    async Task<string> IEducationLoginHost.ExecuteAsync(string script)
        => await Web.CoreWebView2.ExecuteScriptAsync(script);

    async Task<IReadOnlyList<CasCookie>> IEducationLoginHost.ReadCookiesAsync(CampusEndpoints ep)
    {
        var all = new List<CasCookie>();

        foreach (var baseUrl in new[] { ep.CasBaseUrl, ep.EducationBaseUrl })
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var seed)) continue;

            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(baseUrl);
            foreach (var c in cookies)
            {
                // 空值 Cookie 没用，直接丢。
                // Host 取自 seed 而不是 c.Domain：这样 a.jwgl.whu.edu.cn 上的 Cookie
                // 也会被归到 jwgl.whu.edu.cn，下游按域名匹配才稳。
                if (string.IsNullOrEmpty(c.Value)) continue;
                all.Add(new CasCookie(seed.Host, c.Name, c.Value));
            }
        }

        return all
            .GroupBy(c => (c.Host, c.Name))
            .Select(g => g.First())
            .ToList();
    }

    Task IEducationLoginHost.OpenExternalAsync(Uri uri)
    {
        OpenExternal(uri);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 交给系统浏览器打开，失败不影响登录。
    /// </summary>
    /// <remarks>
    /// 刻意做成 void 而不是返回 Task：调用点是事件处理器，
    /// 在 lambda 里写 <c>_ = ...</c> 会被当成给 lambda 的 sender 参数赋值
    /// （lambda 的 <c>_</c> 是参数名，不是丢弃符），编译直接报错。
    /// </remarks>
    private void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 打不开外部浏览器不该让登录失败
        }
    }

    /// <summary>
    /// 打开登录页并跑完整流程。
    /// </summary>
    /// <remarks>
    /// 窗口由调用方 <c>Activate</c>，本方法只负责把流程跑完并返回结果。
    /// </remarks>
    public async Task<CasLoginOutcome> RunAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 脱敏器在链的出口拦截：无论上游日志写了什么（尤其是异常对象，
        // 它不受我们控制、可能回显含明文密码的脚本文本），落盘前一律替换。
        _redactor = new CredentialRedactor(_password, _user);

        _session = new EducationSession(
            _endpoints,
            _plan,
            this,
            (msg, ex) => Log.Error(
                "CAS 登录: " + _redactor.Scrub(msg),
                // 异常单独脱敏：Log.Write 会把 ex.ToString()（含堆栈）整段写入。
                new RedactedException(_redactor.Scrub(msg), _redactor.Scrub(ex))),
            text => SetStatus(_redactor.Scrub(text)));

        try
        {
            await StartWebAsync().ConfigureAwait(true);

            _session.Start();
            var outcome = await _session.RunAsync(_cts.Token).ConfigureAwait(true);

            SetStatus(outcome.Cookies.Count > 0
                ? $"完成：导出 Cookie {outcome.Cookies.Count} 项、代取 {outcome.Payloads.Bodies.Count} 项正文。"
                : "登录未完成。");

            return outcome;
        }
        catch (Exception ex)
        {
            Log.Error("CAS 登录: 初始化浏览器内核失败", ex);
            SetStatus("无法初始化浏览器内核：" + ex.Message);
            return CasLoginOutcome.Empty;
        }
    }

    private async Task StartWebAsync()
    {
        SetStatus("正在启动浏览器内核…");

        Directory.CreateDirectory(WebUserDataFolder);

        // WinUI 3 的 WebView2 与 WPF 版 API **不同**，别照抄 WPF 的写法：
        //   * WPF 投影有 CoreWebView2Environment.CreateAsync(browser, udf, options)；
        //     WinUI 3 的 CsWinRT 投影**没有**这个三参重载，必须用 CreateWithOptionsAsync。
        //   * ExecuteScriptAsync 返回 WinRT 的 IAsyncOperation<T>，它没有
        //     ConfigureAwait()，只能裸 await。
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder: WebUserDataFolder,
            options: new CoreWebView2EnvironmentOptions());

        await Web.EnsureCoreWebView2Async(env);

        // Mica 与 WebView2 在 WinUI 3 里**都是"外部内容"**，合成器无法把 Mica
        // 画在 WebView2 下面——那块区域只会显示网页或 DefaultBackgroundColor。
        // 所以显式把它设成与 Mica 接近的底色，否则登录窗口里会突兀地挖出一块白/黑方块。
        Web.DefaultBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x20, 0x1C, 0x1A);

        var settings = Web.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;      // 见 EducationSession 里的安全提示
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;

        // 教务会按 UA 嗅探（/js/browse/browse-judge.js）并据此下发降级模板，
        // 这里显式给一个不含 Edg/ 的 Chrome UA。历史上成绩页模板缺 jQuery
        // 与 UA 无关（见 EducationClient 的注释），但 UA 仍要保持与 WPF 版一致。
        settings.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        // CAS 页面偶尔会开新窗口（隐私政策等）。必须拦下并交给系统浏览器，
        // 否则它会在登录窗口里弹出来抢走视线。
        Web.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) OpenExternal(uri);
        };

        var prefill = EducationSession.BuildPrefillScript(_user, _password);

        // 两处注册都需要：第一个覆盖首个文档，之后每次导航完成再补一次。
        await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(prefill);

        Web.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            Url.Text = ((IEducationLoginHost)this).CurrentUrl;

            // 到达教务主机后不再重复注入预填脚本。
            // 教务页面上没有 #pwdFromId，注入在功能上是空操作，
            // 但明文密码会作为 window.__hamPass 暴露在教务主机上，
            // 少注入一次就少一次暴露面。
            if (_session?.ReachedEducation == true) return;

            try { await Web.CoreWebView2.ExecuteScriptAsync(prefill); }
            catch { /* 注入失败不阻断登录，用户仍可手动输入 */ }
        };

        Web.CoreWebView2.NavigationStarting += (_, _) =>
            Url.Text = ((IEducationLoginHost)this).CurrentUrl;

        SetStatus("请在下方完成登录（首次使用需先「账号激活」）…");

        var loginUrl = _endpoints.BuildSsoLoginUrl();
        Web.CoreWebView2.Navigate(loginUrl);
    }

    private void SetStatus(string text)
    {
        // 完成回调可能来自任意线程，必须切回 UI 线程。
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                Status.Text = text;
                if (text.Contains("失败") || text.Contains("未完成") || text.Contains("超时"))
                {
                    Busy.IsActive = false;
                }
            }))
        {
            // 队列已关闭：窗口正在退出，静默忽略即可。
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _session?.Fail("登录已取消。");
        CloseOnce();
    }

    /// <summary>
    /// 关闭窗口。
    /// </summary>
    /// <remarks>
    /// 只在这里和流程收尾处调用。重复 Close 在 WinUI 3 里会抛，
    /// 因此用 <c>_closing</c> 闩住。
    /// </remarks>
    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch (Exception ex) { Log.Error("CAS 登录: 关闭窗口失败", ex); }
    }

    /// <summary>
    /// 承载已脱敏文本的异常。
    /// </summary>
    /// <remarks>
    /// 日志写入器会把 <c>ex.ToString()</c> 整段落盘（含原始消息与堆栈），
    /// 所以只改消息不换对象没用——必须把原异常换成一个不含敏感信息的替身。
    /// 原始堆栈信息在 <see cref="_inner"/> 里保留，但同样已经过脱敏。
    /// </remarks>
    private sealed class RedactedException : Exception
    {
        public RedactedException(string message, string scrubbedOriginal)
            : base(message) => Original = scrubbedOriginal;

        /// <summary>脱敏后的原始异常文本，供排查用。</summary>
        public string Original { get; }

        public override string ToString() => Original.Length > 0 ? Original : base.ToString();
    }
}
