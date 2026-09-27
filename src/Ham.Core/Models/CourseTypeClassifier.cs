namespace Ham.Core.Models;

/// <summary>
/// 课程类别判定结果。
/// </summary>
/// <remarks>
/// 移植自 ham-rn <c>scorecalc/courseTypeInfo.ts</c>。
/// 判定依赖课程类别文本中是否含"公/专/通/必/选"，以及开课学院是否等于用户学院。
/// 跨专业（<c>kua</c>）判定规则：<b>任一侧为空则视为 false</b>。
/// </remarks>
public static class CourseTypeClassifier
{
    /// <summary>课程是否属于 B1（必修、通识）。</summary>
    /// <param name="courseType">课程类别文本，例如 "公共基础必修"。</param>
    /// <param name="userCollege">用户所在学院；用于判断"专"是否为跨专业。</param>
    /// <param name="courseCollege">开课学院。</param>
    public static bool IsPrimaryCourse(string? courseType, string? userCollege, string? courseCollege)
    {
        var (gong, zhuan, tong, bi, _) = Classify(courseType);
        var kua = IsCrossMajor(userCollege, courseCollege);

        if (gong && bi) return true;          // 公共基础必修
        if (tong && bi) return true;          // 通识必修
        if (!kua && zhuan && bi) return true; // 本学院专业必修
        return false;
    }

    /// <summary>课程是否属于 B2（选修、跨专业）。</summary>
    public static bool IsOtherCollegeMajorCourse(string? courseType, string? userCollege, string? courseCollege)
    {
        var (_, zhuan, _, bi, xuan) = Classify(courseType);
        var kua = IsCrossMajor(userCollege, courseCollege);

        if (kua && zhuan && bi) return true;  // 跨学院专业必修
        if (kua && zhuan && xuan) return true; // 跨学院专业选修
        return false;
    }

    /// <summary>课程归属的类别分组。</summary>
    public static CourseCategoryGroup Group(string? courseType, string? userCollege, string? courseCollege)
    {
        if (IsPrimaryCourse(courseType, userCollege, courseCollege)) return CourseCategoryGroup.Primary;
        if (IsOtherCollegeMajorCourse(courseType, userCollege, courseCollege)) return CourseCategoryGroup.CrossMajor;
        return CourseCategoryGroup.Other;
    }

    /// <summary>是否跨专业：开课学院与用户学院不同，且两侧都非空。</summary>
    public static bool IsCrossMajor(string? userCollege, string? courseCollege)
    {
        if (string.IsNullOrWhiteSpace(userCollege) || string.IsNullOrWhiteSpace(courseCollege)) return false;
        return !string.Equals(userCollege.Trim(), courseCollege.Trim(), StringComparison.Ordinal);
    }

    private static (bool Gong, bool Zhuan, bool Tong, bool Bi, bool Xuan) Classify(string? courseType)
    {
        var text = courseType ?? string.Empty;
        return (
            Gong: text.Contains('公'),
            Zhuan: text.Contains('专'),
            Tong: text.Contains('通'),
            Bi: text.Contains('必'),
            Xuan: text.Contains('选'));
    }
}

/// <summary>课程在综测计算中的分组。</summary>
public enum CourseCategoryGroup
{
    /// <summary>B1：必修、通识。</summary>
    Primary,

    /// <summary>B2：选修、跨专业。</summary>
    CrossMajor,

    /// <summary>其他（既非 B1 也非 B2）。</summary>
    Other,
}
