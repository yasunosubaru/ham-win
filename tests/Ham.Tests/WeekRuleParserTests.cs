using Ham.Core.Models;
using Xunit;

namespace Ham.Tests;

public class WeekRuleParserTests
{
    [Theory]
    // 连续范围
    [InlineData("1-17周", new[] { 1 }, 1, 17)]
    [InlineData("1-8周", null, 1, 8)]
    // 倒置范围自动纠正
    [InlineData("8-1周", null, 1, 8)]
    public void ParsesContinuousRanges(string text, int[]? _, int from, int to)
    {
        var rule = WeekRuleParser.Parse(text);
        Assert.Equal(from, rule.WeekFrom);
        Assert.Equal(to, rule.WeekTo);
        Assert.True(rule.IsContinuous);
    }

    [Fact]
    public void StripsDiZhiPrefix()
    {
        // 剥离 "第" 是关键：不剥离会让 parseInt("第1") 得 NaN 从而丢掉整门课。
        var rule = WeekRuleParser.Parse("第1-17周");
        Assert.Equal(1, rule.WeekFrom);
        Assert.Equal(17, rule.WeekTo);
        Assert.Equal(17, rule.Weeks.Count);
    }

    [Theory]
    [InlineData("1-17周(单)", WeekParity.Odd)]
    [InlineData("1-17周(双)", WeekParity.Even)]
    [InlineData("1-17周", WeekParity.Normal)]
    public void DetectsParity(string text, WeekParity expected)
    {
        Assert.Equal(expected, WeekRuleParser.Parse(text).Parity);
    }

    [Fact]
    public void OddWeeksAreEveryOtherWeek()
    {
        var rule = WeekRuleParser.Parse("1-17周(单)");
        Assert.Equal(9, rule.Weeks.Count);              // 1,3,5,...,17
        Assert.Equal(1, rule.Weeks[0]);
        Assert.Equal(17, rule.Weeks[^1]);
        Assert.DoesNotContain(2, rule.Weeks);
        Assert.False(rule.IsContinuous);                // 不连续 → -1
        Assert.Equal(-1, rule.WeekFrom);
    }

    [Fact]
    public void EvenWeeks()
    {
        var rule = WeekRuleParser.Parse("1-17周(双)");
        Assert.Equal(8, rule.Weeks.Count);               // 2,4,...,16
        Assert.Equal(2, rule.Weeks[0]);
        Assert.Equal(16, rule.Weeks[^1]);
    }

    [Fact]
    public void ContradictorySingleWeekDegradesToNormal()
    {
        // "2周(单)" 自相矛盾：第 2 周不是单周。降级为每周并保留该周。
        var rule = WeekRuleParser.Parse("2周(单)");
        Assert.Equal(WeekParity.Normal, rule.Parity);
        Assert.Equal(new[] { 2 }, rule.Weeks);
    }

    [Fact]
    public void SplitsOnAllSeparatorKinds()
    {
        var rule = WeekRuleParser.Parse("1-4周,5-8周、9-12周，13-16周");
        Assert.Equal(16, rule.Weeks.Count);
        Assert.Equal(1, rule.WeekFrom);
        Assert.Equal(16, rule.WeekTo);
    }

    [Fact]
    public void DeduplicatesOverlappingSegments()
    {
        var rule = WeekRuleParser.Parse("1-8周,5-12周");
        Assert.Equal(12, rule.Weeks.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("无")]
    public void EmptyOrUnparsableYieldsEmptyRule(string? text)
    {
        var rule = WeekRuleParser.Parse(text);
        Assert.True(rule.IsEmpty);
        Assert.Equal(-1, rule.WeekFrom);
        Assert.Equal(-1, rule.WeekTo);
    }

    [Fact]
    public void NonContiguousWeeksKeepSentinel()
    {
        var rule = WeekRuleParser.Parse("1-3周,7-9周");
        Assert.Equal(6, rule.Weeks.Count);
        Assert.False(rule.IsContinuous);
        Assert.Equal(-1, rule.WeekFrom);
        Assert.Equal(-1, rule.WeekTo);
    }

    [Fact]
    public void DescribeRoundTrips()
    {
        var rule = WeekRuleParser.Parse("1-17周(单)");
        Assert.Equal("1-17周(单)", WeekRuleParser.Describe(rule));
    }
}
