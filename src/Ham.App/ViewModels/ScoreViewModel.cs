using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Core.Models;

namespace Ham.App.ViewModels;

/// <summary>成绩页：成绩列表、GPA / 加权均分、综测 F2、分布、排名。</summary>
public sealed class ScoreViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private bool _showDisabledOnly;
    private IReadOnlySet<string>? _customB2;
    private bool _b2SelectionMode;

    public ScoreViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync);
        ShowAllCommand = new RelayCommand(() => ShowDisabledOnly = false);
        ShowDisabledCommand = new RelayCommand(() => ShowDisabledOnly = true);
        ClearCustomB2Command = new RelayCommand(() => { _customB2 = null; Refresh(); });
        SelectB2Command = new RelayCommand(BeginB2Selection);
    }

    public AsyncRelayCommand ToggleEnabledCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowDisabledCommand { get; }
    public RelayCommand ClearCustomB2Command { get; }
    public RelayCommand SelectB2Command { get; }

    public ObservableCollection<ScoreRow> Rows { get; } = [];

    public ObservableCollection<SemesterScoreSummary> BySemester { get; } = [];

    public ObservableCollection<ScoreBucket> Distribution { get; } = [];

    /// <summary>分布图的展示行，条宽已换算为像素。</summary>
    public ObservableCollection<DistributionRow> DistributionRows { get; } = [];

    private const double BarMaxWidth = 190;

    public IReadOnlyList<GpaScale> GpaScales { get; } = GpaScale.Presets;

    public IReadOnlyList<ComprehensiveScoreMethod> Methods { get; } =
        [ComprehensiveScoreMethod.NewF2, ComprehensiveScoreMethod.LegacyF2];

    public int TotalCount => _service.Scores.Count;

    public int EnabledCount => _service.Scores.Count(s => s.IsEnabled);

    public bool ShowDisabledOnly
    {
        get => _showDisabledOnly;
        set
        {
            if (SetProperty(ref _showDisabledOnly, value)) Rebuild();
        }
    }

    public bool B2SelectionMode
    {
        get => _b2SelectionMode;
        set
        {
            if (SetProperty(ref _b2SelectionMode, value)) Rebuild();
        }
    }

    public GpaScale SelectedScale
    {
        get => _service.GpaScale;
        set
        {
            if (value is null) return;
            _service.Settings.GpaScaleName = value.Name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GpaText));
            OnPropertyChanged(nameof(ScaleDescription));
            _ = _main.CommitAsync($"绩点换算已切换为「{value.Name}」。");
        }
    }

    public ComprehensiveScoreMethod SelectedMethod
    {
        get => _service.ComprehensiveMethod;
        set
        {
            if (!Enum.IsDefined(value)) return;
            _service.Settings.ComprehensiveMethod = value.ToString();
            OnPropertyChanged();
            Rebuild();
            _ = _main.CommitAsync();
        }
    }

    public string College => string.IsNullOrWhiteSpace(_service.Settings.College)
        ? "未填写学院（跨专业判定不可用）"
        : _service.Settings.College;

    public double Gpa => ScoreCalculator.Gpa(_service.Scores, _service.GpaScale);

    public string GpaText => Gpa.ToString("F3");

    public double WeightedAverage => ScoreCalculator.WeightedAverage(_service.Scores);

    public string WeightedAverageText => WeightedAverage.ToString("F2");

    public double TotalCredit => _service.Scores.Where(s => s.IsEnabled).Sum(s => s.Credit);

    public double EarnedCredit => ScoreCalculator.EarnedCredit(_service.Scores);

    public string ScaleDescription => _service.GpaScale.Description;

    public double Comprehensive => _summary?.Value ?? 0;

    public string ComprehensiveText => Comprehensive.ToString("F3");

    public double B2Average => ComprehensiveScoreCalculator.B2Average(
        _service.Scores, _service.Settings.College, _service.ComprehensiveMethod, _customB2);

    public string B2SelectionSummary
    {
        get
        {
            if (_customB2 is null) return "当前按规则自动选取 B2 课程";
            return $"已手动指定 {CustomB2Rows.Count(r => r.IsInB2)} 门 B2 课程";
        }
    }

    public int B2SelectedCount => CustomB2Rows.Count(r => r.IsInB2);

    public int B2Cap => ComprehensiveScoreMethod.NewF2 == _service.ComprehensiveMethod
        ? ComprehensiveScoreCalculator.MaxB2CourseCount
        : CustomB2Rows.Count;

    public ObservableCollection<ScoreRow> CustomB2Rows { get; } = [];

    public string MethodDescription => _service.ComprehensiveMethod switch
    {
        ComprehensiveScoreMethod.NewF2 =>
            "F2 = B1 + B2 × 0.002，其中 B2 最多选取 8 门（按成绩从高到低）。",
        ComprehensiveScoreMethod.LegacyF2 =>
            "F2 = B1 × 0.98 + B2 × 0.02。",
        _ => "使用自定义脚本计算。",
    };

    private ScoreSummary? _summary;

    public void Refresh()
    {
        OnPropertyChanged(nameof(SelectedScale));
        OnPropertyChanged(nameof(SelectedMethod));
        OnPropertyChanged(nameof(College));
        OnPropertyChanged(nameof(ScaleDescription));
        OnPropertyChanged(nameof(MethodDescription));
        Rebuild();
    }

    private void Rebuild()
    {
        var records = _service.Scores.ToList();

        _summary = ComprehensiveScoreCalculator.Calculate(
            records, _service.Settings.College, _service.ComprehensiveMethod, _customB2);

        var selected = _summary.SelectedCourseIds.ToHashSet(StringComparer.Ordinal);

        var source = ShowDisabledOnly ? records.Where(r => !r.IsEnabled) : records;

        StatusViewModel.Replace(Rows, source
            .OrderByDescending(r => r.Year)
            .ThenByDescending(r => r.SemesterNumber)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new ScoreRow(r, selected.Contains(ComprehensiveScoreCalculator.CourseKey(r)))));

        StatusViewModel.Replace(BySemester, ScoreCalculator.BySemester(records, _service.GpaScale));

        // 分布图的条宽在视图模型里算好：XAML 里用 MultiBinding + RelativeSource 取祖先集合
        // 既难读又容易静默失效，直接给出像素宽度最可靠。
        var buckets = ScoreCalculator.Distribution(records);
        var max = buckets.Count > 0 ? buckets.Max(b => b.Count) : 0;
        StatusViewModel.Replace(DistributionRows, buckets.Select(b => new DistributionRow(
            b.From, b.To, b.Count, max <= 0 ? 0 : Math.Round(BarMaxWidth * b.Count / max, 1))));

        // B2 候选：仅列出真正属于 B2 的课程，供用户手动勾选。
        StatusViewModel.Replace(CustomB2Rows, records
            .Where(r => CourseTypeClassifier.IsOtherCollegeMajorCourse(
                r.CourseType, _service.Settings.College, r.CourseCollege))
            .OrderByDescending(r => r.Score)
            .Select(r => new ScoreRow(r, _customB2?.Contains(ComprehensiveScoreCalculator.CourseKey(r)) ?? false))
            .ToList());

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(Gpa));
        OnPropertyChanged(nameof(GpaText));
        OnPropertyChanged(nameof(WeightedAverage));
        OnPropertyChanged(nameof(WeightedAverageText));
        OnPropertyChanged(nameof(TotalCredit));
        OnPropertyChanged(nameof(EarnedCredit));
        OnPropertyChanged(nameof(Comprehensive));
        OnPropertyChanged(nameof(ComprehensiveText));
        OnPropertyChanged(nameof(B2Average));
        OnPropertyChanged(nameof(B2SelectionSummary));
        OnPropertyChanged(nameof(B2SelectedCount));
        OnPropertyChanged(nameof(B2Cap));
    }

    private void BeginB2Selection()
    {
        _b2SelectionMode = !_b2SelectionMode;
        if (!_b2SelectionMode) _customB2 = null;
        Rebuild();
    }

    /// <summary>在 B2 自选模式下切换某门课是否计入 B2。</summary>
    public void ToggleB2(ScoreRow row)
    {
        var key = ComprehensiveScoreCalculator.CourseKey(row.Record);
        var set = _customB2 is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_customB2, StringComparer.Ordinal);

        if (!set.Add(key)) set.Remove(key);
        _customB2 = set;

        _service.Settings.CustomB2CourseIds = set.Count == 0 ? null : string.Join(",", set);
        Rebuild();
    }

    private async Task ToggleEnabledAsync(object? parameter)
    {
        if (parameter is not ScoreRow row) return;
        _service.ToggleScoreEnabled(row.Record);
        Rebuild();
        await _main.CommitAsync(
            row.Record.IsEnabled ? $"已启用「{row.Name}」参与计算。" : $"已关闭「{row.Name}」不参与计算。");
    }
}

/// <summary>分数分布的一行（含已换算的条宽）。</summary>
public sealed class DistributionRow
{
    public DistributionRow(int from, int to, int count, double barWidth)
    {
        From = from;
        To = to;
        Count = count;
        BarWidth = barWidth;
    }

    public int From { get; }
    public int To { get; }
    public int Count { get; }

    /// <summary>柱条像素宽度（0 表示无数据）。</summary>
    public double BarWidth { get; }

    public string Label => $"{From}-{To}";
}

/// <summary>成绩行。</summary>
public sealed class ScoreRow
{
    public ScoreRow(ScoreRecord record, bool isInCalculation)
    {
        Record = record;
        IsInCalculation = isInCalculation;
    }

    public ScoreRecord Record { get; }

    public string Name => Record.Name;
    public string SemesterText => $"{Record.Year}-{Record.SemesterNumber}";
    public string Instructor => Record.Instructor;
    public string CourseType => Record.CourseType;
    public string CourseCollege => Record.CourseCollege;
    public double Credit => Record.Credit;
    public double Score => Record.Score;
    public bool IsEnabled => Record.IsEnabled;

    /// <summary>是否参与了当前 F2 计算。</summary>
    public bool IsInCalculation { get; }

    public bool IsInB2 { get; init; }

    public string ScoreText => Record.Score.ToString("0");

    public string CategoryLabel => CourseTypeClassifier.Group(
        Record.CourseType, null, Record.CourseCollege) switch
    {
        CourseCategoryGroup.Primary => "B1",
        CourseCategoryGroup.CrossMajor => "B2",
        _ => "—",
    };
}
