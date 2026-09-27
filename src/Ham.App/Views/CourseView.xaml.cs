using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ham.App.ViewModels;
using Ham.Core.Models;

namespace Ham.App.Views;

public partial class CourseView : UserControl
{
    public CourseView() => InitializeComponent();

    private CourseViewModel? Vm => DataContext as CourseViewModel;

    /// <summary>点击课表格子选中课程。放在 code-behind 是因为需要命中课程本身，
    /// 而 ItemsControl 生成的容器会把 DataContext 设为 Course。</summary>
    private void CourseCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { DataContext: Course course }) Vm.Selected = course;
    }
}
