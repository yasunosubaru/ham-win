using Ham.Ui.Pages;
using Ham.Ui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ham.Ui;

/// <summary>主窗口的交互逻辑。</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppState _state = new();
    private bool _ready;

    public MainWindow()
    {
        Title = "武汉大学";
        InitializeComponent();

        // 先把骨架渲染出来，再异步读盘——
        // DataStore 是异步 IO，阻塞在构造函数里会让窗口白屏。
        Nav.SelectedIndex = 0;
        Status.Text = "正在载入本地数据…";
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _state.LoadAsync();

        Root.RequestedTheme = _state.Dark ? ElementTheme.Dark : ElementTheme.Light;
        UpdateTerm();
        _ready = true;
        Show(_state.SectionIndex);

        Status.Text = _state.SyncSummary
            + (_state.LoadError is null ? "" : "　⚠ " + _state.LoadError);

        _ = RefreshWeatherAsync();
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;          // 初始化期间 SelectedIndex=0 会触发一次，此时还没数据
        var i = Nav.SelectedIndex;
        if (i < 0) return;
        _state.SectionIndex = i;
        Show(i);
    }

    private void Show(int index)
    {
        var key = index >= 0 && index < Sections.All.Count ? Sections.All[index].Key : "status";

        Page page = key switch
        {
            "course" => new CoursePage(_state),
            "schedule" => new SchedulePage(_state),
            "score" => new ScorePage(_state),
            "library" => new LibraryPage(_state),
            "sport" => new SportPage(_state),
            "bus" => new BusPage(_state),
            "rating" => new RatingPage(_state),
            "settings" => new SettingsPage(_state),
            _ => new StatusPage(_state, RefreshWeatherAsync),
        };

        Content.Content = page;
    }

    private async Task RefreshWeatherAsync()
    {
        Status.Text = "正在获取天气…";
        try
        {
            var report = await new Ham.Infrastructure.Campus.WeatherClient().GetAsync();
            _state.Weather = report;
            Status.Text = $"{report.Condition} {report.TemperatureText} · {report.City}"
                          + "　|　" + _state.SyncSummary;
            if (Content.Content is StatusPage sp) sp.Apply(report);
        }
        catch (HttpRequestException)
        {
            // 状态栏只有一行位置，直接把 .NET 的英文异常原文（常常还带
            // "see inner exception"）糊上去既难看又没信息量。
            Fail("天气获取失败：网络不可达", "取不到实时天气，通常是网络问题。点「刷新」可重试。");
        }
        catch (TaskCanceledException)
        {
            Fail("天气获取超时", "请求超时，通常是网络问题。点「刷新」可重试。");
        }
        catch (Exception ex)
        {
            Fail("天气获取失败", ex.Message);
        }
    }

    /// <summary>天气失败的统一处理：状态栏给一行摘要，页面里给可读的说明。</summary>
    private void Fail(string status, string? pageHint)
    {
        Status.Text = status + (_state.Weather is null ? "" : "（已沿用上次结果）");
        if (Content.Content is StatusPage sp) sp.ShowWeatherUnavailable(pageHint);
    }

    private void UpdateTerm()
    {
        var s = _state.Settings;
        TermText.Text = $"{s.SemesterYear} 学年第 {s.SemesterNumber} 学期";
    }

    private async void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        _state.Dark = !_state.Dark;
        Root.RequestedTheme = _state.Dark ? ElementTheme.Dark : ElementTheme.Light;
        Title = _state.Dark ? "武汉大学（深色）" : "武汉大学";
        try { await _state.SaveSettingsAsync(); } catch { /* 主题写盘失败不影响使用 */ }
    }
}
