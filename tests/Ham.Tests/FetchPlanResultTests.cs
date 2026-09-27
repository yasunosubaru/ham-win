using Ham.Infrastructure.Cas;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// <see cref="FetchPlanResult.Parse"/> 的回归测试。
/// </summary>
/// <remarks>
/// 这些用例针对的是真实发生过的静默失败：页面脚本把结果写进
/// <c>window.__hamFetch</c>，宿主用 <c>ExecuteScriptAsync</c> 轮询取回。
/// 当时数据明明在返回里（mock 也确认收到了请求并返回了 JSON），
/// 解析出来却是 0 项，而且<b>不抛任何异常</b>——因为
/// <see cref="System.Text.Json.JsonSerializer"/> 的
/// <c>PropertyNameCaseInsensitive</c> 默认为 <c>false</c>，
/// 页面的小写键 <c>bodies</c>/<c>failures</c>/<c>done</c> 绑不到 PascalCase 属性上。
/// </remarks>
public class FetchPlanResultTests
{
    /// <summary>
    /// 构造与运行时完全一致的形态：页面 <c>JSON.stringify</c> 一次，
    /// WebView2 的 <c>ExecuteScriptAsync</c> 再编码一次。
    /// </summary>
    private static string AsBrowserPoll(string innerJson)
        => System.Text.Json.JsonSerializer.Serialize(innerJson);

    [Fact]
    public void ParsesDoubleEncodedLowercaseKeys()
    {
        var raw = AsBrowserPoll(
            "{\"done\":true,\"bodies\":{\"/kbcx/a.html\":\"{}\",\"cjcx/b.html\":\"[]\"},"
            + "\"failures\":[]}");

        var result = FetchPlanResult.Parse(raw);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Bodies.Count);
        Assert.Equal("{}", result.BodyOf("/kbcx/a.html"));
        Assert.Equal("[]", result.BodyOf("cjcx/b.html"));
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// 这条是核心回归：用<b>默认</b> JsonSerializerOptions 反序列化同样的数据会得到 0 项，
    /// 而且不报错。断言本实现必须能绑上小写键。
    /// </summary>
    [Fact]
    public void LowercaseKeysActuallyBind()
    {
        var raw = AsBrowserPoll(
            "{\"done\":true,\"bodies\":{\"/kbcx/a.html\":\"{}\"},\"failures\":[\"x\"]}");

        var result = FetchPlanResult.Parse(raw);

        Assert.NotNull(result);
        Assert.Single(result!.Bodies);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void CarriesFailures()
    {
        var raw = AsBrowserPoll(
            "{\"done\":true,\"bodies\":{},\"failures\":[\"/kbcx/a.html -> HTTP 901 (空正文)\"]}");

        var result = FetchPlanResult.Parse(raw);

        Assert.NotNull(result);
        Assert.Empty(result!.Bodies);
        Assert.False(result.AllSucceeded);
        Assert.Contains("HTTP 901", result.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesFullWidthSpacesInBody()
    {
        // 真实教务用 U+3000 缩进，解析层要原样拿到
        const string body = "{\u3000\"kbList\": []\u3000}";
        var inner = System.Text.Json.JsonSerializer.Serialize(new
        {
            done = true,
            bodies = new Dictionary<string, string> { ["/kbcx/a.html"] = body },
            failures = Array.Empty<string>(),
        });

        var result = FetchPlanResult.Parse(AsBrowserPoll(inner));

        Assert.NotNull(result);
        Assert.Equal(body, result!.BodyOf("/kbcx/a.html"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("undefined")]
    public void EmptyPollMeansNotReadyYet(string? raw)
        => Assert.Null(FetchPlanResult.Parse(raw));

    /// <summary>
    /// 回归：<c>done=false</c> 必须返回 <c>null</c>（"还没开始"，调用方要继续轮询）。
    /// </summary>
    /// <remarks>
    /// 这个用例<b>曾经断言了相反的行为</b>（"未完成也要能解析出内容"），
    /// 于是把一个真实存在的 bug 固化进了测试：
    /// 页面脚本一启动就把 <c>window.__hamFetch</c> 初始化成
    /// <c>{done:false, bodies:{}, failures:[]}</c>，第一次轮询立刻读到它；
    /// 若此时返回结果，界面就会显示"没有数据"，而真实情况是请求还没发出去。
    /// 实际后果：用户报告"课表和成绩出不来"，日志显示"代取完成：0 项正文，0 项失败"。
    /// </remarks>
    [Fact]
    public void NotDoneMeansNotReadyYet()
    {
        var raw = AsBrowserPoll("{\"done\":false,\"bodies\":{\"/kbcx/a.html\":\"{}\"},\"failures\":[]}");

        Assert.Null(FetchPlanResult.Parse(raw));
    }

    /// <summary>轮询尚未启动时页面返回 null，也必须被当成"还没好"。</summary>
    [Fact]
    public void NullStateMeansNotReadyYet()
    {
        // window.__hamFetch 尚未建立时，脚本返回 JSON.stringify(null)
        var raw = AsBrowserPoll("null");
        Assert.Null(FetchPlanResult.Parse(raw));
    }

    /// <summary>完成但确实没有数据时，返回空结果而不是 null——两者必须能区分。</summary>
    [Fact]
    public void DoneWithNoDataIsAValidResult()
    {
        var raw = AsBrowserPoll("{\"done\":true,\"bodies\":{},\"failures\":[]}");

        var result = FetchPlanResult.Parse(raw);

        Assert.NotNull(result);
        Assert.Empty(result!.Bodies);
        Assert.Empty(result.Failures);
        Assert.True(result.AllSucceeded);
    }
}
