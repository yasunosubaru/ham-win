using Ham.Core.Models;
using Ham.Infrastructure.Campus.Models;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Sport.Models;

namespace Ham.App.Services;

/// <summary>
/// 提醒调度器。
/// </summary>
/// <remarks>
/// 每分钟在后台线程池上比对一次"未来 15 分钟内应当触发"的提醒，已触发过的用键去重。
/// 采用<b>轮询</b>而非长时间定时器，原因是应用可能被系统挂起，轮询在恢复后会立即补算，
/// 而定时器会漂移甚至丢失。
/// <para>
/// 停机期间错过的提醒<b>不补发</b>：课程已开始，通知"你迟到了"没有意义。
/// 只对"尚未开始"的事项提醒。
/// </para>
/// </remarks>
public sealed class ReminderScheduler : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(15);

    private readonly AppService _service;
    private readonly NotificationService _notifications;
    private readonly Func<SemesterCalendar> _calendarAccessor;
    private readonly HashSet<string> _fired = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private Timer? _timer;
    private bool _disposed;

    public ReminderScheduler(
        AppService service,
        NotificationService notifications,
        Func<SemesterCalendar> calendarAccessor)
    {
        _service = service;
        _notifications = notifications;
        _calendarAccessor = calendarAccessor;
    }

    public void Start()
    {
        if (_disposed || _timer is not null) return;

        _timer = new Timer(_ => TickOnce(), null, TimeSpan.FromSeconds(20), Tick);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>清空去重记录（切换学期或清空数据后调用，避免旧键抑制新提醒）。</summary>
    public void ResetFired()
    {
        lock (_gate) _fired.Clear();
    }

    /// <summary>执行一次检查。公开以便测试与手动触发。</summary>
    public void TickOnce()
    {
        try
        {
            if (!_service.Settings.EnableNotifications) return;

            var now = DateTime.Now;
            var horizon = now.Add(Lead);
            if (horizon < now) horizon = now.AddMinutes(1);

            if (_service.Settings.EnableCourseReminder) CheckCourses(now, horizon);
            if (_service.Settings.EnableScheduleReminder) CheckSchedules(now, horizon);
            CheckLibrary(now, horizon);
            CheckSport(now, horizon);
        }
        catch
        {
            // 调度失败不得影响应用运行。
        }
    }

    private void CheckCourses(DateTime now, DateTime horizon)
    {
        var calendar = _calendarAccessor();
        var week = calendar.CurrentWeek(now);
        if (week is null) return;

        for (var offset = 0; offset < 2; offset++)
        {
            var date = now.Date.AddDays(offset);
            var dayWeek = calendar.CurrentWeek(date);
            if (dayWeek is null) continue;

            var weekday = (int)date.DayOfWeek;
            foreach (var course in _service.GetCoursesAt(dayWeek.Value, weekday))
            {
                if (course.ClassFrom <= 0 || course.ClassTo < course.ClassFrom) continue;

                var (start, _) = SemesterCalendar.ClassPeriodTimes(
                    DateOnly.FromDateTime(date), course.ClassFrom, course.ClassTo);
                if (start <= now || start > horizon) continue;

                var key = $"c-{course.CourseId}-{start:yyyyMMddHHmm}";
                if (!TryMarkFired(key)) continue;

                _notifications.NotifyCourse(course, start);
            }
        }
    }

    private void CheckSchedules(DateTime now, DateTime horizon)
    {
        var timeline = _service.BuildTimeline();
        var occurrences = timeline.Occurrences(now, horizon);

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Start <= now) continue;

            // 优先使用日程自带的提醒时间；没有则用统一的提前 15 分钟。
            var lead = occurrence.Item.ReminderMinutes ?? (int)Lead.TotalMinutes;
            var remindAt = occurrence.Start.AddMinutes(-lead);
            if (remindAt <= now || remindAt > horizon) continue;

            var key = $"s-{occurrence.Item.Id}-{occurrence.Start:yyyyMMddHHmm}";
            if (!TryMarkFired(key)) continue;

            _notifications.NotifySchedule(occurrence.Item, occurrence.Start);
        }
    }

    private void CheckLibrary(DateTime now, DateTime horizon)
    {
        foreach (var booking in _service.LibraryBookings)
        {
            if (booking.Status != BookingStatus.Reserved) continue;
            if (booking.Start <= now || booking.Start > horizon) continue;

            var key = $"l-{booking.Id}-{booking.Start:yyyyMMddHHmm}";
            if (!TryMarkFired(key)) continue;

            _notifications.NotifyLibrary(booking.Describe(), booking.Start, booking.End);
        }
    }

    private void CheckSport(DateTime now, DateTime horizon)
    {
        foreach (var booking in _service.SportBookings)
        {
            if (booking.Status != SportOrderStatus.PendingPayment) continue;
            if (booking.Start <= now || booking.Start > horizon) continue;

            var key = $"o-{booking.Id}-{booking.Start:yyyyMMddHHmm}";
            if (!TryMarkFired(key)) continue;

            _notifications.NotifySportOrder(booking.VenueName, booking.Start, booking.Price);
        }
    }

    private bool TryMarkFired(string key)
    {
        lock (_gate) return _fired.Add(key);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
