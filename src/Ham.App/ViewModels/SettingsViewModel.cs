using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;
using Ham.Infrastructure.Diagnostics;
using Ham.Infrastructure.Storage;

namespace Ham.App.ViewModels;

/// <summary>设置页：信息门户、课表参数、提醒、外观、数据管理。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private string _studentId = string.Empty;
    private string _portalPassword = string.Empty;
    private string _semesterStartDate = string.Empty;
    private int _semesterYear;
    private int _semesterNumber = 1;
    private int _totalWeeks = 20;
    private string _theme = "Light";

    public SettingsViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        SyncCommand = new AsyncRelayCommand(SaveAsync);
        SyncEducationCommand = new AsyncRelayCommand(SyncEducationAsync);
        LoadDemoCommand = new AsyncRelayCommand(LoadDemoAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync);
        TestNotificationCommand = new RelayCommand(TestNotification);
        ClearHistoryCommand = new RelayCommand(() =>
        {
            (System.Windows.Application.Current as App)?.Notifications?.ClearHistory();
            OnPropertyChanged(nameof(HasNotificationHistory));
            OnPropertyChanged(nameof(NotificationHistory));
        });
    }

    public AsyncRelayCommand RunDiagnosticsCommand { get; }

    public RelayCommand TestNotificationCommand { get; }

    /// <summary>连接诊断结果。</summary>
    public ObservableCollection<DiagnosticStep> DiagnosticSteps { get; } = [];

    public string DiagnosticSummary { get; private set; } = "尚未运行诊断";

    public bool HasDiagnostics => DiagnosticSteps.Count > 0;

    /// <summary>当前生效的通知通道。</summary>
    public string NotificationChannel
    {
        get
        {
            var app = System.Windows.Application.Current as App;
            return app?.Notifications?.ChannelLabel ?? "未初始化";
        }
    }

    /// <summary>
    /// 最近提醒历史。
    /// </summary>
    /// <remarks>
    /// Windows 的 Toast 在鼠标悬停时会被自动关闭，这是系统行为，应用无法阻止。
    /// 把提醒同时记入这里，保证"系统通知被关掉"不等于"提醒丢失"。
    /// </remarks>
    public ObservableCollection<HistoryEntry> NotificationHistory
        => (System.Windows.Application.Current as App)?.Notifications?.History ?? [];

    public bool HasNotificationHistory => NotificationHistory.Count > 0;

    public RelayCommand ClearHistoryCommand { get; }

    /// <summary>发送一条测试通知，确认通道真的可用。</summary>
    public void TestNotification()
    {
        var notifications = (System.Windows.Application.Current as App)?.Notifications;
        if (notifications is null)
        {
            _main.ReportError("通知服务尚未初始化。");
            return;
        }

        notifications.Notify(new ReminderItem(
            ReminderKind.Schedule,
            DateTime.Now,
            "Ham 通知测试",
            "系统通知会在几秒后自动消失（Windows 行为），但这条记录会保留在下方历史里。",
            Tag: "ham-test-" + DateTime.Now.ToString("HHmmss")));

        OnPropertyChanged(nameof(NotificationChannel));
        OnPropertyChanged(nameof(NotificationError));
        OnPropertyChanged(nameof(HasNotificationHistory));
        OnPropertyChanged(nameof(NotificationHistory));

        _main.Report(notifications.IsAvailable
            ? $"已通过「{notifications.ChannelLabel}」发送测试通知，并记入下方历史。"
            : "通知通道不可用：" + notifications.LastError);
    }

    public string NotificationError
    {
        get
        {
            var app = System.Windows.Application.Current as App;
            var err = app?.Notifications?.LastError;
            return string.IsNullOrEmpty(err) ? string.Empty : err;
        }
    }

    /// <summary>执行连接诊断（无需凭据即可完成前几步）。</summary>
    public async Task RunDiagnosticsAsync()
    {
        DiagnosticSteps.Clear();
        DiagnosticSummary = "正在诊断…";
        OnPropertyChanged(nameof(HasDiagnostics));
        _main.Report("正在运行连接诊断…");

        try
        {
            var diagnostics = new CasDiagnostics();
            var report = await diagnostics.RunNetworkStepsAsync().ConfigureAwait(true);

            StatusViewModel.Replace(DiagnosticSteps, report.Steps);
            DiagnosticSteps.Add(CasDiagnostics.ValidateCredentials(StudentId, PortalPassword));

            DiagnosticSummary = report.Summary;
            OnPropertyChanged(nameof(DiagnosticSummary));
            OnPropertyChanged(nameof(HasDiagnostics));

            _main.Report(report.AllPassed
                ? $"连接诊断通过：{report.Summary}，耗时 {report.Elapsed.TotalSeconds:F1}s。"
                : $"连接诊断发现问题：{report.Summary}。请查看逐项说明。");
        }
        catch (Exception ex)
        {
            _main.ReportError("诊断执行失败：" + ex.Message);
            DiagnosticSummary = "诊断执行失败";
            OnPropertyChanged(nameof(DiagnosticSummary));
        }
    }



    public AsyncRelayCommand SyncCommand { get; }
    public AsyncRelayCommand SyncEducationCommand { get; }

    /// <summary>同步进行中，用于拦截连点。</summary>
    private bool _isSyncing;
    public AsyncRelayCommand LoadDemoCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public IReadOnlyList<int> Years { get; } =
        Enumerable.Range(DateTime.Now.Year - 3, 7).ToList();

    public IReadOnlyList<int> Semesters { get; } = [1, 2, 3];

    public IReadOnlyList<int> WeekOptions { get; } = [16, 17, 18, 19, 20, 21, 22];

    public IReadOnlyList<string> Themes { get; } = ["Light", "Dark"];

    public string StudentId
    {
        get => _studentId;
        set => SetProperty(ref _studentId, value);
    }

    public string PortalPassword
    {
        get => _portalPassword;
        set => SetProperty(ref _portalPassword, value);
    }

    public string SemesterStartDate
    {
        get => _semesterStartDate;
        set
        {
            if (SetProperty(ref _semesterStartDate, value)) OnPropertyChanged(nameof(SemesterStartHint));
        }
    }

    public string SemesterStartHint
        => DateOnly.TryParse(SemesterStartDate, out var d)
            ? $"第 1 周起始：{d:yyyy 年 MM 月 dd 日}（{ConflictDetector.DayOfWeekName((int)d.DayOfWeek)}）"
            : "日期格式无效";

    public int SemesterYear
    {
        get => _semesterYear;
        set => SetProperty(ref _semesterYear, value);
    }

    public int SemesterNumber
    {
        get => _semesterNumber;
        set => SetProperty(ref _semesterNumber, value);
    }

    public int TotalWeeks
    {
        get => _totalWeeks;
        set => SetProperty(ref _totalWeeks, value);
    }

    public bool EnableNotifications
    {
        get => _service.Settings.EnableNotifications;
        set
        {
            if (_service.Settings.EnableNotifications == value) return;
            _service.Settings.EnableNotifications = value;
            OnPropertyChanged();
        }
    }

    public bool EnableCourseReminder
    {
        get => _service.Settings.EnableCourseReminder;
        set
        {
            if (_service.Settings.EnableCourseReminder == value) return;
            _service.Settings.EnableCourseReminder = value;
            OnPropertyChanged();
        }
    }

    public bool EnableScheduleReminder
    {
        get => _service.Settings.EnableScheduleReminder;
        set
        {
            if (_service.Settings.EnableScheduleReminder == value) return;
            _service.Settings.EnableScheduleReminder = value;
            OnPropertyChanged();
        }
    }

    public int CourseReminderMinutes
    {
        get => _service.Settings.CourseReminderMinutes;
        set
        {
            if (_service.Settings.CourseReminderMinutes == value) return;
            _service.Settings.CourseReminderMinutes = value;
            OnPropertyChanged();
        }
    }

    public int LibraryAutoRefreshMinutes
    {
        get => _service.Settings.LibraryAutoRefreshMinutes;
        set
        {
            if (_service.Settings.LibraryAutoRefreshMinutes == value) return;
            _service.Settings.LibraryAutoRefreshMinutes = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<int> ReminderMinuteOptions { get; } = [0, 5, 10, 15, 30, 60];

    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            _service.Settings.Theme = value;
            ApplyTheme();
        }
    }

    public string DataFilePath => _service.DataPath;

    public string SourceLabel => _main.SourceLabel;

    public bool IsDemo => _main.IsDemo;

    public string SyncHint => _service.CanAccessEducation
        ? $"将以学号 {StatusViewModel.MaskStudentId(_studentId)} 登录信息门户并同步课表与成绩。"
        : "填写学号与信息门户密码后即可同步课表与成绩。";

    public void Refresh()
    {
        _studentId = _service.Settings.StudentId;
        _portalPassword = _service.Settings.PortalPassword;
        _semesterStartDate = _service.Settings.SemesterStartDate;
        _semesterYear = _service.Settings.SemesterYear;
        _semesterNumber = _service.Settings.SemesterNumber;
        _totalWeeks = _service.Settings.TotalWeeks;
        _theme = _service.Settings.Theme;

        OnPropertyChanged(nameof(StudentId));
        OnPropertyChanged(nameof(PortalPassword));
        OnPropertyChanged(nameof(SemesterStartDate));
        OnPropertyChanged(nameof(SemesterStartHint));
        OnPropertyChanged(nameof(SemesterYear));
        OnPropertyChanged(nameof(SemesterNumber));
        OnPropertyChanged(nameof(TotalWeeks));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(EnableNotifications));
        OnPropertyChanged(nameof(EnableCourseReminder));
        OnPropertyChanged(nameof(EnableScheduleReminder));
        OnPropertyChanged(nameof(CourseReminderMinutes));
        OnPropertyChanged(nameof(LibraryAutoRefreshMinutes));
        OnPropertyChanged(nameof(DataFilePath));
        OnPropertyChanged(nameof(SyncHint));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(NotificationChannel));
        OnPropertyChanged(nameof(NotificationError));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(DiagnosticSummary));
    }

    private async Task SaveAsync()
    {
        _service.Settings.StudentId = StudentId.Trim();
        _service.Settings.PortalPassword = PortalPassword;
        _service.Settings.SemesterStartDate = SemesterStartDate;
        _service.Settings.SemesterYear = SemesterYear;
        _service.Settings.SemesterNumber = SemesterNumber;
        _service.Settings.TotalWeeks = TotalWeeks;

        await _main.CommitAsync("设置已保存。");
        _main.Course.Refresh();
    }

    private async Task SyncEducationAsync()
    {
        // 同步失败时用户很容易连点。这里必须挡住：
        // 一是教务侧对高频请求敏感（实测存在安全设备），二是并发同步会互相覆盖
        // Cookie 罐与 IsBusy 状态，日志里会出现多条交错的结果、难以判断。
        if (_isSyncing) return;
        _isSyncing = true;

        _main.IsBusy = true;
        try
        {
            var (ok, message) = await _service.SyncEducationAsync(StudentId.Trim(), PortalPassword);
            if (ok) _main.Report(message);
            else _main.ReportError(message);

            if (ok)
            {
                _service.Settings.StudentId = StudentId.Trim();
                _service.Settings.PortalPassword = PortalPassword;
                _main.Course.Refresh();
                _main.Score.Refresh();
            }
        }
        finally
        {
            _isSyncing = false;
            _main.IsBusy = false;
            OnPropertyChanged(nameof(SyncHint));
        }
    }

    private async Task LoadDemoAsync()
    {
        await _service.LoadDemoAsync().ConfigureAwait(true);
        _main.NotifyDataChanged();
        _main.Course.Refresh();
        _main.Score.Refresh();
        _main.Schedule.Refresh();
        _main.Library.Refresh();
        _main.Rating.Refresh();
        _main.Report("已载入演示数据。");
    }

    private async Task ResetAsync()
    {
        await _service.ResetAsync().ConfigureAwait(true);
        _main.NotifyDataChanged();
        _main.Course.Refresh();
        _main.Score.Refresh();
        _main.Schedule.Refresh();
        _main.Library.Refresh();
        _main.Rating.Refresh();
        Refresh();
        _main.Report("已清空本地数据。");
    }

    private void ApplyTheme()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var target = new Uri($"Themes/{Theme}.xaml", UriKind.Relative);
        foreach (var dict in app.Resources.MergedDictionaries.ToList())
        {
            if (dict.Source?.OriginalString?.Contains("Themes/", StringComparison.Ordinal) == true)
            {
                app.Resources.MergedDictionaries.Remove(dict);
            }
        }

        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = target });
    }

    private void OpenDataFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(DataStore.DefaultPath())!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _main.ReportError("无法打开数据目录：" + ex.Message);
        }
    }
}
