using System.Collections.ObjectModel;
using Ham.App.Mvvm;
using Ham.App.Services;
using Ham.Infrastructure.Demo;
using Ham.Infrastructure.Sport.Models;

namespace Ham.App.ViewModels;

/// <summary>运动场馆页：查看场次、快速预定、收藏预定、订单中心。</summary>
public sealed class SportViewModel : ObservableObject
{
    private readonly AppService _service;
    private readonly MainViewModel _main;

    private SportType? _sportType;
    private SportVenue? _venue;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private SportSlot? _selectedSlot;

    public SportViewModel(AppService service, MainViewModel main)
    {
        _service = service;
        _main = main;

        SelectTypeCommand = new RelayCommand(p =>
        {
            if (p is SportType t) { SportType = t; Venue = Venues.FirstOrDefault(v => v.SportTypeId == t.Id); }
        });

        SelectVenueCommand = new RelayCommand(p =>
        {
            if (p is SportVenue v) Venue = v;
        });

        BookCommand = new AsyncRelayCommand(BookAsync);
        NextDayCommand = new RelayCommand(() => { _date = _date.AddDays(1); RebuildSlots(); });
        PrevDayCommand = new RelayCommand(() => { _date = _date.AddDays(-1); RebuildSlots(); });
    }

    public RelayCommand SelectTypeCommand { get; }
    public RelayCommand SelectVenueCommand { get; }
    public AsyncRelayCommand BookCommand { get; }
    public RelayCommand NextDayCommand { get; }
    public RelayCommand PrevDayCommand { get; }

    public ObservableCollection<SportType> Types { get; } = [];

    public ObservableCollection<SportVenue> Venues { get; } = [];

    public ObservableCollection<SportSlot> Slots { get; } = [];

    public ObservableCollection<SportBooking> Orders { get; } = [];

    public SportVenue? Venue
    {
        get => _venue;
        set
        {
            if (SetProperty(ref _venue, value))
            {
                SelectedSlot = null;
                RebuildSlots();
            }
        }
    }

    public SportType? SportType
    {
        get => _sportType;
        set
        {
            if (SetProperty(ref _sportType, value))
            {
                RefreshTypeSelection();
                RebuildVenues();
            }
        }
    }

    /// <summary>同步"运动项目"按钮的选中态。</summary>
    public void RefreshTypeSelection()
    {
        foreach (var t in Types) t.IsSelected = t.Id == SportType?.Id;
    }

    public DateOnly Date
    {
        get => _date;
        set
        {
            if (SetProperty(ref _date, value)) RebuildSlots();
        }
    }

    public SportSlot? SelectedSlot
    {
        get => _selectedSlot;
        set => SetProperty(ref _selectedSlot, value);
    }

    public string SlotsHint
    {
        get
        {
            if (Venue is null) return "请先选择运动项目与场馆";
            var available = Slots.Count(s => !s.IsFull);
            return $"{Venue.Name} · {Date:MM 月 dd 日} · 可预定 {available} / {Slots.Count} 个场次";
        }
    }

    public SportBooking? PendingOrder
        => _service.SportBookings.FirstOrDefault(b => b.Status == SportOrderStatus.PendingPayment);

    public string PendingOrderText => PendingOrder is null
        ? "没有待支付的订单"
        : $"{PendingOrder.Describe()} · 待支付 {PendingOrder.Price:F2} 元";

    public bool HasPendingOrder => PendingOrder is not null;

    public string OrdersHint
        => _service.SportBookings.Count == 0
            ? "暂无订单"
            : $"共 {_service.SportBookings.Count} 条订单";

    public void Refresh()
    {
        if (Types.Count == 0)
        {
            StatusViewModel.Replace(Types, DemoData.BuildSportTypes());
            SportType = Types.FirstOrDefault();
        }

        StatusViewModel.Replace(Orders, _service.SportBookings.OrderByDescending(o => o.Start));
        RebuildSlots();
        OnPropertyChanged(nameof(PendingOrderText));
        OnPropertyChanged(nameof(HasPendingOrder));
        OnPropertyChanged(nameof(OrdersHint));
    }

    private void RebuildVenues()
    {
        StatusViewModel.Replace(Venues,
            DemoData.BuildVenues(DemoData.BuildSportTypes())
                .Where(v => v.SportTypeId == SportType?.Id)
                .ToList());

        Venue = Venues.FirstOrDefault();
    }

    private void RebuildSlots()
    {
        Slots.Clear();
        if (Venue is not null)
        {
            StatusViewModel.Replace(Slots, DemoData.BuildSlots(Venue, _date));
        }

        OnPropertyChanged(nameof(SlotsHint));
    }

    private async Task BookAsync()
    {
        if (Venue is null || SelectedSlot is null)
        {
            _main.ReportError("请先选择场馆与场次。");
            return;
        }

        if (SelectedSlot.IsFull)
        {
            _main.ReportError("该场次已约满，请选择其他时段。");
            return;
        }

        var booking = new SportBooking
        {
            Id = Guid.NewGuid().ToString("N"),
            VenueId = Venue.Id,
            VenueName = Venue.Name,
            SportTypeName = SportType?.Name ?? string.Empty,
            Start = _date.ToDateTime(SelectedSlot.Start),
            End = _date.ToDateTime(SelectedSlot.End),
            Status = SportOrderStatus.PendingPayment,
            Price = 10m,
            PaymentUrl = "https://payment.whu.edu.cn/ham-sport-demo",
        };

        _service.SportBookings.Add(booking);
        StatusViewModel.Replace(Orders, _service.SportBookings.OrderByDescending(o => o.Start));
        RebuildSlots();
        OnPropertyChanged(nameof(PendingOrderText));
        OnPropertyChanged(nameof(HasPendingOrder));
        OnPropertyChanged(nameof(OrdersHint));

        await _main.CommitAsync($"已预定 {booking.Describe()}，请在订单中心完成支付。");
    }
}
