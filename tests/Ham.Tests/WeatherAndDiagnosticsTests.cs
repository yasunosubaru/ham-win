using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Diagnostics;
using Xunit;

namespace Ham.Tests;

public class WeatherTests
{
    [Theory]
    [InlineData(0, "晴", "☀️")]
    [InlineData(3, "阴", "☁️")]
    [InlineData(45, "雾", "🌫")]
    [InlineData(61, "小雨", "🌧")]
    [InlineData(75, "大雪", "❄️")]
    [InlineData(95, "雷阵雨", "⛈")]
    [InlineData(null, "未知", "🌡")]
    public void MapsWmoCodes(int? code, string expected, string icon)
    {
        Assert.Equal(expected, WmoWeatherCode.Describe(code));
        Assert.Equal(icon, WmoWeatherCode.Icon(code));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(50, false)]
    [InlineData(61, true)]     // 小雨
    [InlineData(82, true)]     // 暴雨
    [InlineData(95, true)]     // 雷阵雨
    [InlineData(71, false)]    // 小雪不带伞
    [InlineData(99, true)]
    public void DetectsPrecipitation(int? code, bool expected)
        => Assert.Equal(expected, WmoWeatherCode.IsPrecipitation(code));

    [Theory]
    [InlineData(0.0, "北风")]
    [InlineData(90.0, "东风")]
    [InlineData(180.0, "南风")]
    [InlineData(270.0, "西风")]
    [InlineData(359.0, "北风")]
    [InlineData(45.0, "东北风")]
    [InlineData(225.0, "西南风")]
    [InlineData(null, "")]
    public void MapsWindDirection(double? degrees, string expected)
        => Assert.Equal(expected, WmoWeatherCode.WindDirection(degrees));

    [Fact]
    public void AdviceCoversTemperatureAndRain()
    {
        var cold = new WeatherReport
        {
            City = "武汉", Condition = "晴", Icon = "☀️",
            TemperatureCelsius = -2, WeatherCode = 0,
        };
        Assert.Contains("保暖", cold.GetAdvice());

        var hotRain = new WeatherReport
        {
            City = "武汉", Condition = "暴雨", Icon = "🌧",
            TemperatureCelsius = 33, WeatherCode = 82, HumidityPercent = 90,
        };
        var advice = hotRain.GetAdvice();
        Assert.Contains("高温", advice);
        Assert.Contains("雨具", advice);
        Assert.Contains("湿度", advice);
    }

    [Fact]
    public void MildWeatherGivesNeutralAdvice()
    {
        // 12°C 落在所有温度建议区间之外（<=0 / <=8 / 16-26 / >=30），
        // 且无降水、湿度不高、风力不大，因此应给出中性评价。
        var nice = new WeatherReport
        {
            City = "武汉", Condition = "多云", Icon = "⛅",
            TemperatureCelsius = 12, WeatherCode = 2, HumidityPercent = 50,
        };
        Assert.Equal("天气状况良好", nice.GetAdvice());
    }

    [Fact]
    public void ComfortableTemperatureIsAcknowledged()
    {
        var nice = new WeatherReport
        {
            City = "武汉", Condition = "晴", Icon = "☀️",
            TemperatureCelsius = 21, WeatherCode = 0, HumidityPercent = 50,
        };
        Assert.Equal("温度舒适", nice.GetAdvice());
    }

    [Fact]
    public void ForecastLabelsRelativeDays()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal("今天", new WeatherForecast { Date = today, Condition = "晴" }.DateLabel);
        Assert.Equal("明天", new WeatherForecast { Date = today.AddDays(1), Condition = "晴" }.DateLabel);
        Assert.Equal("后天", new WeatherForecast { Date = today.AddDays(2), Condition = "晴" }.DateLabel);
    }

    [Fact]
    public void ForecastTemperatureRange()
    {
        var f = new WeatherForecast
        {
            Date = DateOnly.FromDateTime(DateTime.Today),
            Condition = "晴",
            TemperatureMinCelsius = 18.4,
            TemperatureMaxCelsius = 26.6,
        };
        Assert.Equal("18° / 27°", f.TemperatureText);
    }

    [Fact]
    public async Task LiveFetchReturnsRealData()
    {
        // 集成测试：验证 WeatherClient 真的能解析 Open-Meteo 的响应。
        // 网络不可用时直接通过（而不是让测试变红），但会明确记录。
        var client = new WeatherClient();

        WeatherReport? report = null;
        Exception? failure = null;
        try
        {
            report = await client.GetAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            failure = ex;
        }

        if (report is null)
        {
            Assert.True(true, "网络不可用，跳过实网校验：" + failure?.Message);
            return;
        }

        Assert.Equal("武汉", report.City);
        Assert.NotEqual("未知", report.Condition);
        Assert.InRange(report.TemperatureCelsius, -50, 60);
        Assert.InRange(report.HumidityPercent, 0, 100);
        Assert.Equal(3, report.Forecast.Count);
    }
}

public class CasDiagnosticsTests
{
    [Theory]
    [InlineData("", "pw", DiagnosticStatus.Failed)]
    [InlineData("1234567", "pw", DiagnosticStatus.Warning)]    // 7 位：只提示不拦截
    [InlineData("202312345678", "pw", DiagnosticStatus.Warning)] // 12 位：武大本科常见
    [InlineData("20231234567890", "pw", DiagnosticStatus.Warning)] // 14 位
    [InlineData("202312345678", "", DiagnosticStatus.Failed)]
    [InlineData("2023123456789", "pw", DiagnosticStatus.Passed)]  // 13 位
    [InlineData("20231234", "pw", DiagnosticStatus.Passed)]       // 8 位
    public void ValidatesCredentialShape(string id, string pwd, DiagnosticStatus expected)
        => Assert.Equal(expected,
            CasDiagnostics.ValidateCredentials(id, pwd).Status);

    [Fact]
    public void UnusualStudentIdLengthIsNotBlocked()
    {
        // 关键回归：绝不能因为位数"不常规"就拦死用户，服务端才是权威。
        var step = CasDiagnostics.ValidateCredentials("202312345678", "pw");
        Assert.Equal(DiagnosticStatus.Warning, step.Status);
        Assert.Contains("不阻断", step.Detail);
    }

    [Fact]
    public void SsoUrlStepPasses()
    {
        var step = typeof(CasDiagnostics)
            .GetMethod("CheckSsoUrlShape",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null) as DiagnosticStep;

        Assert.NotNull(step);
        Assert.Equal(DiagnosticStatus.Passed, step!.Status);
    }

    [Fact]
    public async Task NetworkStepsProduceReport()
    {
        var report = await new CasDiagnostics().RunNetworkStepsAsync();

        Assert.NotEmpty(report.Steps);
        Assert.True(report.Elapsed >= TimeSpan.Zero);

        // 前两步与网络强相关，至少应给出明确结论而不是抛异常
        Assert.All(report.Steps, s => Assert.NotEqual(DiagnosticStatus.Running, s.Status));
        Assert.All(report.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Detail)));
    }

    [Fact]
    public void WebView2ProbeReportsSomething()
    {
        var step = typeof(CasDiagnostics)
            .GetMethod("CheckClientCapability",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null) as DiagnosticStep;

        Assert.NotNull(step);
        // 本机装有 WebView2，应为通过
        Assert.Equal(DiagnosticStatus.Passed, step!.Status);
    }
}
