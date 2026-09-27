using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Ham.App.Mvvm;

/// <summary>UI 线程调度辅助。</summary>
internal static class UiThread
{
    /// <summary>当前是否位于 UI 线程。</summary>
    public static bool IsOnUiThread
        => System.Windows.Application.Current?.Dispatcher is { } d && d.CheckAccess();

    /// <summary>在 UI 线程上执行；已在 UI 线程则同步执行，否则异步派发。</summary>
    /// <remarks>
    /// 服务层普遍使用 <c>ConfigureAwait(false)</c>，await 链的续体可能落在线程池线程。
    /// 此时若直接触发 <c>PropertyChanged</c> 或 <c>CanExecuteChanged</c>，WPF 会抛
    /// <c>InvalidOperationException</c>；若发生在 <c>finally</c> 里，还会把命令永久卡在"忙碌"。
    /// 所有跨线程的通知派发都必须经过这里。
    /// </remarks>
    public static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.BeginInvoke(action);
        }
        catch
        {
            // 应用正在退出时派发可能失败，忽略即可。
        }
    }
}

/// <summary>可观察对象基类：所有属性变更通知都会自动切回 UI 线程。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        UiThread.Post(Raise);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>同步执行的命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public void RaiseCanExecuteChanged()
        => UiThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>异步命令，执行期间自动禁用自身，避免重复点击。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private int _isRunning;
    private bool _disposed;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(false);

    public async Task ExecuteAsync(object? parameter)
    {
        if (_disposed || !CanExecute(parameter)) return;

        Interlocked.Exchange(ref _isRunning, 1);
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(false);
        }
        finally
        {
            // 此处极可能运行在非 UI 线程（服务层普遍 ConfigureAwait(false)）。
            // RaiseCanExecuteChanged 负责切回 UI 线程；
            // 若直接在此触发 CanExecuteChanged，会抛跨线程异常并把命令永久卡在"忙碌"。
            Interlocked.Exchange(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
        => UiThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

    public void Dispose() => _disposed = true;
}
