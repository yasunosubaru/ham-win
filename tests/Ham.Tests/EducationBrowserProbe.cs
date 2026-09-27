using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Net;

namespace Ham.Tests;

/// <summary>
/// 以「浏览器页面内 XHR」的特征发请求，用于在测试中复现生产路径。
/// </summary>
/// <remarks>
/// 生产环境由 <c>CasLoginWindow.RunFetchPlanAsync</c> 在 WebView2 页面内执行
/// <c>XMLHttpRequest</c> 完成代取。这里用 <see cref="HttpClient"/> 复刻它的**可观察特征**
/// （同源、带会话、带 Referer、带 <c>X-Requested-With</c>），以便在不启动浏览器内核的
/// 前提下验证整条链路与解析逻辑。
/// <para>
/// 它<b>不是</b>生产实现：真实路径必须由浏览器发，理由见 <see cref="FetchPlan"/>。
/// 因此这里刻意<b>不</b>复用 <c>EducationClient</c> 的请求构造——那样会把
/// "不带 X-Requested-With" 的失败路径和"页面内 XHR"的成功路径混为一谈。
/// </para>
/// </remarks>
internal static class EducationBrowserProbe
{
    public static async Task<Dictionary<string, string>> FetchAsync(
        CampusHttpClient http, CampusEndpoints endpoints, FetchPlan plan)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in plan.Targets)
        {
            var url = endpoints.EducationBaseUrl + target.Path;
            using var request = new HttpRequestMessage(
                target.IsPost ? HttpMethod.Post : HttpMethod.Get, url);

            request.Headers.Referrer = new Uri(endpoints.EducationBaseUrl + "/");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            if (target.IsPost)
            {
                request.Content = new FormUrlEncodedContent(
                    target.Form!.ToDictionary(k => k.Key, v => v.Value));
            }

            using var response = await http.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            // 与生产一致：任何非 2xx 都要显式失败，不能把错误页当数据
            if (!response.IsSuccessStatusCode)
            {
                throw new CampusNetworkException(
                    $"{target.Path} -> HTTP {(int)response.StatusCode}");
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            if (finalUrl.Contains("/authserver/login", StringComparison.OrdinalIgnoreCase)
                || text.Contains("请先登录", StringComparison.Ordinal))
            {
                throw new CasReAuthRequiredException(finalUrl, "教务会话已失效，请重新登录信息门户。");
            }

            bodies[target.Path] = text;
        }

        return bodies;
    }
}
