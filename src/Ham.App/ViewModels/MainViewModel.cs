using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;
using Ham.Infrastructure.Campus.Models;

namespace Ham.App.ViewModels;

public enum NavSection
{
    Status,
    Course,
    Schedule,
    Score,
    Library,
    Sport,
    Bus,
    Rating,
    Settings,
}

/// <summary>侧边导航项。</summary>
public sealed record NavItem(NavSection Section, string Title, string Icon);

/// <summary>主视图模型：负责导航、状态聚合与全局命令。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppService _service;
    private NavSection _current = NavSection.Status;
    private string _statusMessage = "就绪";
    private bool _isBusy;
    private WeatherInfo? _weather;

    public MainViewModel(AppService service)
    {
        _service = service;

        Status = new StatusViewModel(service, this);
        Course = new CourseViewModel(service, this);
        Schedule = new ScheduleViewModel(service, this);
        Score = new ScoreViewModel(service, this);
        Library = new LibraryViewModel(service, this);
        Sport = new SportViewModel(service, this);
        Bus = new BusViewModel(service, this);
        Rating = new RatingViewModel(service, this);
        Settings = new SettingsViewModel(service, this);
    }

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new(NavSection.Status, "状态", "🏠"),
        new(NavSection.Course, "课程", "📚"),
        new(NavSection.Schedule, "日程", "📅"),
        new(NavSection.Score, "成绩", "📊"),
        new(NavSection.Library, "图书馆", "📚"),
        new(NavSection.Sport, "运动", "🏀"),
        new(NavSection.Bus, "校巴", "🚌"),
        new(NavSection.Rating, "给分", "⭐"),
        new(NavSection.Settings, "设置", "⚙"),
    ];

    public StatusViewModel Status { get; }
    public CourseViewModel Course { get; }
    public ScheduleViewModel Schedule { get; }
    public ScoreViewModel Score { get; }
    public LibraryViewModel Library { get; }
    public SportViewModel Sport { get; }
    public BusViewModel Bus { get; }
    public RatingViewModel Rating { get; }
    public SettingsViewModel Settings { get; }

    public NavSection Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentIcon));
            Activate(value);
        }
    }

    public string CurrentTitle => NavItems.First(n => n.Section == _current).Title;
    public string CurrentIcon => NavItems.First(n => n.Section == _current).Icon;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public WeatherInfo? Weather
    {
        get => _weather;
        set
        {
            if (SetProperty(ref _weather, value)) OnPropertyChanged(nameof(WeatherAdvice));
        }
    }

    public string WeatherAdvice => _weather?.GetAdvice() ?? string.Empty;

    public DataSource Source => _service.Source;

    public string SourceLabel => _service.Source switch
    {
        DataSource.Live => "已同步教务数据",
        DataSource.Demo => "演示数据",
        _ => "尚未加载数据",
    };

    public bool IsDemo => _service.Source == DataSource.Demo;

    /// <summary>演示模式角标文案，避免把演示数据误认为真实数据。</summary>
    public string DemoBanner => _service.Source == DataSource.Demo
        ? "当前显示的是本地演示数据。登录信息门户并同步后将自动替换为真实数据。"
        : string.Empty;

    public string SemesterLabel
    {
        get
        {
            var cal = _service.Calendar;
            var week = cal.CurrentWeek(DateTime.Now);
            var head = $"{_service.Settings.SemesterYear} 学年第 {_service.Settings.SemesterNumber} 学期";
            return week is null
                ? $"{head} · 当前不在教学周内"
                : $"{head} · 第 {week} 教学周";
        }
    }

    private void Activate(NavSection section)
    {
        switch (section)
        {
            case NavSection.Status: Status.Refresh(); break;
            case NavSection.Course: Course.Refresh(); break;
            case NavSection.Schedule: Schedule.Refresh(); break;
            case NavSection.Score: Score.Refresh(); break;
            case NavSection.Library: Library.Refresh(); break;
            case NavSection.Sport: Sport.Refresh(); break;
            case NavSection.Bus: Bus.Refresh(); break;
            case NavSection.Rating: Rating.Refresh(); break;
            case NavSection.Settings: Settings.Refresh(); break;
        }
    }

    public async Task InitializeAsync()
    {
        OnPropertyChanged(nameof(SemesterLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBanner));

        // 所有分区都要在首帧之前完成一次刷新：视图首次绑定时读取到的必须是最终状态，
        // 否则后续的数据加载不会触发属性变更，界面会停留在空数据上。
        Status.Refresh();
        Course.Refresh();
        Schedule.Refresh();
        Score.Refresh();
        Library.Refresh();
        Sport.Refresh();
        Bus.Refresh();
        Rating.Refresh();
        Settings.Refresh();

        if (_service.Source == DataSource.None)
        {
            StatusMessage = "欢迎使用 Ham。可在「设置 → 数据 → 载入演示数据」先体验全部功能。";
        }
        else
        {
            StatusMessage = $"已加载 {_service.Courses.Count} 门课程、{_service.Scores.Count} 条成绩。";
        }

        await Task.CompletedTask;
    }

    /// <summary>供子视图模型在数据变化后刷新派生信息。</summary>
    public void NotifyDataChanged()
    {
        OnPropertyChanged(nameof(SemesterLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(DemoBanner));
    }

    public void Report(string message) => StatusMessage = message;

    public void ReportError(string message)
    {
        StatusMessage = message;
        _service.LastError = message;
    }

    /// <summary>统一的持久化入口：先落盘再刷新派生状态。</summary>
    public async Task CommitAsync(string? message = null)
    {
        await _service.PersistAsync().ConfigureAwait(true);
        NotifyDataChanged();
        if (message is not null) StatusMessage = message;
    }
}
