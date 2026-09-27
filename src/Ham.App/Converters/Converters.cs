using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Ham.App.ViewModels;

namespace Ham.App.Converters;

/// <summary>布尔值转可见性。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>取反的布尔可见性。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>
/// 导航可见性：仅当 <c>Current</c> 等于 <c>ConverterParameter</c> 指定的分区时可见。
/// </summary>
public sealed class NavVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not NavSection current) return Visibility.Collapsed;
        if (parameter is NavSection wanted) return current == wanted ? Visibility.Visible : Visibility.Collapsed;

        // XAML 中 ConverterParameter 只能传字符串，故同时支持名称解析。
        if (parameter is string name && Enum.TryParse<NavSection>(name, out var parsed))
            return current == parsed ? Visibility.Visible : Visibility.Collapsed;

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>集合非空时可见。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count) return Visibility.Collapsed;
        var empty = string.Equals(parameter as string, "Empty", StringComparison.OrdinalIgnoreCase);
        return empty ? (count == 0 ? Visibility.Visible : Visibility.Collapsed)
                     : (count > 0 ? Visibility.Visible : Visibility.Collapsed);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串非空时可见。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 对象为 null 时可见；传入 <c>Inverse</c> 参数时反转为"非 null 时可见"。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inverse = string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null;

        if (inverse) isNull = !isNull;
        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 十六进制颜色字符串转画刷。
/// </summary>
/// <remarks>
/// 课程颜色在 Core 层以 <c>#RRGGBB</c> 文本形式持久化（便于 JSON 存储），
/// 这里在展示层转成 <see cref="SolidColorBrush"/>。遇到非法值时回退到中性灰，
/// 不抛异常——一条脏数据不应导致整页渲染失败。
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0)
            return new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        }
        catch (InvalidOperationException)
        {
            return new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 枚举值转中文标签。用于 <c>RecurrenceFrequency</c>、
/// <c>SportOrderStatus</c>、<c>BookingStatus</c> 等在界面上的展示。
/// </summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => string.Empty,
            Core.Models.RecurrenceFrequency f => f switch
            {
                Core.Models.RecurrenceFrequency.None => "不重复",
                Core.Models.RecurrenceFrequency.Daily => "每天",
                Core.Models.RecurrenceFrequency.Weekly => "每周",
                Core.Models.RecurrenceFrequency.Monthly => "每月",
                _ => f.ToString(),
            },
            Infrastructure.Library.Models.BookingStatus b => b switch
            {
                Infrastructure.Library.Models.BookingStatus.Reserved => "已预约",
                Infrastructure.Library.Models.BookingStatus.CheckedIn => "已入馆",
                Infrastructure.Library.Models.BookingStatus.Cancelled => "已取消",
                Infrastructure.Library.Models.BookingStatus.Expired => "已过期",
                _ => b.ToString(),
            },
            Infrastructure.Sport.Models.SportOrderStatus s => s switch
            {
                Infrastructure.Sport.Models.SportOrderStatus.PendingPayment => "待支付",
                Infrastructure.Sport.Models.SportOrderStatus.Paid => "已支付",
                Infrastructure.Sport.Models.SportOrderStatus.Completed => "已完成",
                Infrastructure.Sport.Models.SportOrderStatus.Cancelled => "已取消",
                Infrastructure.Sport.Models.SportOrderStatus.Expired => "已过期",
                _ => s.ToString(),
            },
            Core.Models.ComprehensiveScoreMethod m => m switch
            {
                Core.Models.ComprehensiveScoreMethod.NewF2 => "新版 F2 = B1 + B2 × 0.002",
                Core.Models.ComprehensiveScoreMethod.LegacyF2 => "旧版 F2 = B1 × 0.98 + B2 × 0.02",
                Core.Models.ComprehensiveScoreMethod.Script => "自定义脚本",
                _ => m.ToString(),
            },
            bool b => b ? "是" : "否",
            _ => value.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>提醒分钟数转中文标签（<c>null</c> 表示不提醒）。</summary>
public sealed class ReminderLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int minutes) return "不提醒";
        return minutes switch
        {
            <= 0 => "不提醒",
            < 60 => $"{minutes} 分钟前",
            _ when minutes % 60 == 0 => $"{minutes / 60} 小时前",
            _ => $"{minutes / 60} 小时 {minutes % 60} 分钟前",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>学期号转中文标签（1/2/3 → 第一/二/三学期）。</summary>
public sealed class SemesterLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int s when s is 1 or 2 or 3 => $"第 {s} 学期",
            Core.Models.Semester sem => $"{sem.Year} 学年第 {sem.SemesterNumber} 学期",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 依据"当前值 / 集合最大值"计算柱状图宽度。
/// </summary>
/// <remarks>
/// 传入两个值：当前数量、整个集合。集合为空或最大值为 0 时返回 0，
/// 避免除零产生 <see cref="double.NaN"/> 进而让 WPF 布局崩溃。
/// </remarks>
public sealed class BarWidthConverter : IMultiValueConverter
{
    private const double MaxBarWidth = 200;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not int count) return 0d;

        if (values[1] is not System.Collections.IEnumerable list) return 0d;

        var max = 0;
        foreach (var item in list)
        {
            if (item is Core.Models.ScoreBucket bucket && bucket.Count > max) max = bucket.Count;
        }

        if (max <= 0) return 0d;
        return MaxBarWidth * (double)count / max;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [Binding.DoNothing];
}

/// <summary>数值转粗体强调文本（用于展示 GPA、绩点等关键指标）。</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double score) return new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2E));

        // 依据 4.0 制绩点着色，让用户在列表里一眼看出强弱。
        var gpa = string.Equals(parameter as string, "Gpa", StringComparison.OrdinalIgnoreCase)
            ? score
            : Math.Clamp((score - 60) / 40.0 * 4.0, 0, 4);

        var color = gpa switch
        {
            >= 3.5 => Color.FromRgb(0x3F, 0x9C, 0x6D),
            >= 2.5 => Color.FromRgb(0x44, 0x78, 0xA8),
            >= 1.5 => Color.FromRgb(0xC8, 0x8A, 0x2E),
            _ => Color.FromRgb(0xC4, 0x52, 0x4A),
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
