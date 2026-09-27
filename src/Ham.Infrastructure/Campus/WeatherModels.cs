using System.Globalization;

namespace Ham.Infrastructure.Campus;

/// <summary>
/// WMO 天气代码（WMO 4677）到中文描述、图标与风向的映射。
/// </summary>
/// <remarks>
/// Open-Meteo 返回的是 WMO 天气代码而非文本。集中放在一处便于核对与本地化。
/// </remarks>
public static class WmoWeatherCode
{
    /// <summary>取天气现象的中文描述。</summary>
    public static string Describe(int? code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 => "雾",
        48 => "雾凇",
        51 => "小毛毛雨",
        53 => "毛毛雨",
        55 => "大毛毛雨",
        56 => "冻毛毛雨",
        57 => "强冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 => "冻雨",
        67 => "强冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 => "阵雪",
        86 => "强阵雪",
        95 => "雷阵雨",
        96 => "雷阵雨伴小冰雹",
        99 => "雷阵雨伴大冰雹",
        _ => "未知",
    };

    /// <summary>取用于界面展示的图标（Emoji，无需额外资源文件）。</summary>
    public static string Icon(int? code) => code switch
    {
        0 => "☀️",
        1 => "🌤️",
        2 => "⛅",
        3 => "☁️",
        45 or 48 => "🌫",
        51 or 53 or 55 or 56 or 57 => "🌦",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧",
        71 or 73 or 75 or 77 or 85 or 86 => "❄️",
        95 or 96 or 99 => "⛈",
        _ => "🌡",
    };

    /// <summary>判断是否为需要带伞/注意出行的天气。</summary>
    public static bool IsPrecipitation(int? code) => code switch
    {
        >= 51 and <= 67 or >= 80 and <= 82 or >= 95 and <= 99 => true,
        _ => false,
    };

    /// <summary>把度数转成中文风向。</summary>
    public static string WindDirection(double? degrees)
    {
        if (degrees is null) return string.Empty;

        // 8 方位：正北为 0°，顺时针递增。
        var index = (int)Math.Round(degrees.Value / 45.0) % 8;
        return index switch
        {
            0 => "北风",
            1 => "东北风",
            2 => "东风",
            3 => "东南风",
            4 => "南风",
            5 => "西南风",
            6 => "西风",
            _ => "西北风",
        };
    }
}

/// <summary>天气查询结果。</summary>
public sealed class WeatherReport
{
    public required string City { get; init; }
    public required string Condition { get; init; }
    public required string Icon { get; init; }
    public int WeatherCode { get; init; }
    public required double TemperatureCelsius { get; init; }
    public double FeelsLikeCelsius { get; init; }
    public int HumidityPercent { get; init; }
    public double WindSpeedKmh { get; init; }
    public string WindDirection { get; init; } = string.Empty;
    public double PrecipitationMm { get; init; }
    public string ObservedAt { get; init; } = string.Empty;

    public IReadOnlyList<WeatherForecast> Forecast { get; init; } = [];

    public bool HasPrecipitation => WmoWeatherCode.IsPrecipitation(WeatherCode);

    /// <summary>基于天气给出穿衣与出行建议。</summary>
    public string GetAdvice()
    {
        var advice = new List<string>();

        if (TemperatureCelsius <= 0) advice.Add("严寒，注意保暖防滑");
        else if (TemperatureCelsius <= 8) advice.Add("气温较低，注意添衣");
        else if (TemperatureCelsius >= 30) advice.Add("高温炎热，注意补水防晒");
        else if (TemperatureCelsius is >= 16 and < 26) advice.Add("温度舒适");

        if (HasPrecipitation) advice.Add("有降水，建议携带雨具");
        if (HumidityPercent >= 80) advice.Add("湿度较高，体感可能偏闷");

        if (WindSpeedKmh >= 30) advice.Add("风力较大");

        return advice.Count > 0 ? string.Join("；", advice) : "天气状况良好";
    }

    public string TemperatureText => $"{TemperatureCelsius:F0}°C";
}

/// <summary>一天的天气预报。</summary>
public sealed class WeatherForecast
{
    public required DateOnly Date { get; init; }
    public required string Condition { get; init; }
    public double TemperatureMaxCelsius { get; init; }
    public double TemperatureMinCelsius { get; init; }
    public int PrecipitationProbability { get; init; }

    public string TemperatureText
        => $"{TemperatureMinCelsius:F0}° / {TemperatureMaxCelsius:F0}°";

    public string Icon => WmoWeatherCode.Icon(
        PrecipitationProbability >= 60 ? 61 : null);

    public string DateLabel
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return Date switch
            {
                _ when Date == today => "今天",
                _ when Date == today.AddDays(1) => "明天",
                _ when Date == today.AddDays(2) => "后天",
                _ => Date.ToString("MM-dd", CultureInfo.InvariantCulture),
            };
        }
    }
}
