using System.Globalization;

using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Education;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Logging;
using Ham.Infrastructure.Net;
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

    /// <summary>最近一次同步的结果摘要，供界面显示。</summary>
    public string? LastSyncMessage { get; private set; }

    /// <summary>
    /// 登录信息门户并同步课表与成绩。
    /// </summary>
    /// <remarks>
    /// 这是 WinUI 3 版的<b>独立同步入口</b>——本应用不再依赖 WPF 版才能拿到数据。
    /// 流程与 WPF 版共用 <see cref="EducationSession"/>，只有浏览器宿主不同。
    /// <para>
    /// <b>判定成功的依据是「解析出了数据」，不是「拿到了 Cookie」。</b>
    /// 原因：<c>CoreWebView2CookieManager.GetCookiesAsync</c> 是否返回 HttpOnly
    /// Cookie 并没有明确文档（社区 issue #2199 至今无结论），而 CAS 的
    /// JSESSIONID/CASTGC 极可能是 HttpOnly。若拿它当硬门槛，一旦读不到就会
    /// 误判为登录失败——而此时数据其实已经取回来了。
    /// </para>
    /// </remarks>
    public async Task<(bool Ok, string Message)> SyncEducationAsync(
        Action<string>? status = null,
        CancellationToken ct = default)
    {
        var studentId = Settings.StudentId?.Trim() ?? string.Empty;
        var password = Settings.PortalPassword ?? string.Empty;

        if (studentId.Length == 0) return (false, "请先填写学号。");
        if (password.Length == 0) return (false, "请先填写信息门户密码。");

        var plan = BuildEducationPlan();
        var window = new LoginWindow(CampusEndpoints.Default, plan, studentId, password);
        window.Activate();

        CasLoginOutcome outcome;
        try
        {
            outcome = await window.RunAsync(ct);
        }
        finally
        {
            window.CloseOnce();
        }

        // 失败必须落日志。曾经这里只把消息返回界面，日志里看不到任何痕迹，
        // 导致"登录成功但读不出成绩"完全没有诊断线索。
        if (outcome.Payloads.Failures.Count > 0)
        {
            Log.Error("同步教务: 浏览器代取失败 -> "
                + string.Join(" | ", outcome.Payloads.Failures), null);
        }

        if (outcome.Cookies.Count > 0)
        {
            BrowserCookies.Clear();
            BrowserCookies.AddRange(outcome.Cookies);
        }

        var courseJson = outcome.Payloads.BodyOf(EducationEndpoints.CoursePath);
        var courses = EducationParser.ParseCourses(
            courseJson ?? string.Empty, Settings.SemesterYear, Settings.SemesterNumber);

        var scoreJson = outcome.Payloads.BodyOf(EducationEndpoints.ScorePath);
        var scoreRecords = EducationParser.ParseScores(scoreJson ?? string.Empty)?.Scores;

        // 数据一条都没解析出来，才算真的失败。
        var gotCourses = courses?.Courses.Count > 0;
        var gotScores = scoreRecords?.Count > 0;
        if (!gotCourses && !gotScores)
        {
            var why = outcome.Payloads.Failures.Count > 0
                ? "接口返回了错误：" + string.Join("；", outcome.Payloads.Failures)
                : "登录窗口没有带回任何可解析的数据。"
                  + "若窗口提示超时，请确认学号密码，以及账号是否已完成「账号激活」。";
            LastSyncMessage = "同步失败：" + why;
            Log.Error("同步教务: " + why, null);
            return (false, LastSyncMessage);
        }

        Data.Courses = courses!.Courses
            .Where(c => !courses.IgnoredCourseNames.Contains(c.Name))
            .ToList();
        Data.Scores = scoreRecords!.ToList();

        if (outcome.Payloads.BodyOf(EducationEndpoints.UserInfoPath) is { } infoHtml
            && EducationClient.ParseUserInfoHtml(infoHtml) is { } info)
        {
            if (info.StudentId.Length > 0) Settings.StudentId = info.StudentId;
            if (info.College.Length > 0) Settings.College = info.College;
            if (info.Major.Length > 0) Settings.Major = info.Major;
        }

        Settings.PortalPassword = password;
        Data.LastEducationSync = DateTimeOffset.Now;
        await SaveSettingsAsync();

        LastSyncMessage = $"同步成功：{Data.Courses.Count} 门课程、{Data.Scores.Count} 条成绩。";
        status?.Invoke(LastSyncMessage);
        return (true, LastSyncMessage);
    }

    /// <summary>
    /// 教务代取计划。
    /// </summary>
    /// <remarks>
    /// 三个目标全部是 XHR 模式，路径与 WPF 版<b>逐字一致</b>——
    /// 改这里等于改两个应用，所以不要只改一处。
    /// <list type="bullet">
    /// <item>成绩动作必须带 <c>doType=query</c>：zfsoft 对无 doType 的 GET 返 404。</item>
    /// <item><c>validate</c> 只需存在且非空；实验记录见
    /// <c>EducationClient.BuildScoreValidateValue()</c>。</item>
    /// </list>
    /// </remarks>
    public static FetchPlan BuildEducationPlan()
    {
        var semesterCode = SemesterCode.ToInternal(new AppSettings().SemesterNumber);

        return new FetchPlan(
        [
            new FetchTarget(EducationEndpoints.CoursePath, new Dictionary<string, string>
            {
                ["validate"] = EducationClient.BuildValidateToken(),
                ["xnm"] = new AppSettings().SemesterYear.ToString(CultureInfo.InvariantCulture),
                ["xqm"] = semesterCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["xzlx"] = "ck",
            }),
            new FetchTarget(EducationEndpoints.ScorePath, new Dictionary<string, string>
            {
                ["validate"] = EducationClient.BuildScoreValidateValue(),
                ["xnm"] = string.Empty,
                ["xqm"] = string.Empty,
                ["sy_id"] = string.Empty,
                ["sq_id"] = string.Empty,
                ["sfzgcj"] = string.Empty,
                ["zd_fzdm"] = EducationEndpoints.ScoreStudentFlag,
                ["queryModel.showCount"] = "150",
                ["queryModel.currentPage"] = "1",
            }),
            new FetchTarget(EducationEndpoints.UserInfoPath),
        ]);
    }

    /// <summary>浏览器侧取得的 Cookie，图书馆换票需要 CAS 票据。</summary>
    public List<CasCookie> BrowserCookies { get; } = [];
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
