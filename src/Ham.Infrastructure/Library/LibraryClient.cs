using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Ham.Infrastructure.Net;

namespace Ham.Infrastructure.Library;

/// <summary>
/// 图书馆座位预约系统客户端（只读）。
/// </summary>
/// <remarks>
/// <para>
/// <b>不需要浏览器内核。</b>CAS 票据（CASTGC）由信息门户登录流程取得，本客户端据此
/// 走完三步拿到图书馆会话 token，之后所有请求都在 <see cref="CampusHttpClient"/> 里发出。
/// 这与教务不同——教务必须借浏览器页面上下文（见 <c>FetchPlan</c>），
/// 图书馆这边则完全接受纯 HTTP 请求。
/// </para>
/// <para>
/// 认证链（全部对照线上 <c>app.949ae52dbe27da10244e.js</c> 核实，2026-09）：
/// </para>
/// <list type="number">
/// <item><c>POST /jsq/static/public/cg/getSysSet/PC</c>（公开）→ 拿到
/// <c>vueConfig.CASSSERVICE</c>、<c>vueConfig.CASLOGIN</c>、<c>hmac</c> 与加密后的 <c>hmacKey</c>；</item>
/// <item><c>GET cas.whu.edu.cn/authserver/login?service=&lt;CASSSERVICE + /static/sso/webOAuthRed&gt;</c>
/// → 302 回 <c>...?token=&lt;JWT&gt;</c>；</item>
/// <item><c>POST /jsq/static/public/auth/cas/&lt;JWT&gt;</c>，体 <c>{token,loginType:"PC"}</c>
/// → <c>data.data.token</c> 即图书馆会话 token。</item>
/// </list>
/// <para>
/// 实测：<c>hmacKey</c> 用 AES-128-CBC（key=<c>server_date_time</c>、iv=<c>client_date_time</c>）
/// 解密后得到 <c>whu2024lib</c>；带正确签名但无 token 时，接口返回
/// <c>{"status":false,"code":20002,"message":"token认证无效"}</c>——
/// 说明签名已通过校验，唯一缺的就是会话。
/// </para>
/// <para>
/// <b>只实现只读接口。</b>预约、取消、签到需要图形验证码与更严格的风控，不在此列。
/// </para>
/// </remarks>
public sealed class LibraryClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly CampusHttpClient _http;
    private string? _token;
    private byte[]? _hmacKey;
    private bool _hmacRequired;

    public LibraryClient(CampusHttpClient http) => _http = http;

    /// <summary>是否已完成认证。</summary>
    public bool IsAuthenticated => _token is { Length: > 0 };

    /// <summary>
    /// 用 CAS 票据完成认证。
    /// </summary>
    /// <param name="casService">
    /// <c>vueConfig.CASSSERVICE</c>，由 <see cref="FetchSystemConfigAsync"/> 取得。
    /// </param>
    public async Task AuthenticateAsync(string casService, CancellationToken ct = default)
    {
        await FetchSystemConfigAsync(ct).ConfigureAwait(false);

        if (_hmacKey is null)
            throw new CampusNetworkException("未能取得图书馆接口签名密钥。");

        // CAS 侧已登录时会直接 302 回 OAuth 回调并带上 token
        var url = LibraryEndpoints.BuildSsoLoginUrl(casService);
        using var response = await _http.Client.GetAsync(url, ct).ConfigureAwait(false);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        if (finalUrl.Contains("ReAuth", StringComparison.OrdinalIgnoreCase))
            throw new CampusNetworkException("图书馆要求重新认证信息门户。");

        var jwt = ExtractQueryValue(finalUrl, "token");
        if (jwt is null)
        {
            throw new CampusNetworkException(
                "图书馆 CAS 换票失败：未取得票据。"
                + "请确认信息门户会话仍然有效（CASTGC 过期时需要重新登录）。");
        }

        // 用 JWT 换图书馆会话 token
        var path = LibraryEndpoints.CasAuthPathPrefix + Uri.EscapeDataString(jwt);
        var body = JsonSerializer.Serialize(new { token = jwt, loginType = "PC" });
        var envelope = await PostJsonAsync(path, body, ct).ConfigureAwait(false);

        if (!envelope.GetBoolean("status"))
        {
            var msg = envelope.GetString("message") ?? $"code {envelope.GetInt32("code")}";
            throw new CampusNetworkException("图书馆登录失败：" + msg);
        }

        var data = envelope.GetObject("data");
        _token = data?.GetString("token");
        if (_token is not { Length: > 0 })
            throw new CampusNetworkException("图书馆未返回会话 token。");
    }

    /// <summary>取 <c>vueConfig</c> 与签名配置（公开端点，无需认证）。</summary>
    public async Task<JsonElement> FetchSystemConfigAsync(CancellationToken ct = default)
    {
        var envelope = await PostJsonAsync(LibraryEndpoints.SysSetPath, "{}", ct).ConfigureAwait(false);
        if (!envelope.GetBoolean("status"))
            throw new CampusNetworkException("读取图书馆系统配置失败。");

        var data = envelope.GetObject("data")
                   ?? throw new CampusNetworkException("图书馆系统配置缺少 data 字段。");

        _hmacRequired = data.GetInt32("hmac") == 1;

        var encrypted = data.GetString("hmacKey");
        _hmacKey = encrypted is { Length: > 0 }
            ? LibraryEndpoints.DecryptHmacKey(encrypted)
            : null;

        return data.GetObject("vueConfig") ?? default;
    }

    /// <summary>取 <c>vueConfig</c> 里的 CAS 服务地址。</summary>
    public async Task<string> GetCasServiceAsync(CancellationToken ct = default)
    {
        var vueConfig = await FetchSystemConfigAsync(ct).ConfigureAwait(false);
        return vueConfig.GetString("CASSSERVICE")
               ?? throw new CampusNetworkException("图书馆配置缺少 CASSSERVICE。");
    }

    /// <summary>查询某分馆某日的座位大盘（各区域余座）。</summary>
    public Task<JsonElement> GetSeatOverviewAsync(
        string library, DateOnly date, CancellationToken ct = default)
    {
        var venueId = LibraryEndpoints.VenueId(library);
        var path = LibraryEndpoints.FindRoomDurationPath(venueId, FormatDate(date));
        return PostJsonAsync(path, LibraryEndpoints.RoomDurationBody(), ct);
    }

    /// <summary>查询某区域某日的座位排布。</summary>
    public Task<JsonElement> GetSeatMapAsync(
        string areaId, DateOnly date, int beginMinute = 492, CancellationToken ct = default)
    {
        var path = LibraryEndpoints.FreeSeatIdsDurationPath(areaId, FormatDate(date));
        return PostJsonAsync(path, LibraryEndpoints.SeatMapBody(beginMinute), ct);
    }

    /// <summary>我的预约记录。</summary>
    public Task<JsonElement> GetMyReservationsAsync(CancellationToken ct = default)
        => PostJsonAsync(LibraryEndpoints.HistoryPath, "{}", ct);

    /// <summary>当前正在使用的座位。</summary>
    public Task<JsonElement> GetCurrentUsageAsync(CancellationToken ct = default)
        => PostJsonAsync(LibraryEndpoints.CurrentUsagePath, "{}", ct);

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>发一个带签名的 JSON POST，并返回响应的 data 节点。</summary>
    private async Task<JsonElement> PostJsonAsync(string path, string body, CancellationToken ct)
    {
        var url = LibraryEndpoints.ApiBaseUrl + path;

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        {
            // 站点声明的 charset 就是 UTF-8，但 .NET 默认会追加 utf-8 到 Content-Type，
            // 与站点不一致时可能被拒。显式指定避免出现 `; charset=utf-8` 的差异。
        };
        content.Headers.ContentType!.CharSet = "UTF-8";

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("loginType", "PC");

        if (_token is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("token", _token);

        if (_hmacRequired && _hmacKey is not null)
        {
            foreach (var (name, value) in LibraryEndpoints.MakeSignature(_hmacKey))
                request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.Client.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Infrastructure.Logging.Log.Warn(
                $"图书馆接口 {path} -> HTTP {(int)response.StatusCode}; 正文前 200 字: "
                + text[..Math.Min(200, text.Length)]);
            throw new CampusNetworkException(
                $"图书馆接口返回 HTTP {(int)response.StatusCode}。");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            Infrastructure.Logging.Log.Warn($"图书馆接口 {path} 返回的不是 JSON: {text[..Math.Min(200, text.Length)]}");
            throw new CampusNetworkException("图书馆接口返回了非 JSON 内容。", ex);
        }

        using (doc)
        {
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.False)
            {
                var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;

                // 20002/20003 都是"请重新登录信息门户"，不是接口坏了
                if (code is LibraryEndpoints.CodeTokenInvalid or LibraryEndpoints.CodeAuthExpired)
                {
                    throw new CasReAuthRequiredException(url,
                        "图书馆会话已失效，请重新登录信息门户。");
                }

                throw new CampusNetworkException($"图书馆接口返回错误 {code}：{msg}");
            }

            return root.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return null;

        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (!Uri.UnescapeDataString(pair[..eq]).Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            return value.Length == 0 ? null : value;
        }

        return null;
    }
}

/// <summary><see cref="JsonElement"/> 上的容错访问扩展。</summary>
internal static class JsonElementExtensions
{
    public static bool GetBoolean(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.True;

    public static string? GetString(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int GetInt32(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var n)
            ? n
            : 0;

    public static JsonElement? GetObject(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Object
            ? v
            : null;
}
