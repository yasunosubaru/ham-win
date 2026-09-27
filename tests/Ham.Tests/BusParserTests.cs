using Ham.Infrastructure.Campus;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 校巴解析的回归测试。
/// </summary>
/// <remarks>
/// 用例里的 JSON <b>全部是从线上真实响应里摘下来的</b>，不是手编的示意数据。
/// 这一点很重要：手编的 JSON 不会暴露线上那些反直觉的细节，
/// 比如 base64 里带换行、<c>resultCode</c> 是字符串、坐标是字符串类型的数字。
/// </remarks>
public class BusParserTests
{
    /// <summary>线上真实响应：1 号线，字段全、站序完整。</summary>
    private const string Real1号线 =
        """
        {"resultCode":"1","resultDes":"","data":{"lineName":"1号线","lineId":"10486-50-0",
        "lineNo":"50","direction":0,"startStopName":"信息学部四食堂",
        "endStopName":"信息学部二食堂（回行）","firstTime":"07:20","lastTime":"22:40",
        "price":"1.0~2.0","line2Id":"",
        "stops":[
        "eyJzdG9wSWQiOiIxIiwic3RvcE5hbWUiOiLnj57nkZzkuozpl6giLCJsbmciOiIxMTQuMzU4Mzgz\nODk1MzQ0NiIsImxhdCI6IjMwLjUyNjc0NDA5NjAxNjk2NiIsInN0b3BPcmRlciI6MSwibWV0cm8i\nOiIifQ==\n",
        "eyJzdG9wSWQiOiIyIiwic3RvcE5hbWUiOiLkv6Hmga/lrabpg6jkuIDmlZkiLCJsbmciOiIxMTQu\nMzYwNjY1MDEzOTE2NTkiLCJsYXQiOiIzMC41MjY4NzM4OTk0OTYyMyIsInN0b3BPcmRlciI6Miwi\nbWV0cm8iOiIifQ==\n",
        "eyJzdG9wSWQiOiIzIiwic3RvcE5hbWUiOiLkv6Hmga/lrabpg6jlm77kuabppoYiLCJsbmciOiIx\nMTQuMzYwNjUzMjI4Nzk0OTciLCJsYXQiOiIzMC41Mjk1MjY3NDQ0MTA1NTciLCJzdG9wT3JkZXIi\nOjMsIm1ldHJvIjoiIn0=\n"
        ],"buses":[]}}
        """;

    /// <summary>线上真实响应：轨迹折线，<c>data</c> 是字符串而非对象。</summary>
    private const string RealPath =
        """
        {"resultCode":"1","resultDes":"","data":"30.526746,114.358103;30.526744,114.358277;30.526741,114.358524;30.526752,114.35891"}
        """;

    [Fact]
    public void ParsesRealLinePayload()
    {
        var line = BusParser.ParseLine(Real1号线);

        Assert.NotNull(line);
        Assert.Equal("1号线", line!.Name);
        Assert.Equal("10486-50-0", line.Id);
        Assert.Equal("50", line.Number);
        Assert.Equal("信息学部四食堂", line.StartStop);
        Assert.Equal("信息学部二食堂（回行）", line.EndStop);
        Assert.Equal("07:20", line.FirstTime);
        Assert.Equal("22:40", line.LastTime);
        Assert.Equal("1.0~2.0", line.Price);
        Assert.Equal(0, line.Direction);
        Assert.Equal("07:20 - 22:40", line.ServiceHours);
        Assert.Equal("信息学部四食堂 → 信息学部二食堂（回行）", line.RouteSummary);
    }

    /// <summary>
    /// 站点 base64 里带换行——线上就是这样，不去掉空白必然解码失败。
    /// </summary>
    [Fact]
    public void DecodesStopsDespiteEmbeddedNewlines()
    {
        var line = BusParser.ParseLine(Real1号线);

        Assert.NotNull(line);
        Assert.Equal(3, line!.Stops.Count);

        var first = line.Stops[0];
        Assert.Equal("1", first.Id);
        // 首站是「珞瑜二门」，**不是** line.startStopName 里的「信息学部四食堂」——
        // 两者是不同的字段，误当成同一个会写出错误的断言（这个坑真的踩过）。
        Assert.Equal("珞瑜二门", first.Name);
        Assert.Equal(1, first.Order);
        Assert.True(first.HasLocation);

        // 坐标是**字符串**形式的数字，必须能解析出来
        Assert.Equal(114.3583838953446, first.Longitude, 9);
        Assert.Equal(30.526744096016966, first.Latitude, 9);
    }

    /// <summary>站序按 stopOrder 升序，不按出现顺序。</summary>
    [Fact]
    public void OrdersStopsByStopOrder()
    {
        var line = BusParser.ParseLine(Real1号线);

        Assert.NotNull(line);
        Assert.Equal([1, 2, 3], line!.Stops.Select(s => s.Order).ToArray());
    }

    /// <summary>5 号线线上带 line2Id（回程线路），1/2 号线为空。</summary>
    [Fact]
    public void ReadsReturnLineIdWhenPresent()
    {
        const string five = """
            {"resultCode":"1","resultDes":"","data":{"lineName":"5号线","lineId":"10486-56-0",
            "lineNo":"56","startStopName":"教五转盘","endStopName":"医学部毛主席像",
            "firstTime":"12:30","lastTime":"21:10","line2Id":"10486-56-1",
            "stops":[],"buses":[]}}
            """;

        var line = BusParser.ParseLine(five);

        Assert.NotNull(line);
        Assert.Equal("10486-56-1", line!.ReturnLineId);
        Assert.Equal("12:30 - 21:10", line.ServiceHours);
        Assert.Empty(line.Stops);
    }

    /// <summary>折线是 "纬度,经度"，顺序反了会落到几内亚湾。</summary>
    [Fact]
    public void ParsesPathAsLatitudeThenLongitude()
    {
        var path = BusParser.ParsePath(RealPath);

        Assert.Equal(4, path.Count);
        Assert.Equal(30.526746, path[0].Latitude, 6);
        Assert.Equal(114.358103, path[0].Longitude, 6);
    }

    [Fact]
    public void AttachesPathToLine()
    {
        var line = BusParser.ParseLine(Real1号线);
        Assert.NotNull(line);
        Assert.Empty(line!.Path);

        var withPath = BusParser.AttachPath(line, RealPath);
        Assert.Equal(4, withPath.Path.Count);
        Assert.Equal("1号线", withPath.Name);
    }

    /// <summary>
    /// <c>resultCode</c> 是<b>字符串</b> "1"。写成数字比较会永远判失败——
    /// 这正是把「校巴一直显示暂无数据」归因到别处的典型陷阱。
    /// </summary>
    [Fact]
    public void AcceptsStringResultCode()
    {
        Assert.True(BusParser.TryUnwrap("""{"resultCode":"1","data":{"a":1}}""", out _));
        // 数字形式也接受，容错
        Assert.True(BusParser.TryUnwrap("""{"resultCode":1,"data":{"a":1}}""", out _));
    }

    [Fact]
    public void RejectsFailedResultCode()
    {
        Assert.False(BusParser.TryUnwrap("""{"resultCode":"0","data":null}""", out _));
        Assert.False(BusParser.TryUnwrap("""{"resultCode":"999","data":null}""", out _));
        Assert.Null(BusParser.ParseLine("""{"resultCode":"0","data":null}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>404</html>")]
    [InlineData("not json")]
    [InlineData("{\"resultCode\":\"1\"")]
    public void MalformedInputYieldsNothingRatherThanThrowing(string bad)
    {
        Assert.Null(BusParser.ParseLine(bad));
        Assert.Empty(BusParser.ParsePath(bad));
    }

    [Fact]
    public void FallsBackToRequestedIdWhenResponseOmitsIt()
    {
        const string noId = """
            {"resultCode":"1","data":{"lineName":"2号线","startStopName":"校大门","stops":[]}}
            """;

        var line = BusParser.ParseLine(noId, "10486-55-0", "备用名");

        Assert.NotNull(line);
        Assert.Equal("10486-55-0", line!.Id);
        Assert.Equal("2号线", line.Name);
    }

    [Fact]
    public void CountsOnlineBuses()
    {
        const string withBuses = """
            {"resultCode":"1","data":{"lineName":"1号线","lineId":"10486-50-0",
            "buses":[{"lng":1,"lat":2},{"lng":3,"lat":4}]}}
            """;

        var line = BusParser.ParseLine(withBuses);

        Assert.NotNull(line);
        Assert.Equal(2, line!.OnlineBusCount);
    }

    [Theory]
    [InlineData("!!!not base64!!!")]
    [InlineData("YWJj")]           // 合法 base64 但不是 JSON
    [InlineData("")]
    public void BadStopPayloadIsSkippedNotFatal(string b64)
    {
        Assert.Null(BusParser.DecodeStop(b64));
    }

    /// <summary>
    /// 前端 <c>app.js</c> 里写死的线路 id 是占位值 123456，不能拿来当真实 id 用。
    /// </summary>
    [Fact]
    public void KnownLineIdsAreNotTheFrontendPlaceholders()
    {
        Assert.NotEmpty(BusEndpoints.Lines);
        foreach (var l in BusEndpoints.Lines)
        {
            Assert.NotEqual("123456", l.Id);
            Assert.StartsWith(BusEndpoints.Tenant + "-", l.Id);
        }
    }

    /// <summary>不带 index.html 的路径会被 CAS 拦下，必须锁住正确入口。</summary>
    [Fact]
    public void PageUrlIsTheCasExemptPath()
    {
        Assert.EndsWith("/mobile/index.html", BusEndpoints.PageUrl);
        Assert.Contains(BusEndpoints.Tenant + "/line/", BusEndpoints.Raw.Line("X"));
        Assert.Contains(BusEndpoints.Tenant + "/linePath/", BusEndpoints.Raw.LinePath("X"));
        Assert.Contains(BusEndpoints.Tenant + "/line/X/bus", BusEndpoints.Raw.Bus("X"));
    }
}
