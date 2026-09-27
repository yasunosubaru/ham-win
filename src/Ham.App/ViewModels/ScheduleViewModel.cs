using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;

namespace Ham.App.ViewModels;

/// <summary>日程页：最近日程、分组筛选、增删改。</summary>
public sealed class ScheduleViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private ScheduleItem? _selected;
    private ScheduleEditorViewModel? _editor;
    private string? _activeGroupId;
    private bool _showOnlyUpcoming;

    public ScheduleViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        AddCommand = new RelayCommand(BeginAdd);
        EditCommand = new RelayCommand(BeginEdit);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => Editor = null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        ToggleGroupCommand = new RelayCommand(p => ActiveGroupId = p as string);
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand ToggleGroupCommand { get; }

    public ObservableCollection<ScheduleOccurrence> Occurrences { get; } = [];

    public ObservableCollection<ScheduleGroup> Groups { get; } = [];

    public ScheduleOccurrence? Nearest { get; private set; }

    public string? ActiveGroupId
    {
        get => _activeGroupId;
        set
        {
            if (SetProperty(ref _activeGroupId, value)) Rebuild();
        }
    }

    public bool ShowOnlyUpcoming
    {
        get => _showOnlyUpcoming;
        set
        {
            if (SetProperty(ref _showOnlyUpcoming, value)) Rebuild();
        }
    }

    public ScheduleItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(SelectedDetail));
        }
    }

    public bool IsEditing => _editor is not null;

    public ScheduleEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            _editor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditing));
        }
    }

    public string SelectedDetail
    {
        get
        {
            if (Selected is null) return string.Empty;
            var lines = new List<string>
            {
                $"时间 {Selected.Start:yyyy-MM-dd HH:mm}"
                + (Selected.End is { } e ? $" – {e:HH:mm}" : string.Empty),
            };

            if (!string.IsNullOrWhiteSpace(Selected.Location)) lines.Add($"地点 {Selected.Location}");
            if (!string.IsNullOrWhiteSpace(Selected.Note)) lines.Add($"备注 {Selected.Note}");
            if (Selected.ReminderMinutes is { } r) lines.Add($"提前 {r} 分钟提醒");
            if (Selected.Recurrence.IsRepeating) lines.Add("重复：" + DescribeRecurrence(Selected.Recurrence));
            if (!string.IsNullOrWhiteSpace(Selected.LinkedCourseName)) lines.Add($"关联课程 {Selected.LinkedCourseName}");

            return string.Join("\n", lines);
        }
    }

    public static string DescribeRecurrence(RecurrenceRule rule) => rule.Frequency switch
    {
        RecurrenceFrequency.Daily => "每天",
        RecurrenceFrequency.Weekly when rule.WeekInterval > 1 =>
            $"每 {rule.WeekInterval} 周的{string.Join("、", rule.WeeklyDays.Select(d => "周" + ConflictDetector.DayOfWeekLabel(d)))}",
        RecurrenceFrequency.Weekly =>
            "每周的" + string.Join("、", rule.WeeklyDays.Count > 0
                ? rule.WeeklyDays.Select(d => "周" + ConflictDetector.DayOfWeekLabel(d))
                : ["指定日"]),
        RecurrenceFrequency.Monthly => rule.MonthlyByDayOfWeek ? "每月同一星期" : "每月同一日期",
        _ => "不重复",
    };

    public void Refresh()
    {
        StatusViewModel.Replace(Groups, _service.ScheduleGroups);
        Rebuild();
    }

    private void Rebuild()
    {
        var timeline = _service.BuildTimeline();
        var now = DateTime.Now;

        Nearest = timeline.NearestUpcoming(now);

        var from = ShowOnlyUpcoming ? now : now.AddDays(-30);
        var to = now.AddDays(90);

        var list = timeline.Occurrences(from, to);

        if (ActiveGroupId is { } groupId)
        {
            list = list.Where(o => o.Item.GroupId == groupId).ToList();
        }

        StatusViewModel.Replace(Occurrences, list.Take(200));
        OnPropertyChanged(nameof(Nearest));
    }

    private void BeginAdd() => Editor = new ScheduleEditorViewModel(_service.ScheduleGroups.ToList(), null);

    private void BeginEdit()
    {
        if (Selected is null)
        {
            _main.ReportError("请先选择一条日程。");
            return;
        }

        Editor = new ScheduleEditorViewModel(_service.ScheduleGroups.ToList(), Selected);
    }

    private async Task SaveAsync()
    {
        if (Editor is null) return;

        if (!Editor.TryBuildItem(out var item, out var error) || item is null)
        {
            _main.ReportError(error);
            return;
        }

        // 记录是不可变类型：编辑时按 Id 定位并整体替换，而不是原地修改。
        var index = _service.Schedules.IndexOf(
            _service.Schedules.FirstOrDefault(s => s.Id == item.Id)!);
        if (index >= 0) _service.Schedules[index] = item;
        else _service.Schedules.Add(item);

        Editor = null;
        Selected = item;
        Rebuild();
        await _main.CommitAsync($"已保存日程「{item.Title}」。");
    }

    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var title = Selected.Title;
        _service.Schedules.Remove(Selected);
        Selected = null;
        Rebuild();
        await _main.CommitAsync($"已删除日程「{title}」。");
    }
}

/// <summary>日程编辑面板。</summary>
public sealed class ScheduleEditorViewModel : ObservableObject
{
    private readonly ScheduleItem? _original;

    private string _title = string.Empty;
    private string _location = string.Empty;
    private string _note = string.Empty;
    private DateTime _start = DateTime.Now.AddHours(1);
    private bool _hasEnd;
    private DateTime _end = DateTime.Now.AddHours(2);
    private string? _groupId;
    private int? _reminderMinutes;
    private RecurrenceFrequency _frequency = RecurrenceFrequency.None;
    private string _linkedCourseId = string.Empty;
    private string _linkedCourseName = string.Empty;
    private int _weekInterval = 1;
    private bool _monthlyByDayOfWeek;
    private bool _hasEndDate;
    private DateOnly _endDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(3));

    public ScheduleEditorViewModel(IReadOnlyList<ScheduleGroup> groups, ScheduleItem? original)
    {
        Groups = groups;
        _original = original;

        if (original is not null)
        {
            _title = original.Title;
            _location = original.Location;
            _note = original.Note;
            _start = original.Start;
            _hasEnd = original.End is not null;
            _end = original.End ?? original.Start.AddHours(1);
            _groupId = original.GroupId;
            _reminderMinutes = original.ReminderMinutes;
            _frequency = original.Recurrence.Frequency;
            _linkedCourseId = original.LinkedCourseId ?? string.Empty;
            _linkedCourseName = original.LinkedCourseName ?? string.Empty;
            _weekInterval = original.Recurrence.WeekInterval;
            _monthlyByDayOfWeek = original.Recurrence.MonthlyByDayOfWeek;
            _hasEndDate = original.Recurrence.EndDate is not null;
            _endDate = original.Recurrence.EndDate ?? DateOnly.FromDateTime(DateTime.Today.AddMonths(3));
        }
    }

    public IReadOnlyList<ScheduleGroup> Groups { get; }

    public IReadOnlyList<Course> AvailableCourses { get; set; } = [];

    public IReadOnlyList<int> ReminderOptions { get; } = [10, 30, 60, 120, 240, 480];

    public string ReminderOptionsLabel => "不提醒";
    public IReadOnlyList<int?> ReminderChoices { get; } = [null, 10, 30, 60, 120, 240, 480];

    public IReadOnlyList<RecurrenceFrequency> Frequencies { get; } =
        [RecurrenceFrequency.None, RecurrenceFrequency.Daily, RecurrenceFrequency.Weekly, RecurrenceFrequency.Monthly];

    public IReadOnlyList<int> Weekdays { get; } = [0, 1, 2, 3, 4, 5, 6];

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public DateTime Start
    {
        get => _start;
        set
        {
            if (!SetProperty(ref _start, value)) return;
            if (!_hasEnd) _end = value.AddHours(1);
            OnPropertyChanged(nameof(End));
        }
    }

    public bool HasEnd
    {
        get => _hasEnd;
        set => SetProperty(ref _hasEnd, value);
    }

    public DateTime End
    {
        get => _end;
        set => SetProperty(ref _end, value);
    }

    public string? GroupId
    {
        get => _groupId;
        set => SetProperty(ref _groupId, value);
    }

    public int? ReminderMinutes
    {
        get => _reminderMinutes;
        set => SetProperty(ref _reminderMinutes, value);
    }

    public RecurrenceFrequency Frequency
    {
        get => _frequency;
        set => SetProperty(ref _frequency, value);
    }

    public int WeekInterval
    {
        get => _weekInterval;
        set => SetProperty(ref _weekInterval, value);
    }

    public bool MonthlyByDayOfWeek
    {
        get => _monthlyByDayOfWeek;
        set => SetProperty(ref _monthlyByDayOfWeek, value);
    }

    public bool HasEndDate
    {
        get => _hasEndDate;
        set => SetProperty(ref _hasEndDate, value);
    }

    public DateOnly EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    public string LinkedCourseId
    {
        get => _linkedCourseId;
        set => SetProperty(ref _linkedCourseId, value);
    }

    public string LinkedCourseName
    {
        get => _linkedCourseName;
        set => SetProperty(ref _linkedCourseName, value);
    }

    public string CourseChoicesLabel
    {
        get => AvailableCourses.Count == 0 ? "暂无可关联课程" : "不关联课程";
        set { }
    }

    public bool TryBuildItem(out ScheduleItem? item, out string error)
    {
        item = null;

        var end = HasEnd ? End : (DateTime?)null;
        if (ScheduleItem.Validate(Title, Start, end) is { } problem)
        {
            error = problem;
            return false;
        }

        var rule = new RecurrenceRule
        {
            Frequency = Frequency,
            WeekInterval = Math.Max(1, WeekInterval),
            WeeklyDays = Frequency == RecurrenceFrequency.Weekly
                ? [(int)Start.DayOfWeek]
                : [],
            MonthlyByDayOfWeek = MonthlyByDayOfWeek,
            EndDate = HasEndDate ? EndDate : null,
        };

        item = new ScheduleItem
        {
            Id = _original?.Id ?? Guid.NewGuid().ToString("N"),
            Title = Title.Trim(),
            Location = Location.Trim(),
            Note = Note.Trim(),
            Start = Start,
            End = end,
            GroupId = GroupId,
            ReminderMinutes = ReminderMinutes,
            Recurrence = rule,
            LinkedCourseId = string.IsNullOrWhiteSpace(LinkedCourseId) ? null : LinkedCourseId,
            LinkedCourseName = string.IsNullOrWhiteSpace(LinkedCourseName) ? null : LinkedCourseName,
        };

        error = string.Empty;
        return true;
    }
}
