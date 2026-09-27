using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;

namespace Ham.App.ViewModels;

/// <summary>课程表页：周视图、课程增删改、冲突检测。</summary>
public sealed class CourseViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private int _currentWeek = 1;
    private bool _showAllWeeks;
    private string _filter = string.Empty;
    private Course? _selected;
    private CourseEditorViewModel? _editor;

    public CourseViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        PreviousWeekCommand = new RelayCommand(() => { if (_currentWeek > 1) CurrentWeek--; });
        NextWeekCommand = new RelayCommand(() =>
        {
            if (_currentWeek < _service.Settings.TotalWeeks) CurrentWeek++;
        });
        TodayCommand = new RelayCommand(() => CurrentWeek = _service.Calendar.CurrentWeek(DateTime.Now) ?? 1);
        AddCommand = new RelayCommand(BeginAdd);
        EditSelectedCommand = new RelayCommand(BeginEdit);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => Editor = null);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        DetectConflictsCommand = new RelayCommand(DetectConflicts);
    }

    public RelayCommand PreviousWeekCommand { get; }
    public RelayCommand NextWeekCommand { get; }
    public RelayCommand TodayCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand EditSelectedCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand DetectConflictsCommand { get; }

    /// <summary>一周七天的课程，索引 0=周日。</summary>
    public ObservableCollection<DayColumn> Days { get; } = [];

    public ObservableCollection<Course> AllCourses { get; } = [];

    public ObservableCollection<CourseConflict> Conflicts { get; } = [];

    public int TotalWeeks => _service.Settings.TotalWeeks;

    public int CurrentWeek
    {
        get => _currentWeek;
        set
        {
            if (!SetProperty(ref _currentWeek, Math.Clamp(value, 1, Math.Max(1, TotalWeeks)))) return;
            Rebuild();
        }
    }

    public bool ShowAllWeeks
    {
        get => _showAllWeeks;
        set
        {
            if (SetProperty(ref _showAllWeeks, value)) Rebuild();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) Rebuild();
        }
    }

    public Course? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedWeeksText));
                OnPropertyChanged(nameof(SelectedSchedules));
            }
        }
    }

    /// <summary>编辑面板是否打开（由 <see cref="Editor"/> 是否存在决定）。</summary>
    public bool IsEditing => _editor is not null;

    public CourseEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            _editor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditing));
        }
    }

    public string SelectedWeeksText
    {
        get
        {
            if (Selected is null) return string.Empty;
            var rule = WeekRuleParser.Parse(Selected.RawWeekText ?? string.Empty);
            var described = WeekRuleParser.Describe(rule);
            return string.IsNullOrEmpty(described) ? "周次未设置" : described;
        }
    }

    public string SelectedDetail
    {
        get
        {
            if (Selected is null) return string.Empty;
            var parts = new List<string>
            {
                $"{Selected.Name}",
                $"教师 {Selected.Instructor}",
                $"地点 {Selected.Location}",
                $"节次 {Selected.ClassFrom}-{Selected.ClassTo}",
                SelectedWeeksText,
            };

            if (!string.IsNullOrWhiteSpace(Selected.CourseType)) parts.Add(Selected.CourseType);
            if (Selected.Credit > 0) parts.Add($"{Selected.Credit} 学分");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>与所选课程关联的日程。</summary>
    public IReadOnlyList<ScheduleItem> SelectedSchedules
        => Selected is null
            ? []
            : _service.Schedules.Where(s => s.LinkedCourseId == Selected.CourseId).ToList();

    public void Refresh()
    {
        var week = _service.Calendar.CurrentWeek(DateTime.Now);
        if (week is not null) _currentWeek = week.Value;
        StatusViewModel.Replace(AllCourses, _service.Courses);
        OnPropertyChanged(nameof(TotalWeeks));
        Rebuild();
    }

    private void Rebuild()
    {
        Days.Clear();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            var list = _showAllWeeks
                ? _service.Courses.Where(c => c.Weekday == weekday).OrderBy(c => c.ClassFrom).ToList()
                : _service.GetCoursesAt(_currentWeek, weekday);

            if (!string.IsNullOrWhiteSpace(_filter))
            {
                var keyword = _filter.Trim();
                list = list.Where(c =>
                    c.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || c.Instructor.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || c.Location.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            Days.Add(new DayColumn(weekday, list));
        }

        OnPropertyChanged(nameof(CurrentWeek));
    }

    private void BeginAdd() => Editor = new CourseEditorViewModel(null);

    private void BeginEdit()
    {
        if (Selected is null)
        {
            _main.ReportError("请先在课表中选择一门课程。");
            return;
        }

        Editor = new CourseEditorViewModel(Selected);
    }

    private async Task SaveAsync()
    {
        if (Editor is null) return;

        if (!Editor.TryBuildCourse(out var course, out var error))
        {
            _main.ReportError(error);
            return;
        }

        _service.UpsertCourse(course!);
        Editor = null;
        StatusViewModel.Replace(AllCourses, _service.Courses);
        Rebuild();
        await _main.CommitAsync($"已保存课程「{course!.Name}」。");
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        _service.RemoveCourse(Selected);
        Selected = null;
        StatusViewModel.Replace(AllCourses, _service.Courses);
        Rebuild();
        await _main.CommitAsync($"已删除课程「{name}」。");
    }

    private void DetectConflicts()
    {
        var conflicts = _service.DetectConflicts();
        StatusViewModel.Replace(Conflicts, conflicts);
        _main.Report(conflicts.Count == 0 ? "未发现课程冲突。" : $"发现 {conflicts.Count} 处课程冲突。");
    }
}

/// <summary>课表中的一天。</summary>
public sealed class DayColumn
{
    public DayColumn(int weekday, IReadOnlyList<Course> courses)
    {
        Weekday = weekday;
        Courses = courses;
    }

    public int Weekday { get; }

    public IReadOnlyList<Course> Courses { get; }

    public string Name => ConflictDetector.DayOfWeekName(Weekday);

    public string Short => ConflictDetector.DayOfWeekLabel(Weekday);

    public bool IsToday => Weekday == (int)DateTime.Now.DayOfWeek;

    public int CourseCount => Courses.Count;
}

/// <summary>课程编辑面板。</summary>
public sealed class CourseEditorViewModel : ObservableObject
{
    private readonly Course? _original;

    private string _name = string.Empty;
    private string _instructor = string.Empty;
    private string _location = string.Empty;
    private string _courseType = string.Empty;
    private string _courseId = string.Empty;
    private double _credit;
    private int _weekday = 1;
    private int _classFrom = 1;
    private int _classTo = 2;
    private string _weekText = string.Empty;

    public CourseEditorViewModel(Course? original)
    {
        _original = original;

        _name = original?.Name ?? string.Empty;
        _instructor = original?.Instructor ?? string.Empty;
        _location = original?.Location ?? string.Empty;
        _courseType = original?.CourseType ?? string.Empty;
        _courseId = original?.CourseId ?? string.Empty;
        _credit = original?.Credit ?? 2.0;
        _weekday = original?.Weekday ?? 1;
        _classFrom = original?.ClassFrom > 0 ? original.ClassFrom : 1;
        _classTo = original?.ClassTo > 0 ? original.ClassTo : 2;
        _weekText = original?.RawWeekText ?? "1-17周";
    }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(ParsedWeeksHint)); }
    }

    public string Instructor
    {
        get => _instructor;
        set => SetProperty(ref _instructor, value);
    }

    public string Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string CourseType
    {
        get => _courseType;
        set => SetProperty(ref _courseType, value);
    }

    public string CourseId
    {
        get => _courseId;
        set { if (SetProperty(ref _courseId, value)) OnPropertyChanged(nameof(PreviewColor)); }
    }

    public double Credit
    {
        get => _credit;
        set => SetProperty(ref _credit, value);
    }

    public int Weekday
    {
        get => _weekday;
        set { if (SetProperty(ref _weekday, value)) OnPropertyChanged(nameof(WeekdayName)); }
    }

    public int ClassFrom
    {
        get => _classFrom;
        set => SetProperty(ref _classFrom, value);
    }

    public int ClassTo
    {
        get => _classTo;
        set => SetProperty(ref _classTo, value);
    }

    public string WeekText
    {
        get => _weekText;
        set { if (SetProperty(ref _weekText, value)) OnPropertyChanged(nameof(ParsedWeeksHint)); }
    }

    public IReadOnlyList<int> Periods { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    public IReadOnlyList<int> Weekdays { get; } = [0, 1, 2, 3, 4, 5, 6];

    public IReadOnlyList<string> CourseTypes { get; } =
        ["公共基础必修", "专业必修", "专业选修", "通识必修", "公共基础选修", "专业限选", "其他"];

    public string WeekdayName => ConflictDetector.DayOfWeekName(Weekday);

    public string PreviewColor => CourseColorAssigner.ForCourseId(
        string.IsNullOrWhiteSpace(_courseId) ? _name : _courseId);

    /// <summary>实时提示周次解析结果，避免用户填完保存才发现格式不对。</summary>
    public string ParsedWeeksHint
    {
        get
        {
            var rule = WeekRuleParser.Parse(_weekText);
            if (rule.IsEmpty) return "无法识别周次";
            return $"共 {rule.Weeks.Count} 个教学周 · {WeekRuleParser.Describe(rule)}";
        }
    }

    /// <summary>把面板输入转换为课程对象；失败时返回原因。</summary>
    public bool TryBuildCourse(out Course? course, out string error)
    {
        course = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "课程名称不能为空。";
            return false;
        }

        if (ClassFrom < 1 || ClassTo > 12 || ClassFrom > ClassTo)
        {
            error = "节次范围无效，应为 1–12 且起始节不大于结束节。";
            return false;
        }

        var rule = WeekRuleParser.Parse(WeekText);
        if (rule.IsEmpty)
        {
            error = $"无法解析周次「{WeekText}」，请使用如 1-17周、1-17周(单) 的格式。";
            return false;
        }

        var id = string.IsNullOrWhiteSpace(CourseId) ? Name.Trim() : CourseId.Trim();

        course = new Course
        {
            Name = Name.Trim(),
            CourseId = id,
            Instructor = Instructor.Trim(),
            InstructorType = _original?.InstructorType ?? string.Empty,
            WeekFrom = rule.WeekFrom,
            WeekTo = rule.WeekTo,
            ClassFrom = ClassFrom,
            ClassTo = ClassTo,
            Weekday = Weekday,
            CourseType = CourseType,
            Credit = Credit,
            Location = Location.Trim(),
            Color = CourseColorAssigner.ForCourseId(id),
            Year = _original?.Year,
            SemesterNumber = _original?.SemesterNumber,
            RawWeekText = WeekText,
            Weeks = rule.Weeks,
        };

        error = string.Empty;
        return true;
    }
}
