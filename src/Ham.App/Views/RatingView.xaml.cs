using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ham.App.ViewModels;
using Ham.Infrastructure.Rating.Models;

namespace Ham.App.Views;

public partial class RatingView : UserControl
{
    public RatingView() => InitializeComponent();

    private RatingViewModel? Vm => DataContext as RatingViewModel;

    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { DataContext: CourseRating rating }) Vm.Selected = rating;
    }
}
