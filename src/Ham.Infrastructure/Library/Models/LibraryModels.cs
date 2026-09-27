namespace Ham.Infrastructure.Library.Models;

/// <summary>图书馆房间。</summary>
public sealed record LibraryRoom
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Building { get; init; } = string.Empty;
    public int Floor { get; init; }
    public int TotalSeats { get; init; }
    public bool SupportsReservation { get; init; } = true;
}

/// <summary>座位及其在某日的占用情况。</summary>
public sealed record LibrarySeat
{
    public required string Id { get; init; }
    public required string RoomId { get; init; }
    public required string RoomName { get; init; }
    public required string Label { get; init; }

    /// <summary>座位特性，例如 "靠窗" "带电源"。</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    public bool HasPower { get; init; }
    public bool IsWindowSeat { get; init; }

    /// <summary>该座位在目标日期/时段是否已被占用。</summary>
    public bool IsOccupied { get; init; }

    public string? OccupiedByHint { get; init; }
}

/// <summary>一个预约时段。</summary>
public sealed record ReservationSlot
{
    public required DateOnly Date { get; init; }
    public required TimeOnly Start { get; init; }
    public required TimeOnly End { get; init; }

    public TimeSpan Duration => End.ToTimeSpan() - Start.ToTimeSpan();

    public bool Contains(TimeOnly t) => t >= Start && t < End;

    public override string ToString() => $"{Date:yyyy-MM-dd} {Start:HH\\:mm}–{End:HH\\:mm}";
}

/// <summary>预约状态。</summary>
public enum BookingStatus
{
    /// <summary>预约成功且未入馆。</summary>
    Reserved,

    /// <summary>已入馆，需在时限内返回。</summary>
    CheckedIn,

    /// <summary>已取消。</summary>
    Cancelled,

    /// <summary>已过期。</summary>
    Expired,
}

/// <summary>一条图书馆预约记录。</summary>
public sealed record LibraryBooking
{
    public required string Id { get; init; }
    public required string RoomId { get; init; }
    public required string RoomName { get; init; }
    public required string SeatId { get; init; }
    public required string SeatLabel { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public BookingStatus Status { get; init; } = BookingStatus.Reserved;

    /// <summary>入馆时间；用于计算是否已超时需重新预约。</summary>
    public DateTime? CheckedInAt { get; init; }

    public string? Building { get; init; }

    /// <summary>中途离开后需返回的截止时间。</summary>
    public DateTime? MustReturnBy { get; init; }

    public bool IsActive => Status is BookingStatus.Reserved or BookingStatus.CheckedIn
        && End > DateTime.Now;

    public string Describe() => $"{RoomName} · {SeatLabel} · {Start:MM-dd HH\\:mm}–{End:HH\\:mm}";
}

/// <summary>看板查询结果。</summary>
public sealed record SeatBoard
{
    public required IReadOnlyList<LibraryRoom> Rooms { get; init; }
    public required IReadOnlyList<LibrarySeat> Seats { get; init; }
    public required ReservationSlot Slot { get; init; }

    public int AvailableCount => Seats.Count(s => !s.IsOccupied);

    public int TotalCount => Seats.Count;
}
