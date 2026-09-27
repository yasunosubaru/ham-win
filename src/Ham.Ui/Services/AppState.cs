using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Rating.Models;
using Ham.Infrastructure.Storage;
using Microsoft.UI.Dispatching;

namespace Ham.Ui.Services;

/// <summary>
/// WinUI 3 侧的应用状态。
/// </summary>
/// <remarks>
/// <b>关键设计：数据层与 WPF 版完全共用。</b>
/// 这里通过 <see cref="DataStore"/> 读取 <c>%LOCALAPPDATA%\Ham\appdata.json</c>，
/// 也就是 <c>Ham.App</c> 登录同步后写下的那一份。因此在 WinUI 3 里看到的
/// 课表、成绩、日程、图书馆预约<em>就是真实同步来的数据</em>，不是另造一份。
/// <para>
/// 迁移只发生在表现层：<c>Ham.Core</c> / <c>Ham.Infrastructure</c> 两个项目原样引用，
/// 解析、绩点计算、持久化、天气、校巴解析走的都是与已验证版本相同的代码。
/// </para>
/// </remarks>
public sealed class AppState
{
    private readonly DataStore _store = new();

    /// <summary>从磁盘读入的完整数据。</summary>
    public AppData Data { get; private set; } = new();

    /// <summary>最近一次成功获取的天气（Open-Meteo 真实数据）。</summary>
    public WeatherReport? Weather { get; set; }

    /// <summary>校巴线路（<c>bus.whu.edu.cn</c> 真实数据）。</summary>
    public IReadOnlyList<BusLine> BusLines { get; set; } = [];

    /// <summary>上次停留的分区下标。</summary>
    public int SectionIndex { get; set; }

    /// <summary>上次退出时是否深色主题。</summary>
    public bool Dark { get; set; }

    /// <summary>载入失败时的说明（例如文件损坏）。</summary>
    public string? LoadError { get; private set; }

    public AppSettings Settings => Data.Settings;

    public IReadOnlyList<Course> Courses => Data.Courses;
    public IReadOnlyList<ScoreRecord> Scores => Data.Scores;
    public IReadOnlyList<ScheduleItem> Schedules => Data.Schedules;
    public IReadOnlyList<ScheduleGroup> ScheduleGroups => Data.ScheduleGroups;
    public IReadOnlyList<LibraryBooking> LibraryBookings => Data.LibraryBookings;
    public IReadOnlyList<LibrarySeat> PreferredSeats => Data.PreferredSeats;
    public IReadOnlyList<CourseRating> CourseRatings => Data.CourseRatings;
    public IReadOnlyList<CourseReview> CourseReviews => Data.CourseReviews;
    public IReadOnlyList<CourseWish> CourseWishes => Data.CourseWishes;

    /// <summary>教务同步时间；从未同步过则为 <c>null</c>。</summary>
    public DateTimeOffset? LastEducationSync => Data.LastEducationSync;

    /// <summary>是否已经同步过教务数据。</summary>
    public bool HasEducation => Data.Courses.Count > 0 || Data.Scores.Count > 0;

    /// <summary>已完成的同步时间摘要，未同步时给出明确指引而不是空白。</summary>
    public string SyncSummary
    {
        get
        {
            if (LastEducationSync is not { } at) return "尚未同步教务数据";
            var lib = Data.LastLibrarySync;
            var s = $"教务 {at:yyyy-MM-dd HH:mm}";
            if (lib is { } l) s += $" · 图书馆 {l:yyyy-MM-dd HH:mm}";
            return s;
        }
    }

    /// <summary>载入持久化数据。失败不抛——界面要能起来并把问题显示出来。</summary>
    public async Task LoadAsync()
    {
        try
        {
            Data = await _store.LoadAsync();
            LoadError = _store.LastLoadError;
            Dark = string.Equals(Settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
    }

    /// <summary>写回设置（主题等）。</summary>
    public Task SaveSettingsAsync()
    {
        Settings.Theme = Dark ? "Dark" : "Light";
        return _store.SaveAsync(Data);
    }
}

/// <summary>一个导航分区。</summary>
/// <param name="Key">程序内部标识。</param>
/// <param name="Title">显示名称。</param>
/// <param name="Icon">图标（与 WPF 版保持一致）。</param>
/// <param name="HasRealData">
/// 该分区是否已有可用的真实数据来源。
/// <c>false</c> 的分区一律显示明确占位，<b>不填充假数据</b>——
/// 界面上摆一堆看起来像真的、其实编出来的内容，比空白更有害。
/// </param>
public sealed record Section(string Key, string Title, string Icon, bool HasRealData = true);

/// <summary>九个分区。顺序与 WPF 版一致。</summary>
public static class Sections
{
    public static IReadOnlyList<Section> All { get; } =
    [
        new("status", "状态", "🏠"),
        new("course", "课程", "📚"),
        new("schedule", "日程", "📅"),
        new("score", "成绩", "📊"),
        new("library", "图书馆", "📚"),
        new("sport", "运动", "🏀", HasRealData: false),
        new("bus", "校巴", "🚌"),
        new("rating", "给分", "⭐", HasRealData: false),
        new("settings", "设置", "⚙"),
    ];
}
