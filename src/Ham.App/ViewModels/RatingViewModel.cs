using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Infrastructure.Demo;
using Ham.Infrastructure.Rating.Models;

namespace Ham.App.ViewModels;

/// <summary>给分页：课程 / 教师搜索、给分分布浏览、课程评价与"想上"。</summary>
public sealed class RatingViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private string _keyword = string.Empty;
    private CourseRating? _selected;
    private int _reviewScore = 5;
    private string _reviewText = string.Empty;

    public RatingViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearCommand = new RelayCommand(() => { Keyword = string.Empty; SearchCommand.Execute(null); });
        WishCommand = new AsyncRelayCommand(WishAsync);
        SubmitReviewCommand = new AsyncRelayCommand(SubmitReviewAsync);
    }

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand WishCommand { get; }
    public AsyncRelayCommand SubmitReviewCommand { get; }

    public ObservableCollection<CourseRating> Results { get; } = [];

    public ObservableCollection<CourseWish> Wishes { get; } = [];

    public ObservableCollection<CourseReview> MyReviews { get; } = [];

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    public CourseRating? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedDetail));
                OnPropertyChanged(nameof(SelectedRangesMax));
            }
        }
    }

    public string SelectedDetail => Selected is null
        ? string.Empty
        : $"{Selected.CourseName} · {Selected.Instructor} · 平均 {Selected.Average:F1} 分 · {Selected.Total} 人评价";

    public int SelectedRangesMax => Selected?.Ranges.Count > 0
        ? Selected.Ranges.Max(r => r.Total)
        : 0;

    public int ReviewScore
    {
        get => _reviewScore;
        set => SetProperty(ref _reviewScore, Math.Clamp(value, 1, 5));
    }

    public string ReviewText
    {
        get => _reviewText;
        set => SetProperty(ref _reviewText, value);
    }

    public string EmptyHint => Results.Count == 0
        ? "输入课程名或教师姓名后搜索，例如「毛概」或「张伟」。"
        : string.Empty;

    public string WishesHint => Wishes.Count == 0 ? "还没有标记「想上」的课程" : $"已标记 {Wishes.Count} 门";

    public void Refresh()
    {
        if (Results.Count == 0 && _service.CourseRatings.Count > 0)
        {
            StatusViewModel.Replace(Results, _service.CourseRatings);
        }

        StatusViewModel.Replace(Wishes, _service.Data.CourseWishes);
        StatusViewModel.Replace(MyReviews, _service.Data.CourseReviews);
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(WishesHint));
    }

    /// <summary>
    /// 搜索给分。
    /// </summary>
    /// <remarks>
    /// 开放平台的搜索接口需要 API Key（机器凭据，与用户身份无关）。
    /// 未配置时退回到本地数据，并明确告知用户这是本地数据。
    /// </remarks>
    private async Task SearchAsync()
    {
        var keyword = Keyword.Trim();
        if (keyword.Length == 0)
        {
            Results.Clear();
            OnPropertyChanged(nameof(EmptyHint));
            return;
        }

        var pool = _service.CourseRatings.Count > 0
            ? _service.CourseRatings
            : DemoData.BuildRatings();

        var matches = pool
            .Where(r => r.CourseName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || r.Instructor.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        StatusViewModel.Replace(Results, matches);
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(EmptyHint));

        _main.Report(matches.Count == 0
            ? $"未找到与「{keyword}」匹配的给分信息。"
            : _service.CourseRatings.Count > 0
                ? $"找到 {matches.Count} 条本地给分记录。"
                : $"找到 {matches.Count} 条演示给分记录（未配置开放平台 API Key）。");

        await Task.CompletedTask;
    }

    private async Task WishAsync()
    {
        if (Selected is null)
        {
            _main.ReportError("请先选择一门课程。");
            return;
        }

        var exists = _service.Data.CourseWishes.Any(w => w.CourseName == Selected.CourseName);
        if (exists)
        {
            _main.Report($"你已标记过「{Selected.CourseName}」。");
            return;
        }

        _service.Data.CourseWishes.Add(new CourseWish
        {
            CourseId = Selected.Id,
            CourseName = Selected.CourseName,
        });

        StatusViewModel.Replace(Wishes, _service.Data.CourseWishes);
        OnPropertyChanged(nameof(WishesHint));
        await _main.CommitAsync($"已标记「想上」：{Selected.CourseName}");
    }

    private async Task SubmitReviewAsync()
    {
        if (Selected is null)
        {
            _main.ReportError("请先选择一门课程再发表评价。");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReviewText))
        {
            _main.ReportError("请填写评价内容。");
            return;
        }

        _service.Data.CourseReviews.Add(new CourseReview
        {
            CourseName = Selected.CourseName,
            Instructor = Selected.Instructor,
            Rating = ReviewScore,
            Comment = ReviewText.Trim(),
        });

        ReviewText = string.Empty;
        StatusViewModel.Replace(MyReviews, _service.Data.CourseReviews);
        await _main.CommitAsync("评价已发布（匿名）。");
    }
}
