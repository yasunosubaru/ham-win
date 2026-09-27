using System.Runtime.InteropServices;
using System.Windows.Forms;
using Ham.Core.Models;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Ham.App.Services;

/// <summary>提醒类型。</summary>
public enum ReminderKind
{
    Course,
    Schedule,
    Library,
    Sport,
}

/// <summary>一条待提醒事项。</summary>
public sealed record ReminderItem(
    ReminderKind Kind,
    DateTime At,
    string Title,
    string Body,
    string? Tag = null);

/// <summary>通知实际使用的通道。</summary>
public enum NotifyChannel
{
    /// <summary>尚未初始化。</summary>
    None,

    /// <summary>Windows 原生 Toast（通知中心可见）。</summary>
    Toast,

    /// <summary>托盘气泡提示（Toast 不可用时的兜底）。</summary>
    Balloon,
}

/// <summary>
/// 通知服务。
/// </summary>
/// <remarks>
/// <b>为什么必须用 WinRT 而不是 NuGet 包</b>：
/// <c>CommunityToolkit.WinUI.Notifications</c> 与 <c>Microsoft.Toolkit.Uwp.Notifications</c>
/// 的最新稳定版（均为 7.1.x）都<b>没有</b> <c>ToastNotificationManager</c>，
/// 只保留了旧的 <c>ToastContentBuilder</c>。因此这里直接用
/// <c>Windows.UI.Notifications</c> 的 WinRT 投影（需 TFM 带 Windows SDK 版本号）。
///
/// <b>为什么必须先注册 AUMID</b>：桌面（未打包）应用若不调用
/// <c>SetCurrentProcessExplicitAppUserModelID</c>，Toast 无法归属到本应用，
/// 要么抛 0x80070490，要么被记到宿主进程名下。该调用必须在创建任何窗口之前完成。
///
/// <b>为什么还有兜底</b>：企业策略/系统设置可能禁用通知，此时
/// <c>CreateToastNotifier</c> 会失败。退化到托盘气泡，保证"提醒"这个功能本身不消失。
/// </remarks>
public sealed class NotificationService : IDisposable
{
    public const string AppUserModelId = "whu.ham.windows";

    private readonly string _iconPath;
    private NotifyIcon? _tray;
    private ToastNotifierHandle? _toast;
    private bool _appIdRegistered;
    private bool _disposed;

    public NotificationService()
    {
        var exe = Environment.ProcessPath;
        _iconPath = !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : string.Empty;
    }

    /// <summary>实际生效的通知通道。</summary>
    public NotifyChannel Channel { get; private set; } = NotifyChannel.None;

    /// <summary>最近一次通知失败的原因；正常时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>通道是否可用（至少有一种能发出去）。</summary>
    public bool IsAvailable => Channel != NotifyChannel.None;

    public string ChannelLabel => Channel switch
    {
        NotifyChannel.Toast => "Windows 通知中心",
        NotifyChannel.Balloon => "托盘气泡提示",
        _ => "不可用",
    };

    /// <summary>
    /// 必须在创建任何窗口之前调用：注册显式 AppUserModelID 并初始化通知通道。
    /// </summary>
    public void Initialize()
    {
        if (_appIdRegistered) return;
        _appIdRegistered = true;

        TryRegisterAppUserModelId();
        TryInitializeToast();
        if (_toast is null) TryInitializeTray();
    }

    private void TryRegisterAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            LastError = "注册 AppUserModelID 失败：" + ex.Message;
        }
    }

    private void TryInitializeToast()
    {
        try
        {
            _toast = new ToastNotifierHandle(
                ToastNotificationManager.CreateToastNotifier(AppUserModelId));
            Channel = NotifyChannel.Toast;
        }
        catch (Exception ex)
        {
            _toast = null;
            LastError = $"Toast 不可用（0x{ex.HResult:X8}），将退回托盘气泡。";
        }
    }

    private void TryInitializeTray()
    {
        try
        {
            _tray = new NotifyIcon
            {
                Text = "Ham",
                Visible = true,
            };

            if (!string.IsNullOrEmpty(_iconPath))
            {
                try
                {
                    _tray.Icon = new System.Drawing.Icon(_iconPath);
                }
                catch
                {
                    // 某些情况下无法从 exe 提取图标，退回默认图标即可。
                }
            }

            Channel = NotifyChannel.Balloon;
        }
        catch (Exception ex)
        {
            _tray = null;
            Channel = NotifyChannel.None;
            LastError = "托盘气泡也不可用：" + ex.Message;
        }
    }

    /// <summary>弹出一条通知。两条通道都不可用时静默失败，绝不抛异常影响主流程。</summary>
    public void Notify(ReminderItem item)
    {
        if (_disposed) return;

        // 无论系统通知是否被显示，都先记入历史。
        // Windows Toast 在鼠标悬停时会被自动关闭（系统行为，应用代码无法阻止），
        // 课程/日程提醒若只依赖系统通知，用户很容易错过。
        AddToHistory(item);

        if (_toast is not null)
        {
            try
            {
                _toast.Show(BuildXml(item), item.Tag);
                LastError = null;
                return;
            }
            catch (Exception ex)
            {
                LastError = $"Toast 发送失败（0x{ex.HResult:X8}），已退回托盘气泡。";
                _toast = null;
                if (_tray is null) TryInitializeTray();
            }
        }

        if (_tray is null) return;

        try
        {
            _tray.BalloonTipTitle = item.Title;
            _tray.BalloonTipText = item.Body;
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(10);
            Channel = NotifyChannel.Balloon;
        }
        catch (Exception ex)
        {
            LastError = "气泡提示失败：" + ex.Message;
        }
    }

    /// <summary>最近提醒历史（最新在前），最多保留 <see cref="HistoryCapacity"/> 条。</summary>
    public System.Collections.ObjectModel.ObservableCollection<HistoryEntry> History { get; } = [];

    public const int HistoryCapacity = 50;

    public bool HasHistory => History.Count > 0;

    private void AddToHistory(ReminderItem item)
    {
        var entry = new HistoryEntry(DateTime.Now, item.Title, item.Body, item.Kind);

        void Insert()
        {
            History.Insert(0, entry);
            while (History.Count > HistoryCapacity) History.RemoveAt(History.Count - 1);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Insert);
        else Insert();
    }

    public void ClearHistory()
    {
        void Clear() => History.Clear();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Clear);
        else Clear();
    }

    /// <summary>构造 ToastGeneric 类型的通知 XML。</summary>
    private static string BuildXml(ReminderItem item)
    {
        static string Esc(string s) => new System.Xml.Linq.XText(s).ToString();

        // duration="long" 把显示时间从约 7 秒延长到约 25 秒。
        // 但 Windows 在鼠标悬停时仍会关闭通知，这是系统行为，应用无法阻止，
        // 因此必须有 History 兜底。
        return
            "<toast launch=\"action\" scenario=\"reminder\" duration=\"long\">" +
              "<visual>" +
                "<binding template=\"ToastGeneric\">" +
                  "<text>" + Esc(item.Title) + "</text>" +
                  "<text>" + Esc(item.Body) + "</text>" +
                "</binding>" +
              "</visual>" +
              "<audio src=\"Notification.Default\"/>" +
            "</toast>";
    }

    /// <summary>课程上课提醒。</summary>
    public void NotifyCourse(Course course, DateTime classStart)
    {
        var (start, _) = SemesterCalendar.ClassPeriodTimes(
            DateOnly.FromDateTime(classStart), course.ClassFrom, course.ClassTo);

        Notify(new ReminderItem(
            ReminderKind.Course,
            classStart,
            $"即将上课：{course.Name}",
            $"{start:HH\\:mm} 开始"
            + (string.IsNullOrWhiteSpace(course.Location) ? string.Empty : $" · {course.Location}")
            + (string.IsNullOrWhiteSpace(course.Instructor) ? string.Empty : $" · {course.Instructor}"),
            Tag: $"course-{course.CourseId}-{classStart:yyyyMMddHHmm}"));
    }

    /// <summary>日程提醒。</summary>
    public void NotifySchedule(ScheduleItem item, DateTime start)
        => Notify(new ReminderItem(
            ReminderKind.Schedule,
            start,
            item.Title,
            $"{start:HH\\:mm} 开始"
            + (string.IsNullOrWhiteSpace(item.Location) ? string.Empty : $" · {item.Location}"),
            Tag: $"schedule-{item.Id}-{start:yyyyMMddHHmm}"));

    /// <summary>图书馆入馆提醒。</summary>
    public void NotifyLibrary(string seat, DateTime start, DateTime end)
        => Notify(new ReminderItem(
            ReminderKind.Library,
            start,
            "图书馆预约即将开始",
            $"{seat} · {start:HH\\:mm}–{end:HH\\:mm}",
            Tag: $"library-{start:yyyyMMddHHmm}"));

    /// <summary>场馆订单支付提醒。</summary>
    public void NotifySportOrder(string venue, DateTime start, decimal price)
        => Notify(new ReminderItem(
            ReminderKind.Sport,
            start,
            "场馆订单待支付",
            $"{venue} · {start:MM-dd HH\\:mm} · {price:F2} 元",
            Tag: $"sport-{venue}-{start:yyyyMMddHHmm}"));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _toast?.Dispose();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch
        {
            // 清理失败不影响退出。
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}

/// <summary>一条历史提醒记录。</summary>
public sealed record HistoryEntry(
    DateTime At,
    string Title,
    string Body,
    ReminderKind Kind)
{
    public string TimeText => At.ToString("MM-dd HH:mm");

    public string KindText => Kind switch
    {
        ReminderKind.Course => "课程",
        ReminderKind.Schedule => "日程",
        ReminderKind.Library => "图书馆",
        ReminderKind.Sport => "运动",
        _ => "提醒",
    };
}

/// <summary>WinRT Toast 通知句柄的薄封装，便于统一释放与判空。</summary>
internal sealed class ToastNotifierHandle : IDisposable
{
    private readonly ToastNotifier _notifier;

    public ToastNotifierHandle(ToastNotifier notifier) => _notifier = notifier;

    public void Show(string xml, string? tag)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var toast = new ToastNotification(doc);
        if (!string.IsNullOrEmpty(tag)) toast.Tag = tag;

        _notifier.Show(toast);
    }

    public void Dispose() { }
}
