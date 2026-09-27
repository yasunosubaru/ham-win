using System.Text.Json;
using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2;
using Windows.Graphics;

namespace Ham.Ui.Services;

/// <summary>
/// 校巴数据抓取器。
/// </summary>
/// <remarks>
/// <b>为什么必须借 WebView2，不能直接 HttpClient。</b>
/// 校巴接口路径是
/// <c>base64(RSA签名(时间戳 + "/" + 真实路径)).replace("/", "*")</c>，
/// 而私钥在前端 JS 里是<b>故意截断</b>的，要靠 8 个 base64 片段才拼得全。
/// 与其在 .NET 里重实现一套容易出错、且随前端更新即失效的加密，
/// 更稳的做法是让<b>页面自己签名</b>——它本来就该发出这些请求，我们只在旁路记录。
/// 这与课表、成绩已验证的做法同源。
/// <para>
/// <b>另一个好处：完全不需要学生凭据。</b>
/// <c>/mobile/</c> 会跳统一身份认证，但 <c>/mobile/index.html</c> 直接 200，
/// 不在 CAS 保护范围内。所以校巴是唯一一个既能取真数据、又不碰账号密码的功能。
/// </para>
/// </remarks>
public sealed class BusFetcher : IDisposable
{
    private WebView2? _web;
    /// <summary>抓取全部已知线路（含站点与运营时段）。</summary>
    public async Task<IReadOnlyList<BusLine>> FetchAsync(
        IReadOnlyList<BusEndpoints.KnownLine> lines, CancellationToken ct = default)
    {
        await EnsureWebAsync();

        // 每条线路点一次，前端就会自己去拉对应的接口。
        // 但**不能只点一次**：SPA 起来有快有慢，页面上线路标签可能还没渲染，
        // 一次性点击很容易落空（实测就是只点一次拿到 0 条）。
        // 所以在轮询里反复点，拿到足够多的线路就停。
        var deadline = DateTime.UtcNow.AddSeconds(50);
        string report = string.Empty;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await _web!.CoreWebView2.ExecuteScriptAsync(BuildClickScript(lines));
            await Task.Delay(2000, ct);

            report = await EvalAsync(BusReportScript);
            if (CountLines(report) >= lines.Count) break;
        }

        // 收尾再点一轮，抓可能刚好回来的最后一批。
        // 用 _web!：上面的轮询里已经证明过它非空（EnsureWebAsync 成功后才会走到这），
        // 但编译器跨 await 之后无法推断，漏掉 ! 会报 CS8602。
        await _web!.CoreWebView2.ExecuteScriptAsync(BuildClickScript(lines));
        await Task.Delay(2500, ct);
        report = await EvalAsync(BusReportScript);

        return ParseLines(report, lines);
    }

    /// <summary>在应用自己的窗口里挂一个 WebView2 当抓取通道。</summary>
    private async Task EnsureWebAsync()
    {
        if (_web is not null) return;

        // WebView2 必须有宿主窗口。用一个尽量不打扰用户的小窗，
        // 抓完立刻关掉——它只是传输通道，不是界面。
        var host = new Window
        {
            Title = "校巴数据通道",
        };
        _web = new WebView2();
        host.Content = _web;
        host.Activate();

        // WinUI 3 的 Window 没有 Position 属性，要挪窗口得走 AppWindow。
        // 挪到屏幕外，避免抓取过程闪现一个小窗。
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            appWindow.Resize(new Windows.Graphics.SizeInt32(400, 300));
        }
        catch
        {
            // 挪不动就算了：它只是个通道窗口，显示出来也不影响功能。
        }

        await _web.EnsureCoreWebView2Async();

        // 在页面脚本执行前挂钩 XHR 与 fetch
        await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HookScript);

        _web.CoreWebView2.Navigate(BusEndpoints.PageUrl);
        await Task.Delay(6000);
    }

    private async Task<string> EvalAsync(string script)
    {
        try
        {
            var json = await _web!.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CountLines(string report)
        => report.Split('\n').Count(l => l.StartsWith("LINE|", StringComparison.Ordinal));

    private static string BuildClickScript(IReadOnlyList<BusEndpoints.KnownLine> lines)
    {
        // 前端把线路渲染成一排可点的标签。
        // 匹配放宽到「文本包含线路名」而不是全等——标签里常夹着图标或空白，
        // 全等匹配过一次都没中（实测）。
        var names = string.Join(",", lines.Select(l => $"\"{l.Name}\""));
        return $$"""
            (function () {
              const want = [{{names}}];
              const all = Array.from(document.querySelectorAll('*'));
              let n = 0;
              want.forEach(w => {
                // 先找叶子节点，找不到就退到任何包含该文本的元素
                let hit = all.find(x => x.children.length === 0
                          && (x.textContent||'').trim() === w)
                      || all.find(x => (x.textContent||'').indexOf(w) >= 0
                          && (x.textContent||'').length < w.length + 4);
                if (!hit) return;
                // 冒泡到最近的可点祖先，模拟真人点击
                let t = hit;
                for (let i = 0; i < 4 && t; i++) {
                  try { t.click(); n++; break; } catch (e) { break; }
                }
              });
              return 'clicked ' + n;
            })();
            """;
    }

    /// <summary>从探针报告文本里还原线路列表。</summary>
    private static IReadOnlyList<BusLine> ParseLines(
        string report, IReadOnlyList<BusEndpoints.KnownLine> known)
    {
        var result = new List<BusLine>();

        foreach (var raw in report.Split('\n'))
        {
            if (!raw.StartsWith("LINE|", StringComparison.Ordinal)) continue;

            var parts = raw.Split('|', 3);
            if (parts.Length < 3) continue;

            var fallback = known.FirstOrDefault(k => k.Name == parts[1]);
            var line = BusParser.ParseLine(parts[2], fallback?.Id ?? "", parts[1]);
            if (line is not null) result.Add(line);
        }

        return result;
    }

    // ── 注入脚本 ──

    /// <summary>
    /// 挂钩页面自身的 XHR / fetch，把 whubus 响应存到 <c>__hamCap</c>。
    /// </summary>
    private const string HookScript = """
        (function () {
          window.__hamCap = [];
          const XO = XMLHttpRequest.prototype.open;
          const XS = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.open = function (m, u) {
            this.__hamUrl = String(u);
            return XO.apply(this, arguments);
          };
          XMLHttpRequest.prototype.send = function () {
            this.addEventListener('load', function () {
              try {
                if (!/whubus/i.test(this.__hamUrl || '')) return;
                window.__hamCap.push({ url: this.__hamUrl, body: (this.responseText||'').slice(0, 200000) });
              } catch (e) {}
            });
            return XS.apply(this, arguments);
          };
          const OF = window.fetch;
          if (OF) {
            window.fetch = function (u, o) {
              return OF.apply(this, arguments).then(function (r) {
                try {
                  if (!/whubus/i.test(String(u))) return r;
                  r.clone().text().then(function (t) {
                    window.__hamCap.push({ url: String(u), body: t.slice(0, 200000) });
                  });
                } catch (e) {}
                return r;
              });
            };
          }
        })();
        """;

    /// <summary>把抓到的响应整理成便于解析的文本。</summary>
    private const string BusReportScript = """
        (function () {
          const c = window.__hamCap || [];
          const o = [];
          const seen = {};
          c.forEach(x => {
            const t = (x.body || '');
            let name = '';
            try { name = (JSON.parse(t).data || {}).lineName || ''; } catch (e) {}
            if (!name) return;                 // 只要线路详情，折线/车辆另说
            if (seen[name]) return;           // 同一条线路点多次只取一次
            seen[name] = 1;
            o.push('LINE|' + name + '|' + t);
          });
          return o.join('\n');
        })();
        """;

    /// <summary>
    /// 释放抓取通道。
    /// </summary>
    /// <remarks>
    /// WinUI 3 的 <c>WebView2</c> 控件没有 <c>Dispose</c>（那是 WPF 版才有的），
    /// 所以这里只断开引用；宿主窗口由页面切换时的垃圾回收带走。
    /// </remarks>
    public void Release()
    {
        _web = null;
    }

    void IDisposable.Dispose() => Release();
}
