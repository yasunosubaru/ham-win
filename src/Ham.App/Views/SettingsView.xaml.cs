using System.Windows;
using System.Windows.Controls;
using Ham.App.ViewModels;

namespace Ham.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>PasswordBox 没有内置双向绑定，故显式回写到视图模型。</summary>
    private void PasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is PasswordBox box) Vm.PortalPassword = box.Password;
    }
}
