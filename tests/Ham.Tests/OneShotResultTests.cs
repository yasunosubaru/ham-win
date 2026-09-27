using Ham.Core;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// <see cref="OneShotResult{T}"/> 的回归测试。
/// </summary>
/// <remarks>
/// 这些用例针对的是 CAS 登录窗口里真实发生过的缺陷：
/// 「完成标志」与「任务」是两套独立状态，导致登录明明成功，
/// 结果却先被空列表完成、真实 Cookie 变成空操作，最终同步永远失败。
/// </remarks>
public class OneShotResultTests
{
    [Fact]
    public async Task FirstCompletionWinsAndLaterOnesAreIgnored()
    {
        var r = new OneShotResult<string>();

        Assert.True(r.TryComplete("cookies"));
        Assert.False(r.TryComplete("something-else"));
        Assert.False(r.TryComplete("yet-another"));

        Assert.Equal("cookies", await r.Task);
    }

    /// <summary>
    /// 关键回归：CAS 登录成功的顺序是「先拿到 Cookie 就完成」，
    /// 而关窗/取消走的是「空结果完成」。谁先到谁生效，且必须如实反映。
    /// </summary>
    [Fact]
    public async Task SuccessThenCancelKeepsTheRealResult()
    {
        var r = new OneShotResult<IReadOnlyList<(string Name, string Value)>>();

        Assert.True(r.TryComplete([("CASTGC", "TGT-1")]));
        Assert.False(r.TryComplete([]));

        var cookies = await r.Task;
        Assert.Single(cookies);
        Assert.Equal("CASTGC", cookies[0].Name);
        Assert.Equal("TGT-1", cookies[0].Value);
    }

    [Fact]
    public async Task CancelThenSuccessYieldsEmpty()
    {
        var r = new OneShotResult<IReadOnlyList<(string Name, string Value)>>();

        Assert.True(r.TryComplete([]));
        Assert.False(r.TryComplete([("CASTGC", "TGT-1")]));

        Assert.Empty(await r.Task);
    }

    [Fact]
    public void IsCompletedReflectsState()
    {
        var r = new OneShotResult<int>();
        Assert.False(r.IsCompleted);
        Assert.True(r.TryComplete(1));
        Assert.True(r.IsCompleted);
    }

    /// <summary>并发完成时只有一方生效，不会抛异常也不会产生多个结果。</summary>
    [Fact]
    public async Task ConcurrentCompletionsAreSafe()
    {
        var r = new OneShotResult<int>();
        var wins = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            if (r.TryComplete(i)) Interlocked.Increment(ref wins);
        })));

        Assert.Equal(1, wins);
        var value = await r.Task;
        Assert.InRange(value, 0, 31);
    }
}
