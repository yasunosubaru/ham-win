using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Ui.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ham.Ui.Pages;

/// <summary>
/// 校巴页：真实数据，无需任何凭据。
/// </summary>
/// <remarks>
/// 这是唯一一个<b>既接真实数据、又不碰学生账号</b>的功能：
/// <c>bus.whu.edu.cn/mobile/index.html</c> 不在统一认证保护范围内，
/// 页面自己用内嵌的 RSA 私钥给接口路径签名，我们只旁路记录它的响应。
/// </remarks>
public sealed class BusPage : HamPage, IDisposable
{
    private readonly BusFetcher _fetcher = new();
    private readonly StackPanel _lines = new() { Spacing = 10 };
    private readonly TextBlock _status = Body("尚未获取。");
    private Button? _button;

    public BusPage(AppState state) : base(state)
    {
        _button = new Button { Content = "获取校巴数据" };
        _button.Click += async (_, _) => await RefreshAsync();

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_status);
        Grid.SetColumn(_button, 1);
        header.Children.Add(_button);

        Compose("校巴",
            Card(header),
            Card(_lines, "线路"),
            SourceNote(
                "数据来自 bus.whu.edu.cn 的真实接口（whubus/weben）。\n"
                + "该页不在统一认证保护范围内，因此这里不需要你的学号密码，"
                + "也不产生任何账号风险。\n"
                + "接口路径是 RSA 签名后的密文，由页面自己生成，本应用只读取响应。"));

        if (State.BusLines.Count > 0) Render(State.BusLines);
    }

    private async Task RefreshAsync()
    {
        if (_button is not null) _button.IsEnabled = false;
        Set("正在打开校巴页面并等待接口返回…");

        try
        {
            var lines = await _fetcher.FetchAsync(BusEndpoints.Lines);
            State.BusLines = lines;
            Set(lines.Count > 0
                ? $"已获取 {lines.Count} 条线路"
                : "接口没有返回线路数据（可能是线路改版或页面结构变化）");
            Render(lines);
        }
        catch (Exception ex)
        {
            Set("获取失败：" + ex.Message);
        }
        finally
        {
            if (_button is not null) _button.IsEnabled = true;
        }
    }

    private void Set(string text) => DispatcherQueue.TryEnqueue(() => _status.Text = text);

    private void Render(IReadOnlyList<BusLine> lines)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lines.Children.Clear();
            if (lines.Count == 0)
            {
                _lines.Children.Add(Body("没有数据。", 0.8));
                return;
            }

            foreach (var l in lines)
            {
                var panel = new StackPanel { Spacing = 3 };
                panel.Children.Add(Title($"{l.Name}　{l.RouteSummary}", 15));
                panel.Children.Add(Body(
                    $"运营 {l.ServiceHours}　票价 {l.Price} 元　"
                    + $"站点 {l.Stops.Count} 个　在线车辆 {l.OnlineBusCount}"));

                if (l.Stops.Count > 0)
                {
                    var stops = string.Join(" → ", l.Stops.Take(12).Select(s => s.Name));
                    if (l.Stops.Count > 12) stops += " …";
                    panel.Children.Add(Body(stops, 0.7));
                }

                _lines.Children.Add(new Border
                {
                    Child = panel,
                    Background = SubtleFill,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                });
            }
        });
    }

    public void Dispose()
    {
        // WinUI 3 的 WebView2 控件没有 Dispose；置空即可，
        // 宿主窗口随页面切换被回收。
        _fetcher.Release();
    }
}
