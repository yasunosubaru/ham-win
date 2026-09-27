using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ham.Core.Models;

/// <summary>
/// 课程配色分配器。
/// </summary>
/// <remarks>
/// 移植自 ham-rn <c>src/business/education/course/color.ts</c>。
/// 关键细节：取 MD5 十六进制摘要的 <b>前 8 个字符</b>，取各自 ASCII 码，
/// <b>按十进制拼接</b>成一个大整数，再对 18 取模。
/// 注意是十进制拼接而非十六进制解析——换写法会导致同一课头号颜色全变。
/// </remarks>
public static class CourseColorAssigner
{
    /// <summary>18 色定长调色板（index 0..17）。</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#F3A5A5", "#D6A5E0", "#C7A5E6", "#B5A5E1", "#A5B9F3", "#A5D6F4",
        "#A5E3F4", "#A5E6E1", "#C7F3B5", "#D6F3A5", "#E6F3A5", "#F3F3A5",
        "#F3E6A5", "#F3D6A5", "#F3C7A5", "#C7B5A5", "#D6D6D6", "#B5C7D6",
    ];

    /// <summary>为课头号分配一个稳定颜色。相同课头号恒定得到相同颜色。</summary>
    public static string ForCourseId(string courseId)
    {
        if (string.IsNullOrEmpty(courseId)) return Palette[0];
        return Palette[IndexForCourseId(courseId)];
    }

    /// <summary>计算课头号对应的调色板下标。</summary>
    public static int IndexForCourseId(string courseId)
    {
        if (string.IsNullOrEmpty(courseId)) return 0;

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(courseId));
        var builder = new StringBuilder(8);
        for (var i = 0; i < 8 && i < hash.Length; i++)
        {
            builder.Append(hash[i].ToString(CultureInfo.InvariantCulture));
        }

        // 十进制拼接（可能超长，用 BigInteger 之外的 decimal 也不够宽），
        // 因此逐位取模，避免溢出。
        var remainder = 0;
        foreach (var ch in builder.ToString())
        {
            remainder = ((remainder * 10) + (ch - '0')) % Palette.Count;
        }

        return remainder < 0 ? remainder + Palette.Count : remainder;
    }
}
