using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ham.Core;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Ham.App.Services;

/// <summary>
/// 信息门户登录窗口（WebView2 承载超星 CAS 页面）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须用浏览器内核</b>：超星 CAS 的口令加密在远端页面脚本内
/// （<c>encrypt.js</c>），密钥不在公开仓库中，无法用纯 HTTP 复刻。</para>
///
/// <para><b>走桌面端 SSO 直连，而不是移动端入口。</b>
/// 本机实测（2026-09）武大 CAS 的移动端入口 <c>/authserver/mobile/auth?appId=985180443</c>
/// 登录成功后落到 <c>/authserver/mobile/default.html#mobile_token=…</c>，但浏览器里只有
/// <c>happyVoyage</c>、<c>MULTIFACTOR_BROWSER_FINGERPRINT</c>、<c>…LOCALE</c> 三条 Cookie，
/// <b>没有 CASTGC 也没有 JSESSIONID</b>，因此换不到教务会话——表现为"登录成功却同步不出任何数据"。
/// <c>happyVoyage</c> 是超星移动端 App 的标记，这条流程本就不是给第三方客户端用的。
/// </para>
///
/// <para><b>因此这里直接导航到桌面端地址</b>
/// <c>/authserver/login?service=&lt;编码后的教务 SSO&gt;</c>，让 CAS 在登录成功后
/// 302 到教务并种下真正的会话。用户看到的主观流程和浏览器里点一次「登录信息门户」完全一致。</para>
///
/// <para><b>成功判据是「已落到教务主机」，而不是匹配某个 URL。</b>
/// 登录前地址在 <c>cas.whu.edu.cn</c>，登录成功后被 302 到 <c>jwgl.whu.edu.cn</c>，
/// 这个跨越本身就���可靠信号；再辅以教务会话 Cookie 出现作为快速路径。</para>
///
/// <para><b>必须有超时</b>：任何等待都不能无限进行，否则用户在账号异常、
/// 需要先「账号激活」等情况会一直面对一个没有反馈的窗口。</para>
/// </remarks>
public sealed class CasLoginWindow : Window
{
    /// <summary>等待用户完成登录的最长时间。</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// 到达教务主机后，等待其会话 Cookie 落地的最长时间。
    /// </summary>
    /// <remarks>
    /// CAS 302 到 <c>/sso/jznewsixlogin?ticket=…</c> 时只是把浏览器送到教务入口，
    /// 教务会话是在这一跳的响应里种下的。此刻立即读 Cookie 存在竞态，
    /// 因此再等一小段；等不到就把已有 Cookie 交出去，让上层报真实错误。
    /// </remarks>
    private static readonly TimeSpan SessionSettleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>未获取到任何 Cookie 时使用的空结果。</summary>
    private static readonly CasLoginOutcome EmptyOutcome = new([], FetchPlanResult.Empty);

    private readonly WebView2 _webView = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _urlText = new();
    private readonly ProgressBar _progress = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly string? _prefillUser;
    private readonly string? _prefillPassword;

    /// <summary>
    /// 登录结果。只能完成一次，且必须以<b>最终</b>结果完成——
    /// 详见 <see cref="OneShotResult{T}"/>.
    /// </summary>
    private readonly OneShotResult<CasLoginOutcome> _result = new();

    /// <summary>需要本窗口在页面上下文中代取的数据。</summary>
    private FetchPlan _fetchPlan = FetchPlan.Empty;

    private DateTime _openedAt;
    private string _lastUrl = string.Empty;
    private bool _isClosing;

    /// <summary>首次到达教务主机的时刻；用于等待教务会话落地。</summary>
    private DateTime? _reachedEducationAt;

    /// <summary>接入点配置。默认生产环境；测试时可指向本地 mock。</summary>
    private readonly CampusEndpoints _endpoints;

    public CasLoginWindow(
        string? studentId = null, string? password = null, CampusEndpoints? endpoints = null)
    {
        _prefillUser = studentId;
        _prefillPassword = password;
        _endpoints = endpoints ?? CampusEndpoints.Default;

        Title = "登录信息门户 — 武汉大学";
        Width = 1040;
        Height = 880;
        MinWidth = 560;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8));
        Topmost = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;

        var root = new DockPanel();

        var header = new Grid { Margin = new Thickness(20, 16, 20, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "武汉大学信息门户登录",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2E)),
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var hint = new TextBlock
        {
            Text = string.IsNullOrEmpty(studentId)
                ? "完成登录后将自动返回并继续同步课表与成绩"
                : $"本次登录使用学号 {studentId}（已自动填入，可在页面上修改）",
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        };
        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _status.Text = "正在加载登录页面…";
        _status.FontSize = 12.5;
        _status.Margin = new Thickness(20, 0, 20, 4);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        _urlText.Text = string.Empty;
        _urlText.FontSize = 11;
        _urlText.Margin = new Thickness(20, 0, 20, 8);
        _urlText.TextTrimming = TextTrimming.CharacterEllipsis;
        _urlText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAC));
        DockPanel.SetDock(_urlText, Dock.Top);
        root.Children.Add(_urlText);

        _progress.Height = 3;
        _progress.IsIndeterminate = true;
        _progress.Margin = new Thickness(20, 0, 20, 0);
        DockPanel.SetDock(_progress, Dock.Bottom);
        root.Children.Add(_progress);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 10, 20, 14),
        };
        var cancel = new Button
        {
            Content = "取消登录",
            Width = 110,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
        };
        cancel.Click += (_, _) => Fail("用户取消了登录。");
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _webView.Margin = new Thickness(20, 0, 20, 0);
        root.Children.Add(_webView);

        Content = root;

        // 只在 Closing 里"标记完成"，绝不在此再次调用 Close()——
        // 那会触发 "Cannot ... Close ... while a Window is closing" 并抛异常。
        Closing += (_, _) => MarkCompleted();
        Loaded += OnLoaded;
        Closed += (_, _) => OnWindowClosed();
    }

    /// <summary>打开窗口并等待登录结果。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="studentId">用于预填的学号。</param>
    /// <param name="password">用于预填的口令。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="endpoints">接入点配置，默认生产环境。</param>
    /// <param name="fetchPlan">需要在页面上下文中代取的数据清单。</param>
    public static async Task<CasLoginOutcome> ShowAsync(
        Window? owner, string? studentId, string? password,
        CancellationToken ct = default, CampusEndpoints? endpoints = null,
        FetchPlan? fetchPlan = null)
    {
        var window = new CasLoginWindow(studentId, password, endpoints) { _fetchPlan = fetchPlan ?? FetchPlan.Empty };
        if (owner is not null && owner.IsLoaded) window.Owner = owner;

        window.Show();
        window.Activate();
        window.Focus();

        using var reg = ct.Register(() => window.Dispatcher.Invoke(() => window.Fail("登录已取消。")));

        return await window._result.Task.ConfigureAwait(false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _openedAt = DateTime.Now;

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ham", "webview");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData).ConfigureAwait(true);

            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            // 去掉 UA 里的 Edg/ 标记，伪装成普通 Chrome。
            // 说明：已实测**这并不是**成绩页缺 jQuery 的原因（改完后 window.jQuery
            // 仍是 undefined），但教务系统带 UA 嗅探（/js/browse/browse-judge.js），
            // 将来任何按 UA 分流模板的行为都可能因此拿到降级页面。
            // 这里保持嵌入式浏览器与用户真实浏览器一致，少一个变量。
            settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

            var prefill = BuildPrefillScript(_prefillUser, _prefillPassword);
            await _webView.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(prefill).ConfigureAwait(true);

            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                Log($"导航完成: {_webView.Source}");
                try
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync(prefill).ConfigureAwait(true);
                }
                catch
                {
                    // 注入失败不阻断登录，用户仍可手动输入。
                }
            };

            // SourceChanged 对 SPA 路由变化同样会触发，比只靠 NavigationCompleted 更稳。
            _webView.SourceChanged += (_, _) => Log($"地址变化: {_webView.Source}");

            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
                    OpenInSystemBrowser(uri);
            };

            // 走桌面端：service 直接指向教务 SSO，CAS 登录成功后 302 过去并建立教务会话。
            // 移动端入口（/authserver/mobile/auth）换不到 CASTGC，实测无法用于教务。
            var loginUrl = _endpoints.BuildSsoLoginUrl();
            Log($"打开登录页: {loginUrl}");
            _webView.CoreWebView2.Navigate(loginUrl);

            _status.Text = "请在下方完成登录（首次使用需先「账号激活」）…";
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            Log("初始化浏览器内核失败", ex);
            Fail("无法初始化浏览器内核（WebView2）：" + ex.Message);
        }
    }

    /// <summary>每秒轮询一次登录结果。</summary>
    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_result.IsCompleted || _isClosing) return;

        var elapsed = DateTime.Now - _openedAt;

        if (elapsed > LoginTimeout)
        {
            // 带上学号：实际案例是学号里两个数字颠倒，页面静默重载，
            // 用户只看到"登录不上"，根本想不到是学号打错。
            var who = string.IsNullOrEmpty(_prefillUser)
                ? string.Empty
                : $"本次使用的学号是 {_prefillUser}，请逐位核对（数字颠倒是最常见原因）。";

            Fail("等待登录超时。若页面仍停在登录界面，通常是学号或密码有误。"
                 + who
                 + "若页面提示需要「账号激活」，请先完成激活。");
            return;
        }

        var url = _webView.Source?.ToString() ?? string.Empty;
        if (url != _lastUrl)
        {
            _lastUrl = url;
            _urlText.Text = "当前页面：" + url;
        }

        var left = (int)Math.Max(0, LoginTimeout.TotalSeconds - elapsed.TotalSeconds);

        // 到达教务主机后由收尾逻辑负责提示，不要被等待提示覆盖掉
        if (_reachedEducationAt is null)
        {
            _status.Text = left < 240
                ? $"请在下方完成登录…（{left / 60} 分 {left % 60} 秒后自动结束）"
                : "请在下方完成登录（首次使用需先「账号激活」）…";
        }

        await TryDetectSuccessAsync().ConfigureAwait(true);
    }

    /// <summary>判定登录是否成功。</summary>
    private async Task TryDetectSuccessAsync()
    {
        var url = _webView.Source?.ToString() ?? string.Empty;
        if (url.Length == 0) return;

        // 教务的二次认证页：CAS 侧成功了但教务要求再次确认身份，必须如实告诉用户。
        if (url.Contains("ReAuth", StringComparison.OrdinalIgnoreCase))
        {
            Log("教务要求二次认证");
            Fail("教务系统要求二次认证，请先在浏览器中完成信息门户的重新确认后再试。");
            return;
        }

        if (HasReachedEducation(url))
        {
            // 到达教务主机只说明 CAS 侧认证完成，<b>不等于</b>教务会话已经建立：
            // /sso/jznewsixlogin?ticket=… 这一跳的响应里才种下教务会话 Cookie，
            // 此刻立刻读取有竞态，可能漏掉它。先进入收尾阶段再继续等。
            if (_reachedEducationAt is null)
            {
                _reachedEducationAt = DateTime.Now;
                Log($"已到达教务主机，等待会话建立: {url}");
            }

            _status.Text = "登录成功，正在建立教务会话…";
            await TryFinishAsync().ConfigureAwait(true);
            return;
        }

        // 还在 CAS 侧：把状态提示恢复成等待用户输入。
        if (_reachedEducationAt is not null)
        {
            // 教务把我们弹回了登录页 —— 会话没建起来，如实报告。
            Log($"已离开教务主机，登录未完成: {url}");
            Fail("教务系统没有接受登录，会话未能建立。请确认账号权限后重试。");
        }
    }

    /// <summary>收尾：等教务会话真正建立后代取数据，再一并交出。</summary>
    private async Task TryFinishAsync()
    {
        // 成绩流程要自己导航到成绩页（为了加载顶象 SDK）。那个导航**也会**被
        // 导航处理器判成「到达教务主机」并再次调 TryFinishAsync，
        // 于是同一个收尾流程被重入，_completing 互斥把它挡掉、
        // 而成绩流程自己那侧看到 _result 已完成就直接 break，
        // 结果顶象还没下载完就报「未能就绪」。
        // 这里显式挡一道：**成绩流程期间不允许再次收尾**。
        if (Volatile.Read(ref _scoreFlowActive) != 0)
        {
            Log("成绩流程进行中，忽略本次收尾触发");
            return;
        }

        var cookies = await ReadAllCookiesAsync().ConfigureAwait(true);

        var eduSession = cookies.FirstOrDefault(c =>
            c.Host.Contains(_endpoints.EducationHost, StringComparison.OrdinalIgnoreCase)
            && c.Name.Contains("SESSION", StringComparison.OrdinalIgnoreCase)
            && c.Value.Length > 0);

        if (eduSession is not null)
        {
            await CompleteAsync("教务会话已建立", cookies).ConfigureAwait(true);
            return;
        }

        // 到达教务主机后仍拿不到会话 Cookie：最多再等一会儿，
        // 超时就把已有 Cookie 交出去，让上层报出真实错误，而不是把用户挂在这里。
        var waited = DateTime.Now - (_reachedEducationAt ?? DateTime.Now);
        if (waited > SessionSettleTimeout)
        {
            Log($"等待教务会话超时（{waited.TotalSeconds:F0}s），按现有 Cookie 继续");
            await CompleteAsync("教务会话未确认", cookies).ConfigureAwait(true);
        }
    }

    /// <summary>读取 CAS 与教务两个域下的全部非空 Cookie。</summary>
    private async Task<List<CasCookie>> ReadAllCookiesAsync()
    {
        var all = new List<CasCookie>();

        foreach (var baseUrl in new[] { _endpoints.CasBaseUrl, _endpoints.EducationBaseUrl })
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var seed)) continue;

            var cookies = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync(baseUrl).ConfigureAwait(true);

            all.AddRange(cookies
                .Where(c => !string.IsNullOrEmpty(c.Value))
                .Select(c => new CasCookie(seed.Host, c.Name, c.Value)));
        }

        return all
            .GroupBy(c => (c.Host, c.Name))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>判断地址是否代表「登录已完成、已交给教务」。</summary>
    /// <remarks>
    /// 要求同时满足两点，缺一不可：
    /// <list type="number">
    /// <item>地址属于教务主机——登录成功后 CAS 会把浏览器 302 到这里；</item>
    /// <item>地址不再停留在 CAS 登录入口上。</item>
    /// </list>
    /// 第二条不是为了防生产环境的错配，而是为了让判据在「CAS 与教务同主机」时依然成立
    /// （本地 mock 验证就是这种拓扑），否则一开始就误判成功、窗口秒关。
    /// </remarks>
    private bool HasReachedEducation(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // about:blank / data: 之类的地址 Host 为空串。必须先排除：
        // 之前用 EducationBaseUrl.Contains(uri.Host) 判断，而空串被任何字符串包含，
        // 导致 WebView 刚创建、还停在 about:blank 时就被判成"已到达教务主机"，
        // 紧接着因地址变回 CAS 页而被判成"已离开教务主机 → 登录失败"，窗口秒关。
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        // 仍停在 CAS 登录入口上不算到达教务
        if (uri.Host.Equals(_endpoints.CasHost, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals(_endpoints.EducationHost, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(
                   _endpoints.EducationHost.StartsWith('.') ? _endpoints.EducationHost : "." + _endpoints.EducationHost,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task CompleteAsync(string reason, List<CasCookie> cookies)
    {
        if (_result.IsCompleted) return;

        // 收尾必须互斥。1 秒轮询定时器会在 RunFetchPlanAsync 等待期间反复重入，
        // 若不加锁就会并发启动多批 XHR，而它们共用同一个 window.__hamFetch，
        // 后启动的会把先启动的结果清空 —— 最终谁也读不到数据，且不报任何错。
        if (Interlocked.Exchange(ref _completing, 1) != 0)
        {
            Log("收尾已在进行中，忽略重复触发");
            return;
        }

        try
        {
            var payloads = await RunFetchPlanAsync().ConfigureAwait(true);

            var list = cookies
                .GroupBy(c => (c.Host, c.Name))
                .Select(g => g.First())
                .ToList();

            Log($"完成登录（{reason}），导出 Cookie {list.Count} 项: "
                + string.Join(", ", list.Select(c => $"{c.Host}/{c.Name}"))
                + $"; 代取 {payloads.Bodies.Count} 项，失败 {payloads.Failures.Count} 项"
                + (payloads.Failures.Count > 0 ? " -> " + string.Join("; ", payloads.Failures) : string.Empty));

            Succeed(list, payloads);
        }
        catch (Exception ex)
        {
            Log("收尾过程异常", ex);
        }
    }

    /// <summary>收尾互斥标志。</summary>
    private int _completing;

    /// <summary>
    /// 成绩（图形验证码）流程是否进行中。
    /// </summary>
    /// <remarks>
    /// 见 <see cref="TryFinishAsync"/>：成绩流程要自己导航到成绩页加载顶象 SDK，
    /// 那个导航会再次触发收尾判定。用这个标志把「成绩流程」与「登录收尾」两个状态机隔开，
    /// 否则两边互相踩——实测症状是顶象刚要加载就被判失败。
    /// </remarks>
    private int _scoreFlowActive;

    /// <summary>
    /// 在当前页面上下文中执行 <see cref="_fetchPlan"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意使用页面内的 <c>XMLHttpRequest</c> 而不是 C# 侧的 <c>HttpClient</c>：
    /// 教务前置安全设备只放行同源、带会话、带 Referer 且由页面脚本发起的请求
    /// （见 <see cref="FetchPlan"/> 的实测记录）。用 <c>XMLHttpRequest</c> 而非
    /// <c>fetch</c>，是为了让浏览器自动带上 <c>X-Requested-With: XMLHttpRequest</c>——
    /// 这正是真实页面 jQuery <c>$.post</c> 的行为，也是安全设备据以放行的特征。
    /// </para>
    /// <para>
    /// <b>不能依赖 <c>ExecuteScriptAsync</c> 自动 await Promise</b>：实测它会直接返回
    /// 未决 Promise 序列化后的 <c>{}</c>，结果全丢（且不报错，极难察觉）。
    /// 因此改成「启动异步任务 → 写入全局状态 → 轮询取结果」的两段式。
    /// </para>
    /// <para>
    /// <b>注入前必须确认文档已经稳定。</b>CAS 302 到教务时，地址先变成目标页，
    /// <c>NavigationCompleted</c> 随后才触发。若在两者之间注入，脚本会跑在
    /// <b>上一个文档</b>里，导航完成后新文档的 <c>window.__hamFetch</c> 荡然无存，
    /// 轮询永远读不到结果（日志里就出现过导航完成与代取同秒发生的情况）。
    /// 因此这里检查地址是否稳定，并在文档被替换时重新注入。
    /// </para>
    /// </remarks>
    private async Task<FetchPlanResult> RunFetchPlanAsync()
    {
        if (_fetchPlan.Targets.Count == 0) return FetchPlanResult.Empty;

        var xhr = _fetchPlan.Targets.Where(t => t.Mode == FetchMode.Xhr).ToList();
        var docs = _fetchPlan.Targets.Where(t => t.Mode == FetchMode.Document).ToList();
        var caps = _fetchPlan.Targets.Where(t => t.Mode == FetchMode.ScoreWithCaptcha).ToList();

        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();

        if (xhr.Count > 0)
        {
            var r = await RunXhrAsync(xhr).ConfigureAwait(true);
            foreach (var kv in r.Bodies) bodies[kv.Key] = kv.Value;
            foreach (var kv in r.Statuses) statuses[kv.Key] = kv.Value;
            failures.AddRange(r.Failures);
        }

        foreach (var target in docs)
        {
            if (_result.IsCompleted) break;
            var r = await RunDocumentAsync(target).ConfigureAwait(true);
            bodies[target.Path] = r.Body;
            statuses[target.Path] = r.Status;
            if (r.Error is not null) failures.Add($"{target.Path} -> {r.Error}");
        }

        // 成绩放最后：它需要用户交互，失败也不该影响课表与学籍。
        foreach (var target in caps)
        {
            if (_result.IsCompleted) break;
            var r = await RunScoreWithCaptchaAsync(target).ConfigureAwait(true);
            if (r.Body is not null) bodies[target.Path] = r.Body;
            statuses[target.Path] = r.Status;
            if (r.Error is not null) failures.Add($"{target.Path} -> {r.Error}");
        }

        return new FetchPlanResult(bodies, failures, statuses);
    }

    /// <summary>
    /// 成绩：摆好顶象控件 → 用户验证 → 拿真 token 发请求 → 读回 JSON。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么绕开页面自己的 <c>popupCaptcha()</c>。</b>
    /// 成绩页模板漏引 <c>jquery.min.js</c>，它的内联脚本与 <c>captcha.js</c> 全部抛
    /// ReferenceError，验证码永远弹不出来（试过三种补 jQuery 的方式，document-created
    /// 阶段 jQuery 工厂起不来；DOM ready 之后能注入，但那时处理器已经错过绑定时机）。
    /// 所以这里按顶象 SDK 的公开接口自己接一遍——滑块仍然要人过，
    /// 换的只是「谁来调 SDK」。
    /// </para>
    /// <para>
    /// <b>style 必须是 <c>embed</c>。</b>传 <c>popup</c> 时顶象会把容器设成
    /// <c>display:none</c> 等待 <c>instance.show()</c>，控件其实已完整渲染但用户看不见——
    /// 实测容器 0×0 + display:none，滑块明明在却无从下手。
    /// </para>
    /// </remarks>
    private async Task<(string? Body, int Status, string? Error)> RunScoreWithCaptchaAsync(FetchTarget target)
    {
        // 顶象 SDK 由成绩页自己加载（cdn.dingxiang-inc.com），所以必须先到那个页面。
        var hub = _endpoints.EducationBaseUrl.TrimEnd('/') + "/cjcx/cjcx_cxDgXscj.html?gnmkdm=N305005";
        Log($"成绩：先到 {hub} 建立页面上下文并加载顶象 SDK");

        Interlocked.Exchange(ref _scoreFlowActive, 1);
        try
        {
            _webView.CoreWebView2.Navigate(hub);

            // 先等导航真正完成。ExecuteScriptAsync 跑在**当前**文档里，
            // 导航还没落定时执行到的还是上一个页面（那里没有 _dx），
            // 于是就出现「明明在成绩页却读不到 SDK」的假象。
            var navigated = new TaskCompletionSource<bool>();
            void OnNav(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
                => navigated.TrySetResult(true);
            _webView.CoreWebView2.NavigationCompleted += OnNav;
            try { await navigated.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true); }
            catch { /* 超时也继续，下面还会轮询就绪状态 */ }
            finally { _webView.CoreWebView2.NavigationCompleted -= OnNav; }

            // SDK 还要从 CDN 下载，appId 也要能读到
            var initOk = false;
            string lastProbe = "(未探测)";
            for (var i = 0; i < 60 && !initOk; i++)
            {
                await Task.Delay(500).ConfigureAwait(true);
                lastProbe = await ExecAsync(ScorePageReadyScript).ConfigureAwait(true);
                if (lastProbe.Contains("READY")) initOk = true;
            }

            if (!initOk)
            {
                Log($"成绩页就绪探测失败，最后一次结果：{lastProbe}");
                return (null, 0, $"成绩页未能就绪（{lastProbe}）");
            }

            Log($"成绩：顶象就绪，初始化控件（embed 模式）。{lastProbe}");
            await ExecAsync(ScoreCaptchaInitScript).ConfigureAwait(true);

            // 1 秒轮询的 TryDetectSuccessAsync 会把状态栏覆盖成「登录成功，正在建立教务会话…」，
            // 成绩流程期间必须停掉它，否则用户看到的提示和实际要做的事完全对不上。
            _pollTimer.Stop();

            _status.Text = "⚠ 成绩需要图形验证码"
                           + "：请在**页面正中间橙色方框**里拖动滑块完成验证。"
                           + "通过后成绩会自动读取，最多等 5 分钟。";
            _status.FontSize = 15;
            _status.FontWeight = FontWeights.SemiBold;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00));

            // 把登录窗口顶到最前，否则它可能藏在主窗口后面，用户根本不知道要看哪里
            try
            {
                Topmost = true;
                Activate();
                Focus();
            }
            catch { /* 置前失败不影响功能 */ }

            var deadline = DateTime.Now.AddMinutes(5);
            while (DateTime.Now < deadline)
            {
                await Task.Delay(1000).ConfigureAwait(true);

                // 这里**不能**看 _result.IsCompleted：成绩流程本身就是被
                // 包在收尾流程里跑的，那时的 _result 必然已完成，
                // 一旦拿它当退出条件就会立刻放弃（实测就是这样失败的）。
                var state = await ExecAsync(ScoreCaptchaStatusScript).ConfigureAwait(true);
                if (state.Contains("RESULT>>"))
                {
                    var body = await ExecAsync(ScoreCaptchaResultScript).ConfigureAwait(true);
                    Log($"成绩：拿到响应 {body.Length} 字符：{Preview(body, 120)}");

                    // 判据是**首个字符**，不是 HTTP 状态。这个主机的失败路径大量
                    // 返回 200 + HTML 登录页；反过来 911 也可能带着合法 JSON。
                    var ok = LooksLikeJson(body);
                    return (ok ? body : null, ok ? 200 : 0,
                            ok ? null : "成绩响应不是 JSON：" + Preview(body));
                }
                if (state.Contains("ERR>>"))
                {
                    var err = await ExecAsync(ScoreCaptchaResultScript).ConfigureAwait(true);
                    return (null, 0, "图形验证码未通过：" + err);
                }
            }

            return (null, 0, "5 分钟内未完成图形验证码");
        }
        finally
        {
            Interlocked.Exchange(ref _scoreFlowActive, 0);
            // 恢复轮询与置前状态，免得成绩流程结束后窗口留在最前
            try { Topmost = false; } catch { /* 忽略 */ }
            if (!_result.IsCompleted)
            {
                try { _pollTimer.Start(); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>
    /// 执行一段页面脚本并把返回值还原成原始字符串。
    /// </summary>
    /// <remarks>
    /// <c>ExecuteScriptAsync</c> 会对脚本结果**再做一次 JSON 编码**，
    /// 所以拿到的最外层一定带引号，必须先剥掉，否则每段正文都会变成
    /// <c>"{\"…\"}</c> 这种双层转义，解析器一律读不出东西。
    /// 这个坑在成绩 DOM 解析上实际发生过。
    /// </remarks>
    private async Task<string> ExecAsync(string script)
    {
        try
        {
            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
            if (string.IsNullOrEmpty(raw) || raw is "null" or "undefined") return string.Empty;
            if (raw[0] != '"') return raw;
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log("执行脚本失败: " + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>成绩页是否就绪：顶象 SDK 在、appId 读得到。</summary>
    private const string ScorePageReadyScript = """
        (function () {
          if (!window._dx || !window._dx.Captcha) return 'WAIT(无顶象 SDK)';
          let appId = '';
          for (const i of document.querySelectorAll('input')) {
            if (/appid/i.test((i.id||'') + ' ' + (i.name||'')) && i.value) { appId = i.value.trim(); break; }
          }
          return appId ? 'READY appId=' + appId : 'WAIT(读不到 appId)';
        })();
        """;

    /// <summary>
    /// 初始化顶象控件（<b>embed</b>），并在验证通过的回调里立刻发成绩请求。
    /// </summary>
    private const string ScoreCaptchaInitScript = """
        (function () {
          try {
            window.__hamSt = ''; window.__hamRes = '';
            if (!window._dx || !window._dx.Captcha) { window.__hamSt = 'ERR>>'; window.__hamRes = '顶象 SDK 未加载'; return 'no-sdk'; }

            let appId = '';
            for (const i of document.querySelectorAll('input')) {
              if (/appid/i.test((i.id||'') + ' ' + (i.name||'')) && i.value) { appId = i.value.trim(); break; }
            }
            if (!appId) { window.__hamSt = 'ERR>>'; window.__hamRes = '读不到 appId'; return 'no-appId'; }

            const api = (document.getElementById('apiServer') || {}).value
                      || 'https://www.dingxiang-inc.com';

            const old = document.getElementById('__hamCapBox');
            if (old) old.remove();

            // 放在**屏幕正中**而不是右下角：嵌入式 WebView2 区域往往不大，
            // 贴角的控件容易被裁掉，用户根本看不到（反馈：登录后只看到教务页面）。
            const box = document.createElement('div');
            box.id = '__hamCapBox';
            box.style.cssText = 'position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);'
              + 'z-index:2147483647;background:#ffffff;padding:16px;border:4px solid #ff6600;'
              + 'width:420px;max-width:92vw;box-shadow:0 10px 40px rgba(0,0,0,.55);'
              + 'font:15px "Microsoft YaHei",sans-serif;color:#000;'
              + 'display:flex;align-items:center;justify-content:center';
            box.innerHTML = '<div style="text-align:center;font-weight:bold;color:#c60">'
                          + '正在加载图形验证码…</div>';
            document.body.appendChild(box);

            // style:'embed' —— popup 模式会把容器置 display:none，控件渲染了但看不见
            window.__hamCap = window._dx.Captcha(box, {
              apiServer: api,
              appId: appId,
              style: 'embed',
              success: function (token) {
                box.innerHTML = '<div style="padding:10px;color:#080;font-weight:bold">'
                              + '验证通过，正在读取成绩…</div>';
                try {
                  const x = new XMLHttpRequest();
                  x.open('POST', '/cjcx/cjcx_cxXsgrcj.html?doType=query', false);
                  x.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                  x.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                  x.send('xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs'
                       + '&queryModel.showCount=150&queryModel.currentPage=1'
                       + '&validate=' + encodeURIComponent(token));
                  window.__hamRes = (x.responseText || '');
                } catch (e) {
                  window.__hamRes = 'XHR 抛错: ' + e;
                }
                window.__hamSt = 'RESULT>>';
                box.remove();
              },
              error: function (e) {
                window.__hamRes = '验证未通过';
                window.__hamSt = 'ERR>>';
              }
            });

            // 顶象偶尔仍会把容器隐藏，强制拉回来并放到最前
            box.style.display = 'flex';
            box.style.visibility = 'visible';
            box.style.opacity = '1';
            document.body.appendChild(box);   // 移到 body 末尾，确保盖在其他节点之上
            try { if (window.__hamCap && window.__hamCap.show) window.__hamCap.show(); } catch (e) {}
            window.__hamSt = 'WAIT';
            return 'OK appId=' + appId;
          } catch (e) {
            window.__hamSt = 'ERR>>'; window.__hamRes = '初始化抛错: ' + e;
            return 'throw';
          }
        })();
        """;

    private const string ScoreCaptchaStatusScript =
        "(function () { return window.__hamSt || ''; })();";

    private const string ScoreCaptchaResultScript =
        "(function () { return window.__hamRes || ''; })();";

    /// <summary>
    /// 成绩响应是否像 JSON（首个字符是 <c>{</c> 或 <c>[</c>）。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>依赖 <c>EducationParser</c>：登录窗口属于 UI 层，
    /// 不该为了一个判据把解析器拖进来。规则本身也简单到不需要解析器。
    /// </remarks>
    private static bool LooksLikeJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var c = text.TrimStart().TrimStart('﻿').TrimStart()[0];
        return c is '{' or '[';
    }

    /// <summary>截一段预览用于日志与错误提示。</summary>
    private static string Preview(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>导航到目标地址并读取渲染后的 DOM。</summary>
    private async Task<(string Body, int Status, string? Error)> RunDocumentAsync(FetchTarget target)
    {
        var url = _endpoints.EducationBaseUrl.TrimEnd('/') + target.Path;
        Log($"导航读取页面: {url}");

        // 成绩页需要人工验证码，必须在**最显眼的位置**提示。
        // 此前只写在副标题里，嵌入式 WebView2 里几乎看不见，用户完全不知道要做什么
        // （反馈原话：「验证码也没弹出来」）。
        if (target.Path.Contains("cjcx", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "⚠ 成绩查询需要图形验证码：正在自动点「查询」把验证码弹出，"
                           + "请在下方页面完成滑块/点选验证，通过后成绩会自动读取。\n"
                           + "（若一直不弹，请在页面里手动点一次「查询」；"
                           + "该页面对浏览器环境较敏感，必要时用 Edge/Chrome 打开教务系统再试。）";
            _status.FontSize = 14;
            _status.FontWeight = FontWeights.SemiBold;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00));
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;

        void OnCompleted(object? _, object __) => Complete();

        void Complete()
        {
            if (settled) return;
            settled = true;
            tcs.TrySetResult(true);
        }

        _webView.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            // 教务页面默认不查询，必须先选学年学期再点「查询」才会出表格。
            // 这里在导航前注入自动查询脚本，与用户手动操作等价。
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                AutoQueryScript).ConfigureAwait(true);

            _webView.CoreWebView2.Navigate(url);
            await Task.WhenAny(tcs.Task, Task.Delay(DocumentSettleTimeout)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _webView.CoreWebView2.NavigationCompleted -= OnCompleted;
            return (string.Empty, 0, "导航失败: " + ex.Message);
        }

        // 页面渲染完通常还要等一会儿表格才有数据
        if (settled) await Task.Delay(800).ConfigureAwait(true);
        _webView.CoreWebView2.NavigationCompleted -= OnCompleted;

        try
        {
            // 教务页面是 jQuery 应用，表格靠 AJAX 渲染，导航完成时正文往往还是空的。
            // 因此轮询到「出现 table 且行数稳定」再取，否则读到的必然是空壳。
            var body = await WaitForRenderedTableAsync().ConfigureAwait(true);
            Log($"页面读取完成: {url}，正文 {body.Length} 字符");
            return (body, 200, null);
        }
        catch (Exception ex)
        {
            return (string.Empty, 0, "读取 DOM 失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 调试用：把页面的完整 HTML 与「可交互元素清单」落盘。
    /// </summary>
    /// <remarks>
    /// 教务的 jQuery 页面结构（有哪些 select、按钮文案到底是什么、有没有 iframe）
    /// 只有真实登录后才能看到，全靠猜会反复走弯路。这里把它 dump 出来供离线分析。
    /// 由环境变量 <c>HAM_DUMP_PAGE_DIR</c> 开启，**默认关闭**，正常使用时零开销。
    /// </remarks>
    private async Task DumpPageDebugAsync(string url)
    {
        var dir = Environment.GetEnvironmentVariable("HAM_DUMP_PAGE_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);

            var html = await _webView.CoreWebView2.ExecuteScriptAsync(
                "document.documentElement ? document.documentElement.outerHTML : ''")
                .ConfigureAwait(true);
            var decodedHtml = System.Text.Json.JsonSerializer.Deserialize<string>(html) ?? string.Empty;

            var inventory = await _webView.CoreWebView2.ExecuteScriptAsync("""
                (function () {
                  const out = [];
                  out.push('URL=' + location.href);
                  out.push('title=' + document.title);
                  out.push('iframes=' + document.querySelectorAll('iframe').length);
                  // 菜单/链接是找功能真实路径的关键，优先收录
                  document.querySelectorAll('a').forEach((e) => {
                    const t = (e.innerText || '').replace(/\s+/g, ' ').trim();
                    const href = e.getAttribute('href');
                    if (href && t && t.length <= 24) out.push('A[' + t + '] -> ' + href);
                  });
                  document.querySelectorAll('li,span,div,p').forEach((e) => {
                    const t = (e.innerText || '').replace(/\s+/g, ' ').trim();
                    if (t && t.length <= 24 && /成绩|课表|评教|评价|教师|排名|查询/.test(t)) {
                      out.push('T[' + t + ']');
                    }
                  });
                  document.querySelectorAll('select').forEach((s, i) => {
                    out.push('select[' + i + '] name=' + (s.name || '-') + ' id=' + (s.id || '-')
                      + ' options=' + s.options.length
                      + ' first5=' + Array.from(s.options).slice(0, 5)
                          .map(o => o.value + ':' + (o.innerText || o.text).trim()).join(' , '));
                  });
                  return out.join('\n');
                })();
                """).ConfigureAwait(true);

            var decodedInventory =
                System.Text.Json.JsonSerializer.Deserialize<string>(inventory) ?? string.Empty;

            var name = SafeName(url);
            File.WriteAllText(Path.Combine(dir, name + ".html"), decodedHtml, Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), decodedInventory, Encoding.UTF8);

            Log($"已 dump 页面: {name}.html ({decodedHtml.Length} 字符) / .txt");
        }
        catch (Exception ex)
        {
            Log("dump 页面失败", ex);
        }
    }

    private static string SafeName(string url)
    {
        var name = new string(url.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>轮询等待页面渲染出表格，返回表格的 HTML。</summary>
    /// <remarks>
    /// 返回 <b>表格的 outerHTML</b> 而不是整页或 innerText：解析器需要 <c>&lt;table&gt;</c>
    /// 结构才能按表头定位列；而整页 HTML 动辄几十万字符，innerText 又丢掉了所有标签。
    /// </remarks>
    private async Task<string> WaitForRenderedTableAsync()
    {
        const string probe = """
            (function () {
              const tables = Array.from(document.querySelectorAll('table'))
                .filter(t => t.querySelectorAll('tr').length > 1);
              return JSON.stringify({
                count: tables.length,
                rows: tables.reduce((n, t) => n + t.querySelectorAll('tr').length, 0)
              });
            })();
            """;

        const string readTables = """
            (function () {
              return Array.from(document.querySelectorAll('table'))
                .filter(t => t.querySelectorAll('tr').length > 1)
                .map(t => t.outerHTML)
                .join('\n');
            })();
            """;

        var deadline = DateTime.UtcNow + DocumentSettleTimeout;
        var best = string.Empty;
        var lastSignature = string.Empty;
        var stable = 0;
        var hinted = false;

        while (DateTime.UtcNow < deadline)
        {
            string raw;
            try
            {
                raw = await _webView.CoreWebView2.ExecuteScriptAsync(probe).ConfigureAwait(true);
            }
            catch
            {
                await Task.Delay(400).ConfigureAwait(true);
                continue;
            }

            var inner = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            var signature = inner.Replace(" ", "");
            var hasRows = !signature.Contains("\"rows\":0", StringComparison.Ordinal);

            // 签名连续两次相同视为渲染稳定
            stable = signature == lastSignature ? stable + 1 : 0;
            lastSignature = signature;

            if (hasRows)
            {
                try
                {
                    var html = await _webView.CoreWebView2
                        .ExecuteScriptAsync(readTables).ConfigureAwait(true);
                    var decoded = System.Text.Json.JsonSerializer.Deserialize<string>(html) ?? string.Empty;
                    if (decoded.Length > best.Length) best = decoded;
                    if (stable >= 1 && best.Length > 0) return best;
                }
                catch
                {
                    // 页面正在重绘，下一轮再试
                }
            }
            else if (!hinted)
            {
                // 兜底提示：若迟迟没有数据，至少让用户知道该做什么
                hinted = true;
                try
                {
                    var hasForm = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "!!document.getElementById('search_go')").ConfigureAwait(true);
                    if (hasForm is "true")
                    {
                        _status.Text = "⚠ 请在上方页面完成图形验证码（滑块或点选），"
                                       + "通过后成绩会自动读取，最多等待 3 分钟。";
                        _status.FontSize = 14;
                        _status.FontWeight = FontWeights.SemiBold;
                        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00));
                        Log("成绩页等待用户完成验证码，已更新提示");
                    }
                }
                catch { /* 忽略 */ }
            }

            if (stable >= 6) break;
            await Task.Delay(400).ConfigureAwait(true);
        }

        // 只要开了 dump 开关就落盘，不管有没有找到表格——
        // 「成功渲染」时同样需要看结构（例如菜单页要靠它找功能路径）。
        await DumpPageDebugAsync(_webView.Source?.ToString() ?? "(unknown)").ConfigureAwait(true);

        // 页面用 jqGrid 发查询，DOM 里未必有可解析的 <table>。
        // 优先返回**页面自己那条请求的响应**：那是它真正的数据源，格式干净。
        var captured = await ReadCapturedAsync().ConfigureAwait(true);
        if (captured.Length > 0) return captured;

        Log(best.Length > 0
            ? "页面表格已稳定"
            : "等待页面表格超时，未找到任何表格");
        return best;
    }

    /// <summary>
    /// 读取被 <c>AutoQueryScript</c> 劫持到的页面自身查询响应。
    /// </summary>
    /// <remarks>
    /// jqGrid 把数据渲染进 DOM，但响应体本身是 JSON/JSONP，比解析 HTML 表格可靠得多，
    /// 也顺带把「页面到底发了什么参数」暴露出来——那正是靠猜猜不出来的东西。
    /// </remarks>
    private async Task<string> ReadCapturedAsync()
    {
        const string probe = """
            (function () {
              const c = window.__hamCaptured || [];
              if (!c.length) return '';
              const last = c[c.length - 1];
              return JSON.stringify({ url: last.url, body: last.body, response: last.response });
            })();
            """;

        try
        {
            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(probe).ConfigureAwait(true);
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            if (json.Length == 0) return string.Empty;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            var response = root.TryGetProperty("response", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;

            Log($"捕获到页面自身查询: {url}");
            Log($"  请求体: {body}");
            Log($"  响应 {response.Length} 字符: {response[..Math.Min(300, response.Length)]}");

            return response;
        }
        catch (Exception ex)
        {
            Log("读取捕获的查询失败", ex);
            return string.Empty;
        }
    }

    /// <summary>单页导航后等待渲染的时间上限。</summary>
    /// <remarks>
    /// 给足 3 分钟：成绩查询需要用户**亲手**完成图形验证码并点查询，
    /// 应用全程等待，不能催着用户。
    /// </remarks>
    private static readonly TimeSpan DocumentSettleTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 教务页面加载后自动补全查询条件并点击「查询」。
    /// </summary>
    /// <remarks>
    /// 教务的成绩/课表页面默认不发起查询：必须先选学年学期再点查询按钮才会渲染表格。
    /// 若不自动触发，读取到的正文只有二十几个字符的空壳。
    /// 这里只做「选第一个选项 + 点查询」，等价于用户在页面上的最少操作，不绕过任何校验。
    /// </remarks>
    /// <summary>
    /// 教务页面需要**人工完成**的操作提示脚本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 成绩查询页强制要求图形验证码。页面自己的逻辑（<c>/js/comp/jwglxt/cjgl/cjcx/cxDgXscj.js</c>）是：
    /// </para>
    /// <code>
    /// $("#search_go").click(function () {
    ///   if (($("#sfxyyzm").val() == "1" || ...) &amp;&amp; $("#jsxx").val() == "xs") {
    ///     popupCaptcha("div-data");   // 先弹验证码
    ///   } else { searchData(); }
    /// });
    /// </code>
    /// <para>
    /// 该生页面上 <c>sfxyyzm=1</c>，且加载了顶象验证码 SDK（<c>cdn.dingxiang-inc.com</c>）。
    /// 因此<b>不验证就不会发出查询请求</b>——这解释了「自动点击查询却一个请求都没有」。
    /// </para>
    /// <para>
    /// <b>刻意不自动点击、不尝试绕过验证码。</b>验证码是校方设置的反自动化手段，
    /// 绕过它既不合适也不可靠。正确做法是让用户像在浏览器里一样自己完成验证，
    /// 应用只负责把渲染好的结果读回来。
    /// </para>
    /// </remarks>
    private const string AutoQueryScript = """
        (function () {
          if (window.__hamAutoQuery) { return; }
          window.__hamAutoQuery = true;

          // ① 劫持 XHR：把页面真正发出的成绩查询请求与响应记录下来。
          //    响应体是干净的 JSON，比解析 jqGrid 生成的 DOM 可靠得多。
          window.__hamCaptured = [];
          var _open = XMLHttpRequest.prototype.open;
          var _send = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.open = function (m, u) {
            this.__hamUrl = u; this.__hamMethod = m;
            return _open.apply(this, arguments);
          };
          XMLHttpRequest.prototype.send = function (body) {
            var self = this;
            this.addEventListener('load', function () {
              try {
                window.__hamCaptured.push({
                  url: String(self.__hamUrl || ''),
                  method: String(self.__hamMethod || ''),
                  body: body == null ? '' : String(body),
                  status: self.status,
                  response: self.responseText
                });
              } catch (e) { /* 忽略 */ }
            });
            return _send.apply(this, arguments);
          });

          // ③ 自动反复点击「查询」，把验证码弹出来。
          //    注意两个坑：
          //    (a) 曾写成「按钮一出现就点一次然后停」——点击落在页面自身绑定处理器之前，click 落空；
          //    (b) 曾干脆完全不点——页面在等验证码，不会自己发查询，用户根本不知道要做什么，
          //        结果就是「验证码也没弹出来」。正确是**持续点**，直到验证码出现或拿到数据。
          var ticks = 0;
          var clicks = 0;
          var captchaShown = false;
          var timer = setInterval(function () {
            ticks++;

            // 学年/学期用页面默认的「全部」
            var xnm = document.getElementById('xnm');
            var xqm = document.getElementById('xqm');
            if (xnm && xnm.options && xnm.options.length > 1 && !xnm.dataset.hamDone) {
              xnm.dataset.hamDone = '1';
              xnm.selectedIndex = 0;
              xnm.dispatchEvent(new Event('change', { bubbles: true }));
            }
            if (xqm && xqm.options && xqm.options.length > 1 && !xqm.dataset.hamDone) {
              xqm.dataset.hamDone = '1';
              xqm.selectedIndex = 0;
              xqm.dispatchEvent(new Event('change', { bubbles: true }));
            }

            // 验证码弹窗一旦出现就停止点击，把操作权完全交回用户
            if (!captchaShown) {
              var cap = document.querySelector(
                '#captcha_div, .zfdun-captcha, [id*=captcha][style*=block], .dx-captcha');
              if (cap && cap.offsetParent !== null) { captchaShown = true; clearInterval(timer); }
            }

            // 延迟 1.5 秒再开始点，确保页面自身已绑定处理器
            if (ticks > 6 && !captchaShown && clicks < 8) {
              var btn = document.getElementById('search_go');
              if (btn) { clicks++; btn.click(); }
            }

            var grid = document.getElementById('tabGrid');
            if (grid && grid.rows.length > 1) { clearInterval(timer); }
            if (ticks > 240) { clearInterval(timer); }   // 约 60 秒后交还控制权
          }, 250);

          window.__hamStopClicking = function () { clearInterval(timer); };
        })();
        """;

    /// <summary>在当前页面上下文中执行 XHR 批次的代取。</summary>
    private async Task<FetchPlanResult> RunXhrAsync(IReadOnlyList<FetchTarget> targets)
    {
        var deadline = DateTime.UtcNow + FetchTimeout;
        string? injectedAtUrl = null;
        var started = false;

        while (DateTime.UtcNow < deadline)
        {
            var currentUrl = _webView.Source?.ToString() ?? string.Empty;

            // 文档被替换（导航完成）后 window.__hamFetch 会丢失，需要重新注入
            if (started && currentUrl != injectedAtUrl)
            {
                Log($"代取期间文档已切换，重新注入: {currentUrl}");
                started = false;
            }

            if (!started)
            {
                if (currentUrl.Length == 0)
                {
                    await Task.Delay(200).ConfigureAwait(true);
                    continue;
                }

                // 地址连续两次相同视为文档已稳定
                if (currentUrl != _lastUrl)
                {
                    _lastUrl = currentUrl;
                    await Task.Delay(300).ConfigureAwait(true);
                    continue;
                }

                try
                {
                    await _webView.CoreWebView2
                        .ExecuteScriptAsync(BuildFetchStartScript(targets)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log("代取脚本启动失败", ex);
                    return FetchPlanResult.Empty;
                }

                injectedAtUrl = currentUrl;
                started = true;
                Log($"在页面上下文中代取 {targets.Count} 个地址（文档 {currentUrl}）");
            }

            await Task.Delay(200).ConfigureAwait(true);
            if (_result.IsCompleted) return FetchPlanResult.Empty;

            string raw;
            try
            {
                raw = await _webView.CoreWebView2
                    .ExecuteScriptAsync(PollFetchScript).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("代取轮询失败", ex);
                return FetchPlanResult.Empty;
            }

            // done 为 false 时返回 null，表示"还没好"，继续轮询
            var result = FetchPlanResult.Parse(raw);
            if (result is null) continue;

            Log($"代取完成：{result.Bodies.Count} 项正文，{result.Failures.Count} 项失败");
            return result;
        }

        Log("代取超时");
        return FetchPlanResult.Empty;
    }

    /// <summary>代取任务在页面上的总时限。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>轮询页面内的代取状态。返回的是字符串，会被 <c>ExecuteScriptAsync</c> 再编码一层。</summary>
    private const string PollFetchScript = "JSON.stringify(window.__hamFetch || null)";

    /// <summary>启动页面内代取；异步执行，结果写入 <c>window.__hamFetch</c>。</summary>
    private static string BuildFetchStartScript(IReadOnlyList<FetchTarget> targets)
    {
        var requests = System.Text.Json.JsonSerializer.Serialize(targets.Select(t => new
        {
            path = t.Path,
            post = t.IsPost,
            form = t.Form?.ToDictionary(k => k.Key, v => v.Value),
        }));

        return $$"""
            (function () {
              const reqs = {{requests}};
              const state = window.__hamFetch =
                { done: false, bodies: {}, statuses: {}, failures: [] };

              const xhr = (r) => new Promise((resolve) => {
                const req = new XMLHttpRequest();
                req.open(r.post ? 'POST' : 'GET', r.path, true);
                req.withCredentials = true;
                // 与页面 jQuery $.post 行为一致：同源 + 会话 + Referer + XHR 标记
                req.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                if (r.post) {
                  req.setRequestHeader('Content-Type',
                                       'application/x-www-form-urlencoded; charset=UTF-8');
                }
                req.onload = () => resolve({ ok: true, status: req.status, text: req.responseText });
                req.onerror = () => resolve({ ok: false, status: 0, text: '' });
                req.ontimeout = () => resolve({ ok: false, status: 0, text: '' });
                req.timeout = 30000;
                if (r.post) {
                  const p = new URLSearchParams();
                  for (const k in (r.form || {})) { p.append(k, r.form[k]); }
                  req.send(p.toString());
                } else {
                  req.send();
                }
              });

              (async () => {
                for (let i = 0; i < reqs.length; i++) {
                  const r = reqs[i];
                  try {
                    const res = await xhr(r);
                    // 关键：非 2xx 也要保留正文。
                    // 教务大量使用自定义状态码（实测 910），而 910 的响应体
                    // 往往是**合法且有内容的 JSON**（例如成绩为空时返回 items:[]）。
                    // 若按惯例把非 2xx 当失败丢掉正文，就��"接口报错"与
                    // "查询成功但没有数据"混为一谈。
                    if (res.text) {
                      state.bodies[r.path] = res.text;
                      state.statuses[r.path] = res.status;
                    }
                    if (!(res.ok && res.status >= 200 && res.status < 300)) {
                      state.failures.push(r.path + ' -> HTTP ' + res.status + ' '
                        + (res.text ? res.text.slice(0, 200) : '(空正文)'));
                    }
                  } catch (e) {
                    state.failures.push(r.path + ' -> ' + String(e));
                  }
                }
                state.done = true;
              })();

              return 'started';
            })();
            """;
    }

    /// <summary>以成功结果结束流程（幂等）。</summary>
    private void Succeed(IReadOnlyList<CasCookie> cookies, FetchPlanResult payloads)
    {
        _pollTimer.Stop();
        if (!_result.TryComplete(new CasLoginOutcome(cookies, payloads)))
        {
            Log("结果已被抢先完成，丢弃本次收尾结果"
                + $"（本次 Cookie {cookies.Count} 项、正文 {payloads.Bodies.Count} 项）");
            return;
        }
        Close();
    }

    /// <summary>标记流程已结束（幂等）。用于窗口被直接关闭的情形。</summary>
    /// <remarks>
    /// <b>收尾进行中时不能以空结果收场。</b>
    /// 实测症状：日志先打「同步教务: 登录窗口未返回任何 Cookie」，
    /// 紧接着又打「完成登录…导出 Cookie 6 项; 代取 0 项」——
    /// 因为 <see cref="Succeed"/> 之前 <c>_result</c> 已被
    /// <c>EmptyOutcome</c>（0 Cookie）抢先完成，后来的真结果
    /// <c>TryComplete</c> 直接失败，**没有人再监听**。
    /// 一旦进入收尾阶段，结果的所有权就归收尾流程，窗口关闭也不能把它顶掉。
    /// </remarks>
    private void MarkCompleted()
    {
        _pollTimer.Stop();

        if (Volatile.Read(ref _completing) != 0)
        {
            // 正在收尾：让收尾流程自己交结果，这里不要抢先完成
            Log("收尾进行中，窗口关闭不再以空结果收场");
            return;
        }

        _result.TryComplete(EmptyOutcome);
    }

    private void OnWindowClosed()
    {
        _pollTimer.Stop();
        MarkCompleted();
    }

    private void Fail(string? reason = null)
    {
        // 关闭窗口期间不得再次调用 Close()，否则会抛
        // "Cannot set Visibility ... while a Window is closing"。
        if (_isClosing) return;
        if (_result.IsCompleted) return;

        if (!string.IsNullOrEmpty(reason))
        {
            Log("登录未完成：" + reason);
            _status.Text = reason;
        }

        _pollTimer.Stop();
        _result.TryComplete(EmptyOutcome);

        if (!IsVisible) return;

        _isClosing = true;
        try { Close(); } catch (Exception ex) { Log("关闭窗口失败", ex); }
    }

    private static void Log(string message, Exception? ex = null)
        => App.LogCritical("CAS 登录: " + message, ex);

    private void OpenInSystemBrowser(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // 外部浏览器启动失败不影响登录流程。
        }
    }

    /// <summary>构造预填脚本：注入 ham-rn 同款的本地化逻辑，并回填学号 / 密码。</summary>
    private static string BuildPrefillScript(string? user, string? password)
    {
        var localization = CasClient.BuildLocalizationScript();

        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password))
            return localization;

        var u = System.Text.Json.JsonSerializer.Serialize(user ?? string.Empty);
        var p = System.Text.Json.JsonSerializer.Serialize(password ?? string.Empty);

        return localization + $$"""

            (function () {
              var __hamUser = {{u}};
              var __hamPass = {{p}};
              if (!__hamUser && !__hamPass) { return; }

              // 表单可能异步渲染，因此做有上限的重试，而不是只试一次就放弃。
              var fill = function () {
                var form = document.getElementById('pwdFromId');
                var u = document.getElementById('username');
                var pw = document.getElementById('password');
                if (!form || !u || !pw) { return false; }

                if (__hamUser && !u.value) { u.value = __hamUser; }
                if (__hamPass && !pw.value) { pw.value = __hamPass; }

                // 部分前端框架会侦听 input/change 来同步内部状态，这里补发一次。
                try {
                  u.dispatchEvent(new Event('input', { bubbles: true }));
                  u.dispatchEvent(new Event('change', { bubbles: true }));
                  if (__hamPass) {
                    pw.dispatchEvent(new Event('input', { bubbles: true }));
                    pw.dispatchEvent(new Event('change', { bubbles: true }));
                  }
                } catch (e) { /* 忽略 */ }
                return true;
              };

              if (fill()) { return; }

              var tries = 0;
              var timer = setInterval(function () {
                tries++;
                if (fill() || tries > 40) { clearInterval(timer); }
              }, 150);
            })();
            """;
    }
}
