using System.Globalization;
using System.Text;
using System.Text.Json;
using Ham.Core.Models;

namespace Ham.Infrastructure.Campus;

/// <summary>
/// 校巴接口响应解析。
/// </summary>
/// <remarks>
/// 线上真实结构（抓包实测，不是猜的）：
/// <code>
/// { "resultCode":"1", "resultDes":"", "data": { ... } }          线路详情
/// { "resultCode":"1", "resultDes":"", "data": "lat,lng;lat,lng;…" }  轨迹折线
/// </code>
/// <para>
/// <b>站点数组每一项都是 base64 编码的 JSON</b>，而且 base64 文本里带换行符
/// （每行约 64 字符）。解码前必须去掉所有空白，否则 <c>Convert.FromBase64String</c>
/// 会直接抛异常——这是实测最容易踩的坑。
/// </para>
/// </remarks>
public static class BusParser
{
    /// <summary>
    /// 解析线路详情响应。
    /// </summary>
    /// <param name="json">接口返回的 JSON 原文。</param>
    /// <param name="fallbackId">
    /// 当响应里没带 <c>lineId</c> 时使用的 id（通常是请求时用的那个）。
    /// </param>
    /// <param name="fallbackName">当响应里没带 <c>lineName</c> 时使用的名称。</param>
    /// <returns>解析出的线路；外壳校验不通过或 data 不是对象时返回 <c>null</c>。</returns>
    public static BusLine? ParseLine(string? json, string fallbackId = "", string fallbackName = "")
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        if (!TryUnwrap(json, out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Str(data, "lineId") is { Length: > 0 } v ? v : fallbackId;
        var name = Str(data, "lineName") is { Length: > 0 } n ? n : fallbackName;
        if (id.Length == 0 && name.Length == 0) return null;

        var buses = 0;
        if (data.TryGetProperty("buses", out var b) && b.ValueKind == JsonValueKind.Array)
        {
            buses = b.GetArrayLength();
        }

        return new BusLine
        {
            Id = id,
            Name = name,
            Number = Str(data, "lineNo"),
            Direction = Int(data, "direction"),
            StartStop = Str(data, "startStopName"),
            EndStop = Str(data, "endStopName"),
            FirstTime = Str(data, "firstTime"),
            LastTime = Str(data, "lastTime"),
            Price = Str(data, "price"),
            ReturnLineId = Str(data, "line2Id"),
            Stops = ParseStops(data),
            OnlineBusCount = buses,
        };
    }

    /// <summary>
    /// 解析轨迹折线响应。
    /// </summary>
    /// <remarks>
    /// <c>data</c> 是 <c>"30.526746,114.358103;30.526744,114.358277;…"</c> 这种
    /// 分号分隔、<b>纬度在前经度在后</b>的串。顺序反了会落到几内亚湾，
    /// 所以这里按 <c>lat,lng</c> 解析。
    /// </remarks>
    public static IReadOnlyList<(double Latitude, double Longitude)> ParsePath(string? json)
    {
        if (!TryUnwrap(json, out var data) || data.ValueKind != JsonValueKind.String) return [];

        var text = data.GetString();
        if (string.IsNullOrWhiteSpace(text)) return [];

        var points = new List<(double, double)>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) continue;
            points.Add((lat, lng));
        }

        return points;
    }

    /// <summary>合并解析一条线路的详情与轨迹。</summary>
    public static BusLine AttachPath(BusLine line, string? pathJson)
    {
        var path = ParsePath(pathJson);
        return path.Count == 0 ? line : line with { Path = path };
    }

    /// <summary>解析 <c>stops</c> 数组（每项是 base64 JSON）。</summary>
    public static IReadOnlyList<BusStop> ParseStops(JsonElement data)
    {
        if (!data.TryGetProperty("stops", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var stops = new List<BusStop>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var stop = DecodeStop(item.GetString());
            if (stop is not null) stops.Add(stop);
        }

        // 站序缺失时用出现顺序兜底，否则排序会把站点打乱
        return stops
            .Select((s, i) => s.Order > 0 ? s : s with { Order = i + 1 })
            .OrderBy(s => s.Order)
            .ToList();
    }

    /// <summary>解码单个站点的 base64 JSON。</summary>
    public static BusStop? DecodeStop(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        // 关键：线上 base64 里带换行，不去掉空白就解码失败
        var cleaned = new string(base64.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (cleaned.Length == 0) return null;

        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(cleaned)));
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;

            var name = Str(o, "stopName");
            if (name.Length == 0) return null;

            return new BusStop
            {
                // 线上字段是小写 d 的 stopId。写成 stopID 会静默取到空串——
                // 这处是照着真实响应解出来的，不是猜的字段名。
                Id = Str(o, "stopId") is { Length: > 0 } id ? id : Str(o, "stopID"),
                Name = name,
                Longitude = Dbl(o, "lng"),
                Latitude = Dbl(o, "lat"),
                Order = Int(o, "stopOrder"),
                Metro = Str(o, "metro"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 剥掉外壳 <c>{resultCode, resultDes, data}</c> 并校验成功标志。
    /// </summary>
    /// <remarks>
    /// <b><c>resultCode</c> 是字符串 "1"</b>，不是数字。写成 <c>== 1</c> 会永远判失败。
    /// </remarks>
    public static bool TryUnwrap(string? json, out JsonElement data)
    {
        data = default;
        if (string.IsNullOrWhiteSpace(json)) return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("resultCode", out var code))
            {
                var ok = code.ValueKind == JsonValueKind.String
                    ? code.GetString() == BusEndpoints.SuccessCode
                    : code.ValueKind == JsonValueKind.Number && code.GetInt32() == 1;
                if (!ok) return false;
            }

            data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            return true;
        }
    }

    // ── 小工具 ──

    private static string Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            // 线路号之类的字段线上是字符串，但别排除某天改成数字
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty,
        };
    }

    private static int Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    private static double Dbl(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            _ = double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
            return d;
        }
        return 0;
    }

    /// <summary>base64 长度不是 4 的倍数时补齐，否则解码抛异常。</summary>
    private static string PadBase64(string s)
        => s.Length % 4 == 0 ? s : s.PadRight(s.Length + (4 - s.Length % 4), '=');
}
