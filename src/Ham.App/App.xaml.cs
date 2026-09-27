using System.Windows;
using System.Windows.Threading;
using Ham.App.Services;
using Ham.App.ViewModels;
using Ham.App.Views;

namespace Ham.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    public AppService Service { get; private set; } = null!;

    public MainViewModel Shell { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstance())
        {
            // 已有实例在运行：把它的窗口唤到前台，而不是只弹一个提示框然后退出。
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        LogCritical("应用启动", null);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCritical("非 UI 线程未处理异常", args.ExceptionObject as Exception);

        // 启动初始化放到后台并显式 try/catch。
        // 不能写成 async void OnStartup：那样任何异常都会在 Show() 之前逃逸，
        // 结果是"进程活着但一个窗口都没有"，而且还占着单实例互斥量。
        var startup = StartAsync();

        if (startup.IsCompletedSuccessfully)
        {
            CompleteStartup(startup.Result);
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                CompleteStartup(await startup.ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                LogCritical("启动失败", ex);
                ShowFatalError(ex);
                Shutdown(1);
            }
        });
    }

    private async Task<(MainViewModel Shell, AppService Service, string? Warning)> StartAsync()
    {
        var service = new AppService();
        await service.InitializeAsync().ConfigureAwait(true);

        // --demo：启动即载入演示数据，便于验收与截图。
        // 必须在创建窗口之前完成，否则视图首次绑定会读到空数据且收不到后续变更通知。
        if (Environment.GetCommandLineArgs().Any(a =>
                a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
        {
            await service.LoadDemoAsync().ConfigureAwait(true);
        }

        var shell = new MainViewModel(service);
        service.CasLogin = new WebView2CasLoginProvider(
            () => MainWindow,
            // 把已保存的学号/密码交给登录窗口预填，避免每次都要手输
            () => (service.Settings.StudentId, service.Settings.PortalPassword));
        await shell.InitializeAsync().ConfigureAwait(true);

        // 天气在后台拉取，不阻塞首帧；失败也不影响启动。
        _ = Task.Run(async () =>
        {
            try
            {
                await service.RefreshWeatherAsync().ConfigureAwait(false);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    await dispatcher.InvokeAsync(() => shell.Status.RefreshWeatherFromService());
                }
            }
            catch (Exception ex)
            {
                LogCritical("天气获取失败", ex);
            }
        });

        return (shell, service, service.LoadWarning);
    }

    private void CompleteStartup((MainViewModel Shell, AppService Service, string? Warning) result)
    {
        Service = result.Service;
        Shell = result.Shell;

        // 通知必须在创建窗口前初始化（AUMID 注册 + 通道探测）。
        Notifications = new NotificationService();
        Notifications.Initialize();

        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;
        window.Show();

        Scheduler = new ReminderScheduler(Service, Notifications, () => Service.Calendar);
        Scheduler.Start();

        if (!string.IsNullOrEmpty(result.Warning))
        {
            Shell.ReportError(result.Warning);
        }
    }

    public NotificationService Notifications { get; private set; } = null!;

    public ReminderScheduler Scheduler { get; private set; } = null!;

    private bool AcquireSingleInstance()
    {
        _singleInstance = new Mutex(true, @"Local\HamForWindows-SingleInstance", out var isNew);
        return isNew;
    }

    /// <summary>把已在运行的实例窗口切到前台，避免用户以为"点了没反应"。</summary>
    private static void ActivateExistingInstance()
    {
        var existing = System.Diagnostics.Process.GetProcessesByName("Ham")
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

        if (existing is null)
        {
            MessageBox.Show("Ham 已在运行，但未能定位到它的窗口。请在任务栏中查看。",
                "Ham", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        NativeMethods.ShowWindow(existing.MainWindowHandle, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(existing.MainWindowHandle);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCritical("UI 线程未处理异常", e.Exception);

        // 主窗口已经存在时，弹窗提示并继续运行；此时吞掉异常是合理的。
        // 但如果连主窗口都没有（启动阶段失败），必须显式退出，
        // 否则会留下一个没有窗口却占着互斥量的僵尸进程。
        if (MainWindow is not null && MainWindow.IsVisible)
        {
            MessageBox.Show(
                "发生了未处理的错误：\n\n" + e.Exception.Message +
                "\n\n应用将继续运行。详情见 %LOCALAPPDATA%\\Ham\\logs\\ham.log",
                "Ham", MessageBoxButton.OK, MessageBoxImage.Warning);

            e.Handled = true;
            return;
        }

        e.Handled = true;
        ShowFatalError(e.Exception);
        Shutdown(1);
    }

    private static void ShowFatalError(Exception ex)
        => MessageBox.Show(
            "Ham 启动失败：\n\n" + ex.Message +
            "\n\n如果反复出现，可删除 %LOCALAPPDATA%\\Ham\\appdata.json 后重试" +
            "（会丢失本地已保存的数据）。\n\n日志：%LOCALAPPDATA%\\Ham\\logs\\ham.log",
            "Ham", MessageBoxButton.OK, MessageBoxImage.Error);

    internal static void LogCritical(string context, Exception? ex)
    {
        Infrastructure.Logging.Log.Error(context, ex);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Scheduler?.Dispose(); } catch (Exception ex) { LogCritical("停止调度器失败", ex); }

        try
        {
            Service?.PersistAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogCritical("退出时保存失败", ex);
        }

        try { Notifications?.Dispose(); } catch { /* 清理通知不影响退出 */ }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

internal static class NativeMethods
{
    public const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
