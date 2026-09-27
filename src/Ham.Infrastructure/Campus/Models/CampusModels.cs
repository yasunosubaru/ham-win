namespace Ham.Infrastructure.Campus.Models;

/// <summary>实时天气。</summary>
public sealed record WeatherInfo
{
    public required string City { get; init; }
    public required string Condition { get; init; }
    public required double TemperatureCelsius { get; init; }
    public double FeelsLikeCelsius { get; init; }
    public int HumidityPercent { get; init; }
    public string WindDirection { get; init; } = string.Empty;
    public string WindScale { get; init; } = string.Empty;

    /// <summary>降水概率（0-100），无数据时为 null。</summary>
    public int? PrecipitationProbability { get; init; }

    public string ObservedAt { get; init; } = string.Empty;

    /// <summary>基于天气给出的穿衣/出行建议。</summary>
    public string GetAdvice()
    {
        var advice = new List<string>();

        if (TemperatureCelsius <= 4) advice.Add("气温较低，注意保暖");
        else if (TemperatureCelsius >= 30) advice.Add("高温炎热，注意补水防晒");
        else if (TemperatureCelsius is >= 16 and < 26) advice.Add("温度舒适");

        if (Condition.Contains("雨")) advice.Add("有降水，建议携带雨具");
        if (PrecipitationProbability is >= 60) advice.Add("降水概率较高");
        if (Condition.Contains("雪")) advice.Add("有降雪，路面湿滑注意安全");

        return advice.Count > 0 ? string.Join("；", advice) : "天气状况良好";
    }
}

/// <summary>校巴站点。</summary>
public sealed record BusStop
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>校巴线路。</summary>
public sealed record BusLine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> StopIds { get; init; } = [];
    public string FirstTime { get; init; } = string.Empty;
    public string LastTime { get; init; } = string.Empty;
}

/// <summary>校巴实时到站信息。</summary>
public sealed record BusArrival
{
    public required string LineId { get; init; }
    public required string LineName { get; init; }
    public required string StopId { get; init; }
    public required string StopName { get; init; }

    /// <summary>预计到达分钟数；null 表示暂无数据。</summary>
    public int? MinutesAway { get; init; }

    /// <summary>距离当前站还有几站。</summary>
    public int? RemainingStops { get; init; }

    public string Direction { get; init; } = string.Empty;
    public DateTime? UpdatedAt { get; init; }

    public string Describe() => MinutesAway switch
    {
        null => $"{LineName} 暂无到站信息",
        0 => $"{LineName} 即将到站",
        _ => $"{LineName} 约 {MinutesAway} 分钟",
    };

    /// <summary>预先格式化的到站文本与剩余站数，供界面直接绑定。</summary>
    public string MinutesAwayText => MinutesAway is null ? "—" : $"{MinutesAway} 分钟";

    public string RemainingStopsText => RemainingStops is null ? string.Empty : $"还有 {RemainingStops} 站";
}

/// <summary>珞珈 E 卡信息。</summary>
public sealed record ECardInfo
{
    public required string CardNumber { get; init; }
    public required string HolderName { get; init; }
    public decimal Balance { get; init; }
    public string QrPayload { get; init; } = string.Empty;
    public DateTime? ExpiresAt { get; init; }
}
