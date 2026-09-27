using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Ham.App.Services;
using Ham.Infrastructure.Cas;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LiveProbe;

/// <summary>
/// 活站点探针：登录武大，然后在**真实页面上下文**里读出验证码与评教的真实契约。
/// </summary>
/// <remarks>
/// 为什么不用纯 HttpClient：教务与顶象验证码都会校验请求来源，
/// 同一个 URL 从 HttpClient 打过来返回 HTML 首页，从页面里 XHR 才返回 JSON。
/// 所以必须借 WebView2 在页面内执行 JS。
/// <para>
/// 凭据只从环境变量 <c>HAM_STUDENT_ID</c> / <c>HAM_PORTAL_PASSWORD</c> 读，
/// 绝不作为命令行参数（进程列表可见）、也绝不落盘。
/// </para>
/// </remarks>
internal static class Program
{
    private const string ScoreUrl =
        "https://jwgl.whu.edu.cn/cjcx/cjcx_cxDgXscj.html?gnmkdm=N305005";
    private const string ReviewUrl = "https://ugsqs.whu.edu.cn/caslogin/";

    /// <summary>不带 Edg/ 标记的普通 Chrome UA。</summary>
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    [STAThread]
    private static int Main()
    {
        // 探针输出全是中文诊断信息。控制台默认按 GBK 输出会变成乱码，
        // 读日志的人根本没法判断看到了什么，所以先强制 UTF-8。
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var code = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { code = await RunAsync(); }
            catch (Exception ex) { Console.WriteLine("[致命] " + ex); }
            finally { app.Dispatcher.Invoke(() => app.Shutdown()); }
        };
        app.Run();
        return code;
    }

    private static async Task<int> RunAsync()
    {
        // 校巴模式：完全不需要登录。
        // bus.whu.edu.cn/mobile/index.html 不在 CAS 保护范围内（实测 200），
        // 而 /mobile/ 才会 302 到统一认证。SPA 自己会带签名去调接口，
        // 我们只要在页面上下文里把它的 XHR 响应抓下来即可。
        // 这样做**完全不用学生的账号密码**，既没有凭据外泄风险，也没有触发 CAS 限流的风险。
        if (Environment.GetEnvironmentVariable("HAM_PROBE_BUS") == "1")
        {
            Console.WriteLine("校巴探测模式（不使用任何凭据）");
            Console.WriteLine("");
            var (busWeb, _) = await OpenWebViewAsync([]);
            await ProbeBusAsync(busWeb);
            return 0;
        }

        var id = Environment.GetEnvironmentVariable("HAM_STUDENT_ID");
        var pwd = Environment.GetEnvironmentVariable("HAM_PORTAL_PASSWORD");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pwd))
        {
            Console.WriteLine("需要环境变量 HAM_STUDENT_ID / HAM_PORTAL_PASSWORD");
            Console.WriteLine("或设 HAM_PROBE_BUS=1 跑不需要凭据的校巴探测");
            return 2;
        }

        Console.WriteLine($"学号 {id}（长度 {id.Length}），密码已从环境变量读取，不回显");
        Console.WriteLine("");

        CasLoginOutcome outcome = default!;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Console.WriteLine($"[登录] 第 {attempt} 次尝试");

            // CAS 真实登录页必须**点击**「登录」才提交表单。
            // 只点一次：曾因连点两次，第二次落在跳转后的新页面上，提交了空表单——
            // 那是一次真实失败登录，重复足以锁号。
            var clicker = StartLoginClicker();

            outcome = await CasLoginWindow.ShowAsync(
                owner: null, id, pwd, CancellationToken.None,
                endpoints: null, fetchPlan: FetchPlan.Empty);

            clicker.Stop();

            Console.WriteLine($"  结束：Cookie {outcome.Cookies.Count} 项");
            if (outcome.Cookies.Count > 0) break;

            if (attempt < 2)
            {
                Console.WriteLine("  没拿到 Cookie，等 5 秒重试");
                await Task.Delay(5000);
            }
        }

        foreach (var c in outcome.Cookies)
            Console.WriteLine($"  {c.Host}/{c.Name} = {Mask(c.Value)}");
        Console.WriteLine("");

        if (outcome.Cookies.Count == 0)
        {
            Console.WriteLine("两次都没拿到任何 Cookie，后续探测无意义。");
            return 1;
        }

        // 把 jwgl 的会话写进指定文件，供 tools/recon 的爬虫做存在性判定。
        // 只写 jwgl 域下的会话 Cookie，不含 CAS 侧，也不回显到控制台。
        // 它是短生命周期的会话凭据，用完由调用方删除。
        if (Environment.GetEnvironmentVariable("HAM_DUMP_COOKIES") == "1"
            && Environment.GetEnvironmentVariable("HAM_COOKIE_FILE") is { Length: > 0 } cookieFile)
        {
            var pairs = outcome.Cookies
                .Where(c => c.Host.Contains("jwgl.whu.edu.cn", StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name}={c.Value}")
                .ToList();
            File.WriteAllText(cookieFile, string.Join("; ", pairs));
            Console.WriteLine($"[Cookie] 已写出 {pairs.Count} 项到 {cookieFile}");
            Console.WriteLine("");
        }

        var (web, host) = await OpenWebViewAsync(outcome.Cookies);
        await ProbeScoreInteractiveAsync(web, host);
        return 0;
    }

    // ─────────────────────────── 成绩：可交互探测 ───────────────────────────

    /// <summary>
    /// 成绩探测：**需要人在窗口里配合过一次图形验证码**。
    /// </summary>
    /// <remarks>
    /// 关键事实（都已实测）：
    /// <list type="bullet">
    /// <item>查询表单与 <c>#search_go</c> 在 <c>cxDgXscj</c> 上，而它是<b>唯一没有 jQuery</b> 的页面，
    /// 内联脚本全抛 ReferenceError，所以它自己的查询链路与验证码都是死的。</item>
    /// <item><c>cxXsgrcj</c> 是结果接收方（grid 的 data action），但它<b>没有表单</b>。</item>
    /// <item><c>cxXscjIndex</c>（成绩查询首页）<b>自带 jQuery</b>——它才是该操作的页面。</item>
    /// </list>
    /// 所以从这里打开首页，看它把表单放在哪、点击后 XHR 打到哪。
    /// <para>
    /// <b>不绕过验证码。</b>脚本只做两件事：周期性点「查询」把顶象弹出来，
    /// 以及被动记录页面自己发出的请求。滑块必须人过。
    /// </para>
    /// </remarks>
    private static async Task ProbeScoreInteractiveAsync(WebView2 web, Window host)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("成绩探测：绕开坏页面，直接发那个 XHR");
        Console.WriteLine(new string('=', 70));

        // 只需要被动记录；不注入 jQuery，不依赖页面那套死掉的查询链路
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HookOnlyScript);

        // 先建会话上下文
        await NavAsync(web, "https://jwgl.whu.edu.cn/xtgl/index_initMenu.html", 25_000);
        await Task.Delay(3000);

        // 进入成绩页，让同源 XHR 能带上会话
        await NavAsync(web, "https://jwgl.whu.edu.cn/cjcx/cjcx_cxDgXscj.html?gnmkdm=N305005", 25_000);
        await Task.Delay(5000);

        Console.WriteLine("--- 页面状态 ---");
        Console.WriteLine(await EvalAsync(web, StateJs));
        Console.WriteLine("");

        try { host.Activate(); } catch { /* 置前失败不影响探测 */ }

        // ═══ 决定性实验：cxXsgrcj 这个动作到底强制要 validate 吗？ ═══
        // 之前只在**带真 token** 的情况下打过它，从没试过不带 token。
        // 如果不带也能出成绩，那整个验证码环节就可以直接删掉。
        Console.WriteLine("--- 不带 validate 打 cxXsgrcj（决定性实验）---");
        Console.WriteLine(await EvalAsync(web, NoTokenJs));
        Console.WriteLine("");

        // ── 不再猜动作：让页面跑它自己的代码 ──
        //
        // 已经排除的：doType=query（恒 57B fail）、doType=list（恒 392B / 0 行，
        // 22 个学年学期组合全试过，含历史学年），doType 各种值、GET/POST、
        // 参数放 body 还是 query、validate 有无、顶象 token 真实有效。
        //
        // 剩下的信息缺口只有一个：**页面自己的 search() 到底发什么请求**。
        // 而看它的唯一办法是让页面那段死掉的脚本活过来。
        // 关键事实：**页面加载完之后注入 jQuery 是成功的**（早前实测 jQuery=1.12.4），
        // 失败的只有 document-created 阶段。
        // 所以：DOM ready 后注入 jQuery + 补 jQuery.founded，
        // 再手工调用页面原生的 paramMap() 与 search()，让 hook 记录它发出的 XHR。

        Console.WriteLine("--- 加载后补 jQuery ---");
        Console.WriteLine(await EvalAsync(web, PostLoadJQueryJs));
        await Task.Delay(2500);
        Console.WriteLine(await EvalAsync(web, DxStatusJs));
        Console.WriteLine("");

        Console.WriteLine("--- 页面原生 search / paramMap 的真实源码 ---");
        Console.WriteLine(await EvalAsync(web, NativeFnsJs));
        Console.WriteLine("");

        Console.WriteLine("--- 手工调用页面原生的 search('tabGrid', paramMap()) ---");
        Console.WriteLine(await EvalAsync(web, CallNativeSearchJs));
        await Task.Delay(12000);

        Console.WriteLine("--- 它发出的请求（由页面内 hook 记录）---");
        Console.WriteLine(await EvalAsync(web, DxResultJs));
        Console.WriteLine("");

        Console.WriteLine("=== 如果它要验证码，现在过滑块 ===");
        Console.WriteLine("初始化顶象: " + await EvalAsync(web, DxInitJs));
        Console.WriteLine("");

        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            var st = await EvalAsync(web, DxStatusJs);
            if (st.Contains("RESULT>>")) { Console.WriteLine(await EvalAsync(web, DxResultJs)); return; }
            if (st.Contains("ERR>>")) { Console.WriteLine("失败: " + await EvalAsync(web, DxResultJs)); return; }
        }

        Console.WriteLine("最终状态: " + await EvalAsync(web, DxStatusJs));
        Console.WriteLine("最终记录:\n" + await EvalAsync(web, DxResultJs));
    }

    /// <summary>
    /// 不带 validate 打成绩接口。
    /// </summary>
    /// <remarks>
    /// 这是决定「能不能彻底去掉验证码」的一步：<c>sfxyyzm=1</c> 说的是**页面**的验证码开关，
    /// 未必等于**这个接口**强制校验 validate。之前只在带真 token 时打过它，
    /// 从没试过不带——所以「必须拖滑块」这个结论其实一直缺一次验证。
    /// </remarks>
    private const string NoTokenJs = """
        (function () {
          const P = '/cjcx/cjcx_cxXsgrcj.html?doType=query';
          const FIVE = 'xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs'
                      + '&queryModel.showCount=150&queryModel.currentPage=1';
          const out = [];
          function go(label, body) {
            try {
              const x = new XMLHttpRequest();
              x.open('POST', P, false);
              x.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
              x.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
              x.send(body);
              const t = (x.responseText || '').trim();
              const has = /"kcmc"/.test(t);
              out.push('  ' + label.padEnd(26) + ' HTTP ' + String(x.status).padEnd(4)
                     + ' len ' + String(t.length).padEnd(8)
                     + (has ? '  <<<<<< 不用验证码就出成绩了！' : '')
                     + '  ' + t.slice(0, 130).replace(/\s+/g, ' '));
              return { t, has };
            } catch (e) { out.push('  ' + label + ' 抛错 ' + e); return { t:'', has:false }; }
          }
          out.push('动作: POST ' + P);
          out.push('');
          const a = go('完全不带 validate', FIVE);
          if (a.has) { out.push('', '完整响应:', a.t.slice(0, 4000)); return out.join('\n'); }
          go('validate 空串', FIVE + '&validate=');
          go('validate=sl+随机数', FIVE + '&validate=sl' + Date.now());
          go('validate=test', FIVE + '&validate=test');
          return out.join('\n');
        })();
        """;

    private const string PostLoadJQueryJs = """
        (function () {
          if (window.jQuery) return '页面已有 jQuery ' + window.jQuery.fn.jquery;
          // 同步取教务自己的第一方 jQuery 并 eval。
          // **这个阶段（DOM ready 之后）是能成功的**——
          // document-created 阶段不行，那时 window.document 元素树还没就绪。
          try {
            const x = new XMLHttpRequest();
            x.open('GET', '/zftal-ui-v5-1.0.2/assets/js/other_jquery/jquery.min.js', false);
            x.send(null);
            if (x.status !== 200) return '拉取 jQuery 失败 HTTP ' + x.status;
            (0, eval)(x.responseText);
            // jQuery.founded 是教务自建扩展，captcha.js 的 popupCaptcha 第一行就依赖它
            if (window.jQuery && !jQuery.founded) {
              jQuery.founded = function (v) {
                return v !== null && v !== undefined
                    && String(v).trim() !== '' && String(v) !== '0';
              };
            }
            return '注入成功 jQuery=' + window.jQuery.fn.jquery
                 + '  founded=' + typeof jQuery.founded;
          } catch (e) { return '注入抛错: ' + e; }
        })();
        """;

    private const string NativeFnsJs = """
        (function () {
          const o = [];
          for (const fn of ['search', 'paramMap', 'refershGrid', 'searchData', 'popupCaptcha',
                            'getGridColModel', 'initCaptchaObj', 'initGrid', 'loadGrid']) {
            o.push('typeof ' + fn + ' = ' + typeof window[fn]);
          }
          o.push('');
          for (const fn of ['search', 'paramMap', 'refershGrid']) {
            if (typeof window[fn] === 'function') {
              o.push('=== ' + fn + '.toString() ===');
              o.push(String(window[fn]).slice(0, 1800));
              o.push('');
            }
          }
          return o.join('\\n');
        })();
        """;

    private const string CallNativeSearchJs = """
        (function () {
          const o = [];
          if (typeof paramMap === 'function') {
            try { o.push('paramMap() = ' + JSON.stringify(paramMap())); }
            catch (e) { o.push('paramMap() 抛错: ' + e); }
          } else { o.push('没有 paramMap'); }

          if (typeof search === 'function') {
            try { o.push('调用 search("tabGrid", paramMap()) …');
                  o.push('返回值: ' + String(search('tabGrid', paramMap())));
                  window.__hamSt = '已调用原生 search'; }
            catch (e) { o.push('search() 抛错: ' + e + ' | ' + (e.stack||'').slice(0,300)); }
          } else {
            o.push('没有 search —— 改试 grid 自身的 reload');
            try {
              const $g = window.jQuery && window.jQuery('#tabGrid');
              if ($g && $g.length) {
                const p = $g.jqGrid('getGridParam', 'postData');
                o.push('#tabGrid 存在。jqGrid postData = ' + JSON.stringify(p));
                o.push('#tabGrid url = ' + JSON.stringify($g.jqGrid('getGridParam', 'url')));
                $g.trigger('reloadGrid');
                o.push('已触发 reloadGrid');
              } else { o.push('#tabGrid 不存在'); }
            } catch (e) { o.push('grid 操作抛错: ' + e); }
          }
          return o.join('\\n');
        })();
        """;

    private const string DxBoxJs = """
        (function () {
          const b = document.getElementById('__hamCapBox');
          if (!b) return '(方框不存在)';
          const r = b.getBoundingClientRect();
          const cs = getComputedStyle(b);
          const kid = b.querySelectorAll('*').length;
          return '位置 ' + Math.round(r.left) + ',' + Math.round(r.top)
               + ' 尺寸 ' + Math.round(r.width) + 'x' + Math.round(r.height)
               + ' display=' + cs.display + ' visibility=' + cs.visibility
               + ' 子元素 ' + kid
               + ' innerHTML 长度 ' + b.innerHTML.length
               + ' 文本 "' + (b.innerText||'').replace(/\s+/g,' ').trim().slice(0,60) + '"'
               + ' canvas=' + b.querySelectorAll('canvas').length
               + ' iframe=' + b.querySelectorAll('iframe').length
               + ' img=' + b.querySelectorAll('img').length;
        })();
        """;

    private const string DxConfigJs = """
        (function () {
          const ins = Array.from(document.querySelectorAll('input'));
          return '隐藏域:\\n  ' + ins.map(i => (i.id||i.name||'-') + '=' + String(i.value||'').slice(0,30))
                   .filter(s => s.indexOf('-=') < 0).join('\\n  ')
               + '\\n顶象全局: _dx=' + typeof window._dx
               + '  dx=' + typeof window.dx
               + '  dxCaptcha=' + typeof window.dxCaptcha
               + (window._dx ? '  _dx.Captcha=' + typeof window._dx.Captcha : '');
        })();
        """;

    private const string DxInitJs = """
        (function () {
          try {
            if (!window._dx || !window._dx.Captcha) return 'NO_SDK: 顶象 SDK 没加载（_dx=' + typeof window._dx + '）';

            // 顶象 appId：页面隐藏域里找，兼容 id / name 两种写法
            let appId = '';
            for (const i of document.querySelectorAll('input')) {
              const k = (i.id || '') + ' ' + (i.name || '');
              if (/appid/i.test(k) && i.value) { appId = i.value.trim(); break; }
            }
            if (!appId) return 'NO_APPID（所有输入框: ' +
              Array.from(document.querySelectorAll('input')).map(i => i.id + '=' + i.value).join(' | ') + '）';

            const api = (document.getElementById('apiServer')||{}).value
                     || 'https://www.dingxiang-inc.com';

            const box = document.createElement('div');
            box.id = '__hamCapBox';
            box.style.cssText = 'position:fixed;left:24px;top:24px;z-index:2147483000;'
              + 'background:#fff;padding:10px;border:3px solid #f60;max-width:420px;'
              + 'font:14px "Microsoft YaHei",sans-serif;box-shadow:0 4px 18px rgba(0,0,0,.3)';
            box.innerHTML = '<div style="padding:6px 4px">正在向顶象请求验证码…</div>';
            document.body.appendChild(box);

            window.__hamSt = '已弹窗 appId=' + appId;

            window.__hamCapIns = window._dx.Captcha(box, {
              apiServer: api,
              appId: appId,
              // 必须用 embed（嵌入式）。popup 模式下顶象会把容器设成 display:none
              // 等待 instance.show()，实测：控件其实**已经完整渲染**
              // （49 个子元素 / 10 张图 / 文本含「请拖动左侧滑块还原图片」），
              // 但容器 0x0、display:none，所以用户根本看不到。
              style: 'embed',
              success: function (token) {
                box.innerHTML = '<div style="padding:8px 4px;color:#080">验证通过，正在取成绩…</div>';
                window.__hamSt = '已通过，token 长度 ' + String(token).length;
                window.__hamToken = String(token);
                // 拿到真 token 后立刻发请求。
                //
                // 关键（用户点破）：页面默认学年是**当前学年**（2026 = 2026-2027 学年），
                // 而当前学年的成绩**还没出**，所以 totalResult 一直是 0——
                // 之前把 0 行误判成「参数没绑上」。
                // 正确做法是把**历年各学期**全扫一遍。
                // 学期码实测：3 = 第一学期, 12 = 第二学期, 16 = 第三学期。
                try {
                  const T = encodeURIComponent(token);
                  const FIVE = 'xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs'
                              + '&queryModel.showCount=150&queryModel.currentPage=1&validate=' + T;
                  const lines = ['token 长度 ' + String(token).length,
                    '*** 关键遗漏：grid 的 url 对 jsxx==\'xs\' 是 cxXsgrcj.html?doType=query，',
                    '    我之前只打过 cxDgXscj.html?doType=query。现在补上。', ''];

                  function go(label, url, body) {
                    const x = new XMLHttpRequest();
                    x.open('POST', url, false);
                    x.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                    x.setRequestHeader('Content-Type',
                                       'application/x-www-form-urlencoded; charset=UTF-8');
                    x.send(body);
                    const t = (x.responseText || '').trim();
                    const has = /"kcmc"/.test(t);
                    lines.push('  ' + label.padEnd(48) + ' HTTP ' + String(x.status).padEnd(4)
                             + ' len ' + String(t.length).padEnd(8)
                             + (has ? '  <<<<<< 有成绩！' : '')
                             + '  ' + t.slice(0, 160).replace(/\s+/g, ' '));
                    return { t, has };
                  }

                  // 学生分支的真实 URL —— 这是页面 grid 配置里 jsxx=='xs' 时用的
                  let r = go('POST /cjcx/cjcx_cxXsgrcj.html?doType=query',
                             '/cjcx/cjcx_cxXsgrcj.html?doType=query', FIVE);
                  if (r.has) { lines.push('', r.t.slice(0, 12000)); window.__hamRes = lines.join('\n'); window.__hamSt='RESULT>>'; return; }

                  r = go('同上 + gnmkdm', '/cjcx/cjcx_cxXsgrcj.html?doType=query&gnmkdm=N305005', FIVE);
                  if (r.has) { lines.push('', r.t.slice(0, 12000)); window.__hamRes = lines.join('\n'); window.__hamSt='RESULT>>'; return; }

                  r = go('同上 去掉 zd_fzdm', '/cjcx/cjcx_cxXsgrcj.html?doType=query',
                         'xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&validate=' + T);
                  if (r.has) { lines.push('', r.t.slice(0, 12000)); window.__hamRes = lines.join('\n'); window.__hamSt='RESULT>>'; return; }

                  r = go('同上 去掉分页参数', '/cjcx/cjcx_cxXsgrcj.html?doType=query',
                         'xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs&validate=' + T);
                  if (r.has) { lines.push('', r.t.slice(0, 12000)); window.__hamRes = lines.join('\n'); window.__hamSt='RESULT>>'; return; }

                  // 对照：历年各学期在学生分支 URL 上
                  lines.push('', '-- 若上面还不行，历年各学期换 --');
                  for (const y of ['2026','2025','2024','2023']) {
                    for (const q of ['3','12','16']) {
                      const rr = go('xsgrcj ' + y + '-' + q,
                            '/cjcx/cjcx_cxXsgrcj.html?doType=query',
                            'xnm=' + y + '&xqm=' + q + '&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs&validate=' + T);
                      if (rr.has) {
                        lines.push('', '完整响应(' + y + '-' + q + '):', rr.t.slice(0, 12000));
                        window.__hamRes = lines.join('\n'); window.__hamSt = 'RESULT>>'; return;
                      }
                    }
                  }
                  window.__hamRes = lines.join('\n');
                } catch (e) {
                  window.__hamRes = 'XHR 抛错: ' + e;
                }
                window.__hamSt = 'RESULT>>';
                box.innerHTML = '<div style="padding:8px 4px">完成，结果见控制台</div>';
              },
              error: function (e) {
                window.__hamRes = '验证失败: ' + JSON.stringify(e);
                window.__hamSt = 'ERR>>';
              }
            });
            // 顶象有时仍会把容器置为 display:none / 0 尺寸，这里强制拉回来。
            box.style.display = 'block';
            box.style.visibility = 'visible';
            box.style.minWidth = '360px';
            box.style.minHeight = '260px';
            try { if (window.__hamCapIns && window.__hamCapIns.show) window.__hamCapIns.show(); } catch (e) {}
            return 'OK  api=' + api + '  appId=' + appId + '  style=embed  （已调用 _dx.Captcha）';
          } catch (e) {
            return '初始化抛错: ' + e + '\\n' + (e.stack || '');
          }
        })();
        """;

    private const string DxStatusJs = """
        (function () {
          return '状态: ' + (window.__hamSt || '(未初始化)')
               + '  token=' + (window.__hamToken ? '长度' + window.__hamToken.length : '(无)');
        })();
        """;

    private const string DxResultJs = """
        (function () { return window.__hamRes || '(无结果)'; })();
        """;

    private const string MatrixJs = """
        (function () {
          var pv = function (id) { var e = document.getElementById(id); return e ? e.value : ''; };
          var XN = pv('xnm') || '2026';
          var XQ = pv('xqm') || '3';

          function fire(label, method, url, body, full) {
            try {
              var x = new XMLHttpRequest();
              x.open(method, url, false);
              x.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
              if (body !== null) x.setRequestHeader('Content-Type',
                          'application/x-www-form-urlencoded; charset=UTF-8');
              x.send(body);
              var t = (x.responseText || '').trim();
              var head = t.slice(0, 100).replace(/\s+/g, ' ');
              var tag = '';
              if (/"kcmc"/.test(t)) tag = '  <<<< 含课程名，有数据！';
              else if (/"currentResult"\s*:\s*0/.test(t)) tag = '  (信封有但 0 行)';
              else if (/请先登录|authserver/.test(t)) tag = '  (掉登录)';
              return '  ' + label.padEnd(40) + ' HTTP ' + String(x.status).padEnd(4)
                   + ' len ' + String(t.length).padEnd(7) + head + tag
                   + (full ? '\n      >>> ' + t.slice(0, 1600) : '');
            } catch (e) {
              return '  ' + label.padEnd(40) + ' 抛错: ' + e;
            }
          }

          var L = '/cjcx/cjcx_cxDgXscj.html?doType=list';
          var out = ['页面默认 xnm=' + XN + '  xqm=' + XQ,
                     '已确认 doType=query 恒返回 57 字节 fail；doType=list 返回 911 + 标准列表信封', ''];

          out.push('== 基线：把 911 的信封完整打出来 ==');
          out.push(fire('最小参数', 'POST', L, 'xnm=&xqm=&zd_fzdm=N305005-xs', true));

          out.push('== 每次只改一处，看是否出行 ==');
          out.push(fire('带 xnm/xqm', 'POST', L, 'xnm=' + XN + '&xqm=' + XQ + '&zd_fzdm=N305005-xs'));
          out.push(fire('+sy_id+sq_id', 'POST', L,
              'xnm=' + XN + '&xqm=' + XQ + '&sy_id=&sq_id=&zd_fzdm=N305005-xs'));
          out.push(fire('+sfzgcj', 'POST', L,
              'xnm=' + XN + '&xqm=' + XQ + '&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs'));
          out.push(fire('+分页150', 'POST', L,
              'xnm=' + XN + '&xqm=' + XQ + '&zd_fzdm=N305005-xs&queryModel.showCount=150&queryModel.currentPage=1'));
          out.push(fire('gnmkdm 也在 url', 'POST', L + '&gnmkdm=N305005',
              'xnm=' + XN + '&xqm=' + XQ + '&zd_fzdm=N305005-xs'));
          out.push(fire('zd_fzdm 用 gly', 'POST', L,
              'xnm=' + XN + '&xqm=' + XQ + '&zd_fzdm=N305005-gly'));
          out.push(fire('不带 zd_fzdm', 'POST', L, 'xnm=' + XN + '&xqm=' + XQ));
          out.push(fire('showCount=50', 'POST', L,
              'xnm=' + XN + '&xqm=' + XQ + '&zd_fzdm=N305005-xs&queryModel.showCount=50&queryModel.currentPage=1'));

          out.push('== 对照：课表接口（已知可用，同站点同调用方式）==');
          out.push(fire('课表 xskbcx_cxXsgrkb', 'POST', '/kbcx/xskbcx_cxXsgrkb.html?gnmkdm=N2151',
              'xnm=2026&xqm=3&xzlx=ck&validate='));

          return out.join('\n');
        })();
        """;

    private const string HookOnlyScript = """
        (function () {
          window.__hamScore = [];
          var XO = XMLHttpRequest.prototype.open;
          var XS = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.open = function (m, u) {
            this.__hamUrl = String(u);
            return XO.apply(this, arguments);
          };
          XMLHttpRequest.prototype.send = function () {
            this.addEventListener('load', function () {
              try {
                if (!/cjcx/i.test(this.__hamUrl || '')) return;
                window.__hamScore.push({ url: this.__hamUrl, status: this.status,
                                         body: (this.responseText || '').slice(0, 200000) });
              } catch (e) {}
            });
            return XS.apply(this, arguments);
          };
        })();
        """;

    private const string StateJs = """
        (function () {
          const g = id => { const e = document.getElementById(id); return e ? e.value : '(无)'; };
          const opts = id => Array.from((document.getElementById(id)||{options:[]}).options)
            .map(o => o.value + '=' + o.text).join(' , ');
          let s = 'location: ' + location.href;
          s += '\njsxx=' + g('jsxx') + '  sfxyyzm=' + g('sfxyyzm') + '  cxyzmlx=' + g('cxyzmlx');
          s += '  validate=' + String(g('validate')).slice(0,40);
          s += '\nxnm 选项: ' + opts('xnm');
          s += '\nxqm 选项: ' + opts('xqm');
          s += '\nsy_id_cx=' + g('sy_id_cx') + '  sq_id_cx=' + g('sq_id_cx') + '  sfzgcj=' + g('sfzgcj');
          s += '\n正文前 200: ' + (document.body ? document.body.innerText.replace(/\s+/g,' ').trim().slice(0,200) : '(无)');
          return s;
        })();
        """;

    /// <summary>
    /// 同源、带 X-Requested-With、参数照 paramMap 学生分支，**不带 validate**。
    /// </summary>
    /// <remarks>
    /// 用<b>同步</b> XHR：WebView2 的 <c>ExecuteScriptAsync</c> 不会 await Promise，
    /// 异步写返回的是序列化后的 <c>{}</c>（踩过：探测一度毫无输出）。
    /// 此处页面已完全加载，同步阻塞无副作用。
    /// </remarks>
    private const string DirectScoreXhrJs = """
        (function () {
          var pv = function (id) { var e = document.getElementById(id); return e ? e.value : ''; };
          var xn = pv('xnm'), xq = pv('xqm');
          var body = 'xnm=' + encodeURIComponent(xn)
                   + '&xqm=' + encodeURIComponent(xq)
                   + '&sy_id=&sq_id=&sfzgcj='
                   + '&zd_fzdm=N305005-xs'
                   + '&queryModel.showCount=150&queryModel.currentPage=1';
          var out = [];
          out.push('发送参数: ' + body);
          try {
            var x = new XMLHttpRequest();
            x.open('POST', '/cjcx/cjcx_cxDgXscj.html?doType=query', false);
            x.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
            x.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
            x.send(body);
            var t = x.responseText || '';
            out.push('HTTP ' + x.status + '  长度 ' + t.length
              + '  首字符 ' + JSON.stringify(t.charAt(0))
              + '  contentType ' + (x.getResponseHeader('Content-Type') || '(无)'));
            out.push('--- 正文前 3000 字 ---');
            out.push(t.slice(0, 3000));
          } catch (e) {
            out.push('抛错: ' + e);
          }
          return out.join('\n');
        })();
        """;

    /// <summary>带上页面下拉里实际的学年/学期值再试一次。</summary>
    private const string DirectScoreXhrWithSelJs = """
        (function () {
          var pv = function (id) { var e = document.getElementById(id); return e ? e.value : ''; };
          var xn = pv('xnm'), xq = pv('xqm');
          return '将使用 xnm=' + xn + '  xqm=' + xq + '  sy_id=' + pv('sy_id_cx') + '  sq_id=' + pv('sq_id_cx');
        })();
        """;

    private const string MenuDumpJs = """
        (function () {
          try {
            const o = [];
            const html = document.documentElement.outerHTML;
            // clickMenu('N305005','/cjcx/xxx.html','标题') 形式
            const re = /clickMenu\s*\(\s*['"]([^'"]*)['"]\s*,\s*['"]([^'"]*)['"]\s*,\s*['"]([^'"]*)['"]/g;
            let m;
            while ((m = re.exec(html)) !== null) {
              o.push('  ' + m[1].padEnd(10) + ' ' + m[3] + '  ->  ' + m[2]);
            }
            o.push('--- 所有带 data-dyym / onclick 的元素 ---');
            document.querySelectorAll('[data-dyym],[onclick]').forEach(x => {
              const t = (x.textContent || '').trim().replace(/\s+/g, ' ');
              const h = x.getAttribute('data-dyym') || x.getAttribute('onclick') || '';
              if (t && t.length < 40) o.push('  ' + t + '   ||   ' + h.slice(0, 120));
            });
            o.push('--- 正文 ---');
            o.push('  ' + (document.body ? document.body.innerText.replace(/\s+/g,' ').trim().slice(0,900) : '(无)'));
            return o.join('\n');
          } catch (e) { return '抛错: ' + e; }
        })();
        """;

    private const string MenuPathsJs = """
        (function () {
          try {
            const html = document.documentElement.outerHTML;
            const set = {};
            (html.match(/['"](\/[a-zA-Z0-9_\-]+\/[a-zA-Z0-9_\-]+\.html[^'"]*)['"]/g) || [])
              .forEach(s => set[s.slice(1, -1)] = 1);
            return Object.keys(set).sort().map(s => '  ' + s).join('\n') || '  (无)';
          } catch (e) { return '抛错: ' + e; }
        })();
        """;

    /// <summary>判断这个页面是真页面还是 zftal 的「方法未定义」错误页。</summary>
    private const string PageTruthJs = """
        (function () {
          const t = (document.body ? document.body.innerText : '').replace(/\s+/g,' ').trim();
          const isErr = t.indexOf('未定义') >= 0 || t.indexOf('警告') >= 0;
          return (isErr ? '错误页: ' : '真实页: ') + t.slice(0, 70)
               + '   jQuery=' + (typeof window.jQuery)
               + '  #search_go=' + !!document.getElementById('search_go');
        })();
        """;

    /// <summary>把教务自己的 jQuery 注入进来（同步加载，保证后续脚本能直接用）。</summary>
    private const string InjectJQueryJs = """
        (function () {
          if (window.jQuery) return '已存在 jQuery，无需注入';
          try {
            var x = new XMLHttpRequest();
            x.open('GET', '/zftal-ui-v5-1.0.2/assets/js/other_jquery/jquery.min.js', false);
            x.send(null);
            if (x.status !== 200) return '拉取失败 HTTP ' + x.status;
            (0, eval)(x.responseText);
            return '注入结果: jQuery=' + typeof window.jQuery
                 + (window.jQuery ? ' 版本 ' + window.jQuery.fn.jquery : '');
          } catch (e) { return '注入抛错: ' + e; }
        })();
        """;

    /// <summary>看 jQuery 补上之后，查询按钮到底有没有绑上处理器。</summary>
    private const string HandlerJs = """
        (function () {
          try {
            const b = document.getElementById('search_go');
            if (!b) return '#search_go 不存在';
            const ev = $._data(b, 'events');
            if (!ev) return 'jQuery 事件表为空 —— 处理器仍然没有绑上';
            const keys = Object.keys(ev).map(k => k + '×' + ev[k].length).join(', ');
            let src = '';
            if (ev.click) src = ev.click.map(h => String(h.handler).slice(0, 600)).join('\n---\n');
            return '#search_go 事件: ' + keys + '\n点击处理器:\n' + src;
          } catch (e) { return '读事件抛错: ' + e; }
        })();
        """;

    /// <summary>
    /// 在<b>任何页面脚本之前</b>把教务自己的 jQuery 装上，然后被动记录请求。
    /// </summary>
    /// <remarks>
    /// 时序是关键。先前是在页面加载完之后才注入 jQuery，虽然
    /// <c>window.jQuery</c> 有了，但页面内联脚本早在注入前就已抛过
    /// ReferenceError，<c>$("#search_go").click(...)</c> 根本没绑上——
    /// 实测「jQuery 事件表为空」。必须用
    /// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> 在文档创建时同步装好，
    /// 页面自己的脚本才会认为 jQuery 一直在。
    /// <para>
    /// 同步 XHR 是刻意的：异步的话 jQuery 装好时页面脚本已经跑完了。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 在<b>任何页面脚本之前</b>把教务自己的 jQuery 装上，然后被动记录请求。
    /// </summary>
    /// <remarks>
    /// 两个时序坑，都踩过：
    /// <list type="number">
    /// <item>
    /// 先前是页面加载完才注入 jQuery —— 页面内联脚本早已抛过 ReferenceError，
    /// <c>$("#search_go").click(...)</c> 根本没绑上（实测「jQuery 事件表为空」）。
    /// </item>
    /// <item>
    /// 改成在文档创建时用<b>同步 XHR</b> 去拉 jQuery 再 eval —— 会抛
    /// <c>Cannot read properties of undefined (reading 'createElement')</c>：
    /// 那个阶段 <c>window.document</c> 还没就绪，jQuery 自己的工厂代码读不到它。
    /// </list>
    /// 所以正确做法是：<b>在 C# 侧用 HttpClient 取回源码</b>（静态资源，免凭据），
    /// 再以内联脚本注入。既绕开 document 时序，也不依赖页面内 XHR。
    /// </remarks>
    private static string BuildPreloadScript(string jQuerySource)
    {
        // 成绩页加载了 pinyin-pro.js 等 UMD 模块，页面上会存在 module/exports。
        // jQuery 1.12.4 的 UMD 一旦看到 `typeof module === "object" && typeof module.exports === "object"`
        // 就把导出写进 module.exports，**不会**挂到 window.jQuery——
        // 症状是脚本明明执行了（无报错）但 window.jQuery 仍是 undefined。
        // 先把 CommonJS / AMD 的三个全局抹掉，强制走浏览器分支。
        return """
        ;(function () {
          window.__hamRan = 1;
          window.__hamPre = { module: typeof module, define: typeof define,
                              exports: typeof exports, window: typeof window,
                              doc: typeof document };
        })();
        var module = undefined, exports = undefined, define = undefined;
        """ + jQuerySource + """
        ;(function () {
          window.__hamJqLoaded = typeof window.jQuery;
          // 教务自己给 jQuery 加的扩展。官方 jQuery 没有 founded()，
          // 而 captcha.js 的 popupCaptcha() 第一行就是 jQuery.founded(...)，缺了照样抛。
          try {
            if (window.jQuery && !jQuery.founded) {
              jQuery.founded = function (v) {
                return v !== null && v !== undefined
                    && String(v).trim() !== '' && String(v) !== '0';
              };
            }
          } catch (e) { window.__hamJqErr2 = String(e); }

          // 被动记录页面自己发出的 cjcx 请求，不改写任何东西
          window.__hamScore = [];
          var XO = XMLHttpRequest.prototype.open;
          var XS = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.open = function (m, u) {
            this.__hamUrl = String(u);
            return XO.apply(this, arguments);
          };
          XMLHttpRequest.prototype.send = function () {
            this.addEventListener('load', function () {
              try {
                if (!/cjcx/i.test(this.__hamUrl || '')) return;
                window.__hamScore.push({ url: this.__hamUrl, status: this.status,
                                         body: (this.responseText || '').slice(0, 60000) });
              } catch (e) {}
            });
            return XS.apply(this, arguments);
          };
        })();
        """;
    }

    /// <summary>jQuery 的第一方地址（登录页用的就是它）。</summary>
    private const string JQueryUrl =
        "https://jwgl.whu.edu.cn/zftal-ui-v5-1.0.2/assets/js/other_jquery/jquery.min.js";

    private static string _preload = null!;

    private static async Task<string> GetPreloadScriptAsync()
    {
        if (_preload is not null) return _preload;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var src = await http.GetStringAsync(JQueryUrl);
        Console.WriteLine($"[jQuery] 已取回 {JQueryUrl}  {src.Length} 字符");
        Console.WriteLine($"[jQuery] 开头: {src[..Math.Min(80, src.Length)]}");

        _preload = BuildPreloadScript(src);
        return _preload;
    }

    private const string IndexAnatomyJs = """
        (function () {
          try {
            const o = [];
            o.push('location: ' + location.href);
            o.push('jQuery: ' + typeof window.jQuery + (window.jQuery ? ' ' + window.jQuery.fn.jquery : ''));
            o.push('#search_go: ' + !!document.getElementById('search_go'));
            o.push('jsxx: ' + String((document.getElementById('jsxx')||{}).value));
            o.push('sfxyyzm: ' + String((document.getElementById('sfxyyzm')||{}).value));
            o.push('cxyzmlx: ' + String((document.getElementById('cxyzmlx')||{}).value));
            o.push('xnm 选项: ' + Array.from((document.getElementById('xnm')||{options:[]}).options)
                 .map(x => x.value + '=' + x.text).join(', '));
            o.push('xqm 选项: ' + Array.from((document.getElementById('xqm')||{options:[]}).options)
                 .map(x => x.value + '=' + x.text).join(', '));
            o.push('iframe: ' + document.querySelectorAll('iframe').length);
            o.push('正文: ' + (document.body ? document.body.innerText.replace(/\s+/g,' ').trim().slice(0,500) : '(无)'));
            o.push('可点元素:');
            Array.from(document.querySelectorAll('a[href],button,input[type=button],input[type=submit]'))
              .slice(0,30).forEach(x => o.push('  <' + x.tagName.toLowerCase() + ' id=' + (x.id||'-') +
                '> "' + (x.textContent||x.value||'').trim().replace(/\s+/g,' ').slice(0,28) +
                '" href=' + (x.getAttribute('href') || '-')));
            o.push('script:');
            Array.from(document.scripts).map(s=>s.src).filter(Boolean)
              .forEach(s => o.push('  ' + s));
            return o.join('\n');
          } catch (e) { return '抛错: ' + e; }
        })();
        """;

    /// <summary>周期性点「查询」类元素，把顶象弹出来（不代过滑块）。</summary>
    private const string ClickQueryJs = """
        (function () {
          try {
            const c = Array.from(document.querySelectorAll('a[href],button,input[type=button],input[type=submit],li,span,div'))
              .filter(x => x.children.length === 0 &&
                    /^(查询|查 询|成绩查询|按学期查询|全部)$/.test((x.textContent||'').trim()));
            if (!c.length) return '0';
            c[0].click();
            return 'clicked ' + c.length;
          } catch (e) { return 'err ' + e; }
        })();
        """;

    private const string ScoreStateJs = """
        (function () {
          const d = document.getElementById('captcha_div');
          let s = '注入脚本跑到=' + window.__hamRan;
          s += '  装jQuery后=' + window.__hamJqLoaded;
          s += '  页面环境' + JSON.stringify(window.__hamPre || {});
          s += '\njQuery=' + (window.jQuery ? window.jQuery.fn.jquery : 'undefined');
          s += '  founded=' + (window.jQuery ? typeof jQuery.founded : '-');
          s += '  founded2=' + String(window.__hamJqErr2 || '无');
          s += '\ncaptcha_div=' + (d ? (d.childElementCount > 0 ? '有内容' : '空') : '无');
          s += '  myCaptcha=' + typeof window.myCaptcha;
          s += '  dxCaptcha=' + typeof window.dxCaptcha;
          s += '  cxyzmlx=' + String((document.getElementById('cxyzmlx')||{}).value);
          s += '  validate=' + String((document.getElementById('validate')||{}).value || '').slice(0,40);
          s += '  已记请求=' + (window.__hamScore||[]).length;
          return s;
        })();
        """;

    private const string ScoreReportJs = """
        (function () {
          const c = window.__hamScore || [];
          const o = [];
          c.forEach(x => {
            const t = (x.body || '').trim();
            o.push('URL: ' + x.url);
            o.push('HTTP ' + x.status + '  长度 ' + t.length + '  首字符 ' + JSON.stringify(t.charAt(0)));
            o.push('正文前 1200 字: ' + t.slice(0, 1200));
            o.push('');
          });
          // 出现 items 且非空才算真的拿到成绩
          for (let i = 0; i < c.length; i++) {
            const t = c[i].body || '';
            if (t.indexOf('"items"') >= 0 && t.indexOf('"items":[]') < 0) {
              return 'BODY|' + i + '\n' + o.join('\n');
            }
          }
          return o.length ? '（还没有非空 items）\n' + o.join('\n') : '（还没有记录到任何 cjcx 请求）';
        })();
        """;

    // ─────────────────────────── 校巴：契约已从 app.js 读出，现在抓真实响应 ───────────────────────────

    /// <summary>
    /// 打开校巴 SPA，把它自己发出的接口响应原样抓下来。
    /// </summary>
    /// <remarks>
    /// 前端把 RSA 私钥内嵌在 <c>app.js</c> 里，接口路径是
    /// <c>base64(RSA签名(时间戳 + "/" + 真实路径)).replace("/", "*")</c>，
    /// 密钥字面量还是残缺的（要靠 8 个 base64 片段拼全）。
    /// 与其在 .NET 里重实现这套加密，<b>更可靠的做法是让页面自己签名</b>——
    /// 它本来就该发出这些请求，我们只在旁路记录。
    /// 这样也不需要碰学生凭据。
    /// </remarks>
    private static async Task ProbeBusAsync(WebView2 web)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("校巴 bus.whu.edu.cn");
        Console.WriteLine(new string('=', 70));

        // 用页面内 XHR/fetch 劫持记录响应，而不是 CoreWebView2.WebResourceReceived
        // ——本项目的 WebView2 版本没有那个事件，而劫持 XHR 是课表已验证可行的路子。
        var preload = """
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
                    window.__hamCap.push({
                      url: this.__hamUrl,
                      status: this.status,
                      body: (this.responseText || '').slice(0, 20000)
                    });
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
                        window.__hamCap.push({ url: String(u), status: r.status, body: t.slice(0, 20000) });
                      });
                    } catch (e) {}
                    return r;
                  });
                };
              }
            })();
            """;
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(preload);

        // index.html 不在 CAS 保护范围，直接开
        await NavAsync(web, "https://bus.whu.edu.cn/mobile/index.html", 40_000);
        await Task.Delay(8000);

        Console.WriteLine("页面标题: " + await EvalAsync(web, "document.title"));
        Console.WriteLine("渲染出的文字: "
            + await EvalAsync(web,
                "(document.body?document.body.innerText:'').replace(/\\s+/g,' ').trim().slice(0,400)"));
        Console.WriteLine("");

        // SPA 起来后自己会拉数据；点一下让它把接口都走一遍
        Console.WriteLine("点击: " + await EvalAsync(web, BusClickJs));
        await Task.Delay(9000);
        Console.WriteLine("再点: " + await EvalAsync(web, BusClickJs));
        await Task.Delay(9000);

        Console.WriteLine(new string('-', 70));
        Console.WriteLine(await EvalAsync(web, BusReportJs));
    }

    private const string BusReportJs = """
        (function () {
          const c = window.__hamCap || [];
          const o = ['抓到 ' + c.length + ' 个 whubus 响应'];
          c.forEach(x => {
            o.push('');
            o.push('URL: ' + x.url);
            o.push('HTTP: ' + x.status + '   长度: ' + (x.body||'').length);
            o.push('响应: ' + (x.body||'(空)').slice(0, 1500));
          });
          return o.join('\n');
        })();
        """;

    private const string BusClickJs = """
        (function () {
          try {
            const hits = [];
            document.querySelectorAll('*').forEach(x => {
              if (x.children.length === 0) {
                const t = (x.textContent||'').trim();
                if (/线路|路线|站点|校车|查询|全部|1号|2号|5号/.test(t) && t.length < 20) hits.push(x);
              }
            });
            hits.slice(0, 6).forEach(x => { try { x.click(); } catch (e) {} });
            return '点了 ' + hits.length + ' 个可点元素: '
                 + hits.slice(0,6).map(x => (x.textContent||'').trim()).join(' / ');
          } catch (e) { return '点击抛错: ' + e; }
        })();
        """;

    // ─────────────────────────── 成绩页：验证码为什么没弹出来 ───────────────────────────

    private static async Task ProbeScoreAsync(WebView2 web)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("成绩页：验证码契约");
        Console.WriteLine(new string('=', 70));

        // 关键：**不能直接深链**成绩页。
        // 直接访问 /cjcx/... 会被 302 踢回 /xtgl/login_slogin.html，
        // 于是页面上根本没有 #search_go、#captcha_div、popupCaptcha——
        // 看起来就像「验证码没弹出来」，其实是压根没进到成绩页。
        // 必须先访问 index_initMenu.html 让教务把会话上下文建起来，
        // 这也是产品里 SyncEducationAsync 的既有做法。
        Console.WriteLine("[步骤] 先建立教务会话上下文 index_initMenu.html");
        await NavAsync(web, "https://jwgl.whu.edu.cn/xtgl/index_initMenu.html", 25_000);
        Console.WriteLine("  落地: " + await EvalAsync(web, "location.href"));
        Console.WriteLine("  正文前 200 字: "
            + await EvalAsync(web, "(document.body?document.body.innerText:'').slice(0,200).replace(/\\s+/g,' ')"));

        // 关键：ZFTAL 这类系统把子页面放在 iframe 里，jQuery 由父页面提供。
        // 直接把子页面当顶层文档打开，拿到的是一份**没有 jQuery 的残缺模板**，
        // 于是它内联的 jQuery(...) 调用全抛异常，查询按钮和验证码都成了死代码——
        // 这正是「用户在浏览器里能看到验证码，WebView2 里却弹不出来」的原因。
        Console.WriteLine("");
        Console.WriteLine("--- index_initMenu.html 的 iframe 结构 ---");
        Console.WriteLine(await EvalAsync(web, FrameJs));
        Console.WriteLine("");

        // 尝试直接进入 iframe 里的成绩页
        Console.WriteLine("[步骤] 尝试 iframe 内的成绩页");
        var inner = await EvalAsync(web, FrameScoreJs);
        Console.WriteLine(inner);
        Console.WriteLine("");

        Console.WriteLine("[步骤] 再深链到成绩查询页（顶层，对照）");
        await NavAsync(web, ScoreUrl, 25_000);
        var here = await EvalAsync(web, "location.href");
        Console.WriteLine("  落地: " + here);

        if (here.Contains("login_slogin", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  !! 仍被踢回登录页，成绩页探测到此为止。");
            Console.WriteLine("");
            return;
        }

        await Task.Delay(6000);

        var js = await EvalAsync(web, ScoreJs);
        Console.WriteLine(js);
        Console.WriteLine("");

        // 实弹一次，然后立刻回读容器，看 SDK 到底插不插节点
        Console.WriteLine("--- jQuery 与验证码实例的真实状态 ---");
        Console.WriteLine(await EvalAsync(web, EnvJs));

        Console.WriteLine("");
        Console.WriteLine("--- 原生 click #search_go（模拟真人点击，不调 popupCaptcha）---");
        Console.WriteLine(await EvalAsync(web,
            """
            (function () {
              const b = document.getElementById('search_go');
              if (!b) return '无 #search_go';
              b.click();
              return '已 click，等 6 秒观察';
            })();
            """));

        await Task.Delay(6000);
        Console.WriteLine("--- click 之后 ---");
        Console.WriteLine(await EvalAsync(web, AfterJs));
        Console.WriteLine("");

        Console.WriteLine("--- 再等 12 秒，看验证码是否异步出现 ---");
        await Task.Delay(12000);
        Console.WriteLine(await EvalAsync(web, AfterJs));
        Console.WriteLine("");

        // 逐个试所有候选成绩页，看**哪个**页面自带 jQuery。
        // 用户在浏览器里能看到验证码，说明确实存在一份功能正常的页面；
        // 与其继续猜，不如把候选入口一次扫完，直接定位。
        Console.WriteLine(new string('-', 70));
        Console.WriteLine("候选成绩页扫描（找自带 jQuery 的那个）");
        foreach (var (label, url) in CandidateScorePages)
        {
            await NavAsync(web, url, 20_000);
            var href = await EvalAsync(web, "location.href");
            if (href.Contains("login_slogin", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {label,-16} 被踢回登录页");
                continue;
            }
            await Task.Delay(2500);
            Console.WriteLine($"  {label,-16} {await EvalAsync(web, PageHealthJs)}");
        }
        Console.WriteLine("");
    }

    /// <summary>
    /// 深入探测成绩查询**首页**（cxXscjIndex.html）。
    /// </summary>
    /// <remarks>
    /// 候选扫描已证明：<c>cxDgXscj</c> 是唯一没有 jQuery 的页面，而首页 <c>cxXscjIndex</c> 自带 jQuery。
    /// 合理推测是首页把真正的查询表单放在 iframe 里（父页面提供 jQuery 与插件），
    /// 这也解释了为什么用户在浏览器里一切正常——真人走的是首页这条路径，
    /// 而我一直在深链那个残缺的子页面。
    /// </remarks>
    private static async Task ProbeScoreIndexAsync(WebView2 web)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("成绩查询首页 cxXscjIndex.html 深入探测");
        Console.WriteLine(new string('=', 70));

        await NavAsync(web, "https://jwgl.whu.edu.cn/cjcx/cjcx_cxXscjIndex.html?gnmkdm=N305005", 25_000);
        await Task.Delay(4000);

        Console.WriteLine("--- 页面骨架 ---");
        Console.WriteLine(await EvalAsync(web, IndexJs));
        Console.WriteLine("");

        Console.WriteLine("--- 点首页上的查询入口，看跳到哪、验证码弹不弹 ---");
        Console.WriteLine(await EvalAsync(web, IndexClickJs));
        await Task.Delay(5000);
        Console.WriteLine("--- 点击后 ---");
        Console.WriteLine(await EvalAsync(web, AfterJs));
        Console.WriteLine("");

        // 首页可能只是中转，真正的表单在它加载出来的 iframe 里
        Console.WriteLine("--- 首页内的 iframe ---");
        Console.WriteLine(await EvalAsync(web, FrameJs));
        Console.WriteLine("");
        Console.WriteLine("--- iframe 内点查询 ---");
        Console.WriteLine(await EvalAsync(web, FrameScoreJs));
        await Task.Delay(6000);
        Console.WriteLine(await EvalAsync(web, AfterJs));
        Console.WriteLine("");
    }

    private const string IndexJs = """
        (function () {
          try {
            const o = [];
            o.push('location: ' + location.href);
            o.push('jQuery: ' + typeof window.jQuery + '  版本: ' + (window.jQuery ? window.jQuery.fn.jquery : '-'));
            o.push('iframe 数: ' + document.querySelectorAll('iframe').length);
            o.push('正文: ' + (document.body ? document.body.innerText.replace(/\s+/g,' ').trim().slice(0,300) : '(无)'));
            o.push('表单: ' + Array.from(document.querySelectorAll('form'))
                 .map(f => (f.getAttribute('action')||'(无 action)') + '#' + (f.id||'-')).join(' | '));
            o.push('可点击元素:');
            Array.from(document.querySelectorAll('a[href], button, input[type=button], input[type=submit]'))
              .slice(0, 40)
              .forEach(x => o.push('  <' + x.tagName.toLowerCase()
                 + ' id=' + (x.id||'-')
                 + '> "' + (x.textContent||x.value||'').trim().replace(/\s+/g,' ').slice(0,30)
                 + '" href=' + (x.getAttribute && x.getAttribute('href') ? x.getAttribute('href') : '-')));
            return o.join('\n');
          } catch (e) { return 'IndexJs 抛错: ' + e; }
        })();
        """;

    private const string IndexClickJs = """
        (function () {
          try {
            const cands = Array.from(document.querySelectorAll('a[href], button, input[type=button]'))
              .filter(x => /查询|成绩|明细|按学期/.test(
                (x.textContent||'') + ' ' + (x.value||'') + ' ' + (x.getAttribute('href')||'')));
            if (!cands.length) return '首页上没有「查询」类可点元素';
            const lines = cands.slice(0,10).map(x => x.tagName + '#' + (x.id||'-') + ' "'
              + (x.textContent||x.value||'').trim().replace(/\s+/g,' ').slice(0,30) + '"');
            const target = cands[0];
            target.click();
            return '候选:\n  ' + lines.join('\n  ') + '\n已点击第 1 个: '
                 + (target.getAttribute && target.getAttribute('href')
                    ? target.getAttribute('href') : '(无 href)');
          } catch (e) { return 'IndexClickJs 抛错: ' + e; }
        })();
        """;

    /// <summary>
    /// 候选成绩查询入口。教务系统里同名功能挂在不同页面上，
    /// 模板不同、依赖不同，必须逐个实测而不是假设。
    /// </summary>
    private static readonly (string Label, string Url)[] CandidateScorePages =
    [
        ("等级成绩页", "https://jwgl.whu.edu.cn/cjcx/cjcx_cxDgXscj.html?gnmkdm=N305005"),
        ("学生本人成绩", "https://jwgl.whu.edu.cn/cjcx/cjcx_cxXsgrcj.html?gnmkdm=N305006"),
        ("成绩查询首页", "https://jwgl.whu.edu.cn/cjcx/cjcx_cxXscjIndex.html?gnmkdm=N305005"),
    ];

    /// <summary>一页的健康度摘要：能不能看到成绩、jQuery 在不在、验证码容器多大。</summary>
    private const string PageHealthJs = """
        (function () {
          try {
            const d = document.getElementById('captcha_div');
            const jq = Array.from(document.scripts).map(s => s.src)
              .filter(u => /\/jquery(\.min)?\.js/i.test(u));
            const txt = document.body ? document.body.innerText : '';
            const gpa = txt.match(/平均学分绩点[：:\s]*([0-9.]+)/);
            return 'jQuery=' + (typeof window.jQuery)
                 + '  jq外链=' + (jq.length ? '有' : '无')
                 + '  captcha容器=' + (d ? d.offsetWidth + 'x' + d.offsetHeight : '无')
                 + '  有#search_go=' + !!document.getElementById('search_go')
                 + '  表格数=' + document.querySelectorAll('table').length
                 + '  正文长度=' + txt.length
                 + (gpa ? '  平均学分绩点=' + gpa[1] : '');
          } catch (e) { return '抛错: ' + e; }
        })();
        """;

    private const string ScoreJs = """
        (function () {
          const out = [];
          const push = (k, v) => out.push('### ' + k + ' ###\n' + v);
          const val = id => { const e = document.getElementById(id); return e ? e.value : '(无)'; };
          const cls = e => e ? (e.id || '-') + '.' + (typeof e.className === 'string' ? e.className : '') : '(不存在)';

          push('location', location.href);
          push('sfxyyzm', val('sfxyyzm'));
          push('validate', val('validate'));
          push('apiServer', val('apiServer'));
          push('appKey', val('appKey'));
          push('captcha_div', (document.getElementById('captcha_div')||{}).outerHTML || '(不存在)');
          push('captcha_div 子节点数',
               String((document.getElementById('captcha_div')||{}).childElementCount));

          const btn = document.getElementById('search_go');
          push('#search_go', btn ? btn.outerHTML : '(不存在)');
          push('#search_go type', btn ? btn.type + ' disabled=' + btn.disabled : '-');

          // jQuery 绑了哪些事件 —— 决定 btn.click() 有没有用
          try {
            const ev = $._data(btn, 'events');
            push('#search_go jQuery 事件',
                 ev ? Object.keys(ev).map(t => t + '×' + ev[t].length).join(', ') : '(无绑定)');
            if (ev && ev.click) {
              push('#search_go click handler',
                   ev.click.map(h => String(h.handler).slice(0, 1500)).join('\n---\n'));
            }
          } catch (e) { push('读 jQuery events 失败', String(e)); }

          for (const fn of ['popupCaptcha', 'searchData', 'initCaptcha', 'showCaptcha', 'checkCaptcha']) {
            push('typeof ' + fn, typeof window[fn]);
            if (typeof window[fn] === 'function') {
              push(fn + '.toString()', String(window[fn]).slice(0, 3000));
            }
          }

          push('顶象全局',
               ['DxCaptcha','dxCaptcha','initDX','captchaUI','initNoCatchCaptcha']
                 .map(k => k + '=' + typeof window[k]).join('  '));
          push('验证码相关 script',
               Array.from(document.scripts).map(s => s.src)
                 .filter(s => s && /captcha|dingxiang|ctu-group/i.test(s)).join('\n') || '(无)');
          push('页面全部 script',
               Array.from(document.scripts).map(s => s.src).filter(Boolean).join('\n'));
          return out.join('\n\n');
        })();
        """;

    private const string FrameJs = """
        (function () {
          try {
          const o = [];
          const fs = document.querySelectorAll('iframe');
          o.push('顶层 jQuery: ' + typeof window.jQuery);
          o.push('顶层 location: ' + location.href);
          o.push('iframe 数量: ' + fs.length);
          for (let i = 0; i < fs.length; i++) {
            const f = fs[i];
            o.push('  [' + i + '] id=' + (f.id||'-') + ' name=' + (f.name||'-')
                 + ' size=' + f.offsetWidth + 'x' + f.offsetHeight
                 + ' src=' + (f.getAttribute('src')||'(动态设置)'));
            try {
              const d = f.contentDocument;
              if (!d) { o.push('       contentDocument=null（跨域或未加载）'); continue; }
              o.push('       子文档 jQuery=' + typeof d.defaultView.jQuery
                   + ' readyState=' + d.readyState
                   + ' 脚本数=' + d.scripts.length
                   + ' 子文档URL=' + d.location.href);
            } catch (e) { o.push('       读子文档失败: ' + e); }
          }
          const a = Array.from(document.querySelectorAll('a[href]'))
            .filter(x => /cjcx|成绩/.test((x.getAttribute('href')||'') + (x.textContent||'')));
          o.push('菜单里的成绩入口:');
          a.slice(0,5).forEach(x => o.push('  ' + (x.textContent||'').trim()
               + '  href=' + x.getAttribute('href') + '  target=' + (x.target||'-')));
          return o.join('\n');
          } catch (e) { return 'FrameJs 抛错: ' + e + '\n' + e.stack; }
        })();
        """;

    /// <summary>在 iframe 里逐个探 jQuery，找到含成绩页的那个就点它的查询。</summary>
    private const string FrameScoreJs = """
        (function () {
          try {
          const o = [];
          const fs = Array.from(document.querySelectorAll('iframe'));
          let hit = null;
          for (let i = 0; i < fs.length; i++) {
            const f = fs[i];
            let d; try { d = f.contentDocument; }
            catch (e) { o.push('  ['+i+'] 读不到: '+e); continue; }
            if (!d) { o.push('  ['+i+'] contentDocument=null'); continue; }
            const w = d.defaultView;
            const hasJq = typeof w.jQuery !== 'undefined';
            const txt = d.body ? d.body.innerText : '';
            const isScore = /cjcx|成绩|学分绩点/.test(String(f.src) + txt);
            o.push('  [' + i + '] ' + String(f.src).slice(0, 90));
            o.push('       jQuery=' + typeof w.jQuery + '  含成绩字样=' + isScore
                 + '  子文档URL=' + d.location.href.slice(0, 80));
            if (hasJq && isScore && !hit) hit = f;
          }
          if (!hit) { o.push('  没有找到「既含 jQuery 又是成绩页」的 iframe'); return o.join('\n'); }

          o.push('  >>> 在该 iframe 内点「查询」');
          try {
            const d = hit.contentDocument;
            const b = d.getElementById('search_go');
            if (!b) { o.push('  iframe 内没有 #search_go'); return o.join('\n'); }
            b.click();
            o.push('  已 click，等 SDK 弹验证码');
          } catch (e) { o.push('  点击失败: ' + e); }
          return o.join('\n');
          } catch (e) { return 'FrameScoreJs 抛错: ' + e; }
        })();
        """;

    private const string EnvJs = """
        (function () {
          const o = [];
          const p = (k, v) => o.push(k.padEnd(34) + ' = ' + v);
          p('typeof window.jQuery', typeof window.jQuery);
          p('typeof window.$', typeof window.$);
          p('jQuery 版本', window.jQuery ? window.jQuery.fn.jquery : '(无)');
          p('window.cxyzmlx', String(window.cxyzmlx));
          p('typeof myCaptcha', typeof window.myCaptcha);
          p('typeof captchaIns', typeof window.captchaIns);
          p('typeof zfcaptchaBusiness', typeof window.zfcaptchaBusiness);
          p('typeof dxCaptcha', typeof window.dxCaptcha);
          p('#captcha_div 尺寸',
            (function () { const d = document.getElementById('captcha_div');
                           return d ? d.offsetWidth + 'x' + d.offsetHeight : 'N/A'; })());

          // 页面里到底有没有 jQuery 的痕迹：外链、CDN、或被内联塞进来的
          const tags = Array.from(document.scripts)
            .map(s => s.src || ('[inline] ' + s.textContent.slice(0, 60).replace(/\s+/g, ' ')));
          const jq = tags.filter(s => /jquery/i.test(s) && !/jqgrid|validate|typeahead|dragsort|filehandle/i.test(s));
          p('疑似 jQuery 外链', jq.length ? jq.join(' | ') : '(无)');

          // iframe：jQuery 有可能装在子框架里
          p('iframe 数', String(document.querySelectorAll('iframe').length));
          p('performance 资源里的 jquery',
            performance.getEntriesByType('resource')
              .map(r => r.name).filter(n => /jquery/i.test(n) && !/jqgrid|validate|typeahead|dragsort|filehandle/i.test(n))
              .join(' | ') || '(无)');
          p('document.readyState', document.readyState);
          p('navigator.userAgent', navigator.userAgent);
          p('UA 含 Edg 标记', /Edg\//.test(navigator.userAgent) ? '是（会被教务判成 Edge）' : '否');
          return o.join('\n');
        })();
        """;

    private const string AfterJs = """
        (function () {
          const d = document.getElementById('captcha_div');
          const nodes = Array.from(document.querySelectorAll('div,iframe'))
            .filter(x => /captcha|dx-|dingxiang/i.test((x.id||'') + ' ' + (x.className||'')))
            .map(x => x.tagName + '#' + (x.id||'-') + '.' + (x.className||'-')
                      + ' 子节点=' + x.childElementCount
                      + ' 可见=' + (x.offsetParent !== null)
                      + ' 尺寸=' + x.offsetWidth + '×' + x.offsetHeight);
          return 'captcha_div 子节点=' + (d ? d.childElementCount : 'N/A')
               + '\nvalidate=' + (document.getElementById('validate')||{}).value
               + '\n可疑节点:\n' + (nodes.join('\n') || '(无)');
        })();
        """;

    // ─────────────────────────── 评教：复用同一个 CAS，换 service ───────────────────────────

    private static async Task ProbeReviewAsync(WebView2 web)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("评教 ugsqs");
        Console.WriteLine(new string('=', 70));

        Console.WriteLine("落地: " + await EvalAsync(web, "location.href"));
        await NavAsync(web, ReviewUrl, 30_000);
        await Task.Delay(8000);

        Console.WriteLine("--- 落地页 ---");
        Console.WriteLine(await EvalAsync(web,
            "location.href + '\\n---\\n' + (document.body?document.body.innerText:'').slice(0,1200)"));
        Console.WriteLine("");

        Console.WriteLine("--- 入口链接 / 表单 ---");
        Console.WriteLine(await EvalAsync(web,
            """
            (function () {
              const a = Array.from(document.querySelectorAll('a[href]'))
                .map(x => ((x.innerText||'').trim().replace(/\s+/g,' ')) + '  =>  ' + x.getAttribute('href'))
                .filter(s => s.length > 5);
              const f = Array.from(document.querySelectorAll('form'))
                .map(x => 'FORM action=' + x.getAttribute('action') + ' method=' + x.getAttribute('method'));
              const i = Array.from(document.querySelectorAll('input'))
                .map(x => 'INPUT ' + x.type + ' name=' + x.name + ' id=' + x.id);
              return '链接:\n' + (a.slice(0,40).join('\n')||'(无)')
                   + '\n\n' + (f.join('\n')||'(无表单)')
                   + '\n\n' + (i.slice(0,30).join('\n')||'(无输入)');
            })();
            """));
        Console.WriteLine("");

        Console.WriteLine("--- 脚本 ---");
        Console.WriteLine(await EvalAsync(web,
            "Array.from(document.scripts).map(s=>s.src).filter(Boolean).slice(0,40).join('\\n')||'(无)'"));
    }

    // ─────────────────────────── 基础设施 ───────────────────────────

    private static async Task<(WebView2 Web, Window Host)> OpenWebViewAsync(
        IReadOnlyList<CasCookie> cookies)
    {
        // 与 CasLoginWindow 用同一个 user data folder。
        // 但**不能指望**登录窗口的会话自动传过来：实测同一个 folder 下的
        // 第二个 WebView2 实例仍然会被 302 踢回 /xtgl/login_slogin.html，
        // 因为 JSESSIONID 这类会话 Cookie 在登录窗口关闭时没有被刷进 cookie 库。
        // 所以显式注入——CookieManager 是唯一可靠的迁移途径。
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ham", "webview");
        Directory.CreateDirectory(userData);

        var win = new Window
        {
            Width = 1280,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = "LiveProbe",
        };
        var web = new WebView2();
        win.Content = web;
        win.Show();

        await web.EnsureCoreWebView2Async();

        // 关键：必须伪装成普通 Chrome。
        // WebView2 默认 UA 里带 `Edg/` 标记，教务系统的 /js/browse/browse-judge.js
        // 会据此走 UA 嗅探分支，给出一个**降级页面模板**——那份模板不引入 jquery.min.js，
        // 于是页面自己的 popupCaptcha() 第一行 jQuery.founded(...) 直接抛
        // ReferenceError，#captcha_div 永远是空的，验证码永远不弹。
        // 用户在真实浏览器里能正常看到验证码，差异就在这里。
        Console.WriteLine($"[UA] 默认: {web.CoreWebView2.Settings.UserAgent}");
        web.CoreWebView2.Settings.UserAgent = ChromeUa;
        Console.WriteLine($"[UA] 覆盖: {ChromeUa}");
        Console.WriteLine("");

        var manager = web.CoreWebView2.CookieManager;
        int ok = 0;
        foreach (var c in cookies)
        {
            try
            {
                // 本项目用的 WebView2 版本是旧 API：CreateCookie + AddOrUpdateCookie
                manager.AddOrUpdateCookie(
                    manager.CreateCookie(c.Name, c.Value, c.Host, "/"));
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  注入 Cookie 失败 {c.Host}/{c.Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"[Cookie] 注入 {ok}/{cookies.Count} 项");
        foreach (var c in cookies)
            Console.WriteLine($"    {c.Host}/{c.Name} = {Mask(c.Value)}");
        Console.WriteLine("");

        return (web, win);
    }

    /// <summary>
    /// 执行 JS 并把 WebView2 返回的 JSON 字符串还原成原文。
    /// </summary>
    /// <remarks>
    /// 页面内 JS 抛异常时 WebView2 返回的不是 JSON 字符串，直接反序列化会得到 null，
    /// 诊断信息全丢——之前就因为这个把「iframe 结构」打成了 (null)，白跑一轮登录。
    /// 所以解析失败时回退到原始输出，宁可难看也不能把错误藏起来。
    /// </remarks>
    private static async Task<string> EvalAsync(WebView2 web, string script)
    {
        string json;
        try
        {
            json = await web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            return "(执行抛出) " + ex.Message;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? "(返回 null)";
        }
        catch (JsonException)
        {
            return "(非字符串返回) " + json;
        }
    }

    /// <summary>导航并等待完成，超时不阻塞后续流程。</summary>
    private static async Task NavAsync(WebView2 web, string url, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
            => tcs.TrySetResult(true);

        web.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            web.CoreWebView2.Navigate(url);
            await WithTimeout(tcs.Task, timeoutMs);
        }
        finally
        {
            web.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    private static async Task WithTimeout(Task task, int ms)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) Console.WriteLine($"[等待超时 {ms / 1000}s]");
    }

    /// <summary>
    /// 用 UI Automation 在真实 CAS 页面上点一次「登录」。
    /// </summary>
    /// <remarks>
    /// 只点一次。之前连点两次，第二次落在跳转后的新页面上，提交了**空表单**——
    /// 那是一次真实的失败登录，重复若干次足以让账号被锁。
    /// <para>
    /// 这里遍历**所有**顶层窗口再按控件名匹配，而不是按窗口类名筛：
    /// 早先只认 <c>IEFrame</c>，在探针这种无标题宿主窗口下匹配不到，
    /// 结果就是登录按钮一直没被点，探针空跑一轮拿不到任何 Cookie。
    /// </para>
    /// </remarks>
    private static DispatcherTimer StartLoginClicker()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        var clicks = 0;
        var ticks = 0;

        timer.Tick += (_, _) =>
        {
            if (clicks > 0) { timer.Stop(); return; }
            // 先给页面 4 秒把预填脚本和事件绑定跑完再点。
            // 抢跑会点在「按钮已渲染但 handler 还没绑上」的窗口里，click 落空。
            if (++ticks < 4) return;
            try
            {
                foreach (AutomationElement w in AutomationElement.RootElement.FindAll(
                             TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
                {
                    if (w.Current.IsOffscreen) continue;

                    foreach (AutomationElement e in w.FindAll(
                                 TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                    {
                        var name = e.Current.Name?.Trim();
                        if (name != "登录" && name != "登 录") continue;

                        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                        {
                            Console.WriteLine("[自动登录] 已点击「登录」（只点一次）");
                            ((InvokePattern)invoke).Invoke();
                        }
                        else
                        {
                            Console.WriteLine("[自动登录] 找到「登录」但无 InvokePattern，改用点击");
                            var r = e.Current.BoundingRectangle;
                            Mouse.Click(r.X + r.Width / 2, r.Y + r.Height / 2);
                        }
                        clicks++;
                        timer.Stop();
                        return;
                    }
                }
            }
            catch { /* 页面还在跳转，下一拍再试 */ }
        };
        timer.Start();
        return timer;
    }

    private static string Mask(string v)
        => v.Length <= 12 ? v : v[..12] + "…(" + v.Length + ")";
}

/// <summary>
/// 屏幕绝对坐标点击。
/// </summary>
/// <remarks>
/// UI Automation 的 <c>InvokePattern</c> 对网页里的某些可点击元素不可用，
/// 这时只能退回到真的把鼠标移过去点一下。WebView2 里的登录按钮就走这条路。
/// </remarks>
internal static class Mouse
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.IntPtr e);

    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    public static void Click(double x, double y)
    {
        SetCursorPos((int)x, (int)y);
        System.Threading.Thread.Sleep(120);
        mouse_event(LeftDown, 0, 0, 0, System.IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(LeftUp, 0, 0, 0, System.IntPtr.Zero);
    }
}
