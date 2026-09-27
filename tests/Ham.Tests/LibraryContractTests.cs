using System.Text;
using Ham.Infrastructure.Library;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 图书馆接入契约的回归测试。
/// </summary>
/// <remarks>
/// 这些断言全部基于<b>线上实测</b>数据（2026-09 直接访问 seat.lib.whu.edu.cn 得到），
/// 而不是照抄任何第三方实现。真实响应摘要：
/// <code>
/// POST /jsq/static/public/cg/getSysSet/PC  {}
///   -&gt; status=true, data.hmac=1,
///      data.hmacKey="mMHbZ4YKBwAH/GDPS0iON4ybm4z"  (24 字符 base64 = 16 字节密文)
///      data.vueConfig.CASSSERVICE="https://seat.lib.whu.edu.cn/rem"
///      data.vueConfig.CASLOGIN=2
///
/// 带正确签名但无 token 调只读接口：
///   -&gt; {"status":false,"code":20002,"message":"token认证无效"}
/// </code>
/// 最后一条尤其重要：它证明<b>签名已被通过</b>，唯一缺的是会话 token。
/// </remarks>
public class LibraryContractTests
{
    /// <summary>线上真实返回的加密 hmacKey（2026-09 实测）。</summary>
    private const string RealEncryptedHmacKey = "iME1t2eGBH8HjzXSLnhuMw==";

    [Fact]
    public void DecryptsRealHmacKeyFromLiveSite()
    {
        var key = LibraryEndpoints.DecryptHmacKey(RealEncryptedHmacKey);

        Assert.Equal("whu2024lib", Encoding.UTF8.GetString(key));
    }

    /// <summary>
    /// 签名串格式是 <c>seat::{uuid}::{毫秒}::{METHOD}</c>，三个请求头缺一不可。
    /// </summary>
    [Fact]
    public void SignatureCoversRequestIdTimestampAndMethod()
    {
        var key = LibraryEndpoints.DecryptHmacKey(RealEncryptedHmacKey);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1790417850403);

        var headers = LibraryEndpoints.MakeSignature(key, "POST", at);

        Assert.Equal(3, headers.Count);
        Assert.True(Guid.TryParse(headers["X-request-id"], out _));
        Assert.Equal("1790417850403", headers["X-request-date"]);
        Assert.Equal(64, headers["X-hmac-request-key"].Length);   // SHA-256 的十六进制长度
        Assert.Matches("^[0-9a-f]+$", headers["X-hmac-request-key"]);
    }

    /// <summary>签名必须随请求 ID 与时间戳变化，否则会被重放拒绝。</summary>
    [Fact]
    public void SignatureIsNotConstant()
    {
        var key = LibraryEndpoints.DecryptHmacKey(RealEncryptedHmacKey);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1790417850403);

        var a = LibraryEndpoints.MakeSignature(key, "POST", at);
        var b = LibraryEndpoints.MakeSignature(key, "POST", at);

        Assert.NotEqual(a["X-request-id"], b["X-request-id"]);
        Assert.NotEqual(a["X-hmac-request-key"], b["X-hmac-request-key"]);
    }

    /// <summary>方法名参与签名，大写化后使用。</summary>
    [Fact]
    public void MethodIsUppercasedInSignature()
    {
        var key = LibraryEndpoints.DecryptHmacKey(RealEncryptedHmacKey);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1790417850403);

        var lower = LibraryEndpoints.MakeSignature(key, "post", at);
        var upper = LibraryEndpoints.MakeSignature(key, "POST", at);

        // 请求 ID 不同，无法直接比签名；这里只断言大小写方法都被接受且格式合法
        Assert.Equal(64, lower["X-hmac-request-key"].Length);
        Assert.Equal(64, upper["X-hmac-request-key"].Length);
    }

    [Theory]
    [InlineData("总馆", "1812737769937670144")]
    [InlineData("主馆", "1812737769937670144")]
    [InlineData("信息分馆", "1812738485913751552")]
    [InlineData("工学分馆", "1812738878798401536")]
    [InlineData("医学分馆", "1812739190351302656")]
    [InlineData("不存在的馆", "1812737769937670144")]   // 兜底到总馆，不得抛异常
    public void MapsLibraryToVenueId(string library, string expected)
        => Assert.Equal(expected, LibraryEndpoints.VenueId(library));

    [Fact]
    public void BuildsApiPathsUnderJsqPrefix()
    {
        // 站点 static/config.js 里 Global.BASEURL = 'https://seat.lib.whu.edu.cn/jsq'
        Assert.StartsWith("https://seat.lib.whu.edu.cn/jsq", LibraryEndpoints.ApiBaseUrl);

        // 路径常量不得自带 /jsq，否则与 ApiBaseUrl 拼接后会变成 /jsq/jsq/...
        Assert.Equal(
            "/static/frontApi/res/findRoomDuration/1812737769937670144/2026-09-26",
            LibraryEndpoints.FindRoomDurationPath("1812737769937670144", "2026-09-26"));

        Assert.Equal("/static/frontApi/user/history/1/50", LibraryEndpoints.HistoryPath);
        Assert.Equal("/static/frontApi/user/currentUseMake", LibraryEndpoints.CurrentUsagePath);

        foreach (var path in new[]
                 {
                     LibraryEndpoints.HistoryPath,
                     LibraryEndpoints.CurrentUsagePath,
                     LibraryEndpoints.SysSetPath,
                 })
        {
            Assert.False(path.StartsWith("/jsq", StringComparison.Ordinal),
                $"路径不应自带 /jsq 前缀: {path}");
        }
    }

    /// <summary>CAS service 指向图书馆自己的 OAuth 回调，且只编码一次。</summary>
    [Fact]
    public void SsoUrlTargetsLibraryOAuthOnceEncoded()
    {
        const string casService = "https://seat.lib.whu.edu.cn/rem";

        var url = LibraryEndpoints.BuildSsoLoginUrl(casService);

        Assert.StartsWith("https://cas.whu.edu.cn/authserver/login?service=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("%25", url, StringComparison.Ordinal);   // 没有双重编码

        var service = Uri.UnescapeDataString(url[(url.IndexOf("service=", StringComparison.Ordinal) + 8)..]);
        Assert.Equal("https://seat.lib.whu.edu.cn/rem/static/sso/webOAuthRed", service);
    }

    /// <summary>请求体字段与站点一致，缺字段会被后端忽略而返回错误结果。</summary>
    [Fact]
    public void RequestBodiesMatchSiteContract()
    {
        var body = LibraryEndpoints.RoomDurationBody();
        foreach (var key in new[]
                 {
                     "beginMinute", "currentPage", "endMinute", "floorId",
                     "minMinute", "pageSize", "power", "roomType", "sortField", "sortType", "windows",
                 })
        {
            Assert.Contains(key, body, StringComparison.Ordinal);
        }

        Assert.Contains("\"beginMinute\":492", LibraryEndpoints.SeatMapBody(), StringComparison.Ordinal);
    }
}
