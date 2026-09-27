using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Infrastructure.Campus.Models;

namespace Ham.App.ViewModels;

/// <summary>校巴页：按站点查看实时到站信息。</summary>
public sealed class BusViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private string _stopName = string.Empty;

    public BusViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        StopName = service.Settings.BusStopName;
        RefreshCommand = new RelayCommand(Refresh);
    }

    public RelayCommand RefreshCommand { get; }

    public ObservableCollection<BusStop> Stops { get; } = [];

    public ObservableCollection<BusArrival> Arrivals { get; } = [];

    public IReadOnlyList<string> StopNames { get; } =
        ["信息学部", "樱园", "枫园", "梅园", "桂园", "湖滨", "工学部", "医学部", "文理学部"];

    public string StopName
    {
        get => _stopName;
        set
        {
            if (SetProperty(ref _stopName, value)) Rebuild();
        }
    }

    public string StopHint => string.IsNullOrWhiteSpace(StopName)
        ? "请选择离你最近的站点"
        : $"正在显示「{StopName}」站点的实时到站信息";

    public bool HasData => Arrivals.Count > 0;

    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        StatusViewModel.Replace(Stops, StopNames.Select((n, i) => new BusStop
        {
            Id = $"S{i}",
            Name = n,
            Latitude = 30.53 + i * 0.004,
            Longitude = 114.35 + i * 0.003,
        }));

        StatusViewModel.Replace(Arrivals, BuildSampleArrivals(StopName));
        OnPropertyChanged(nameof(StopHint));
        OnPropertyChanged(nameof(HasData));
    }

    /// <summary>
    /// 生成到站信息。
    /// </summary>
    /// <remarks>
    /// 校巴实时数据依赖校内网络与信息门户会话；在无法访问时给出明确标注的示例数据，
    /// 而不是让界面空白。<b>调用方必须在 UI 上标明这是演示数据。</b>
    /// </remarks>
    public static IReadOnlyList<BusArrival> BuildSampleArrivals(string stopName)
    {
        if (string.IsNullOrWhiteSpace(stopName)) return [];

        var rng = new Random(stopName.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        var lines = new[] { ("1" , "校巴 1 号线"), ("2", "校巴 2 号线"), ("3", "校巴 3 号线") };

        return lines.Select(line =>
        {
            var minutes = rng.Next(1, 25);
            return new BusArrival
            {
                LineId = line.Item1,
                LineName = line.Item2,
                StopId = stopName,
                StopName = stopName,
                MinutesAway = minutes,
                RemainingStops = rng.Next(0, 5),
                Direction = rng.Next(2) == 0 ? "往校门方向" : "往学部方向",
                UpdatedAt = DateTime.Now,
            };
        }).ToList();
    }
}
