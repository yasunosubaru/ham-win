namespace Ham.Core.Models;

/// <summary>校巴站点。</summary>
/// <remarks>
/// 坐标是 <b>GCJ-02</b>（高德/国测局加密坐标），不是 WGS-84。
/// 直接丢给高德地图没问题；丢给 GPS/其他底图会偏移几百米。
/// 校巴前端用的就是高德 JS API，<c>linePath</c> 折线与站点坐标同为 GCJ-02。
/// </remarks>
public sealed record BusStop
{
    /// <summary>站点编号（线路内唯一）。</summary>
    public required string Id { get; init; }

    /// <summary>站名，如「信息学部四食堂」。</summary>
    public required string Name { get; init; }

    /// <summary>经度，GCJ-02。</summary>
    public double Longitude { get; init; }

    /// <summary>纬度，GCJ-02。</summary>
    public double Latitude { get; init; }

    /// <summary>在线路中的先后次序，从 1 开始。</summary>
    public int Order { get; init; }

    /// <summary>所属地铁/接驳标识，线上数据实测为空串。</summary>
    public string Metro { get; init; } = string.Empty;

    /// <summary>坐标是否有效。既非 0/0 也非空串才算有效。</summary>
    public bool HasLocation => Math.Abs(Latitude) > 1e-6 || Math.Abs(Longitude) > 1e-6;
}

/// <summary>校巴线路。</summary>
public sealed record BusLine
{
    /// <summary>线路 ID，形如 <c>10486-50-0</c>。调接口要原样传回去，<b>不能只用 lineNo</b>。</summary>
    public required string Id { get; init; }

    /// <summary>对外名称，如「1号线」。</summary>
    public required string Name { get; init; }

    /// <summary>线路内部编号，如 <c>50</c>。</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>方向标记，线上实测为 0。</summary>
    public int Direction { get; init; }

    /// <summary>起点站名。</summary>
    public string StartStop { get; init; } = string.Empty;

    /// <summary>终点站名。</summary>
    public string EndStop { get; init; } = string.Empty;

    /// <summary>首班时间，形如 <c>07:20</c>。</summary>
    public string FirstTime { get; init; } = string.Empty;

    /// <summary>末班时间，形如 <c>22:40</c>。</summary>
    public string LastTime { get; init; } = string.Empty;

    /// <summary>票价文案，形如 <c>1.0~2.0</c>（元）。</summary>
    public string Price { get; init; } = string.Empty;

    /// <summary>回程线路 ID。为空表示该线路未提供反向数据（实测 1/2 号线为空，5 号线有）。</summary>
    public string ReturnLineId { get; init; } = string.Empty;

    /// <summary>站点，按 <see cref="BusStop.Order"/> 升序。</summary>
    public IReadOnlyList<BusStop> Stops { get; init; } = [];

    /// <summary>行驶轨迹折线（GCJ-02），点集；无数据时为空。</summary>
    public IReadOnlyList<(double Latitude, double Longitude)> Path { get; init; } = [];

    /// <summary>当前在线车辆数。</summary>
    public int OnlineBusCount { get; init; }

    /// <summary>运营时段文案，例如「07:20 - 22:40」。</summary>
    public string ServiceHours => FirstTime.Length > 0 && LastTime.Length > 0
        ? $"{FirstTime} - {LastTime}"
        : string.Empty;

    /// <summary>起讫点摘要，例如「信息学部四食堂 → 信息学部二食堂（回行）」。</summary>
    public string RouteSummary => StartStop.Length > 0 && EndStop.Length > 0
        ? $"{StartStop} → {EndStop}"
        : (StartStop.Length > 0 ? StartStop : EndStop);
}
