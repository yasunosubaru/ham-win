using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ham.Infrastructure.Net;

namespace Ham.Infrastructure.Library;

/// <summary>武大图书馆座位预约系统（seat.lib.whu.edu.cn）的接入参数。</summary>
/// <remarks>
/// <para>
/// 站点是 SPA，<c>frontApi/*</c> 要求 HMAC-SHA256 签名，签名密钥本身又被
/// AES-128-CBC 加密后放在 <c>sessionStorage['jsq_p-systemInfo'].hmacKey</c>。
/// 逆向自该站点的 <c>app.*.js</c>（Axios 拦截器）与其登录态存储。
/// </para>
/// <para>
/// 密钥与 IV 是<b>硬编码在站点前端里</b>的常量，不是机密；本项目复刻它们只是为了
/// 能算出同样的签名。<b>只实现只读接口</b>：预约/取消需要图形验证码，不在此列。
/// </para>
/// </remarks>
public static class LibraryEndpoints
{
    public const string Host = "seat.lib.whu.edu.cn";
    public const string BaseUrl = "https://seat.lib.whu.edu.cn";

    /// <summary>后端服务根。取自站点 <c>static/config.js</c> 的 <c>Global.BASEURL</c>。</summary>
    public const string ApiBaseUrl = "https://seat.lib.whu.edu.cn/jsq";

    /// <summary>前端入口页（站点标题 jsq-pc）。</summary>
    public const string SeatPagePath = "/seat/";

    /// <summary>公开端点：取 <c>vueConfig</c> 与 <c>systemInfo</c>（含加密后的 hmacKey）。</summary>
    public const string SysSetPath = "/static/public/cg/getSysSet/PC";

    /// <summary>CAS 回调路径（挂在 <c>vueConfig.CASSSERVICE</c> 之下）。</summary>
    public const string OAuthPath = "/static/sso/webOAuthRed";

    /// <summary>用 CAS 票据换取图书馆会话 token。</summary>
    public const string CasAuthPathPrefix = "/static/public/auth/cas/";

    /// <summary>业务错误码：token 无效/过期。</summary>
    public const int CodeTokenInvalid = 20002;

    /// <summary>业务错误码：登录认证已过期。</summary>
    public const int CodeAuthExpired = 20003;

    /// <summary>AES-128-CBC 密钥与 IV（站点前端常量，16 字节）。</summary>
    private static readonly byte[] Key = "server_date_time"u8.ToArray();
    private static readonly byte[] Iv = "client_date_time"u8.ToArray();

    /// <summary>
    /// 解密 <c>sessionStorage['jsq_p-systemInfo'].hmacKey</c>，得到真实 HMAC 密钥。
    /// </summary>
    public static byte[] DecryptHmacKey(string encryptedBase64)
    {
        var cipherText = Convert.FromBase64String(encryptedBase64);
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var transform = aes.CreateDecryptor();
        var plain = transform.TransformFinalBlock(cipherText, 0, cipherText.Length);

        // 前端在加密前会 strip 尾部空字节，解密后要去掉 PKCS7 之外的填充残留
        return plain;
    }

    /// <summary>
    /// 生成签名请求头。签名串：<c>seat::{uuid}::{毫秒时间戳}::{METHOD}</c>。
    /// </summary>
    public static IReadOnlyDictionary<string, string> MakeSignature(
        byte[] hmacKey, string method = "POST", DateTimeOffset? now = null)
    {
        var requestId = Guid.NewGuid().ToString();
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var signString = $"seat::{requestId}::{timestamp}::{method.ToUpperInvariant()}";

        var signature = Convert.ToHexString(
            HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(signString))).ToLowerInvariant();

        return new Dictionary<string, string>
        {
            ["X-request-id"] = requestId,
            ["X-request-date"] = timestamp,
            ["X-hmac-request-key"] = signature,
        };
    }

    /// <summary>分馆 ID。</summary>
    public static string VenueId(string library) => library switch
    {
        "信息分馆" => "1812738485913751552",
        "工学分馆" => "1812738878798401536",
        "医学分馆" => "1812739190351302656",
        _ => "1812737769937670144",   // 总馆 / 主馆
    };

    /// <summary>全部可查分馆。</summary>
    public static IReadOnlyList<string> Libraries { get; } =
        ["总馆", "信息分馆", "工学分馆", "医学分馆"];

    /// <summary>某分馆某日的座位大盘。</summary>
    public static string FindRoomDurationPath(string venueId, string date)
        => $"/static/frontApi/res/findRoomDuration/{venueId}/{date}";

    /// <summary>某区域某日的座位排布。</summary>
    public static string FreeSeatIdsDurationPath(string areaId, string date)
        => $"/static/frontApi/res/freeSeatIdsDuration/{areaId}/{date}";

    /// <summary>我的预约记录。</summary>
    public const string HistoryPath = "/static/frontApi/user/history/1/50";

    /// <summary>当前正在使用的座位。</summary>
    public const string CurrentUsagePath = "/static/frontApi/user/currentUseMake";

    /// <summary>findRoomDuration 的请求体。</summary>
    public static string RoomDurationBody(int beginMinute = 492, int pageSize = 200)
        => JsonSerializer.Serialize(new
        {
            beginMinute,
            currentPage = 1,
            endMinute = 0,
            floorId = 0,
            minMinute = 0,
            pageSize,
            power = false,
            roomType = false,
            sortField = "",
            sortType = "",
            windows = false,
        });

    /// <summary>freeSeatIdsDuration 的请求体。</summary>
    public static string SeatMapBody(int beginMinute = 492)
        => JsonSerializer.Serialize(new { beginMinute, endMinute = 0 });

    /// <summary>拼出 CAS 登录地址。<paramref name="casService"/> 来自 <c>vueConfig.CASSSERVICE</c>。</summary>
    public static string BuildSsoLoginUrl(string casService)
        => $"https://cas.whu.edu.cn/authserver/login?service="
           + Uri.EscapeDataString(casService.TrimEnd('/') + OAuthPath);
}
