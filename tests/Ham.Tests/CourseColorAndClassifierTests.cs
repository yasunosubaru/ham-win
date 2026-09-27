using Ham.Core.Models;
using Xunit;

namespace Ham.Tests;

public class CourseColorAssignerTests
{
    [Fact]
    public void PaletteHasEighteenEntries()
    {
        Assert.Equal(18, CourseColorAssigner.Palette.Count);
        Assert.All(CourseColorAssigner.Palette, c => Assert.Matches("^#[0-9A-F]{6}$", c));
    }

    [Fact]
    public void SameCourseIdAlwaysYieldsSameColor()
    {
        const string courseId = "CS101";
        var first = CourseColorAssigner.ForCourseId(courseId);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(first, CourseColorAssigner.ForCourseId(courseId));
        }
    }

    [Fact]
    public void IndexAlwaysInRange()
    {
        for (var i = 0; i < 500; i++)
        {
            var index = CourseColorAssigner.IndexForCourseId($"COURSE{i}");
            Assert.InRange(index, 0, 17);
        }
    }

    [Fact]
    public void EmptyCourseIdFallsBackToFirstColor()
    {
        Assert.Equal(CourseColorAssigner.Palette[0], CourseColorAssigner.ForCourseId(""));
    }

    [Fact]
    public void DistributionIsReasonablySpread()
    {
        // 不应所有课头号都撞到同一色。
        var used = Enumerable.Range(0, 300)
            .Select(i => CourseColorAssigner.IndexForCourseId($"X{i}"))
            .Distinct()
            .Count();
        Assert.True(used >= 15, $"only {used} distinct colors used");
    }
}

public class CourseTypeClassifierTests
{
    [Theory]
    // 公共基础必修 → B1
    [InlineData("公共基础必修", "计算机学院", "计算机学院", true)]
    // 通识必修 → B1
    [InlineData("通识必修", "计算机学院", "计算机学院", true)]
    // 本学院专业必修 → B1
    [InlineData("专业必修", "计算机学院", "计算机学院", true)]
    // 跨学院专业必修 → B2 而非 B1
    [InlineData("专业必修", "计算机学院", "数学与统计学院", false)]
    public void ClassifiesPrimaryCourses(string type, string user, string college, bool expected)
        => Assert.Equal(expected,
            CourseTypeClassifier.IsPrimaryCourse(type, user, college));

    [Theory]
    [InlineData("专业必修", "计算机学院", "数学与统计学院", true)]
    [InlineData("专业选修", "计算机学院", "数学与统计学院", true)]
    [InlineData("专业选修", "计算机学院", "计算机学院", false)]
    public void ClassifiesCrossMajorCourses(string type, string user, string college, bool expected)
        => Assert.Equal(expected,
            CourseTypeClassifier.IsOtherCollegeMajorCourse(type, user, college));

    [Fact]
    public void EmptyCollegeIsNeverCrossMajor()
    {
        // 复刻 ham-rn 行为：任一侧为空则 kua=false
        Assert.False(CourseTypeClassifier.IsCrossMajor("计算机学院", ""));
        Assert.False(CourseTypeClassifier.IsCrossMajor("", "计算机学院"));
        Assert.False(CourseTypeClassifier.IsCrossMajor(null, null));
    }

    [Fact]
    public void GroupAssignsExactlyOneBucket()
    {
        Assert.Equal(CourseCategoryGroup.Primary,
            CourseTypeClassifier.Group("公共基础必修", "计算机学院", "计算机学院"));
        Assert.Equal(CourseCategoryGroup.CrossMajor,
            CourseTypeClassifier.Group("专业必修", "计算机学院", "数学与统计学院"));
        Assert.Equal(CourseCategoryGroup.Other,
            CourseTypeClassifier.Group("专业选修", "计算机学院", "计算机学院"));
    }
}
