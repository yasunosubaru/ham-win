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
        ApplyMica();
        ApplyIcon();

        // 先把骨架渲染出来，再异步读盘——
        // DataStore 是异步 IO，阻塞在构造函数里会让窗口白屏。
        //
        // 这里**不要**先 Select 导航项：初始化完成后再选同一项不会触发
        // SelectionChanged，OnNavChanged 里的 Show() 就永远不会被调用，
        // 结果是内容区一片空白。选中必须只发生一次，且发生在 _ready 之后。
        Status.Text = "正在载入本地数据…";
        _ = InitializeAsync();
    }

    /// <summary>
    /// 按 Tag 选中导航项。
    /// </summary>
    /// <remarks>
    /// 用 Tag 而不是下标：导航项增删时不必改任何调用点，
    /// 而且 <see cref="Show"/> 也改成按 Tag 分派，两者天然对齐。
    /// </remarks>
    private void Select(string key)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == key) { Nav.SelectedItem = item; return; }
        }
    }

    /// <summary>
    /// 窗口材质：Mica（云母）。
    /// </summary>
    /// <remarks>
    /// Mica 是 Windows 11 的系统材质，会把桌面壁纸的模糊与色调采样进来，
    /// 并随浅色/深色自动切换。相比纯色卡片，它给出的层次感是"免费"的，
    /// 因为内容是系统合成的，不额外吃 GPU。
    /// <para>
    /// 用 try/catch 是必要的：Mica 只在 Windows 11 上有效果，
    /// Windows 10 上设置 SystemBackdrop 可能抛异常，不能因此让应用起不来。
    /// 退路是 Acrylic（Win10 上可用），再退就是什么都不设。
    /// </para>
    /// <para>
    /// 材质生效的前提是根容器不要刷不透明背景色——所以 Root 保持透明，
    /// 卡片用自己的半透明填充色叠在材质上。
    /// </para>
    /// </remarks>
    private void ApplyMica()
    {
        // 根容器必须透明，否则 Mica 会被自己的背景盖住，看不到任何效果。
        Root.Background = null;

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
            catch { /* Windows 10 且 Acrylic 也不可用：保持纯色，功能不受影响 */ }
        }
    }

    /// <summary>
    /// 设置窗口与任务栏图标。
    /// </summary>
    /// <remarks>
    /// unpackaged 应用没有包身份，csproj 里的 <c>ApplicationIcon</c> 只会改
    /// <b>exe 资源</b>；窗口标题栏和任务栏仍然用系统默认图标，
    /// 必须显式走 <c>AppWindow.SetIcon</c>。这条在 packaged 应用里不需要。
    /// </remarks>
    private void ApplyIcon()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ham-win.ico");
            if (System.IO.File.Exists(icon)) appWindow.SetIcon(icon);
        }
        catch (Exception ex)
        {
            // 图标设不上不影响功能，别为此让应用起不来。
            Ham.Infrastructure.Logging.Log.Error("设置窗口图标失败", ex);
        }
    }

    private async Task InitializeAsync()
    {
        await _state.LoadAsync();

        Root.RequestedTheme = _state.Dark ? ElementTheme.Dark : ElementTheme.Light;
        UpdateTerm();
        _ready = true;

        // 选中会触发 OnNavChanged → Show()。这是页面第一次被创建的唯一入口，
        // 所以必须放在 _ready = true 之后，且只能调用这一次。
        Select(Sections.All[Math.Clamp(_state.SectionIndex, 0, Sections.All.Count - 1)].Key);

        Status.Text = _state.SyncSummary
            + (_state.LoadError is null ? "" : "　⚠ " + _state.LoadError);

        _ = RefreshWeatherAsync();
    }

    // 注意参数类型：NavigationView 的 SelectionChanged 走的是
    // NavigationViewSelectionChangedEventArgs，不是 ListView 那个 SelectionChangedEventArgs。
    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        // 初始化期间也会触发一次，那时还没数据，直接忽略。
        if (!_ready) return;
        if (Nav.SelectedItem is not NavigationViewItem { Tag: string key }) return;

        _state.SectionIndex = Sections.All.ToList().FindIndex(s => s.Key == key);
        Show(key);
    }

    private void Show(string key)
    {
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
