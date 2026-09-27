using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Campus.Models;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Sport.Models;

namespace Ham.App.ViewModels;

/// <summary>状态页：天气、今日课程、最近日程、预约与校巴摘要。</summary>
public sealed class StatusViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    public StatusViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        RefreshWeatherCommand = new AsyncRelayCommand(() => RefreshWeatherAsync(force: true));
    }

    public AsyncRelayCommand RefreshWeatherCommand { get; }

    public ObservableCollection<Course> TodayCourses { get; } = [];

    public ObservableCollection<Course> TomorrowCourses { get; } = [];

    public ObservableCollection<ScheduleOccurrence> UpcomingSchedules { get; } = [];

    /// <summary>当前有效的图书馆预约（只读派生）。</summary>
    public LibraryBooking? ActiveLibraryBooking
        => _service.LibraryBookings.FirstOrDefault(b => b.IsActive);

    /// <summary>预约状态的一行摘要；无预约时给出明确文案，而不是留空。</summary>
    public string ActiveBookingText => ActiveLibraryBooking?.Describe() ?? "当前没有有效预约";

    public SportBooking? PendingSportBooking
        => _service.SportBookings.FirstOrDefault(b => b.Status == SportOrderStatus.PendingPayment);

    /// <summary>待支付订单摘要；无订单时给出明确文案。</summary>
    public string PendingSportText => PendingSportBooking?.Describe() ?? "暂无待支付订单";

    public BusArrival? NearestBus { get; set; }

    public string NearestBusText => NearestBus?.Describe() ?? "请在「校巴」页选择站点";

    // ── 天气 ────────────────────────────────────────────────────────────────

    private WeatherReport? _weather;
    private string? _weatherError;

    public WeatherReport? Weather
    {
        get => _weather;
        private set
        {
            _weather = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WeatherAdvice));
            OnPropertyChanged(nameof(WeatherSummary));
            OnPropertyChanged(nameof(HasWeather));
        }
    }

    public string? WeatherError
    {
        get => _weatherError;
        private set
        {
            _weatherError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWeatherError));
        }
    }

    public bool HasWeather => Weather is not null;

    public bool HasWeatherError => !string.IsNullOrWhiteSpace(WeatherError);

    public string WeatherAdvice => Weather?.GetAdvice() ?? string.Empty;

    public string WeatherSummary => Weather is null
        ? string.Empty
        : $"{Weather.City} · {Weather.Condition} · {Weather.TemperatureText} · 体感 {Weather.FeelsLikeCelsius:F0}°";

    public ObservableCollection<WeatherForecast> Forecast { get; } = [];

    public string WeatherUpdatedText
    {
        get
        {
            var at = _service.WeatherUpdatedAt;
            if (at is null) return string.Empty;
            var mins = (int)(DateTimeOffset.Now - at.Value).TotalMinutes;
            return mins <= 1 ? "刚刚更新" : $"{mins} 分钟前更新";
        }
    }

    /// <summary>从服务层同步天气到界面（供后台拉取完成后回调）。</summary>
    public void RefreshWeatherFromService()
    {
        Weather = _service.Weather;
        WeatherError = _service.WeatherError;
        Replace(Forecast, Weather?.Forecast ?? []);
        OnPropertyChanged(nameof(WeatherUpdatedText));
    }

    /// <summary>拉取天气。失败时保留上次结果，仅提示错误。</summary>
    public async Task RefreshWeatherAsync(bool force = false)
    {
        var ok = await _service.RefreshWeatherAsync(force).ConfigureAwait(true);
        Weather = _service.Weather;
        WeatherError = _service.WeatherError;

        Replace(Forecast, Weather?.Forecast ?? []);
        OnPropertyChanged(nameof(WeatherUpdatedText));

        if (!ok && Weather is null)
        {
            _main.ReportError("天气获取失败：" + WeatherError);
        }
    }

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            return hour switch
            {
                < 6 => "夜深了",
                < 11 => "早上好",
                < 14 => "中午好",
                < 18 => "下午好",
                _ => "晚上好",
            };
        }
    }

    public string UserDisplay
    {
        get
        {
            var nickname = _service.Settings.Nickname;
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
            var id = _service.Settings.StudentId;
            return string.IsNullOrWhiteSpace(id) ? "未登录" : MaskStudentId(id);
        }
    }

    /// <summary>学号脱敏显示，避免在状态页直接暴露完整学号。</summary>
    public static string MaskStudentId(string id)
        => id.Length <= 4 ? id : id[..4] + new string('*', id.Length - 4);

    public string TodayLabel => DateTime.Now.ToString("M 月 d 日 dddd");

    public void Refresh()
    {
        Replace(TodayCourses, _service.GetTodayCourses());
        Replace(TomorrowCourses, _service.GetTomorrowCourses());

        var timeline = _service.BuildTimeline();
        Replace(UpcomingSchedules,
            timeline.Occurrences(DateTime.Now, DateTime.Now.AddDays(7))
                .Where(o => o.End is null || o.End > DateTime.Now)
                .Take(6));

        NearestBus = BusViewModel.BuildSampleArrivals(_service.Settings.BusStopName).FirstOrDefault();

        Weather = _service.Weather;
        WeatherError = _service.WeatherError;
        Replace(Forecast, Weather?.Forecast ?? []);
        OnPropertyChanged(nameof(WeatherUpdatedText));

        // 派生文本属性必须逐个通知：它们与 NearestBus 等对象属性是独立的绑定路径，
        // 只通知对象属性不会让 Text="{Binding XxxText}" 重新求值。
        OnPropertyChanged(nameof(ActiveLibraryBooking));
        OnPropertyChanged(nameof(ActiveBookingText));
        OnPropertyChanged(nameof(PendingSportBooking));
        OnPropertyChanged(nameof(PendingSportText));
        OnPropertyChanged(nameof(NearestBus));
        OnPropertyChanged(nameof(NearestBusText));
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(UserDisplay));
        OnPropertyChanged(nameof(TodayLabel));
    }

    internal static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
