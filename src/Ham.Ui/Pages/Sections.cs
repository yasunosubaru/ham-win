using Ham.Core.Models;
using Ham.Infrastructure.Campus;
using Ham.Infrastructure.Library.Models;
using Ham.Ui.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ham.Ui.Pages;

/// <summary>状态页：真实天气 + 数据来源总览。</summary>
public sealed class StatusPage : HamPage
{
    private readonly Func<Task> _refresh;
    private readonly TextBlock _now = Title("", 28);
    private readonly TextBlock _detail = Body("");
    private readonly StackPanel _forecast = new() { Spacing = 6 };

    public StatusPage(AppState state, Func<Task> refresh) : base(state)
    {
        _refresh = refresh;

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(Title("珞珈山 · 实时天气", 15));
        left.Children.Add(_now);
        left.Children.Add(_detail);
        Grid.SetColumn(left, 0);
        header.Children.Add(left);

        var btn = new Button { Content = "刷新", VerticalAlignment = VerticalAlignment.Center };
        btn.Click += async (_, _) => await _refresh();
        Grid.SetColumn(btn, 1);
        header.Children.Add(btn);

        Compose("状态",
            Card(header),
            Card(_forecast, "未来三天"),
            Card(BuildSyncOverview()),
            SourceNote("天气来自 Open-Meteo 公共 API（无需认证）。"
                    + "教务、图书馆数据来自 WPF 版同步后写入的 appdata.json。"));

        if (State.Weather is { } w) Apply(w);
    }

    private UIElement BuildSyncOverview()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Field("教务", State.SyncSummary));
        panel.Children.Add(Field("课程", $"{State.Courses.Count} 门"));
        panel.Children.Add(Field("成绩", $"{State.Scores.Count} 条"));
        panel.Children.Add(Field("日程", $"{State.Schedules.Count} 项"));
        panel.Children.Add(Field("图书馆预约", $"{State.LibraryBookings.Count} 条"));
        panel.Children.Add(Field("校巴线路", $"{State.BusLines.Count} 条"));
        if (State.LoadError is { } err)
        {
            panel.Children.Add(SourceNote("读取 appdata.json 时出现问题：" + err, warn: true));
        }
        return panel;
    }

    public void Apply(WeatherReport r)
    {
        _now.Text = $"{r.Condition}  {r.TemperatureText}";
        _detail.Text =
            $"体感 {r.FeelsLikeCelsius:F0}°C · 湿度 {r.HumidityPercent}% · {r.WindDirection} {r.WindSpeedKmh:F1} km/h"
            + (r.HasPrecipitation ? $"\n降水 {r.PrecipitationMm:F1} mm" : "")
            + $"\n{r.GetAdvice()}\n观测于 {r.ObservedAt}";

        _forecast.Children.Clear();
        foreach (var f in r.Forecast)
        {
            _forecast.Children.Add(Field(f.DateLabel, $"{f.Condition} {f.Icon}  {f.TemperatureText}"));
        }
    }
}

/// <summary>课程页：按周几排布的课表网格。</summary>
public sealed class CoursePage : HamPage
{
    public CoursePage(AppState state) : base(state)
    {
        var courses = State.Courses;
        var blocks = new List<UIElement>();

        if (courses.Count == 0)
        {
            blocks.Add(SyncHint("课表"));
        }
        else
        {
            blocks.Add(Card(BuildGrid(courses), $"本周课表（{courses.Count} 门）"));
            blocks.Add(Card(BuildDetail(courses)));
            blocks.Add(ConflictCard(courses));
        }

        blocks.Add(SourceNote("课表来自教务系统 kbcx/xskbcx_cxXsgrkb.html，"
            + "经已登录页面代取后由 EducationParser 解析（实测 20 门课、32601 字节）。"));
        Compose("课程", blocks.ToArray());
    }

    private UIElement BuildGrid(IReadOnlyList<Course> courses)
    {
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 10 };
        for (var i = 0; i < 7; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 8; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 星期表头。用本地表而不是 SemesterCalendar 的辅助方法，
        // 免得页头依赖一个和课表无关的工具类。
        string[] days = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

        for (var d = 0; d < 7; d++)
        {
            var h = Title(days[d], 12);
            h.HorizontalAlignment = HorizontalAlignment.Center;
            h.Opacity = 0.7;
            Grid.SetColumn(h, d);
            grid.Children.Add(h);
        }

        foreach (var c in courses)
        {
            var col = Math.Clamp(c.Weekday - 1, 0, 6);
            var row = Math.Clamp(c.ClassFrom, 1, 7);

            var tb = new TextBlock
            {
                Text = c.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            var sub = new TextBlock
            {
                Text = $"{c.ClassFrom}-{c.ClassTo}节\n{c.Location}",
                FontSize = 10,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            };

            var cell = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
            cell.Children.Add(tb);
            cell.Children.Add(sub);

            var border = new Border
            {
                Child = cell,
                Background = SubtleFill,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
            };
            Grid.SetColumn(border, col);
            Grid.SetRow(border, row);
            grid.Children.Add(border);
        }

        return grid;
    }

    private static UIElement BuildDetail(IReadOnlyList<Course> courses)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var c in courses)
        {
            panel.Children.Add(Field(c.Name, $"{c.RawWeekText}  ·  {c.Location}"));
        }
        return panel;
    }

    private static UIElement ConflictCard(IReadOnlyList<Course> courses)
    {
        var conflicts = ConflictDetector.Detect(courses);
        if (conflicts.Count == 0)
        {
            return Card(Body("未检测到时间冲突。", 0.8), "冲突检查");
        }

        var panel = new StackPanel { Spacing = 4 };
        foreach (var c in conflicts)
        {
            panel.Children.Add(Body($"· {c}", 0.9));
        }
        return Card(panel, $"冲突检查（{conflicts.Count} 处）");
    }
}

/// <summary>日程页：日程分组与最近日程。</summary>
public sealed class SchedulePage : HamPage
{
    public SchedulePage(AppState state) : base(state)
    {
        var blocks = new List<UIElement>();
        var items = State.Schedules;

        if (items.Count == 0)
        {
            blocks.Add(SyncHint("日程"));
        }
        else
        {
            var timeline = new ScheduleTimeline(items);
            var nearest = timeline.NearestUpcoming(DateTime.Now);
            if (nearest is { } n)
            {
                blocks.Add(Card(new StackPanel
                {
                    Children =
                    {
                        Field("标题", n.Title),
                        Field("开始", n.Start.ToString("yyyy-MM-dd HH:mm")),
                        Field("结束", n.End?.ToString("yyyy-MM-dd HH:mm") ?? "—"),
                        Field("地点", string.IsNullOrEmpty(n.Location) ? "—" : n.Location),
                    },
                }, "最近日程"));
            }

            if (State.ScheduleGroups.Count > 0)
            {
                var g = new StackPanel { Spacing = 6 };
                foreach (var grp in State.ScheduleGroups.OrderBy(x => x.Order))
                {
                    var count = items.Count(i => i.GroupId == grp.Id);
                    g.Children.Add(Field($"{grp.Icon} {grp.Name}", $"{count} 项"));
                }
                blocks.Add(Card(g, "分组"));
            }

            var list = new StackPanel { Spacing = 6 };
            foreach (var it in items.OrderBy(i => i.Start).Take(50))
            {
                list.Children.Add(Field(it.Start.ToString("MM-dd HH:mm"),
                    it.Title + (string.IsNullOrEmpty(it.Location) ? "" : $"  @ {it.Location}")));
            }
            blocks.Add(Card(list, $"全部日程（{items.Count} 项）"));
        }

        blocks.Add(SourceNote("日程由用户自建，保存在 appdata.json，不来自学校系统。"));
        Compose("日程", blocks.ToArray());
    }
}

/// <summary>成绩页：真实成绩 + 武大 4.0 制绩点。</summary>
public sealed class ScorePage : HamPage
{
    public ScorePage(AppState state) : base(state)
    {
        var records = State.Scores;
        var blocks = new List<UIElement>();

        if (records.Count == 0)
        {
            blocks.Add(SyncHint("成绩"));
            blocks.Add(Card(Body(
                "成绩查询需要图形验证码（sfxyyzm=1 → popupCaptcha）。\n"
                + "该验证码由顶象 SDK 提供，校方明确作为反自动化手段，"
                + "本应用不做绕过，只在你手动完成验证后读取结果。"), "为什么需要手动验证"));
        }
        else
        {
            var scale = GpaScale.Whu4_0;
            blocks.Add(Card(new StackPanel
            {
                Children =
                {
                    Field("门数", records.Count.ToString()),
                    Field("加权均分", ScoreCalculator.WeightedAverage(records).ToString("F1")),
                    Field("GPA", ScoreCalculator.Gpa(records, scale).ToString("F2")),
                    Field("绩点口径", scale.Name),
                    Field("已获学分", ScoreCalculator.EarnedCredit(records).ToString("F1")),
                },
            }, "总览"));

            var bySem = new StackPanel { Spacing = 6 };
            foreach (var s in ScoreCalculator.BySemester(records, scale))
            {
                bySem.Children.Add(Field(s.SemesterText, $"{s.GpaText}  {s.AverageText}"));
            }
            blocks.Add(Card(bySem, "按学期"));

            var dist = new StackPanel { Spacing = 4 };
            foreach (var b in ScoreCalculator.Distribution(records))
            {
                dist.Children.Add(Field($"{b.From}–{(b.To == 100 ? 100 : b.To)} 分", $"{b.Count} 门"));
            }
            blocks.Add(Card(dist, "分数分布"));

            var list = new StackPanel { Spacing = 6 };
            foreach (var r in records)
            {
                list.Children.Add(Field(r.Name, $"{r.Score:F0} 分  ·  {r.Credit:F1} 学分"));
            }
            blocks.Add(Card(list, $"成绩明细（{records.Count} 条）"));
        }

        blocks.Add(SourceNote("成绩由教务系统成绩页解析而来。绩点换算采用 "
            + GpaScale.Whu4_0.Name + "（60 分计 1.5 绩点，90 分计 4.0）。"));
        Compose("成绩", blocks.ToArray());
    }
}

/// <summary>图书馆页：预约记录与常用座位。</summary>
public sealed class LibraryPage : HamPage
{
    public LibraryPage(AppState state) : base(state)
    {
        var blocks = new List<UIElement>();
        var bookings = State.LibraryBookings;

        if (bookings.Count == 0)
        {
            blocks.Add(SyncHint("图书馆数据"));
        }
        else
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var b in bookings.OrderByDescending(x => x.Start))
            {
                list.Children.Add(Field(
                    b.Start.ToString("yyyy-MM-dd HH:mm"),
                    $"{b.RoomName}  {b.SeatLabel}  [{b.Status}]"));
            }
            blocks.Add(Card(list, $"预约记录（{bookings.Count} 条）"));
        }

        if (State.PreferredSeats.Count > 0)
        {
            var seats = new StackPanel { Spacing = 4 };
            foreach (var s in State.PreferredSeats)
            {
                seats.Children.Add(Field(s.Label, s.Features.Count > 0 ? string.Join(" / ", s.Features) : "—"));
            }
            blocks.Add(Card(seats, "常用座位"));
        }

        blocks.Add(SourceNote(
            "图书馆接口契约已按线上 app.*.js 核实（HMAC-SHA256 + AES）。"
            + "登录换票仍需 CAS，因此查询入口暂时留在 WPF 版；本页显示的是同步回来的结果。"));
        Compose("图书馆", blocks.ToArray());
    }
}

/// <summary>运动场馆页：明确标注尚无公开契约。</summary>
public sealed class SportPage : HamPage
{
    public SportPage(AppState state) : base(state)
    {
        Compose("运动场馆",
            Card(Body(
                "尚未实现。\n\n"
                + "原因：没有找到任何公开接口契约。iOS 端独立探测的结论与此一致——"
                + "未探到独立入口。校方未开放预约接口，页面形态与校巴、图书馆都不同。\n\n"
                + "在拿到真实契约之前，这里不会填入任何编造的数据。"
                + "摆一堆看起来像真的场馆与时段，比留空白更有害："
                + "它会让人误以为功能已经实现。"), "状态"),
            SourceNote("WPF 版此分区目前使用 DemoData，界面上带橙色提示条。"
                + "WinUI 3 连占位数据都不放。", warn: true));
    }
}

/// <summary>给分（教师评教）页：明确标注契约未探明。</summary>
public sealed class RatingPage : HamPage
{
    public RatingPage(AppState state) : base(state)
    {
        Compose("教师评教 / 给分",
            Card(Body(
                "尚未实现。\n\n"
                + "已探明的部分：入口是\n"
                + "  jwgl.whu.edu.cn/xtgl/index_cxDddz.html?type=jser"
                + "&url=https%3A%2F%2Fugsqs.whu.edu.cn%2Fcaslogin\n"
                + "它会 302 到 cas.whu.edu.cn/authserver/login?service=...%2Fcaslogin%2F，"
                + "即复用同一个 CAS，只是 service 不同。\n\n"
                + "卡在哪：CAS 侧只留下 happyVoyage，没有 CASTGC/TGC，"
                + "所以「登录一次就免密进评教」在当前实现下不成立，"
                + "必须带 TGC 为 ugsqs 单独登录一次。\n\n"
                + "在拿到真实接口契约之前，这里不放任何编造的评价数据。"), "状态"),
            SourceNote("WPF 版此分区目前使用 DemoData，界面上带橙色提示条。", warn: true));
    }
}

/// <summary>设置页：读写 appdata.json 里的设置。</summary>
public sealed class SettingsPage : HamPage
{
    public SettingsPage(AppState state) : base(state)
    {
        var s = State.Settings;
        var blocks = new List<UIElement>
        {
            Card(new StackPanel
            {
                Children =
                {
                    Field("学年", s.SemesterYear.ToString()),
                    Field("学期", s.SemesterNumber.ToString()),
                    Field("开学日期", s.SemesterStartDate),
                    Field("总周数", s.TotalWeeks.ToString()),
                },
            }, "学期"),
            Card(new StackPanel
            {
                Children =
                {
                    Field("绩点口径", s.GpaScaleName),
                    Field("课程提前提醒", s.EnableCourseReminder ? $"{s.CourseReminderMinutes} 分钟" : "关闭"),
                    Field("日程提醒", s.EnableScheduleReminder ? "开启" : "关闭"),
                    Field("通知总开关", s.EnableNotifications ? "开启" : "关闭"),
                    Field("主题", s.Theme),
                },
            }, "偏好"),
            Card(new StackPanel
            {
                Children =
                {
                    Field("数据文件", "%LOCALAPPDATA%\\Ham\\appdata.json"),
                    Field("上次教务同步", State.LastEducationSync?.ToString("yyyy-MM-dd HH:mm") ?? "—"),
                    Field("版本", "Ham.Ui · WinUI 3"),
                },
            }, "关于"),
            SourceNote("本页面读取的设置与 WPF 版是同一份 appdata.json，"
                + "两边改动会互相覆盖。设置项的编辑界面仍在 WPF 版中，"
                + "因为写盘要经过已验证的 DataStore 原子写入路径。"),
        };

        Compose("设置", blocks.ToArray());
    }
}
