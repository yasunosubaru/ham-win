using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ham.App.ViewModels;
using Ham.Core.Models;

namespace Ham.App.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView() => InitializeComponent();

    private ScheduleViewModel? Vm => DataContext as ScheduleViewModel;

    private void Occurrence_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { DataContext: ScheduleOccurrence occurrence })
            Vm.Selected = occurrence.Item;
    }
}
