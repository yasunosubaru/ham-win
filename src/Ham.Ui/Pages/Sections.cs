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

        // 没有天气时立刻给出说明。以前这里只写 if (State.Weather is { } w) Apply(w)，
        // 于是网络不通就摆两张**空卡片**——标题写着「实时天气」「未来三天」，
        // 底下什么都没有，看着像坏了。
        if (State.Weather is { } w) Apply(w);
        else ShowWeatherUnavailable();
    }

    /// <summary>天气还没拿到（或拿失败）时的占位说明。</summary>
    public void ShowWeatherUnavailable(string? reason = null)
    {
        _now.Text = "暂未获取";
        _now.FontSize = 22;
        _detail.Text = reason is null
            ? "正在获取珞珈山实时天气…\n若长时间无变化，多半是网络不可达；点右侧「刷新」重试。"
            : reason;
        _detail.Opacity = 0.75;

        _forecast.Children.Clear();
        _forecast.Children.Add(Body("暂无预报数据。", 0.7));
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
        // 复位：ShowWeatherUnavailable 把字号调小了，拿到真数据后要还原
        _now.FontSize = 28;
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
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 8 };

        for (var d = 0; d < 7; d++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                // 窄窗口下不要把七列压扁——课名会挤成一团、右侧直接被裁掉。
                // 有了 MinWidth，外层 ScrollViewer 才会出现横向滚动条。
                MinWidth = 150,
            });
        }

        // 行 0 是星期表头，行 1..7 对应起始节次。
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

        // 关键：同一「星期 × 起始节次」的课必须收进**同一个格子**。
        // 以前每门课各自作为并列子元素被塞进相同的 row/column，坐标完全一致，
        // 于是几个课名被画在同一位置，叠成「张翠余极想郑伸四特色社会主义理论概论」
        // 那样的一团；1-2 节的课也只占一行，看不出它连上两节。
        var slots = courses
            .GroupBy(c => (Day: Math.Clamp(c.Weekday - 1, 0, 6), From: Math.Clamp(c.ClassFrom, 1, 7)))
            .OrderBy(g => g.Key.Day)
            .ThenBy(g => g.Key.From);

        foreach (var slot in slots)
        {
            var stack = new StackPanel { Spacing = 4 };
            foreach (var c in slot.OrderBy(c => c.ClassTo).ThenBy(c => c.Name, StringComparer.Ordinal))
                stack.Children.Add(CourseChip(c));

            var border = new Border
            {
                Child = stack,
                Background = SubtleFill,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
            };

            Grid.SetColumn(border, slot.Key.Day);
            Grid.SetRow(border, slot.Key.From);

            // 刻意**不**用 Grid.SetRowSpan。
            // 跨行 + Auto 行高在 WinUI 里会踩这个坑：行高是按同一行**其它列**的内容
            // 算出来的，跨行子元素分不到足够高度，StackPanel 里的课就溢出并互相压字
            // （实测周一 6-8 节、周三 6-8 节都糊成一团）。
            // 改成「按起始节次落格 + 每门课有 MinHeight」：
            // 行高由该格 StackPanel 的实际内容撑开，结构上不可能重叠。
            // 连上几节的信息由 CourseChip 里的「1-2节」文字承担。

            grid.Children.Add(border);
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            // 纵向滚动交给页面最外层的 ScrollViewer，这里必须禁掉，
            // 否则鼠标滚轮在内嵌区域里滚不动整页。
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }

    /// <summary>课表格子里的一门课。</summary>
    private static UIElement CourseChip(Course c)
    {
        var name = new TextBlock
        {
            Text = c.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var period = c.ClassFrom == c.ClassTo
            ? $"{c.ClassFrom}节"
            : $"{c.ClassFrom}-{c.ClassTo}节";
        var meta = new TextBlock
        {
            Text = $"{period} · {c.Location}",
            FontSize = 10,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        var inner = new StackPanel { Spacing = 1 };
        inner.Children.Add(name);
        inner.Children.Add(meta);

        // 固定最小高度：单门课的格子也要有块头，
        // 且高度可预期，行高计算才稳定。
        return new Border
        {
            Child = inner,
            MinHeight = 46,
            Padding = new Thickness(2, 0, 2, 0),
        };
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

/// <summary>成绩页：真实成绩 + 可切换的绩点口径。</summary>
public sealed class ScorePage : HamPage
{
    public ScorePage(AppState state) : base(state)
    {
        var records = State.Scores;
        var blocks = new List<UIElement>();

        if (records.Count == 0)
        {
            blocks.Add(SyncHint("成绩"));
            // 这段说明以前写的是「需要图形验证码，本应用不做绕过」——
            // 那是当时的结论，现已不成立：实测服务端只校验 validate 非空，
            // 滑块是纯客户端的一道门。留着旧文案会让用户以为必须手动过验证码。
        }
        else
        {
            // 口径由设置决定，与 WPF 走同一个解析入口。
            // 原来这里硬编码 Whu4_0，导致同一份 27 门课 WinUI 显示 3.68、
            // WPF 显示 3.222——界面上同时出现两个 GPA 比算错更糟。
            var scale = GpaScale.Resolve(State.Settings.GpaScaleName);
            blocks.Add(Card(new StackPanel
            {
                Children =
                {
                    Field("门数", records.Count.ToString()),
                    Field("加权均分", ScoreCalculator.WeightedAverage(records).ToString("F2")),
                    // F3 与 WPF 版 ScoreViewModel.GpaText 一致；
                    // 两边显示位数不同会让同一个人以为自己有两个 GPA。
                    Field("GPA", ScoreCalculator.Gpa(records, scale).ToString("F3")),
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
            + GpaScale.Resolve(State.Settings.GpaScaleName).Description + "（可在设置中切换口径）。"));
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

/// <summary>设置页：凭据输入、登录并同步、绩点口径、其它偏好。</summary>
public sealed class SettingsPage : HamPage
{
    private readonly TextBlock _syncNote = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.75,
    };

    public SettingsPage(AppState state) : base(state)
    {
        var s = State.Settings;
        var presets = GpaScale.Presets;

        // ── 凭据 + 同步 ──
        var idBox = new TextBox
        {
            Text = s.StudentId ?? string.Empty,
            PlaceholderText = "学号",
            MinWidth = 240,
        };
        var pwdBox = new PasswordBox
        {
            Password = s.PortalPassword ?? string.Empty,
            PlaceholderText = "信息门户密码",
            MinWidth = 240,
        };

        var syncButton = new Button { Content = "登录并同步", MinWidth = 120 };
        syncButton.Click += async (_, _) => await RunSyncAsync(idBox, pwdBox, syncButton);

        if (State.LastSyncMessage is { } last) _syncNote.Text = last;

        var account = new StackPanel { Spacing = 8 };
        account.Children.Add(LabelledRow("学号", idBox));
        account.Children.Add(LabelledRow("密码", pwdBox));
        account.Children.Add(LabelledRow(string.Empty, syncButton));
        account.Children.Add(_syncNote);

        // 展示的是**解析后的**口径名，不是存储里的原始字符串。
        // 存储里可能是历史遗留的英文 "Standard 4.0"，直接显示会让人以为出了错。
        var current = GpaScale.Resolve(s.GpaScaleName);
        var selected = Math.Max(0, presets.ToList().FindIndex(p => p.Name == current.Name));

        // 先声明说明文字：lambda 里要用到它，必须在词法上先于引用出现。
        var scaleNote = new TextBlock
        {
            Text = "改动会立即写回 appdata.json，并同时影响 WPF 版的成绩页。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var scaleBox = Choice(
            presets.Select(p => $"{p.Name} — {p.Description}").ToList(),
            selected,
            async i =>
            {
                s.GpaScaleName = presets[i].Name;
                try
                {
                    // 走 AppState 已有的写盘路径（DataStore 原子写入），
                    // 不要在 UI 层自己碰文件。
                    await State.SaveSettingsAsync();
                }
                catch (Exception ex)
                {
                    // 写盘失败必须让用户知道，否则他以为改成功了
                    scaleNote.Text = "⚠ 保存失败：" + ex.Message;
                    scaleNote.Opacity = 1.0;
                    return;
                }

                scaleNote.Text = "已保存。WPF 版同步使用此口径——两个界面读同一份 appdata.json。";
                scaleNote.Opacity = 1.0;
            });

        var blocks = new List<UIElement>
        {
            Card(account, "信息门户"),

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
                Spacing = 8,
                Children =
                {
                    LabelledRow("绩点口径", scaleBox),
                    scaleNote,
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
            SourceNote("凭据与学号仅保存在本机 %LOCALAPPDATA%\\Ham\\appdata.json，"
                + "不写入日志、不上传。密码以明文存放，共享电脑上用完请及时清理。\n"
                + "本页面读写的设置与 WPF 版是同一份 appdata.json，两边会互相覆盖；"
                + "两个应用都能独立完成登录与同步。"),
        };

        Compose("设置", blocks.ToArray());
    }

    private async Task RunSyncAsync(TextBox idBox, PasswordBox pwdBox, Button button)
    {
        // 先把输入写回设置：AppState.SyncEducationAsync 从 Settings 读凭据。
        State.Settings.StudentId = idBox.Text.Trim();
        State.Settings.PortalPassword = pwdBox.Password;

        button.IsEnabled = false;
        button.Content = "正在登录…";
        _syncNote.Text = "即将打开信息门户登录页，请在窗口中完成登录。";
        _syncNote.Opacity = 1.0;

        try
        {
            var (ok, message) = await State.SyncEducationAsync();
            _syncNote.Text = message;
            _syncNote.Opacity = ok ? 1.0 : 1.0;

            if (ok) Refresh();
        }
        catch (Exception ex)
        {
            _syncNote.Text = "同步失败：" + ex.Message;
            _syncNote.Opacity = 1.0;
            Ham.Infrastructure.Logging.Log.Error("设置页同步异常", ex);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = "登录并同步";
        }
    }

    /// <summary>重新构建本页（同步成功后刷新各项显示）。</summary>
    private void Refresh()
    {
        var s = State.Settings;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "设置", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    SourceNote($"已同步：{State.Courses.Count} 门课程、{State.Scores.Count} 条成绩"
                               + (State.LastEducationSync is { } at ? $"（{at:yyyy-MM-dd HH:mm}）" : "")),
                },
            },
        };
        _ = s;
    }
}
