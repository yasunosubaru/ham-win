using System.Text.Json;
using Ham.Core.Models;
using Ham.Infrastructure.Campus.Models;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Rating.Models;
using Ham.Infrastructure.Sport.Models;
using Ham.Infrastructure.Storage;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 持久化回归测试。
/// </summary>
/// <remarks>
/// 这组测试的存在理由：曾出现"首次运行正常、第二次运行打不开"的严重缺陷。
/// 根因是 <c>RecurrenceRule.WeeklyDays</c> 声明为 <c>IReadOnlySet&lt;int&gt;</c>，
/// System.Text.Json 无法为其实例化集合，读取数据文件时抛
/// <see cref="NotSupportedException"/>；而 <c>LoadAsync</c> 当时只捕获
/// <see cref="JsonException"/> 与 <see cref="IOException"/>，异常穿透导致启动在
/// <c>Show()</c> 之前中断——进程活着却没有任何窗口。
/// <para>
/// 任何新增到 <see cref="AppData"/> 的模型类型，都必须在 <see cref="AppDataRoundTripsThroughDisk"/>
/// 中被覆盖，否则等于没有回归保护。
/// </para>
/// </remarks>
public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ham-persist-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试清理失败不影响结论 */ }
        GC.SuppressFinalize(this);
    }

    private string NewPath() => Path.Combine(_dir, "appdata.json");

    /// <summary>构造一份"字段全满"的数据：任何被遗漏的类型都不会被发现。</summary>
    private static AppData BuildFullData()
    {
        var start = new DateTime(2026, 9, 7, 19, 0, 0);

        return new AppData
        {
            SchemaVersion = 3,
            Settings = new AppSettings
            {
                StudentId = "202312345678",
                Nickname = "测试",
                College = "计算机学院",
                Major = "计算机科学与技术",
                PortalPassword = "secret",
                SemesterYear = 2026,
                SemesterNumber = 1,
                SemesterStartDate = "2026-09-06",
                TotalWeeks = 20,
                GpaScaleName = "Standard 4.0",
                ComprehensiveMethod = "NewF2",
                CustomB2CourseIds = "A,B",
                SelectedScoreCalcScriptId = "script-1",
                RequireBiometricForScores = false,
                EnableNotifications = true,
                EnableCourseReminder = true,
                EnableScheduleReminder = false,
                CourseReminderMinutes = 15,
                LibraryAutoRefreshMinutes = 20,
                Theme = "Dark",
                AllowOfflineDemoData = false,
                OAuthClientId = "cid",
                OAuthClientSecret = "csec",
                OAuthAccessToken = "tok",
                OAuthTokenExpiry = "2026-10-01T00:00:00Z",
                OpenId = "open-1",
                ApiKey = "ham_key",
                PreferredLibraryRoom = "总馆",
                PreferredLibrarySeat = "3A5",
                PreferredSportVenue = "篮球场 1",
                PreferredSportSlot = "10:00",
                BusStopName = "信息学部",
            },

            Courses =
            [
                new Course
                {
                    Name = "高等数学(上)", CourseId = "MATH101", Instructor = "张老师",
                    InstructorType = "主讲", WeekFrom = 1, WeekTo = 17, ClassFrom = 1, ClassTo = 2,
                    Weekday = 1, CourseType = "公共基础必修", Credit = 5, Location = "教三-201",
                    Color = "#A5B9F3", Year = 2026, SemesterNumber = 1,
                    RawWeekText = "1-17周", Weeks = [1, 2, 3],
                },
                new Course
                {
                    Name = "大学英语", CourseId = "ENGL101", Instructor = "李老师",
                    WeekFrom = -1, WeekTo = -1, ClassFrom = 3, ClassTo = 4, Weekday = 3,
                    CourseType = "公共基础必修", Credit = 3, Location = "外语楼-101",
                    Color = "#C7A5E6", Year = 2026, SemesterNumber = 1, Weeks = [],
                },
            ],

            Scores =
            [
                new ScoreRecord
                {
                    Year = 2026, SemesterNumber = 1, Name = "高等数学(上)", CourseId = "MATH101",
                    Instructor = "张老师", Credit = 5, CourseType = "公共基础必修", Score = 92,
                    CourseCollege = "数学与统计学院", IsEnabled = true,
                },
                new ScoreRecord
                {
                    Year = 2025, SemesterNumber = 2, Name = "大学英语", CourseId = "ENGL101",
                    Instructor = "李老师", Credit = 3, CourseType = "公共基础必修", Score = 78,
                    CourseCollege = "计算机学院", IsEnabled = false,
                },
            ],

            // 关键：WeeklyDays 是历史缺陷所在，必须被覆盖
            Schedules =
            [
                new ScheduleItem
                {
                    Id = "s1", Title = "答疑", Location = "教三-201", Note = "带作业",
                    Start = start, End = start.AddHours(2), GroupId = "study",
                    ReminderMinutes = 30,
                    Recurrence = new RecurrenceRule
                    {
                        Frequency = RecurrenceFrequency.Weekly,
                        WeeklyDays = [1, 3, 5],
                        WeekInterval = 2,
                        MonthlyByDayOfWeek = false,
                        EndDate = new DateOnly(2026, 12, 31),
                        MaxOccurrences = 12,
                    },
                    LinkedCourseId = "MATH101",
                    LinkedCourseName = "高等数学(上)",
                },
                new ScheduleItem
                {
                    Id = "s2", Title = "单次日程", Start = start, End = null,
                    Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.None },
                },
            ],

            ScheduleGroups =
            [
                new ScheduleGroup { Id = "study", Name = "学习", Icon = "📚", Order = 0 },
            ],

            LibraryBookings =
            [
                new LibraryBooking
                {
                    Id = "b1", RoomId = "ROOM0", RoomName = "总馆", SeatId = "ROOM0-S05",
                    SeatLabel = "3A5", Building = "总馆",
                    Start = new DateTime(2026, 9, 27, 14, 0, 0),
                    End = new DateTime(2026, 9, 27, 18, 0, 0),
                    Status = BookingStatus.Reserved,
                    CheckedInAt = new DateTime(2026, 9, 27, 14, 5, 0),
                    MustReturnBy = new DateTime(2026, 9, 27, 15, 0, 0),
                },
            ],

            PreferredSeats =
            [
                new LibrarySeat
                {
                    Id = "ROOM0-S05", RoomId = "ROOM0", RoomName = "总馆", Label = "3A5",
                    Features = ["带电源", "靠窗"], HasPower = true, IsWindowSeat = true,
                    IsOccupied = false, OccupiedByHint = null,
                },
            ],

            SportBookings =
            [
                new SportBooking
                {
                    Id = "o1", VenueId = "basketball-v1", VenueName = "篮球场 1",
                    SportTypeName = "篮球",
                    Start = new DateTime(2026, 9, 28, 10, 0, 0),
                    End = new DateTime(2026, 9, 28, 11, 0, 0),
                    Status = SportOrderStatus.PendingPayment,
                    PaymentUrl = "https://payment.whu.edu.cn/x", Price = 10.5m,
                    PaidAt = null,
                },
            ],

            FavoriteSportBookings =
            [
                new FavoriteSportBooking
                {
                    Id = "f1", SportTypeId = "basketball", SportTypeName = "篮球",
                    VenueId = "basketball-v1", VenueName = "篮球场 1",
                    SlotMinutes = 60, ReminderMinutesBefore = 30, Enabled = true,
                },
            ],

            CourseRatings =
            [
                new CourseRating
                {
                    Id = "r1", CourseName = "高等数学(上)", Instructor = "张老师",
                    Average = 82.5, Total = 120,
                    Ranges = [new RatingRange { From = 90, To = 100, Total = 30, Color = "#C7F3B5" }],
                },
            ],

            CourseReviews =
            [
                new CourseReview
                {
                    CourseName = "高等数学(上)", Instructor = "张老师", Rating = 4,
                    Comment = "讲得清楚", CreatedAt = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero),
                    Anonymous = true,
                },
            ],

            CourseWishes =
            [
                new CourseWish
                {
                    CourseId = "MATH101", CourseName = "高等数学(上)",
                    CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
                },
            ],

            LastEducationSync = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero),
            LastLibrarySync = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero),
        };
    }

    [Fact]
    public async Task AppDataRoundTripsThroughDisk()
    {
        var path = NewPath();
        var original = BuildFullData();

        var store = new DataStore(path);
        await store.SaveAsync(original);
        var loaded = await new DataStore(path).LoadAsync();

        Assert.Null(new DataStore(path).LastLoadError);

        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);

        // 设置
        Assert.Equal("202312345678", loaded.Settings.StudentId);
        Assert.Equal("计算机学院", loaded.Settings.College);
        Assert.Equal("Dark", loaded.Settings.Theme);
        Assert.Equal(20, loaded.Settings.LibraryAutoRefreshMinutes);
        Assert.Equal("ham_key", loaded.Settings.ApiKey);
        Assert.Equal("信息学部", loaded.Settings.BusStopName);
        Assert.False(loaded.Settings.AllowOfflineDemoData);

        // 课程（含集合与非连续周次）
        Assert.Equal(2, loaded.Courses.Count);
        var math = loaded.Courses.Single(c => c.CourseId == "MATH101");
        Assert.Equal("高等数学(上)", math.Name);
        Assert.Equal([1, 2, 3], math.Weeks);
        Assert.Equal("#A5B9F3", math.Color);
        var english = loaded.Courses.Single(c => c.CourseId == "ENGL101");
        Assert.Equal(-1, english.WeekFrom);
        Assert.Empty(english.Weeks);

        // 成绩
        Assert.Equal(2, loaded.Scores.Count);
        Assert.False(loaded.Scores.Single(s => s.CourseId == "ENGL101").IsEnabled);

        // 日程 + 重复规则（历史缺陷点）
        Assert.Equal(2, loaded.Schedules.Count);
        var recurring = loaded.Schedules.Single(s => s.Id == "s1");
        Assert.Equal(RecurrenceFrequency.Weekly, recurring.Recurrence.Frequency);
        Assert.Equal([1, 3, 5], recurring.Recurrence.WeeklyDays);
        Assert.Equal(2, recurring.Recurrence.WeekInterval);
        Assert.Equal(new DateOnly(2026, 12, 31), recurring.Recurrence.EndDate);
        Assert.Equal(12, recurring.Recurrence.MaxOccurrences);
        Assert.Equal(30, recurring.ReminderMinutes);
        Assert.Equal("MATH101", recurring.LinkedCourseId);

        Assert.Single(loaded.ScheduleGroups);

        // 图书馆 / 运动 / 给分
        var booking = Assert.Single(loaded.LibraryBookings);
        Assert.Equal(BookingStatus.Reserved, booking.Status);
        Assert.Equal("3A5", booking.SeatLabel);
        Assert.NotNull(booking.CheckedInAt);

        var seat = Assert.Single(loaded.PreferredSeats);
        Assert.Equal(["带电源", "靠窗"], seat.Features);

        var order = Assert.Single(loaded.SportBookings);
        Assert.Equal(SportOrderStatus.PendingPayment, order.Status);
        Assert.Equal(10.5m, order.Price);

        Assert.Single(loaded.FavoriteSportBookings);
        Assert.Equal(82.5, Assert.Single(loaded.CourseRatings).Average);
        Assert.Single(loaded.CourseReviews);
        Assert.Single(loaded.CourseWishes);

        Assert.NotNull(loaded.LastEducationSync);
        Assert.NotNull(loaded.LastLibrarySync);
    }

    [Fact]
    public async Task WeeklyDaysIsJsonDeserializable()
    {
        // 直接锁定历史缺陷：IReadOnlySet<int> 无法反序列化。
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            WeeklyDays = [1, 3, 5],
        };

        var json = JsonSerializer.Serialize(rule, DataStore.Options);
        var back = JsonSerializer.Deserialize<RecurrenceRule>(json, DataStore.Options);

        Assert.NotNull(back);
        Assert.Equal([1, 3, 5], back!.WeeklyDays);
    }

    /// <summary>反例类型：刻意使用抽象集合接口，模拟历史缺陷的形态。</summary>
    private sealed class BadRecurrence
    {
        public IReadOnlySet<int> WeeklyDays { get; set; } = new HashSet<int>();
    }

    [Fact]
    public void SystemTextJsonCannotDeserializeIntoIReadOnlySet()
    {
        // 这是历史缺陷的机制证明：
        // IReadOnlySet<T> 是抽象接口，System.Text.Json 无法实例化，读取时抛
        // NotSupportedException（而不是 JsonException）——
        // 因此只捕获 JsonException 的 LoadAsync 无法兜住它。
        const string json = """{ "weeklyDays": [1,3,5] }""";

        var ex = Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Deserialize<BadRecurrence>(json, DataStore.Options));

        Assert.Contains("IReadOnlySet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IReadOnlyListIsSafeForPersistence()
    {
        // 对照组：IReadOnlyList<T> 能被正常反序列化。
        const string json = """{ "weeklyDays": [1,3,5] }""";
        var ok = JsonSerializer.Deserialize<RecurrenceRule>(json, DataStore.Options);
        Assert.NotNull(ok);
        Assert.Equal([1, 3, 5], ok!.WeeklyDays);
    }

    [Fact]
    public void WeeklyDaysDistinctDeduplicates()
    {
        var rule = new RecurrenceRule { WeeklyDays = [1, 1, 3, 3, 5] };
        Assert.Equal(3, rule.WeeklyDaysDistinct.Count);
        Assert.Contains(3, rule.WeeklyDaysDistinct);
    }

    [Fact]
    public async Task SaveThenLoadIsIdempotentAcrossRepeatedLaunches()
    {
        // 模拟"启动 → 读 → 写 → 退出"反复执行：第二、三次不能因格式问题失败。
        // （历史缺陷正是"首次运行正常、第二次运行打不开"。）
        var path = NewPath();
        var store = new DataStore(path);
        await store.SaveAsync(BuildFullData());   // 先落盘，之后每轮都必须读得到完整数据

        for (var i = 0; i < 5; i++)
        {
            var round = new DataStore(path);
            var loaded = await round.LoadAsync();

            Assert.Null(round.LastLoadError);
            Assert.Equal(2, loaded.Courses.Count);
            Assert.Equal(2, loaded.Schedules.Count);
            Assert.Equal([1, 3, 5], loaded.Schedules.Single(s => s.Id == "s1").Recurrence.WeeklyDays);

            await round.SaveAsync(loaded);
        }
    }

    [Fact]
    public async Task MissingFileYieldsEmptyData()
    {
        var store = new DataStore(Path.Combine(_dir, "nope.json"));
        var data = await store.LoadAsync();
        Assert.Empty(data.Courses);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public async Task EmptyFileYieldsEmptyData()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "   ");
        var store = new DataStore(path);
        var data = await store.LoadAsync();
        Assert.Empty(data.Courses);
    }

    [Fact]
    public async Task MalformedJsonDoesNotThrow()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "{ this is not json ");
        var store = new DataStore(path);

        var data = await store.LoadAsync();   // 不得抛异常
        Assert.Empty(data.Courses);
        Assert.NotNull(store.LastLoadError);
    }

    [Fact]
    public async Task WrongShapeJsonDoesNotThrow()
    {
        // 数字出现在需要字符串的位置等"类型不兼容"场景。
        var path = NewPath();
        await File.WriteAllTextAsync(path, """{ "settings": { "semesterYear": "not-a-number" } }""");
        var store = new DataStore(path);

        var data = await store.LoadAsync();
        Assert.NotNull(data);
    }

    [Fact]
    public async Task IncompatibleShapeIsBackedUpNotFatal()
    {
        // 模拟"未来版本写入的字段结构"：整体类型不匹配时必须降级而不是崩溃。
        var path = NewPath();
        await File.WriteAllTextAsync(path, """{ "courses": "this-should-be-an-array" }""");

        var store = new DataStore(path);
        var data = await store.LoadAsync();

        Assert.Empty(data.Courses);
        Assert.True(Directory.GetFiles(_dir, "*.corrupt-*").Length > 0,
            "损坏文件应被备份");
    }

    [Fact]
    public async Task CorruptFileIsBackedUpAndAppStillUsable()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "}{ broken");

        var store = new DataStore(path);
        await store.LoadAsync();

        // 降级之后必须还能正常写入，应用才能继续用。
        await store.SaveAsync(BuildFullData());
        var reloaded = await new DataStore(path).LoadAsync();
        Assert.Equal(2, reloaded.Courses.Count);
    }

    [Fact]
    public async Task SaveCreatesDirectoryWhenMissing()
    {
        var nested = Path.Combine(_dir, "a", "b", "c", "appdata.json");
        var store = new DataStore(nested);
        await store.SaveAsync(BuildFullData());
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task ConcurrentSavesDoNotCorrupt()
    {
        var path = NewPath();
        var store = new DataStore(path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.SaveAsync(BuildFullData())));

        var loaded = await new DataStore(path).LoadAsync();
        Assert.Equal(2, loaded.Courses.Count);
    }
}
