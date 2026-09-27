using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ham.Ui;

/// <summary>
/// WinUI 3 应用外壳。
/// </summary>
/// <remarks>
/// 与 WPF 版 <c>Ham.App</c> 的关系：<b>业务逻辑与数据层完全共用</b>
/// （<c>Ham.Core</c> / <c>Ham.Infrastructure</c> 两个项目原样引用，未做任何改写），
/// 因此课表解析、成绩解析、GPA 计算、天气、持久化的行为与已验证的 WPF 版完全一致。
/// 迁移只发生在表现层。
/// </remarks>
public partial class App : Application
{
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}
