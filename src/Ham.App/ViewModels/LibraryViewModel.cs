using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Infrastructure.Library;
using Ham.Infrastructure.Library.Models;

namespace Ham.App.ViewModels;

/// <summary>图书馆页：看板预约、快速预约、预约状态、历史记录。</summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private string? _roomFilter;
    private bool _onlyAvailable;
    private SeatBoard? _board;
    private ReservationSlot _slot = new()
    {
        Date = DateOnly.FromDateTime(DateTime.Today),
        Start = new TimeOnly(14, 0),
        End = new TimeOnly(18, 0),
    };

    public LibraryViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        RefreshBoardCommand = new RelayCommand(RebuildBoard);
        SyncAreasCommand = new AsyncRelayCommand(SyncAreasAsync);
        BookSelectedCommand = new AsyncRelayCommand(BookAsync);
        CancelBookingCommand = new AsyncRelayCommand(CancelAsync);
        SetPreferredCommand = new AsyncRelayCommand(SetPreferredAsync);
        NextDayCommand = new RelayCommand(() => { _date = _date.AddDays(1); RebuildBoard(); });
        PrevDayCommand = new RelayCommand(() => { _date = _date.AddDays(-1); RebuildBoard(); });

        StatusViewModel.Replace(LiveAreas, service.LibraryAreas.OrderByDescending(a => a.FreeRatio));
    }

    public RelayCommand RefreshBoardCommand { get; }
    public AsyncRelayCommand SyncAreasCommand { get; }
    public AsyncRelayCommand BookSelectedCommand { get; }
    public AsyncRelayCommand CancelBookingCommand { get; }
    public AsyncRelayCommand SetPreferredCommand { get; }
    public RelayCommand NextDayCommand { get; }
    public RelayCommand PrevDayCommand { get; }

    public ObservableCollection<LibrarySeat> VisibleSeats { get; } = [];

    public ObservableCollection<LibraryBooking> Bookings { get; } = [];

    public IReadOnlyList<LibraryRoom> Rooms { get; private set; } = [];

    public DateOnly Date
    {
        get => _date;
        set
        {
            if (SetProperty(ref _date, value)) RebuildBoard();
        }
    }

    public ReservationSlot Slot
    {
        get => _slot;
        set
        {
            if (!SetProperty(ref _slot, value)) return;
            _slot = value with { Date = _date };
            RebuildBoard();
        }
    }

    public IReadOnlyList<TimeOnly> SlotStarts { get; } =
        Enumerable.Range(8, 12).Select(h => new TimeOnly(h, 0)).ToList();

    public string? RoomFilter
    {
        get => _roomFilter;
        set
        {
            if (SetProperty(ref _roomFilter, value)) ApplyFilter();
        }
    }

    public bool OnlyAvailable
    {
        get => _onlyAvailable;
        set
        {
            if (SetProperty(ref _onlyAvailable, value)) ApplyFilter();
        }
    }

    public LibrarySeat? Selected { get; set; }

    public SeatBoard? Board
    {
        get => _board;
        private set
        {
            if (!SetProperty(ref _board, value)) return;
            OnPropertyChanged(nameof(AvailableText));
        }
    }

    public string AvailableText => Board is null
        ? string.Empty
        : $"可用 {Board.AvailableCount} / {Board.TotalCount} 个座位";

    public LibraryBooking? ActiveBooking
        => _service.LibraryBookings.FirstOrDefault(b => b.IsActive);

    public string ActiveBookingText => ActiveBooking?.Describe() ?? "当前没有有效预约";

    public bool HasActiveBooking => ActiveBooking is not null;

    public string PreferredSeatText
    {
        get
        {
            var room = _service.Settings.PreferredLibraryRoom;
            var seat = _service.Settings.PreferredLibrarySeat;
            return string.IsNullOrEmpty(seat) ? "尚未设置首选座位" : $"{room} · {seat}";
        }
    }

    public void Refresh()
    {
        _slot = _slot with { Date = _date };

        // 真实余座（来自图书馆系统）；未同步过时列表为空，界面会明确提示，
        // 不再拿示例数据冒充真实数据。
        Rooms = [];
        VisibleSeats.Clear();
        Board = null;

        StatusViewModel.Replace(Bookings, _service.LibraryBookings.OrderByDescending(b => b.Start));

        OnPropertyChanged(nameof(HasLiveAreas));
        OnPropertyChanged(nameof(LiveAreaSummary));
        OnPropertyChanged(nameof(ActiveBookingText));
        OnPropertyChanged(nameof(HasActiveBooking));
        OnPropertyChanged(nameof(PreferredSeatText));
        OnPropertyChanged(nameof(AvailableText));
    }

    /// <summary>是否已取到真实余座。</summary>
    public bool HasLiveAreas => _service.LibraryAreas.Count > 0;

    /// <summary>真实余座摘要。</summary>
    public string LiveAreaSummary
    {
        get
        {
            var areas = _service.LibraryAreas;
            if (areas.Count == 0) return "尚未同步图书馆余座，请点击「同步余座」";

            var free = areas.Sum(a => a.FreeSeats);
            return $"{_service.LibraryName} · {_service.LibraryAreaDate:MM-dd} · "
                   + $"{areas.Count} 个区域，共余 {free} 个座位";
        }
    }

    /// <summary>余座最多、便于直接查看的区域。</summary>
    public ObservableCollection<LibraryAreaAvailability> LiveAreas { get; } = [];

    public IReadOnlyList<string> Libraries { get; } = Infrastructure.Library.LibraryEndpoints.Libraries;

    private string _library = "总馆";

    /// <summary>当前查看的分馆。</summary>
    public string Library
    {
        get => _library;
        set
        {
            if (SetProperty(ref _library, value)) OnPropertyChanged(nameof(Library));
        }
    }

    private bool _isSyncingAreas;


    /// <summary>从图书馆系统同步真实余座与我的预约。</summary>
    private async Task SyncAreasAsync()
    {
        if (_isSyncingAreas) return;
        _isSyncingAreas = true;
        _main.IsBusy = true;
        try
        {
            var (ok, message) = await _service.SyncLibraryAsync(Library, _date);
            if (ok) _main.Report(message);
            else _main.ReportError(message);

            StatusViewModel.Replace(LiveAreas, _service.LibraryAreas
                .OrderByDescending(a => a.FreeRatio));
            StatusViewModel.Replace(Bookings, _service.LibraryBookings.OrderByDescending(b => b.Start));

            OnPropertyChanged(nameof(HasLiveAreas));
            OnPropertyChanged(nameof(LiveAreaSummary));
            OnPropertyChanged(nameof(ActiveBookingText));
            OnPropertyChanged(nameof(HasActiveBooking));
        }
        finally
        {
            _isSyncingAreas = false;
            _main.IsBusy = false;
        }
    }

    private void RebuildBoard()
    {
        _slot = _slot with { Date = _date };
        // 座位看板依赖真实逐座数据，而 findRoomDuration 只给到区域级余量。
        // 没有真实逐座数据时保持空，绝不用示例数据冒充。
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (Board is null) return;

        var list = Board.Seats.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(RoomFilter)) list = list.Where(s => s.RoomId == RoomFilter);
        if (OnlyAvailable) list = list.Where(s => !s.IsOccupied);

        StatusViewModel.Replace(VisibleSeats, list);
    }

    private async Task BookAsync()
    {
        // 座位预约需要图形验证码与服务端风控，本应用<b>不做</b>自动化预约。
        // 与其提供一个只写本地数据、让人误以为真的约上了的按钮，不如直接说清。
        _main.ReportError(
            "座位预约需要图形验证码，本应用不代为预约。请到图书馆座位预约系统"
            + "（seat.lib.whu.edu.cn）或「武汉大学图书馆」公众号完成。");
        await Task.CompletedTask;
    }

    private async Task CancelAsync()
    {
        var active = ActiveBooking;
        if (active is null)
        {
            _main.ReportError("当前没有可取消的预约。");
            return;
        }

        _main.ReportError(
            "取消预约同样需要图形验证码，请到图书馆座位预约系统"
            + "（seat.lib.whu.edu.cn）操作。");
        await Task.CompletedTask;
    }

    private async Task SetPreferredAsync()
    {
        if (Selected is null)
        {
            _main.ReportError("请先选择一个座位。");
            return;
        }

        _service.Settings.PreferredLibraryRoom = Selected.RoomName;
        _service.Settings.PreferredLibrarySeat = Selected.Label;
        OnPropertyChanged(nameof(PreferredSeatText));
        await _main.CommitAsync($"已把 {Selected.RoomName} {Selected.Label} 设为首选座位。");
    }
}
