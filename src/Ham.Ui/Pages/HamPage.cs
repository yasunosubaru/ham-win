using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Library.Models;
using Ham.Ui.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace Ham.Ui.Pages;

/// <summary>
/// 页面基类：统一卡片外观、标题层级、以及「数据从哪来」的说明块。
/// </summary>
/// <remarks>
/// 刻意把「无数据」做成一种<b>明确的界面状态</b>，而不是留空白或塞假数据。
/// 每个分区都要能回答两个问题：现在显示的是什么数据、数据是谁给的。
/// </remarks>
public abstract class HamPage : Page
{
    protected AppState State { get; }

    protected HamPage(AppState state) => State = state;

    // ── 主题资源（随亮/暗自动切换，不写死颜色）──
    protected static Brush CardFill =>
        (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    protected static Brush Stroke =>
        (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];

    protected static Brush SubtleFill =>
        (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    protected static Brush Accent =>
        (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    // ── 构件 ──

    protected static TextBlock Title(string text, double size = 22) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };

    protected static TextBlock Body(string text, double opacity = 0.8) => new()
    {
        Text = text,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>圆角卡片。</summary>
    protected static Border Card(UIElement content, string? heading = null)
    {
        var panel = new StackPanel { Spacing = heading is null ? 0 : 8 };
        if (heading is not null) panel.Children.Add(Title(heading, 15));
        panel.Children.Add(content);
        return new Border
        {
            Child = panel,
            Background = CardFill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
        };
    }

    /// <summary>
    /// 数据来源说明条。这是整个 UI 里最重要的一个控件：
    /// 它让「哪些是真数据、哪些还没有」在任何分区都一目了然。
    /// </summary>
    protected static Border SourceNote(string text, bool warn = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = warn ? 1.0 : 0.75,
        };
        return new Border
        {
            Child = tb,
            Background = warn ? SubtleFill : CardFill,
            BorderBrush = warn ? Accent : Stroke,
            BorderThickness = warn ? new Thickness(0, 0, 2, 0) : new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
        };
    }

    /// <summary>键值一行。</summary>
    protected static Grid Field(string label, string value)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = Body(label, 0.65);
        l.FontSize = 13;
        Grid.SetColumn(l, 0);

        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        Grid.SetColumn(v, 1);

        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    /// <summary>整页骨架：标题 + 若干卡片 + 可滚动。</summary>
    protected void Compose(string title, params UIElement[] blocks)
    {
        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(Title(title));
        foreach (var b in blocks) root.Children.Add(b);
        Content = new ScrollViewer { Content = root };
    }

    /// <summary>统一的「去 WPF 版同步」指引。</summary>
    protected static Border SyncHint(string what) => SourceNote(
        $"尚未同步{what}。\n"
        + "请先运行 WPF 版（publish\\Ham.exe）的「设置 → 登录并同步」。"
        + "同步结果会写进 %LOCALAPPDATA%\\Ham\\appdata.json，本页面直接读同一份数据。\n"
        + "登录流程需要 CAS 与图形验证码，因此登录暂时留在已验证的 WPF 版中，"
        + "WinUI 3 先承接数据展示。", warn: true);
}
