namespace Ham.Core;

/// <summary>
/// 只能完成一次的结果容器。
/// </summary>
/// <typeparam name="T">结果类型。</typeparam>
/// <remarks>
/// <para>
/// 存在的理由是消除"完成标志"与"任务"两套状态不同步的可能。
/// CAS 登录窗口曾用 <c>bool _completed</c> + <c>TaskCompletionSource</c> 并行表示"已结束"，
/// 于是出现过这样的顺序错误：
/// </para>
/// <code>
/// MarkCompleted();        // 内部 _result.TrySetResult([]) —— 用空列表完成了任务
/// _result.TrySetResult(cookies);   // 空操作，真实 Cookie 被丢弃
/// </code>
/// <para>
/// 结果是"明明检测到登录成功，却永远拿到 0 个 Cookie"。
/// 这里把两者合成为单一原子状态：第一次 <see cref="TryComplete"/> 生效，之后一律忽略。
/// </para>
/// <para>
/// 线程安全基于 <see cref="Interlocked"/>，可从任意线程调用。
/// </para>
/// <para>
/// <b>刻意不提供 <c>TryCompleteEmpty()</c> 之类的便捷方法。</b>用 <c>default(T)</c> 充当"空结果"
/// 对引用类型就是 <c>null</c>，会让下游的 <c>.Count</c> / <c>.Length</c> 直接空引用崩溃，
/// 而调用点看上去却像是"正常地返回了空结果"。空值必须由调用方显式给出。
/// </para>
/// </remarks>
public sealed class OneShotResult<T>
{
    private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _done;

    /// <summary>结果任务。</summary>
    public Task<T> Task => _source.Task;

    /// <summary>是否已完成。</summary>
    public bool IsCompleted => Volatile.Read(ref _done) != 0;

    /// <summary>
    /// 尝试以 <paramref name="value"/> 完成。第一次调用生效，返回 <c>true</c>。
    /// </summary>
    /// <remarks>务必传入<b>最终</b>结果——本方法只有一次生效机会。</remarks>
    public bool TryComplete(T value)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return false;
        _source.TrySetResult(value);
        return true;
    }
}
