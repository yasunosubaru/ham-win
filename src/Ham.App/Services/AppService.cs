using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ham.Core.Models;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Demo;
using Ham.Infrastructure.Education;
using Ham.Infrastructure.Library;
using Ham.Infrastructure.Net;
using Ham.Infrastructure.Storage;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Rating.Models;
using Ham.Infrastructure.Sport.Models;

namespace Ham.App.Services;

/// <summary>数据来源模式。</summary>
public enum DataSource
{
    /// <summary>尚未加载任何数据。</summary>
    None,

    /// <summary>已从教务系统同步真实数据。</summary>
    Live,

    /// <summary>使用本地演示数据。</summary>
    Demo,
}

/// <summary>
/// 应用状态中枢：持有全部数据、派生视图模型数据、并负责持久化。
/// </summary>
public sealed class AppService
{
    private readonly DataStore _store = new();

    private CampusHttpClient? _http;

    public AppData Data { get; private set; } = new();

    public DataSource Source { get; private set; } = DataSource.None;

    public ObservableCollection<Course> Courses { get; } = [];

    public ObservableCollection<ScoreRecord> Scores { get; } = [];

    public ObservableCollection<ScheduleItem> Schedules { get; } = [];

    public ObservableCollection<ScheduleGroup> ScheduleGroups { get; } = [];

    public ObservableCollection<LibraryBooking> LibraryBookings { get; } = [];

    public ObservableCollection<SportBooking> SportBookings { get; } = [];

    public ObservableCollection<CourseRating> CourseRatings { get; } = [];

    public AppSettings Settings => Data.Settings;

    /// <summary>本地数据文件路径（设置页展示，便于用户备份或排查）。</summary>
    public string DataPath => _store.FilePath;

    /// <summary>最近一次错误，用于状态栏提示。</summary>
    public string? LastError { get; set; }

    /// <summary>是否已具备访问教务系统的条件。</summary>
    public bool CanAccessEducation =>
        !string.IsNullOrWhiteSpace(Settings.StudentId)
        && !string.IsNullOrWhiteSpace(Settings.PortalPassword);

    public CampusHttpClient Http => _http ??= new CampusHttpClient(endpoints: Endpoints);

    /// <summary>校园各系统的接入点。默认生产环境；测试可注入本地 mock。</summary>
    public CampusEndpoints Endpoints { get; init; } = CampusEndpoints.Default;

    /// <summary>
    /// CAS 登录的执行者。生产环境由 WebView2 窗口实现；
    /// 抽象成接口是为了让 <see cref="SyncEducationAsync"/> 不直接依赖 UI 类型，便于测试替换。
    /// </summary>
    public ICasLoginProvider? CasLogin { get; set; }

    /// <summary>
    /// 当前生效的绩点口径。与 WinUI 3 共用 <see cref="GpaScale.Resolve"/>，
    /// 两边不允许各自选表。
    /// </summary>
    public GpaScale GpaScale => GpaScale.Resolve(Settings.GpaScaleName);

    public ComprehensiveScoreMethod ComprehensiveMethod =>
        Enum.TryParse<ComprehensiveScoreMethod>(Settings.ComprehensiveMethod, out var m)
            ? m
            : ComprehensiveScoreMethod.NewF2;

    public SemesterCalendar Calendar
    {
        get
        {
            var start = DateOnly.TryParse(Settings.SemesterStartDate, out var d)
                ? d
                : DateOnly.FromDateTime(DateTime.Today);
            return new SemesterCalendar(start,
                new Semester(Settings.SemesterYear, Settings.SemesterNumber),
                Settings.TotalWeeks);
        }
    }

    /// <summary>数据文件加载失败时的提示；正常时为 null。启动后会显示在状态栏。</summary>
    public string? LoadWarning { get; private set; }

    private readonly WeatherClient _weather = new();

    /// <summary>最近一次成功的天气数据。</summary>
    public WeatherReport? Weather { get; private set; }

    /// <summary>天气获取失败原因；正常时为 null。</summary>
    public string? WeatherError { get; private set; }

    public DateTimeOffset? WeatherUpdatedAt { get; private set; }

    /// <summary>缓存有效期：15 分钟内不重复请求。</summary>
    private static readonly TimeSpan WeatherCache = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 获取实时天气（Open-Meteo 开放接口，无需 API Key）。
    /// </summary>
    /// <remarks>
    /// 失败时保留上一次的成功结果（若有），只更新 <see cref="WeatherError"/>，
    /// 这样短暂断网不会让状态页的天气卡片整个消失。
    /// </remarks>
    public async Task<bool> RefreshWeatherAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && Weather is not null && WeatherUpdatedAt is { } at
            && DateTimeOffset.Now - at < WeatherCache)
        {
            return true;
        }

        try
        {
            Weather = await _weather.GetAsync(ct: ct).ConfigureAwait(false);
            WeatherUpdatedAt = DateTimeOffset.Now;
            WeatherError = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WeatherError = ex.Message;
            return false;
        }
    }

    public async Task InitializeAsync()
    {
        Data = await _store.LoadAsync().ConfigureAwait(false);
        Source = Data.Courses.Count > 0 || Data.Scores.Count > 0 ? DataSource.Live : DataSource.None;

        if (_store.LastLoadError is { } error)
        {
            LoadWarning = $"本地数据无法读取（{error}），已重置为空白数据。原文件已备份为 appdata.json.corrupt-*。";
        }

        Flush();
    }

    /// <summary>把 <see cref="Data"/> 的集合内容同步到可观察集合。</summary>
    public void Flush()
    {
        Sync(Courses, Data.Courses);
        Sync(Scores, Data.Scores);
        Sync(Schedules, Data.Schedules);
        Sync(ScheduleGroups, Data.ScheduleGroups);
        Sync(LibraryBookings, Data.LibraryBookings);
        Sync(SportBookings, Data.SportBookings);
        Sync(CourseRatings, Data.CourseRatings);
    }

    private static void Sync<T>(ObservableCollection<T> target, List<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    /// <summary>写入回 <see cref="Data"/> 并持久化。</summary>
    public async Task PersistAsync()
    {
        Data.Courses = Courses.ToList();
        Data.Scores = Scores.ToList();
        Data.Schedules = Schedules.ToList();
        Data.ScheduleGroups = ScheduleGroups.ToList();
        Data.LibraryBookings = LibraryBookings.ToList();
        Data.SportBookings = SportBookings.ToList();
        Data.CourseRatings = CourseRatings.ToList();

        await _store.SaveAsync(Data).ConfigureAwait(false);
    }

    // ── 演示数据 ───────────────────────────────────────────────────────────────

    /// <summary>载入演示数据，便于在无凭据时体验全部功能。</summary>
    public async Task LoadDemoAsync()
    {
        Data.Courses = DemoData.BuildCourses(Settings.SemesterYear, Settings.SemesterNumber).ToList();
        Data.Scores = DemoData.BuildScores().ToList();
        Data.Schedules = DemoData.BuildSchedules().ToList();
        Data.ScheduleGroups = DemoData.BuildScheduleGroups().ToList();
        Data.CourseRatings = DemoData.BuildRatings().ToList();
        Data.Settings.StudentId = string.IsNullOrEmpty(Settings.StudentId) ? "202312345678" : Settings.StudentId;
        Data.Settings.College = string.IsNullOrEmpty(Settings.College) ? "计算机学院" : Settings.College;
        Data.Settings.Nickname = string.IsNullOrEmpty(Settings.Nickname) ? "演示用户" : Settings.Nickname;
        Data.Settings.SemesterStartDate = GetCurrentSemesterStart().ToString("yyyy-MM-dd");

        Source = DataSource.Demo;
        Flush();
        await PersistAsync().ConfigureAwait(false);
    }

    /// <summary>清空所有本地数据。</summary>
    public async Task ResetAsync()
    {
        Data = new AppData();
        Source = DataSource.None;
        Flush();
        await PersistAsync().ConfigureAwait(false);
    }

    /// <summary>估算当前学期的开学日期（用于首次运行时给出合理默认值）。</summary>
    public static DateOnly GetCurrentSemesterStart()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // 武汉大学秋季学期通常 9 月初开学，春季 2 月底；取最近的已开学周次的周日。
        var candidate = today.Month >= 8 || today.Month <= 1
            ? new DateOnly(today.Year, 9, 1)
            : new DateOnly(today.Year - 1, 9, 1);

        while (candidate.DayOfWeek != DayOfWeek.Sunday) candidate = candidate.AddDays(-1);

        if (candidate > today.AddDays(28))
        {
            candidate = candidate.AddYears(-1);
            while (candidate.DayOfWeek != DayOfWeek.Sunday) candidate = candidate.AddDays(-1);
        }

        return candidate;
    }

    // ── 课程操作 ───────────────────────────────────────────────────────────────

    public void UpsertCourse(Course course)
    {
        var index = Courses.IndexOf(Courses.FirstOrDefault(c =>
            c.CourseId == course.CourseId && c.Weekday == course.Weekday
            && c.ClassFrom == course.ClassFrom && c.ClassTo == course.ClassTo
            && c.WeekFrom == course.WeekFrom && c.WeekTo == course.WeekTo)!);

        if (index >= 0) Courses[index] = course;
        else Courses.Add(course);
    }

    public void RemoveCourse(Course course) => Courses.Remove(course);

    public void ToggleScoreEnabled(ScoreRecord record)
    {
        var index = Scores.IndexOf(record);
        if (index < 0) return;
        Scores[index] = record with { IsEnabled = !record.IsEnabled };
    }

    public IReadOnlyList<CourseConflict> DetectConflicts()
        => ConflictDetector.Detect(Courses.ToList(), Settings.TotalWeeks);

    /// <summary>今日课程。</summary>
    public IReadOnlyList<Course> GetTodayCourses()
    {
        var calendar = Calendar;
        var week = calendar.CurrentWeek(DateTime.Now);
        if (week is null) return [];

        var weekday = (int)DateTime.Now.DayOfWeek;
        return Courses.Where(c => c.OccursOn(week.Value, weekday))
            .OrderBy(c => c.ClassFrom)
            .ToList();
    }

    /// <summary>明日课程。</summary>
    public IReadOnlyList<Course> GetTomorrowCourses()
    {
        var tomorrow = DateTime.Now.AddDays(1);
        var week = Calendar.CurrentWeek(tomorrow);
        if (week is null) return [];

        var weekday = (int)tomorrow.DayOfWeek;
        return Courses.Where(c => c.OccursOn(week.Value, weekday))
            .OrderBy(c => c.ClassFrom)
            .ToList();
    }

    /// <summary>某一周某天的课程。</summary>
    public IReadOnlyList<Course> GetCoursesAt(int week, int weekday)
        => Courses.Where(c => c.OccursOn(week, weekday)).OrderBy(c => c.ClassFrom).ToList();

    /// <summary>今天是否有课。</summary>
    public bool HasCourseToday => GetTodayCourses().Count > 0;

    public ScheduleTimeline BuildTimeline() => new(Schedules.ToList());

    // ── 教务同步 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 从教务系统同步课表与成绩。
    /// </summary>
    public async Task<(bool Ok, string Message)> SyncEducationAsync(
        string studentId, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(studentId)) return (false, "请先填写学号。");
        // 注意：这里刻意不校验学号长度。官方实现会在客户端硬拦 13/8 位，
        // 但那是页面行为而非服务端规则；武大本科常见 12 位学号，
        // 误拦会让用户彻底无法登录。长度异常由连接诊断以 Warning 形式提示。
        if (string.IsNullOrEmpty(password)) return (false, "请先填写信息门户密码。");

        try
        {
            if (CasLogin is null)
                return (false, "当前环境不支持交互式登录（未找到可用的浏览器内核）。");

            var semesterCode = SemesterCode.ToInternal(Settings.SemesterNumber);

            // 教务前置安全设备只放行"来自页面自身"的请求（带 X-Requested-With、
            // 同源、带会话），纯 HTTP 客户端稳定拿到 901 或 HTML 首页。
            // 因此数据一律交给已认证的浏览器页面代取，见 FetchPlan 的说明。
            //
            // 课表接口用 XHR 即可（实测 200 + 32KB JSON，kbList 20 门课）。
            // 成绩接口不同：实测无论 xnm/xqm 怎么填都返回完全相同的 186 字节空结果
            // （HTTP 910 + items:[] + entityOrField:false），说明服务端没读到查询条件。
            // 所以改用 Document 模式——直接打开成绩页读它自己渲染出来的表格。
            var plan = new FetchPlan(
            [
                new FetchTarget(EducationEndpoints.CoursePath, new Dictionary<string, string>
                {
                    ["validate"] = EducationClient.BuildValidateToken(),
                    ["xnm"] = Settings.SemesterYear.ToString(CultureInfo.InvariantCulture),
                    ["xqm"] = semesterCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["xzlx"] = "ck",
                }),
                // 成绩走普通 XHR 即可：validate 传占位值，**不需要图形验证码**。
                //
                // 动作（2026-09-27 实测出数据，29 条）：
                //   POST /cjcx/cjcx_cxXsgrcj.html?doType=query
                //   xnm=&xqm=&sy_id=&sq_id=&sfzgcj=&zd_fzdm=N305005-xs
                //   &queryModel.showCount=150&queryModel.currentPage=1&validate=<占位值>
                //
                // 两个容易踩的点：
                //  1. **必须带 doType=query**。zfsoft 对无 doType 的 GET 返回 404——
                //     爬虫正是被这个假信号骗了，一度判该动作不存在。
                //  2. **validate 只需存在且非空**。曾以为必须是顶象真 token，
                //     实测四种取值（不带/空串/随机 sl…/test）证明服务端从不核验，
                //     详见 EducationClient.BuildScoreValidateValue() 的实验记录。
                //     这是绕开校方那道客户端滑块门，不是通过了验证；
                //     合规路径仍保留在 FetchMode.ScoreWithCaptcha。
                new FetchTarget(
                    EducationEndpoints.ScorePath,
                    new Dictionary<string, string>
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

            var outcome = await CasLogin.LoginAsync(plan, ct).ConfigureAwait(false);

            if (outcome.Cookies.Count == 0)
            {
                App.LogCritical("同步教务: 登录窗口未返回任何 Cookie", null);
                return (false, "未完成登录。若登录窗口提示超时，请确认学号密码，"
                             + "以及账号是否已完成「账号激活」。");
            }

            // 记下浏览器侧的 Cookie，图书馆换票要用 CAS 票据
            BrowserCookies.Clear();
            BrowserCookies.AddRange(outcome.Cookies);

            // 让 HttpClient 也持有同一份会话，便于后续接口直接复用
            new CasClient(Http, Endpoints).SeedCookies(outcome.Cookies);

            // 失败必须落日志。曾经这里只把消息返回界面，日志里看不到任何痕迹，
            // 导致"登录成功但读不出成绩"这类问题完全没有诊断线索。
            if (outcome.Payloads.Failures.Count > 0)
            {
                App.LogCritical(
                    "同步教务: 浏览器代取失败 -> " + string.Join(" | ", outcome.Payloads.Failures), null);
            }

            var courseJson = outcome.Payloads.BodyOf(EducationEndpoints.CoursePath);

            var courses = EducationParser.ParseCourses(
                              courseJson ?? string.Empty, Settings.SemesterYear, Settings.SemesterNumber)
                ?? throw new CampusNetworkException(
                    "课表接口没有返回可解析的 JSON。"
                    + DescribeBody(courseJson)
                    + DescribeFetchFailures(outcome.Payloads));

            // 成绩：Document 模式拿页面表格；若拿不到表格再退回 JSON 解析。
            // 详情页优先——只有它自带 jQuery，验证码才会真的弹出来。
            var pageText = outcome.Payloads.BodyOf(EducationEndpoints.ScoreDetailPath)
                           ?? outcome.Payloads.BodyOf(EducationEndpoints.ScoreDocumentPath);
            var scoreRecords = EducationScorePageParser.Parse(pageText)
                               ?? EducationParser.ParseScores(
                                      outcome.Payloads.BodyOf(EducationEndpoints.ScorePath)
                                      ?? string.Empty)?.Scores;

            var scores = new ScoreFetchResult(scoreRecords?.ToList() ?? [], null);

            Data.Courses = courses.Courses.Where(c => !courses.IgnoredCourseNames.Contains(c.Name)).ToList();
            Data.Scores = scores.Scores.ToList();

            // 学生信息页是 HTML，从浏览器正文直接解析
            if (outcome.Payloads.BodyOf(EducationEndpoints.UserInfoPath) is { } infoHtml)
            {
                var parsed = EducationClient.ParseUserInfoHtml(infoHtml);
                if (parsed is not null) scores = scores with { UserInfo = parsed };
            }

            if (scores.UserInfo is { } info)
            {
                Settings.StudentId = info.StudentId.Length > 0 ? info.StudentId : studentId;
                Settings.College = info.College.Length > 0 ? info.College : Settings.College;
                Settings.Major = info.Major.Length > 0 ? info.Major : Settings.Major;
            }
            else
            {
                Settings.StudentId = studentId;
            }

            Settings.PortalPassword = password;
            Data.LastEducationSync = DateTimeOffset.Now;
            Source = DataSource.Live;

            Flush();
            await PersistAsync().ConfigureAwait(false);

            var note = courses.IgnoredCourseNames.Count > 0
                ? $"（{courses.IgnoredCourseNames.Count} 门课程周次无法识别，已跳过）"
                : string.Empty;

            App.LogCritical($"同步教务: 成功，课程 {Data.Courses.Count} 门、成绩 {Data.Scores.Count} 条", null);
            return (true, $"同步成功：{Data.Courses.Count} 门课程、{Data.Scores.Count} 条成绩。{note}");
        }
        catch (CasReAuthRequiredException ex)
        {
            App.LogCritical("同步教务: 需要重新认证 - " + ex.Message, null);
            return (false, ex.Message);
        }
        catch (CampusNetworkException ex)
        {
            App.LogCritical("同步教务: 网络/接口错误 - " + ex.Message, null);
            return (false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            App.LogCritical("同步教务: 已取消", null);
            return (false, "已取消同步。");
        }
        catch (Exception ex)
        {
            App.LogCritical("同步教务: 未预期异常", ex);
            return (false, $"同步失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从图书馆座位预约系统同步余座与我的预约。
    /// </summary>
    /// <remarks>
    /// 复用信息门户登录时取得的 CAS 票据（CASTGC）换票，<b>不需要浏览器内核</b>——
    /// 这与教务不同（教务必须借页面上下文，见 FetchPlan）。
    /// </remarks>
    public async Task<(bool Ok, string Message)> SyncLibraryAsync(
        string library, DateOnly date, CancellationToken ct = default)
    {
        try
        {
            var libraryClient = new LibraryClient(Http);

            var casService = await libraryClient.GetCasServiceAsync(ct).ConfigureAwait(false);
            var casServiceCookie = ReadCookie("cas.whu.edu.cn", "CASTGC");
            if (string.IsNullOrEmpty(casServiceCookie))
            {
                // 把 CAS 票据从浏览器侧搬过来
                var fromBrowser = BrowserCookies
                    .Where(c => c.Host.Contains("cas.whu.edu.cn", StringComparison.OrdinalIgnoreCase)
                                && c.Name.Equals("CASTGC", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Value)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(fromBrowser))
                {
                    return (false, "尚未取得信息门户会话，请先在「设置 → 登录并同步」完成登录。");
                }

                casServiceCookie = fromBrowser;
            }

            Http.SeedCookies([
                new System.Net.Cookie("CASTGC", casServiceCookie, "/", "cas.whu.edu.cn"),
            ]);

            await libraryClient.AuthenticateAsync(casService, ct).ConfigureAwait(false);

            var overview = await libraryClient
                .GetSeatOverviewAsync(library, date, ct).ConfigureAwait(false);
            var areas = LibraryParser.ParseAvailability(overview);

            // 解析不出内容时把原始正文落日志：结构需要真实登录才能看到，
            // 与其让用户面对"暂无数据"却不知道是接口坏了还是没座位，不如留下现场。
            if (areas.Count == 0)
            {
                App.LogCritical(
                    $"图书馆余座解析为空。原始 data: {DescribeJson(overview)}", null);
            }

            LibraryAreas = areas;
            LibraryAreaDate = date;
            LibraryName = library;

            try
            {
                var history = await libraryClient.GetMyReservationsAsync(ct).ConfigureAwait(false);
                var bookings = LibraryParser.ParseReservations(history);
                if (bookings.Count == 0)
                    App.LogCritical($"图书馆预约记录解析为空。原始 data: {DescribeJson(history)}", null);

                Data.LibraryBookings = bookings.ToList();
            }
            catch (CasReAuthRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 预约记录取不到不影响余座展示
                App.LogCritical("图书馆预约记录读取失败", ex);
            }

            Source = DataSource.Live;
            await PersistAsync().ConfigureAwait(false);

            var summary = areas.Count == 0
                ? $"图书馆同步完成，但 {library} 未返回任何区域数据"
                : $"图书馆同步成功：{areas.Count} 个区域，"
                  + $"共余 {areas.Sum(a => a.FreeSeats)} 个座位";

            App.LogCritical(summary, null);
            return (true, summary + "。");
        }
        catch (CasReAuthRequiredException ex)
        {
            App.LogCritical("图书馆: 需要重新认证 - " + ex.Message, null);
            return (false, ex.Message);
        }
        catch (CampusNetworkException ex)
        {
            App.LogCritical("图书馆: 接口错误 - " + ex.Message, null);
            return (false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消同步。");
        }
        catch (Exception ex)
        {
            App.LogCritical("图书馆: 未预期异常", ex);
            return (false, $"图书馆同步失败：{ex.Message}");
        }
    }

    /// <summary>最近一次成功同步到的图书馆余座。</summary>
    public IReadOnlyList<LibraryAreaAvailability> LibraryAreas { get; private set; } = [];

    /// <summary>余座对应的日期。</summary>
    public DateOnly LibraryAreaDate { get; private set; }

    /// <summary>余座对应的分馆。</summary>
    public string LibraryName { get; private set; } = string.Empty;

    /// <summary>登录成功后由宿主注入的浏览器 Cookie，供图书馆换票使用。</summary>
    public List<CasCookie> BrowserCookies { get; } = [];

    private string? ReadCookie(string host, string name)
        => Http.Cookies.GetCookies(new Uri($"https://{host}/"))
            .Cast<System.Net.Cookie>()
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrEmpty(c.Value))
            ?.Value;

    private static string DescribeJson(JsonElement e)
    {
        var text = e.ToString();
        return text.Length > 1200 ? text[..1200] + "…" : text;
    }

    /// <summary>
    /// 把代取阶段的失败原因拼进错误消息。
    /// </summary>
    /// <remarks>
    /// 「没拿到数据」有两种完全不同的原因，必须让用户一眼区分：
    /// 浏览器请求被站点拒绝（failures 非空，会带 HTTP 状态码），
    /// 还是请求成功但响应不是预期格式（failures 为空）。
    /// 之前两者都显示成同一句「无法解析的内容」，导致连续几轮都在猜。
    /// </remarks>
    private static string DescribeFetchFailures(FetchPlanResult payloads)
    {
        if (payloads.Failures.Count == 0)
            return "浏览器请求均已返回成功，但响应不是预期的 JSON 格式。";

        var first = payloads.Failures[0];
        var tail = payloads.Failures.Count > 1 ? $"（共 {payloads.Failures.Count} 个地址失败）" : string.Empty;
        return "浏览器代取被站点拒绝：" + first + tail;
    }

    /// <summary>把异常正文的开头拼进错误消息，让用户与日志都能看出拿到了什么。</summary>
    private static string DescribeBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "（正文为空）";
        var head = body.Length > 120 ? body[..120] : body;
        return "实际收到：" + head.Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>
    /// 探测当前 <see cref="Http"/> 的 jar 中是否已存在可用的教务会话。
    /// </summary>
    /// <remarks>
    /// 存在的意义是避免"每次点同步都弹一次登录窗口"。上一轮已经完成过登录时，
    /// 登录窗口会因已有会话而瞬间跳转并自动关闭，表现为反复闪现。
    /// 这里只做廉价的本地检查，不发网络请求，因此失败即视为"无会话"。
    /// </remarks>
    private Task<bool> HasLiveEducationSessionAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{Endpoints.EducationBaseUrl.TrimEnd('/')}/";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var seed))
                return Task.FromResult(false);

            var hasSession = Http.Cookies.GetCookies(seed)
                .Cast<Cookie>()
                .Any(c => !string.IsNullOrEmpty(c.Value)
                          && !c.Expired
                          && c.Name.Contains("SESSION", StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(hasSession);
        }
        catch (Exception ex)
        {
            App.LogCritical("探测教务会话失败", ex);
            return Task.FromResult(false);
        }
    }
}

/// <summary>学号格式提示。仅用于界面提示，不用于拦截。</summary>
public static class CasClientValidation
{
    public static bool IsStudentIdShapeValid(string studentId) => studentId.Length is 13 or 8;
}
