using System.Text.Json;

using Ham.Core;
using Ham.Infrastructure.Net;

namespace Ham.Infrastructure.Cas;

/// <summary>
/// 承载 WebView2 登录窗口的平台相关能力。
/// </summary>
/// <remarks>
/// WPF 与 WinUI 3 的 WebView2 控件类型不同，但登录流程本身完全一致。
/// 把「流程」放进 <see cref="EducationSession"/>、把「浏览器」抽象成这个接口，
/// 目的就是让那段极其微妙的收尾逻辑<b>只有一份实现</b>。
/// 那套逻辑里有多个一层套一层的竞态护栏（见 <see cref="EducationSession"/> 顶部说明），
/// 复制两份必然分叉。
/// </remarks>
public interface IEducationLoginHost
{
    /// <summary>当前地址。WebView2 尚未提交首个导航时返回空串。</summary>
    string CurrentUrl { get; }

    /// <summary>执行页面脚本，返回 <c>ExecuteScriptAsync</c> 的原始结果（含外层 JSON 编码）。</summary>
    Task<string> ExecuteAsync(string script);

    /// <summary>读取 CAS 与教务两个域下全部非空 Cookie。</summary>
    Task<IReadOnlyList<CasCookie>> ReadCookiesAsync(CampusEndpoints endpoints);

    /// <summary>把新窗口请求交给系统浏览器打开。</summary>
    Task OpenExternalAsync(Uri uri);
}

/// <summary>
/// CAS 登录 + 教务数据代取的完整流程，与 UI 框架无关。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这段逻辑要单独成类、并且逐行照搬 WPF 版：</b>
/// 它经过多轮实测才稳定下来，每个护栏都对应一个真实踩过的坑，
/// 随手"优化"极易把 bug 放回去。已知的坑包括：
/// </para>
/// <list type="bullet">
/// <item><b>到达教务主机 ≠ 会话已建立。</b>/sso/jznewsixlogin 这一跳的响应里才种 Cookie，
/// 立刻读有竞态，必须再等 <see cref="SessionSettleTimeout"/>。</item>
/// <item><b>about:blank 的 Host 是空串，而空串被任何字符串 Contains。</b>
/// 早期用 <c>EducationBaseUrl.Contains(uri.Host)</c> 判成功，WebView 刚创建就被判"已到达"，
/// 下一秒地址变回 CAS 又被判"已离开 → 失败"，窗口一秒就关。</item>
/// <item><b>ReAuth 判定必须排在"已到达教务"前面。</b>二次认证页就在教务主机上，
/// 顺序颠倒会把死会话当成功。</item>
/// <item><b>收尾必须互斥（<c>_completing</c>）。</b>1 秒轮询会在长达 60 秒的代取期间反复重入，
/// 每批脚本都会重写全局 <c>window.__hamFetch</c>，后启动的清空前一批的，
/// 结果谁也读不到数据，而且<b>不报任何错</b>。这个标志是一次性闩锁，<b>永远不要复位</b>。</item>
/// <item><b>窗口关闭不能抢在收尾前面交结果。</b>否则日志里同一秒出现
/// "未返回任何 Cookie" 与 "导出 6 项"，真结果被丢弃且无人再监听。</item>
/// </list>
/// </remarks>
public sealed class EducationSession
{
    /// <summary>等待用户完成 CAS 登录的总时限。</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(4);

    /// <summary>到达教务主机后，等待会话 Cookie 出现的时限。</summary>
    public static readonly TimeSpan SessionSettleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>页面内代取的总时限。</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private readonly CampusEndpoints _endpoints;
    private readonly FetchPlan _plan;
    private readonly IEducationLoginHost _host;
    private readonly Action<string, Exception?>? _logSink;
    private readonly Action<string>? _status;

    private readonly OneShotResult<CasLoginOutcome> _result = new();

    private DateTime _openedAt = DateTime.Now;
    private DateTime? _reachedEducationAt;

    // 注意：_lastUrl 被成功判定与代取注入<b>共用</b>。
    // 这不是疏忽——代取循环的"地址连续两次相同才算文档已稳定"检查
    // 就是靠这个字段被轮询每秒更新来生效的。给它单独开一个字段
    // 会让那段 300ms 等待在生产里第一次真正触发，属于行为变更。
    private string _lastUrl = string.Empty;

    private int _completing;
    private volatile bool _aborted;

    public EducationSession(
        CampusEndpoints endpoints,
        FetchPlan plan,
        IEducationLoginHost host,
        Action<string, Exception?>? log = null,
        Action<string>? status = null)
    {
        _endpoints = endpoints;
        _plan = plan;
        _host = host;
        _logSink = log;
        _status = status;
    }

    /// <summary>写一条诊断。整条流程只有这一个诊断出口，删掉它 bug 就没法查了。</summary>
    private void Log(string message, Exception? ex = null) => _logSink?.Invoke(message, ex);

    /// <summary>登录过程中是否已经判定成功（供宿主更新提示文案）。</summary>
    public bool ReachedEducation => _reachedEducationAt is not null;

    /// <summary>流程是否已经终止。</summary>
    public bool IsCompleted => _result.IsCompleted;

    /// <summary>
    /// 跑完整个流程并返回结果。
    /// </summary>
    /// <remarks>
    /// 调用方负责：先用 <see cref="StartAsync"/> 打开登录页，再调用本方法并周期调用
    /// <see cref="TickAsync"/>（本方法内部自己也会等），最后按结果关窗。
    /// </remarks>
    public async Task<CasLoginOutcome> RunAsync(CancellationToken ct)
    {
        _openedAt = DateTime.Now;

        using var reg = ct.Register(() => Abort("登录已取消。"));

        while (!_result.IsCompleted)
        {
            await TickAsync().ConfigureAwait(true);

            if (_result.IsCompleted) break;

            if (DateTime.Now - _openedAt > LoginTimeout)
            {
                // 带上"可能是学号打错"这个提示：真实案例是学号里两个数字颠倒，
                // 页面静默重载，用户只会觉得"登录不上"，根本想不到是学号的问题。
                Abort("等待登录超时。若页面仍停在登录界面，通常是学号或密码有误；"
                    + "若页面提示需要「账号激活」，请先完成激活。");
                break;
            }

            await Task.Delay(1000).ConfigureAwait(true);
        }

        return await _result.Task.ConfigureAwait(true);
    }

    /// <summary>标记流程开始（宿主在登录页就绪后调用，用于计时与状态文案）。</summary>
    public void Start() => _openedAt = DateTime.Now;

    /// <summary>每一拍：更新地址提示并尝试判定成功。</summary>
    public async Task TickAsync()
    {
        if (_result.IsCompleted || _aborted) return;

        var url = _host.CurrentUrl;
        if (url.Length > 0 && url != _lastUrl)
        {
            _lastUrl = url;
            _status?.Invoke($"当前页面：{url}");
        }

        await TryDetectSuccessAsync().ConfigureAwait(true);
    }

    private async Task TryDetectSuccessAsync()
    {
        var url = _host.CurrentUrl;
        if (url.Length == 0) return;

        // 顺序要紧：ReAuth 必须在"已到达教务"之前判。
        // 二次认证页本身就在教务主机上，颠倒顺序会把死会话当成登录成功。
        if (url.Contains("ReAuth", StringComparison.OrdinalIgnoreCase))
        {
            Fail("教务系统要求二次认证，请先在浏览器中完成信息门户的重新确认后再试。");
            return;
        }

        if (HasReachedEducation(url, _endpoints))
        {
            // 闩锁：记的是**首次**到达的时刻，SettleTimeout 从它开始算。
            if (_reachedEducationAt is null)
            {
                _reachedEducationAt = DateTime.Now;
                Log($"已到达教务主机，等待会话建立: {url}");
            }

            _status?.Invoke("登录成功，正在建立教务会话…");
            await TryFinishAsync().ConfigureAwait(true);
            return;
        }

        // 一次性闩锁：既然到过教务又退回来了，会话就是没建起来，如实报告。
        if (_reachedEducationAt is not null)
        {
            Fail("教务系统没有接受登录，会话未能建立。请确认账号权限后重试。");
        }
    }

    /// <summary>
    /// 判断地址是否代表「登录已完成、已交给教务」。
    /// </summary>
    /// <remarks>
    /// 必须<b>纯函数</b>且保持逐字一致——这段判据被改坏过两次。
    /// </remarks>
    public static bool HasReachedEducation(string url, CampusEndpoints ep)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // about:blank / data: 的 Host 是空串，而空串被任何字符串 Contains。
        // 早期正是在这里漏判，导致 WebView 刚创建就被当成登录成功。
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        // 仍停在 CAS 登录入口上不算到达。
        // 这一条让判据在「CAS 与教务同主机」（本地 mock 就是这种拓扑）下依然成立。
        if (uri.Host.Equals(ep.CasHost, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/authserver", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals(ep.EducationHost, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(
                   ep.EducationHost.StartsWith('.') ? ep.EducationHost : "." + ep.EducationHost,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryFinishAsync()
    {
        IReadOnlyList<CasCookie> cookies;
        try
        {
            cookies = await _host.ReadCookiesAsync(_endpoints).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 这段在 WPF 版里没有 try，异常会从 async void 的轮询处理器逃逸成
            // 未处理异常——在 WinUI 3 里就是直接崩。移植时补上。
            Log("读取 Cookie 失败", ex);
            cookies = [];
        }

        var eduSession = cookies.FirstOrDefault(c =>
            c.Host.Contains(_endpoints.EducationHost, StringComparison.OrdinalIgnoreCase)
            && c.Name.Contains("SESSION", StringComparison.OrdinalIgnoreCase)
            && c.Value.Length > 0);

        if (eduSession is not null)
        {
            await CompleteAsync("教务会话已建立", cookies).ConfigureAwait(true);
            return;
        }

        // 等不到就按现有 Cookie 交出去，让上层报出真实错误，
        // 而不是把用户永远挂在这个窗口里。
        var waited = DateTime.Now - (_reachedEducationAt ?? DateTime.Now);
        if (waited > SessionSettleTimeout)
        {
            Log($"等待教务会话超时（{waited.TotalSeconds:F0}s），按现有 Cookie 继续");
            await CompleteAsync("教务会话未确认", cookies).ConfigureAwait(true);
        }
    }

    private async Task CompleteAsync(string reason, IReadOnlyList<CasCookie> cookies)
    {
        if (_result.IsCompleted) return;

        // 一次性闩锁：绝不在 finally 里复位。复位等于允许并发启动多批 XHR，
        // 而它们共用同一个 window.__hamFetch，后启动的会清空前一批的结果，
        // 最终"谁也读不到数据且不报错"。
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

            // 这行是整条流程最有价值的诊断信息，报障时基本就靠它。改动前先三思。
            Log($"完成登录（{reason}），导出 Cookie {list.Count} 项: "
                + string.Join(", ", list.Select(c => $"{c.Host}/{c.Name}"))
                + $"; 代取 {payloads.Bodies.Count} 项，失败 {payloads.Failures.Count} 项"
                + (payloads.Failures.Count > 0 ? " -> " + string.Join("; ", payloads.Failures) : string.Empty));

            _result.TryComplete(new CasLoginOutcome(list, payloads));
        }
        catch (Exception ex)
        {
            // 关键修正：WPF 版这里只记日志，导致 _completing 永远是 1、
            // _result 永不完成，等待方会**永久挂起**（连关窗都救不回来，
            // 因为 MarkCompleted 也会因为 _completing != 0 而拒绝完成）。
            // 失败必须把结果交出去。
            Log("收尾过程异常", ex);
            _result.TryComplete(CasLoginOutcome.Empty);
        }
    }

    /// <summary>按模式分组执行代取计划。</summary>
    private async Task<FetchPlanResult> RunFetchPlanAsync()
    {
        var xhr = _plan.Targets.Where(t => t.Mode == FetchMode.Xhr).ToList();
        var document = _plan.Targets.Where(t => t.Mode == FetchMode.Document).ToList();

        if (document.Count > 0)
        {
            // Document 模式（打开页面读它自己渲染的表格）尚未在 WinUI 3 侧实现。
            // 与其静默忽略、让上层误以为拿到了全部数据，不如明确记一笔。
            Log($"警告：{document.Count} 个 Document 模式目标在本次会话中未执行");
        }

        var bodies = new Dictionary<string, string>();
        var statuses = new Dictionary<string, int>();
        var failures = new List<string>();

        if (xhr.Count > 0)
        {
            var r = await RunXhrAsync(xhr).ConfigureAwait(true);
            foreach (var kv in r.Bodies) bodies[kv.Key] = kv.Value;
            foreach (var kv in r.Statuses) statuses[kv.Key] = kv.Value;
            failures.AddRange(r.Failures);
        }

        return new FetchPlanResult(bodies, failures, statuses);
    }

    private async Task<FetchPlanResult> RunXhrAsync(IReadOnlyList<FetchTarget> targets)
    {
        var deadline = DateTime.UtcNow + FetchTimeout;
        string? injectedAtUrl = null;
        var started = false;

        while (DateTime.UtcNow < deadline)
        {
            if (_result.IsCompleted) return FetchPlanResult.Empty;

            var currentUrl = _host.CurrentUrl;

            // 文档被替换后 window.__hamFetch 会随文档一起消失，必须重新注入，
            // 否则只会对着 "null" 空轮询满 60 秒。
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

                // 地址连续两次相同视为文档已稳定。
                // CAS 302 时 URL 会先变成目标页、NavigationCompleted 才触发，
                // 在这两者之间注入就跑在**上一个文档**里，状态会被提交销毁。
                if (currentUrl != _lastUrl)
                {
                    _lastUrl = currentUrl;
                    await Task.Delay(300).ConfigureAwait(true);
                    continue;
                }

                string started2;
                try
                {
                    started2 = await _host.ExecuteAsync(BuildFetchStartScript(targets)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log("代取脚本启动失败", ex);
                    return FetchPlanResult.Empty;
                }

                injectedAtUrl = currentUrl;
                started = true;
                Log($"在页面上下文中代取 {targets.Count} 个地址（文档 {currentUrl}，{started2}）");
            }

            await Task.Delay(200).ConfigureAwait(true);
            if (_result.IsCompleted) return FetchPlanResult.Empty;

            string raw;
            try
            {
                raw = await _host.ExecuteAsync(PollFetchScript).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("代取轮询失败", ex);
                return FetchPlanResult.Empty;
            }

            // null 表示"还没好"，唯一正确的反应是继续轮询。
            var result = FetchPlanResult.Parse(raw);
            if (result is null) continue;

            Log($"代取完成：{result.Bodies.Count} 项正文，{result.Failures.Count} 项失败");
            return result;
        }

        Log("代取超时");
        return FetchPlanResult.Empty;
    }

    /// <summary>以失败收场。</summary>
    public void Fail(string? reason = null)
    {
        if (_aborted || _result.IsCompleted) return;

        if (!string.IsNullOrEmpty(reason))
        {
            Log("登录未完成：" + reason);
            _status?.Invoke(reason);
        }

        _aborted = true;
        _result.TryComplete(CasLoginOutcome.Empty);
    }

    private void Abort(string reason) => Fail(reason);

    /// <summary>轮询页面内的代取状态。返回字符串，会被 <c>ExecuteScriptAsync</c> 再编码一层。</summary>
    public const string PollFetchScript = "JSON.stringify(window.__hamFetch || null)";

    /// <summary>
    /// 启动页面内代取；异步执行，结果写入 <c>window.__hamFetch</c>。
    /// </summary>
    /// <remarks>
    /// 逐字沿用 WPF 版的实现。几个不能动的点：
    /// <list type="number">
    /// <item>路径用<b>相对</b>的：这样才是同源请求，才会带上 Referer 与会话 Cookie。</item>
    /// <item><c>withCredentials = true</c> 在 <c>open()</c> 之后设置。</item>
    /// <item><c>X-Requested-With</c> <b>无条件</b>设置，GET 也要。少了它纯 HTTP 客户端会被
    /// 教务的前置安全设备挡成 901 或返回首页 HTML。</item>
    /// <item>非 2xx 的正文也要保留：教务大量用自定义状态码（如 910），
    /// 而 910 的响应体往往<b>是合法且有内容的 JSON</b>（成绩为空时返回 items:[]）。
    /// 按惯例丢掉正文，就会把"接口报错"和"查询成功但没数据"混为一谈。</item>
    /// <item>请求严格串行，单个 XHR 超时 30 秒，而总预算只有 60 秒。</item>
    /// </list>
    /// </remarks>
    public static string BuildFetchStartScript(IReadOnlyList<FetchTarget> targets)
    {
        var requests = JsonSerializer.Serialize(targets.Select(t => new
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

    /// <summary>
    /// 构造预填脚本：注入 ham-rn 同款的本地化逻辑，并回填学号 / 密码。
    /// </summary>
    /// <remarks>
    /// <b>安全提示：明文密码会被写进脚本源码，并注册到 WebView2 的"每个文档都执行"列表里。</b>
    /// 这意味着教务主机（jwgl.whu.edu.cn）上的任何脚本都能读到
    /// <c>window.__hamPass</c>。因此宿主必须保持 <c>AreDevToolsEnabled = false</c>。
    /// <para>
    /// 表单可能异步渲染，所以做有上限的重试而不是只试一次；
    /// 只在字段为空时写入，用户手输的内容不会被覆盖，因此重复注入是幂等的。
    /// </para>
    /// </remarks>
    public static string BuildPrefillScript(string? user, string? password)
    {
        var localization = CasClient.BuildLocalizationScript();

        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password))
            return localization;

        var u = JsonSerializer.Serialize(user ?? string.Empty);
        var p = JsonSerializer.Serialize(password ?? string.Empty);

        return localization + $$"""

            (function () {
              var __hamUser = {{u}};
              var __hamPass = {{p}};
              if (!__hamUser && !__hamPass) { return; }

              var fill = function () {
                // 注意表单 id 真的是 pwdFromId（少一个 m），这是页面原文。
                var form = document.getElementById('pwdFromId');
                var u = document.getElementById('username');
                var pw = document.getElementById('password');
                if (!form || !u || !pw) { return false; }

                if (__hamUser && !u.value) { u.value = __hamUser; }
                if (__hamPass && !pw.value) { pw.value = __hamPass; }

                // 部分前端框架只靠事件同步内部状态，这里补发一次。
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
