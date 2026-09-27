using System.Windows;
using System.Windows.Controls;
using Ham.App.ViewModels;

namespace Ham.App.Views;

public partial class MainWindow : Window
{
    private bool _suppressNavEvent;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SelectNavItem(Current);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private NavSection Current => Vm?.Current ?? NavSection.Status;

    /// <summary>侧边导航点击：更新当前分区并同步选中态。</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNavEvent) return;
        if (sender is not RadioButton { Tag: NavSection section }) return;

        if (Vm is { } vm) vm.Current = section;
    }

    /// <summary>切换分区后回写选中项，避免 VM 变更时选中态不同步。</summary>
    private void SelectNavItem(NavSection section)
    {
        _suppressNavEvent = true;
        try
        {
            foreach (var container in FindNavButtons(NavList))
            {
                if (container.Tag is NavSection s) container.IsChecked = s == section;
            }
        }
        finally
        {
            _suppressNavEvent = false;
        }
    }

    private static IEnumerable<RadioButton> FindNavButtons(DependencyObject root)
    {
        if (root is null) yield break;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is RadioButton button) yield return button;
            else
            {
                foreach (var nested in FindNavButtons(child)) yield return nested;
            }
        }
    }
}
