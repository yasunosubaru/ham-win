using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ham.Core.Models;

namespace Ham.Infrastructure.Campus;

/// <summary>
/// 实时天气客户端。
/// </summary>
/// <remarks>
/// 数据源：Open-Meteo（<c>api.open-meteo.com</c>），基于各国气象机构的开放数据聚合，
/// <b>无需注册与 API Key</b>，也不会记录客户端 IP 用途之外的追踪信息。
/// 这对校园客户端很关键：不需要用户去申请密钥，也不产生配额压力。
/// </remarks>
public sealed class WeatherClient
{
    /// <summary>武汉大学樱园位置（WGS84）。</summary>
    public const double WuhanLatitude = 30.5457;
    public const double WuhanLongitude = 114.3419;

    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";

    private readonly HttpClient _http;

    public WeatherClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                Net.CampusHttpClient.DesktopUserAgent);
        }
    }

    /// <summary>取实时天气与短期预报。</summary>
    public async Task<WeatherReport> GetAsync(
        double latitude = WuhanLatitude,
        double longitude = WuhanLongitude,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?latitude={latitude.ToString("F4", CultureInfo.InvariantCulture)}" +
                  $"&longitude={longitude.ToString("F4", CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,apparent_temperature,relative_humidity_2m," +
                  "weather_code,wind_speed_10m,wind_direction_10m,precipitation" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                  "&timezone=Asia%2FShanghai&forecast_days=3";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<OpenMeteoResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (dto?.Current is null) throw new InvalidOperationException("天气服务返回内容不完整。");

        var current = dto.Current;

        var forecast = (dto.Daily?.Time ?? []).Select((date, i) => new WeatherForecast
        {
            Date = DateOnly.Parse(date, CultureInfo.InvariantCulture),
            Condition = WmoWeatherCode.Describe(AtInt(dto.Daily!.WeatherCode, i)),
            TemperatureMaxCelsius = AtDouble(dto.Daily.TemperatureMax, i),
            TemperatureMinCelsius = AtDouble(dto.Daily.TemperatureMin, i),
            PrecipitationProbability = AtInt(dto.Daily.PrecipitationProbabilityMax, i),
        }).ToList();

        return new WeatherReport
        {
            City = "武汉",
            ObservedAt = current.Time ?? string.Empty,
            TemperatureCelsius = current.Temperature,
            FeelsLikeCelsius = current.ApparentTemperature ?? current.Temperature,
            HumidityPercent = current.RelativeHumidity ?? 0,
            WindSpeedKmh = current.WindSpeed ?? 0,
            WindDirection = WmoWeatherCode.WindDirection(current.WindDirectionDegrees),
            PrecipitationMm = current.Precipitation ?? 0,
            Condition = WmoWeatherCode.Describe(current.WeatherCode),
            WeatherCode = current.WeatherCode ?? 0,
            Icon = WmoWeatherCode.Icon(current.WeatherCode ?? 0),
            Forecast = forecast,
        };
    }

    private static int AtInt(List<int?>? list, int index)
        => list is not null && index < list.Count ? list[index] ?? 0 : 0;

    private static double AtDouble(List<double?>? list, int index)
        => list is not null && index < list.Count ? list[index] ?? 0 : 0;

    // ── Open-Meteo DTO ──────────────────────────────────────────────────────

    private sealed class OpenMeteoResponse
    {
        [JsonPropertyName("timezone")] public string? Timezone { get; set; }
        [JsonPropertyName("current")] public Current? Current { get; set; }
        [JsonPropertyName("daily")] public Daily? Daily { get; set; }
    }

    private sealed class Current
    {
        [JsonPropertyName("time")] public string? Time { get; set; }
        [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
        [JsonPropertyName("apparent_temperature")] public double? ApparentTemperature { get; set; }
        [JsonPropertyName("relative_humidity_2m")] public int? RelativeHumidity { get; set; }
        [JsonPropertyName("weather_code")] public int? WeatherCode { get; set; }
        [JsonPropertyName("wind_speed_10m")] public double? WindSpeed { get; set; }
        [JsonPropertyName("wind_direction_10m")] public double? WindDirectionDegrees { get; set; }
        [JsonPropertyName("precipitation")] public double? Precipitation { get; set; }
    }

    private sealed class Daily
    {
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("weather_code")] public List<int?>? WeatherCode { get; set; }
        [JsonPropertyName("temperature_2m_max")] public List<double?>? TemperatureMax { get; set; }
        [JsonPropertyName("temperature_2m_min")] public List<double?>? TemperatureMin { get; set; }
        [JsonPropertyName("precipitation_probability_max")] public List<int?>? PrecipitationProbabilityMax { get; set; }
    }
}
