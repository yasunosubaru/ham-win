using System.Windows;
using System.Windows.Controls;
using Ham.App.ViewModels;
using Ham.Infrastructure.Sport.Models;

namespace Ham.App.Views;

public partial class SportView : UserControl
{
    public SportView() => InitializeComponent();

    private SportViewModel? Vm => DataContext as SportViewModel;

    private void Type_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { Tag: SportType type }) Vm.SelectTypeCommand.Execute(type);
    }

    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { Tag: SportSlot slot }) Vm.SelectedSlot = slot;
    }
}
