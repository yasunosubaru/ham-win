using System.Globalization;
using System.Text.Json;
using Ham.Infrastructure.Library.Models;

namespace Ham.Infrastructure.Library;

/// <summary>图书馆接口返回的座位余量（一层）。</summary>
public sealed record LibraryAreaAvailability(
    string AreaId,
    string AreaName,
    int FreeSeats,
    int TotalSeats)
{
    public int UsedSeats => Math.Max(0, TotalSeats - FreeSeats);

    public double FreeRatio => TotalSeats <= 0 ? 0 : (double)FreeSeats / TotalSeats;

    public string Describe()
        => TotalSeats <= 0
            ? $"{AreaName}（余座未知）"
            : $"{AreaName} 余 {FreeSeats} / {TotalSeats}";
}

/// <summary>
/// 图书馆接口返回的解析器。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意写得容错，且在解析不出内容时把原始正文落进日志。</b>
/// 原因很直接：<c>findRoomDuration</c> 与 <c>user/history</c> 的响应结构
/// 需要登录后才能看到，而登录必须由用户用真实凭据完成。
/// 与其凭猜测写死字段名（错了就静默显示"暂无数据"，用户无从判断是接口坏了还是没课），
/// 不如把原始正文记下来——用户跑一次，我们就能据此校准。
/// </para>
/// <para>
/// 字段名按站前端常见命名多路兜底（freeSeat/freeNum/remain/…），
/// 但任何情况下都以"解析到了几条"为准，不做乐观假设。
/// </para>
/// </remarks>
public static class LibraryParser
{
    private static readonly string[] IdKeys = ["areaId", "roomId", "id", "venueId", "buildingId"];
    private static readonly string[] NameKeys = ["areaName", "roomName", "name", "venueName", "buildingName", "title"];
    private static readonly string[] FreeKeys = ["freeSeat", "freeNum", "freeCount", "remain", "remainNum", "available", "vacant", "idle"];
    private static readonly string[] TotalKeys = ["totalSeat", "totalNum", "totalCount", "seatCount", "count", "sum"];
    private static readonly string[] LabelKeys = ["seatLabel", "label", "seatNo", "seatName", "seatCode"];
    private static readonly string[] StartKeys = ["startTime", "beginTime", "start", "makeTime", "bookTime"];
    private static readonly string[] EndKeys = ["endTime", "finishTime", "end"];
    private static readonly string[] StateKeys = ["status", "state", "makeStatus", "statusName"];

    /// <summary>解析座位余量。解析不出任何条目时返回空列表（原始正文已由调用方落日志）。</summary>
    public static IReadOnlyList<LibraryAreaAvailability> ParseAvailability(JsonElement data)
    {
        var result = new List<LibraryAreaAvailability>();

        foreach (var item in EnumerateItems(data))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = FirstString(item, IdKeys) ?? string.Empty;
            var name = FirstString(item, NameKeys) ?? string.Empty;
            if (id.Length == 0 && name.Length == 0) continue;

            var free = FirstInt(item, FreeKeys);
            var total = FirstInt(item, TotalKeys);
            if (total < free) total = free;

            result.Add(new LibraryAreaAvailability(
                id.Length == 0 ? name : id,
                name.Length == 0 ? id : name,
                free, total));
        }

        return result;
    }

    /// <summary>解析我的预约记录。</summary>
    public static IReadOnlyList<LibraryBooking> ParseReservations(JsonElement data)
    {
        var result = new List<LibraryBooking>();

        foreach (var item in EnumerateItems(data))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = FirstString(item, IdKeys) ?? string.Empty;
            var name = FirstString(item, NameKeys) ?? string.Empty;
            if (id.Length == 0) continue;

            var start = ParseDateTime(FirstRaw(item, StartKeys));
            var end = ParseDateTime(FirstRaw(item, EndKeys));
            if (start is null) continue;

            result.Add(new LibraryBooking
            {
                Id = id,
                RoomId = id,
                RoomName = name,
                SeatId = id,
                SeatLabel = FirstString(item, LabelKeys) ?? string.Empty,
                Start = start.Value,
                End = end ?? start.Value.AddHours(3),
                Status = MapStatus(FirstRaw(item, StateKeys)),
            });
        }

        return result;
    }

    /// <summary>把 data 节点摊平成可枚举条目：既支持数组，也支持以 id 为键的对象。</summary>
    private static IEnumerable<JsonElement> EnumerateItems(JsonElement data)
    {
        switch (data.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in data.EnumerateArray()) yield return item;
                break;

            case JsonValueKind.Object:
                // 形如 { "areaId1": {...}, "areaId2": {...} }
                var allScalar = data.EnumerateObject()
                    .All(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array));
                if (allScalar) yield return data;

                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        // 把键补进对象里当 id，省得调用方猜
                        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["id"] = prop.Name,
                            ["node"] = prop.Value,
                        }));

                        yield return doc.RootElement.Clone();
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray()) yield return item;
                    }
                }
                break;
        }
    }

    private static string? FirstString(JsonElement e, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!e.TryGetProperty(k, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            else if (v.ValueKind == JsonValueKind.Number)
            {
                return v.ToString();
            }
        }
        return null;
    }

    private static int FirstInt(JsonElement e, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!e.TryGetProperty(k, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number when v.TryGetInt32(out var n):
                    return n;
                case JsonValueKind.String:
                    // 有些字段是 "12" 或 "12/100"
                    var digits = new string(v.GetString()!.TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
                        return m;
                    break;
                case JsonValueKind.True:
                    return 1;
            }
        }
        return 0;
    }

    private static JsonElement? FirstRaw(JsonElement e, string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v))
                return v;
        return null;
    }

    /// <summary>
    /// 解析时间。接口可能给毫秒时间戳、<c>yyyy-MM-dd HH:mm</c> 或 ISO 8601。
    /// </summary>
    private static DateTime? ParseDateTime(JsonElement? v)
    {
        if (v is null) return null;

        switch (v.Value.ValueKind)
        {
            case JsonValueKind.Number when v.Value.TryGetInt64(out var ms):
                return FromEpoch(ms);
            case JsonValueKind.String:
                var s = v.Value.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;

                if (long.TryParse(s, out var n)) return FromEpoch(n);

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    return parsed;
                }

                foreach (var fmt in new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyy-MM-dd HH:mm:ss" })
                {
                    if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var exact))
                    {
                        return exact;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static DateTime FromEpoch(long n)
        // 10 位是秒，13 位是毫秒
        => n > 1_000_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(n).LocalDateTime
            : n > 1_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime
                : DateTime.MinValue;

    private static BookingStatus MapStatus(JsonElement? v)
    {
        var text = v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.ToString(),
            _ => null,
        };

        return text?.ToLowerInvariant() switch
        {
            null or "" or "0" => BookingStatus.Reserved,
            "1" => BookingStatus.CheckedIn,
            "2" or "3" or "cancel" or "cancelled" => BookingStatus.Cancelled,
            _ => BookingStatus.Reserved,
        };
    }
}
