using Ham.Core.Models;
using Xunit;

namespace Ham.Tests;

public class SemesterCalendarTests
{
    // 2026-09-06 是一个周日，作为第 1 周起点。
    private static SemesterCalendar Calendar(DateOnly start, int weeks = 20)
        => new(start, new Semester(2026, 1), weeks);

    [Fact]
    public void NonSundayStartIsNormalizedBackToSunday()
    {
        // 2026-09-09 是周三
        var cal = Calendar(new DateOnly(2026, 9, 9));
        Assert.Equal(DayOfWeek.Sunday, cal.FirstDayOfWeek1.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 9, 6), cal.FirstDayOfWeek1);
    }

    [Fact]
    public void DateOfMapsWeekAndWeekday()
    {
        var cal = Calendar(new DateOnly(2026, 9, 6));
        Assert.Equal(new DateOnly(2026, 9, 6), cal.DateOf(1, 0));   // 第1周周日
        Assert.Equal(new DateOnly(2026, 9, 7), cal.DateOf(1, 1));   // 第1周周一
        Assert.Equal(new DateOnly(2026, 9, 12), cal.DateOf(1, 6));  // 第1周周六
        Assert.Equal(new DateOnly(2026, 9, 13), cal.DateOf(2, 0));  // 第2周周日
    }

    [Fact]
    public void OutOfRangeWeekReturnsNull()
    {
        var cal = Calendar(new DateOnly(2026, 9, 6), weeks: 20);
        Assert.Null(cal.DateOf(0, 0));
        Assert.Null(cal.DateOf(21, 0));
        Assert.Null(cal.DateOf(1, 7));
    }

    [Fact]
    public void WeekOfIsInverseOfDateOf()
    {
        var cal = Calendar(new DateOnly(2026, 9, 6));
        for (var w = 1; w <= 20; w++)
        {
            var date = cal.DateOf(w, 3)!.Value;
            Assert.Equal(w, cal.WeekOf(date));
        }
    }

    [Fact]
    public void OutsideSemesterReturnsNull()
    {
        var cal = Calendar(new DateOnly(2026, 9, 6), weeks: 4);
        Assert.Null(cal.WeekOf(new DateOnly(2026, 1, 1)));
        Assert.Null(cal.WeekOf(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void ClassPeriodTimesFollowFortyFiveMinuteBlocks()
    {
        var (start, end) = SemesterCalendar.ClassPeriodTimes(new DateOnly(2026, 9, 7), 1, 2);
        Assert.Equal(new DateTime(2026, 9, 7, 8, 0, 0), start);
        Assert.Equal(new DateTime(2026, 9, 7, 9, 30, 0), end);

        var (s2, e2) = SemesterCalendar.ClassPeriodTimes(new DateOnly(2026, 9, 7), 3, 4);
        Assert.Equal(new DateTime(2026, 9, 7, 9, 30, 0), s2);
        Assert.Equal(new DateTime(2026, 9, 7, 11, 0, 0), e2);
    }
}

public class ConflictDetectorTests
{
    private static Course C(string name, int weekday, int from, int to, int weekFrom = 1, int weekTo = 17)
        => new()
        {
            Name = name,
            CourseId = name,
            Weekday = weekday,
            ClassFrom = from,
            ClassTo = to,
            WeekFrom = weekFrom,
            WeekTo = weekTo,
            Weeks = Enumerable.Range(weekFrom, weekTo - weekFrom + 1).ToList(),
        };

    [Fact]
    public void OverlappingOnSameDayAndWeekConflicts()
    {
        var conflicts = ConflictDetector.Detect([C("A", 1, 2, 4), C("B", 1, 3, 5)]);
        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, c => Assert.Equal(1, c.Weekday));
    }

    [Fact]
    public void AdjacentPeriodsDoNotConflict()
    {
        var conflicts = ConflictDetector.Detect([C("A", 1, 1, 2), C("B", 1, 3, 4)]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void DifferentDaysDoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect([C("A", 1, 1, 4), C("B", 2, 1, 4)]));
    }

    [Fact]
    public void DifferentWeeksDoNotConflict()
    {
        // A 在 1-8 周，B 在 9-16 周
        Assert.Empty(ConflictDetector.Detect([C("A", 1, 1, 4, 1, 8), C("B", 1, 1, 4, 9, 16)]));
    }

    [Fact]
    public void OddEvenWeeksDoNotConflict()
    {
        var odd = C("A", 1, 1, 4) with
        {
            Weeks = [1, 3, 5, 7, 9, 11, 13, 15, 17],
            WeekFrom = -1, WeekTo = -1,
        };
        var even = C("B", 1, 1, 4) with
        {
            Weeks = [2, 4, 6, 8, 10, 12, 14, 16],
            WeekFrom = -1, WeekTo = -1,
        };
        Assert.Empty(ConflictDetector.Detect([odd, even]));
    }

    [Fact]
    public void ConflictDescriptionIsReadable()
    {
        var conflicts = ConflictDetector.Detect([C("高等数学", 1, 2, 4), C("线性代数", 1, 3, 5)]);
        var text = conflicts[0].Describe();
        Assert.Contains("第 1 周", text);
        Assert.Contains("星期一", text);
        Assert.Contains("高等数学", text);
    }

    [Fact]
    public void NoCoursesNoConflicts()
        => Assert.Empty(ConflictDetector.Detect([]));
}

public class ScheduleTests
{
    private static ScheduleItem Item(DateTime start, RecurrenceRule? rule = null) => new()
    {
        Id = "id-1",
        Title = "自习",
        Start = start,
        End = start.AddHours(2),
        Recurrence = rule ?? new RecurrenceRule(),
    };

    [Fact]
    public void NonRepeatingScheduleYieldsOneOccurrence()
    {
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 7, 19, 0, 0))]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Single(list);
        Assert.Equal(new DateTime(2026, 9, 7, 19, 0, 0), list[0].Start);
    }

    [Fact]
    public void DailyRecurrenceExpandsEveryDay()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, EndDate = new DateOnly(2026, 9, 10) };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 7, 8, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Equal(4, list.Count);   // 7,8,9,10
    }

    [Fact]
    public void WeeklyRecurrenceOnSelectedDays()
    {
        // 2026-09-07 是周一
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            WeeklyDays = [1, 3],   // 周一、周三
            EndDate = new DateOnly(2026, 9, 20),
        };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 7, 20, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        // EndDate 2026-09-20 之前：周一 7/14/21 不含 21，周三 9/16 → 共 4 次
        Assert.Equal(4, list.Count);
        Assert.All(list, o => Assert.Contains(o.Start.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }));
    }

    [Fact]
    public void BiweeklyRecurrenceSkipsAlternateWeeks()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly,
            WeeklyDays = [1],
            WeekInterval = 2,
            EndDate = new DateOnly(2026, 10, 31),
        };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 7, 20, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 10, 31));
        // 9/7, 9/21, 10/5, 10/19 → 隔周一次
        Assert.Equal(4, list.Count);
    }

    [Fact]
    public void MaxOccurrencesCapsExpansion()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Daily,
            MaxOccurrences = 3,
        };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 7, 8, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 12, 31));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void MonthlyByFixedDate()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Monthly,
            EndDate = new DateOnly(2026, 12, 31),
        };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 15, 9, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 12, 31));
        Assert.Equal(4, list.Count);
        Assert.All(list, o => Assert.Equal(15, o.Start.Day));
    }

    [Fact]
    public void MonthlyByNthWeekday()
    {
        // 2026-09-16 是第三个周三
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Monthly,
            MonthlyByDayOfWeek = true,
            EndDate = new DateOnly(2026, 12, 31),
        };
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 16, 9, 0, 0), rule)]);
        var list = timeline.Occurrences(new DateTime(2026, 9, 1), new DateTime(2026, 12, 31));
        Assert.Equal(4, list.Count);
        Assert.All(list, o =>
        {
            Assert.Equal(DayOfWeek.Wednesday, o.Start.DayOfWeek);
            Assert.InRange(o.Start.Day, 15, 21);
        });
    }

    [Fact]
    public void NearestUpcomingPicksTheClosestFutureOne()
    {
        var timeline = new ScheduleTimeline(
        [
            Item(new DateTime(2026, 9, 1, 9, 0, 0)),
            Item(new DateTime(2026, 9, 20, 14, 0, 0)),
            Item(new DateTime(2026, 10, 5, 10, 0, 0)),
        ]);

        var nearest = timeline.NearestUpcoming(new DateTime(2026, 9, 10));
        Assert.NotNull(nearest);
        Assert.Equal(new DateTime(2026, 9, 20, 14, 0, 0), nearest!.Start);
    }

    [Fact]
    public void AlreadyFinishedSchedulesAreNotUpcoming()
    {
        var timeline = new ScheduleTimeline([Item(new DateTime(2026, 9, 1, 9, 0, 0))]);
        Assert.Null(timeline.NearestUpcoming(new DateTime(2026, 9, 10)));
    }

    [Theory]
    [InlineData(" ", "2026-09-07T10:00", null, "名称不能为空")]
    [InlineData("ok", "2026-09-07T10:00", "2026-09-07T09:00", "结束时间不能早于开始时间")]
    [InlineData("ok", "2026-09-07T10:00", "2026-09-09T10:00", "开始与结束时间相隔不能超过 24 小时")]
    public void ValidationRejectsBadInput(string title, string start, string? end, string expected)
    {
        var s = DateTime.Parse(start);
        var e = end is null ? (DateTime?)null : DateTime.Parse(end);
        Assert.Equal(expected, ScheduleItem.Validate(title, s, e));
    }

    [Fact]
    public void ExactlyTwentyFourHoursIsAllowed()
    {
        var s = new DateTime(2026, 9, 7, 10, 0, 0);
        Assert.Null(ScheduleItem.Validate("ok", s, s.AddHours(24)));
    }
}
