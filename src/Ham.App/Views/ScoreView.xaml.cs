using System.Windows;
using System.Windows.Controls;
using Ham.App.ViewModels;

namespace Ham.App.Views;

public partial class ScoreView : UserControl
{
    public ScoreView() => InitializeComponent();

    private ScoreViewModel? Vm => DataContext as ScoreViewModel;

    /// <summary>勾选"是否参与计算"。命令参数需要整行对象，故在 code-behind 取 DataContext。</summary>
    private void RowEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { DataContext: ScoreRow row })
            Vm.ToggleEnabledCommand.Execute(row);
    }

    /// <summary>在 B2 自选模式下切换某门课是否计入。</summary>
    private void B2_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { DataContext: ScoreRow row })
            Vm.ToggleB2(row);
    }
}
