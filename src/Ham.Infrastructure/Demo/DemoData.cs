using Ham.Core.Models;
using Ham.Infrastructure.Library.Models;
using Ham.Infrastructure.Rating.Models;
using Ham.Infrastructure.Sport.Models;

namespace Ham.Infrastructure.Demo;

/// <summary>
/// 演示数据源。
/// </summary>
/// <remarks>
/// 用途：<b>不是</b>伪造数据冒充真实结果，而是在无凭据 / 无校园网时，让应用的每个模块
/// 都能完整走通交互流程。所有由此产生的数据在 UI 上均以"演示"角标明确标注，
/// 真实同步成功后会被整体替换。生成过程使用固定种子，因此可复现。
/// </remarks>
public static class DemoData
{
    private static readonly string[] CourseNames =
    [
        "高等数学(上)", "线性代数", "大学英语(一)", "程序设计基础", "数据结构",
        "计算机组成原理", "操作系统", "数据库系统", "计算机网络", "概率论与数理统计",
        "马克思主义基本原理", "中国近现代史纲要", "体育(篮球)", "大学物理(上)",
        "离散数学", "算法设计与分析", "软件工程", "数字逻辑",
    ];

    private static readonly string[] Instructors =
    [
        "张伟", "李娜", "王芳", "刘洋", "陈静", "杨帆", "黄磊", "周敏",
        "吴桐", "徐蕾", "孙浩", "马超", "朱琳", "胡斌", "郭涛", "何欣",
    ];

    private static readonly string[] CourseTypes =
    [
        "公共基础必修", "专业必修", "专业选修", "通识必修", "公共基础选修", "专业限选",
    ];

    private static readonly string[] RoomNames =
    [
        "总馆", "文理分馆", "工学分馆", "信息科学分馆",
    ];

    private static readonly string[] CampusColleges =
    [
        "计算机学院", "数学与统计学院", "经济学院", "物理科学与技术学院",
        "化学与分子科学学院", "生命科学学院", "文学学院", "新闻学院",
    ];

    /// <summary>用固定种子生成课表，保证每次进入演示态看到的内容一致。</summary>
    public static IReadOnlyList<Course> BuildCourses(int year, int semester)
    {
        var rng = new Random(20260926);
        var courses = new List<Course>();

        // 每天固定 6 个节次段（上午 2 段、下午 2 段、晚上 2 段）
        int[][] daySlots =
        [
            [1, 2], [3, 4], [5, 6], [7, 8], [9, 10], [11, 12],
        ];

        for (var i = 0; i < 16; i++)
        {
            var weekday = 1 + i % 5;                 // 周一至周五
            var slot = daySlots[i % daySlots.Length];
            var courseId = $"DEMO{i + 1:000}";

            var weekText = (i % 3) switch
            {
                0 => "1-17周",
                1 => "1-17周(单)",
                _ => "1-16周(双)",
            };

            var rule = WeekRuleParser.Parse(weekText);
            var type = CourseTypes[i % CourseTypes.Length];
            var credit = Math.Round(1.0 + rng.NextDouble() * 4.0, 1);

            courses.Add(new Course
            {
                Name = CourseNames[i % CourseNames.Length],
                CourseId = courseId,
                Instructor = Instructors[rng.Next(Instructors.Length)],
                InstructorType = "主讲",
                WeekFrom = rule.WeekFrom,
                WeekTo = rule.WeekTo,
                ClassFrom = slot[0],
                ClassTo = slot[1],
                Weekday = weekday,
                CourseType = type,
                Credit = credit,
                Location = $"教{2 + i % 6}-{101 + i * 7}",
                Color = CourseColorAssigner.ForCourseId(courseId),
                Year = year,
                SemesterNumber = semester,
                Weeks = rule.Weeks,
            });
        }

        return courses;
    }

    /// <summary>生成跨越两个学期的成绩单。</summary>
    public static IReadOnlyList<ScoreRecord> BuildScores()
    {
        var rng = new Random(20250101);
        var scores = new List<ScoreRecord>();
        var userCollege = "计算机学院";

        for (var year = 2025; year <= 2026; year++)
        {
            for (var semester = 1; semester <= (year == 2026 ? 1 : 2); semester++)
            {
                var count = year == 2026 ? 8 : 9;
                for (var i = 0; i < count; i++)
                {
                    var name = CourseNames[(i + (year - 2025) * 2) % CourseNames.Length];
                    var type = CourseTypes[i % CourseTypes.Length];
                    var isCrossMajor = i % 4 == 3;
                    var college = isCrossMajor
                        ? CampusColleges[(i + 1) % CampusColleges.Length]
                        : userCollege;

                    // 偏高的分数分布，便于演示 F2 取最高 8 门的行为
                    var score = 68 + rng.Next(0, 28);
                    var credit = Math.Round(1.0 + rng.NextDouble() * 4.0, 1);

                    scores.Add(new ScoreRecord
                    {
                        Year = year,
                        SemesterNumber = semester,
                        Name = name,
                        CourseId = $"DEMO{year}{semester}{i:00}",
                        Instructor = Instructors[rng.Next(Instructors.Length)],
                        Credit = credit,
                        CourseType = type,
                        Score = score,
                        CourseCollege = college,
                        IsEnabled = true,
                    });
                }
            }
        }

        return scores;
    }

    public static IReadOnlyList<ScheduleItem> BuildSchedules()
    {
        var now = DateTime.Now;
        var monday = GetMonday(now);

        return
        [
            new ScheduleItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "高等数学答疑",
                Location = "教三-201",
                Note = "带上上周的作业",
                Start = monday.AddDays(1).AddHours(9),
                End = monday.AddDays(1).AddHours(10),
                ReminderMinutes = 30,
                Recurrence = new RecurrenceRule
                {
                    Frequency = RecurrenceFrequency.Weekly,
                    WeeklyDays = [1],
                    EndDate = DateOnly.FromDateTime(monday).AddDays(70),
                },
            },
            new ScheduleItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "小组会议",
                Location = "信息学部会议室",
                Start = monday.AddDays(3).AddHours(14),
                End = monday.AddDays(3).AddHours(15).AddMinutes(30),
                ReminderMinutes = 10,
                Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, WeeklyDays = [3] },
            },
            new ScheduleItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "英语听说训练",
                Start = now.AddDays(1).AddHours(19),
                End = now.AddDays(1).AddHours(20),
                ReminderMinutes = 60,
                GroupId = "study",
            },
            new ScheduleItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "实验室值班",
                Location = "计算机学院 B301",
                Start = now.AddDays(2).AddHours(18),
                End = now.AddDays(2).AddHours(21),
                ReminderMinutes = 30,
                GroupId = "work",
            },
        ];
    }

    public static IReadOnlyList<ScheduleGroup> BuildScheduleGroups() =>
    [
        new ScheduleGroup { Id = "study", Name = "学习", Icon = "📚", Order = 0 },
        new ScheduleGroup { Id = "work", Name = "工作", Icon = "💼", Order = 1 },
        new ScheduleGroup { Id = "life", Name = "生活", Icon = "🌱", Order = 2 },
    ];

    public static IReadOnlyList<LibraryRoom> BuildRooms() =>
        RoomNames.Select((name, i) => new LibraryRoom
        {
            Id = $"ROOM{i}",
            Name = name,
            Building = name,
            Floor = i + 1,
            TotalSeats = 120,
        }).ToList();

    public static SeatBoard BuildSeatBoard(ReservationSlot slot, IReadOnlyList<LibraryRoom> rooms)
    {
        var rng = new Random(slot.Date.DayNumber);
        var seats = new List<LibrarySeat>();

        foreach (var room in rooms)
        {
            for (var i = 1; i <= 24; i++)
            {
                var occupied = rng.NextDouble() < 0.45;
                seats.Add(new LibrarySeat
                {
                    Id = $"{room.Id}-S{i:00}",
                    RoomId = room.Id,
                    RoomName = room.Name,
                    Label = $"{room.Floor}{(char)('A' + i / 8)}{i % 8 + 1}",
                    HasPower = i % 3 == 0,
                    IsWindowSeat = i % 8 is 0 or 7,
                    Features = i % 3 == 0 ? ["带电源"] : [],
                    IsOccupied = occupied,
                    OccupiedByHint = occupied ? "已占用" : null,
                });
            }
        }

        return new SeatBoard { Rooms = rooms, Seats = seats, Slot = slot };
    }

    public static IReadOnlyList<SportType> BuildSportTypes() =>
    [
        new SportType { Id = "basketball", Name = "篮球", Icon = "🏀" },
        new SportType { Id = "badminton", Name = "羽毛球", Icon = "🏸" },
        new SportType { Id = "football", Name = "足球", Icon = "⚽" },
        new SportType { Id = "tennis", Name = "网球", Icon = "🎾" },
        new SportType { Id = "swim", Name = "游泳", Icon = "🏊" },
    ];

    public static IReadOnlyList<SportVenue> BuildVenues(IReadOnlyList<SportType> types)
    {
        var venues = new List<SportVenue>();
        foreach (var type in types)
        {
            for (var i = 1; i <= 2; i++)
            {
                venues.Add(new SportVenue
                {
                    Id = $"{type.Id}-v{i}",
                    Name = $"{type.Name}场 {i}",
                    SportTypeId = type.Id,
                    Location = $"体育中心 {type.Name}区 {i} 号场",
                    OpenHours = "08:00–22:00",
                    SlotMinutes = 60,
                });
            }
        }

        return venues;
    }

    public static IReadOnlyList<SportSlot> BuildSlots(SportVenue venue, DateOnly date)
    {
        var rng = new Random(date.DayNumber * 31 + venue.Id.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        var slots = new List<SportSlot>();
        var cursor = new TimeOnly(8, 0);

        while (cursor < new TimeOnly(21, 0))
        {
            var capacity = 12;
            var booked = rng.Next(0, capacity + 3);
            slots.Add(new SportSlot
            {
                Id = $"{venue.Id}-{date:yyyyMMdd}-{cursor:HHmm}",
                VenueId = venue.Id,
                Date = date,
                Start = cursor,
                End = cursor.AddMinutes(venue.SlotMinutes),
                Capacity = capacity,
                Booked = booked,
            });

            cursor = cursor.AddMinutes(venue.SlotMinutes);
        }

        return slots;
    }

    public static IReadOnlyList<CourseRating> BuildRatings()
    {
        var rng = new Random(4242);
        var ratings = new List<CourseRating>();

        foreach (var name in CourseNames.Take(10))
        {
            var instructor = Instructors[rng.Next(Instructors.Length)];
            var total = 20 + rng.Next(0, 180);
            var average = Math.Round(72 + rng.NextDouble() * 18, 1);

            ratings.Add(new CourseRating
            {
                Id = $"R-{name}",
                CourseName = name,
                Instructor = instructor,
                Average = average,
                Total = total,
                Ranges =
                [
                    new RatingRange { From = 90, To = 100, Total = (int)(total * 0.22), Color = "#C7F3B5" },
                    new RatingRange { From = 80, To = 89, Total = (int)(total * 0.31), Color = "#E6F3A5" },
                    new RatingRange { From = 70, To = 79, Total = (int)(total * 0.25), Color = "#F3F3A5" },
                    new RatingRange { From = 60, To = 69, Total = (int)(total * 0.14), Color = "#F3D6A5" },
                    new RatingRange { From = 0, To = 59, Total = (int)(total * 0.08), Color = "#F3A5A5" },
                ],
            });
        }

        return ratings;
    }

    private static DateTime GetMonday(DateTime date)
    {
        var delta = ((int)date.DayOfWeek + 6) % 7;   // 周一为一周之始
        return date.Date.AddDays(-delta);
    }
}
