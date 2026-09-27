namespace Ham.Infrastructure.Sport.Models;

/// <summary>运动项目。</summary>
public sealed record SportType
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Icon { get; init; } = "🏀";

    /// <summary>是否为当前选中的运动项目（仅用于界面高亮）。</summary>
    public bool IsSelected { get; set; }
}

/// <summary>运动场馆。</summary>
public sealed record SportVenue
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string SportTypeId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string OpenHours { get; init; } = string.Empty;
    public int SlotMinutes { get; init; } = 60;
}

/// <summary>一个可预约的场次时段。</summary>
public sealed record SportSlot
{
    public required string Id { get; init; }
    public required string VenueId { get; init; }
    public required DateOnly Date { get; init; }
    public required TimeOnly Start { get; init; }
    public required TimeOnly End { get; init; }

    /// <summary>该场次总容量。</summary>
    public int Capacity { get; init; }

    /// <summary>已预约数量。</summary>
    public int Booked { get; init; }

    public int Remaining => Math.Max(0, Capacity - Booked);

    public bool IsFull => Remaining <= 0;

    public string Describe() => $"{Date:MM-dd} {Start:HH\\:mm}–{End:HH\\:mm}";
}

/// <summary>订单状态。</summary>
public enum SportOrderStatus
{
    /// <summary>待支付。</summary>
    PendingPayment,

    /// <summary>已支付。</summary>
    Paid,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>已取消。</summary>
    Cancelled,

    /// <summary>已过期。</summary>
    Expired,
}

/// <summary>一条场馆预约订单。</summary>
public sealed record SportBooking
{
    public required string Id { get; init; }
    public required string VenueId { get; init; }
    public required string VenueName { get; init; }
    public required string SportTypeName { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public SportOrderStatus Status { get; init; } = SportOrderStatus.PendingPayment;

    /// <summary>学校支付平台的下单链接（需在浏览器中完成支付）。</summary>
    public string? PaymentUrl { get; init; }

    public decimal Price { get; init; }

    public DateTime? PaidAt { get; init; }

    public string Describe() => $"{VenueName} · {SportTypeName} · {Start:MM-dd HH\\:mm}";
}

/// <summary>收藏的快速预定配置。</summary>
public sealed record FavoriteSportBooking
{
    public required string Id { get; init; }
    public required string SportTypeId { get; init; }
    public required string SportTypeName { get; init; }
    public required string VenueId { get; init; }
    public required string VenueName { get; init; }
    public int SlotMinutes { get; init; } = 60;
    public int ReminderMinutesBefore { get; init; } = 30;
    public bool Enabled { get; init; } = true;
}
