using Ham.Infrastructure.Cas;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 凭据脱敏测试。
/// </summary>
/// <remarks>
/// 这类安全逻辑<b>必须</b>有测试：它靠"没人往日志里塞密码"这种约定维持，
/// 而约定一定会被后人破坏。有了测试，破坏会在 CI 里立刻失败。
/// </remarks>
public class CredentialRedactorTests
{
    [Fact]
    public void ReplacesThePasswordEverywhere()
    {
        // 用占位值，**不要**在这里写真学号或真密码。
        // 这类"顺手写个真实样例"是脱敏最常见的破口：本文件本身就是安全的，
        // 却在第一次提交时带进了真实学号。
        var r = new CredentialRedactor("pw-placeholder-0001", "id-placeholder-0001");

        var scrubbed = r.Scrub(
            "填入 window.__hamPass = \"pw-placeholder-0001\"，学号 id-placeholder-0001");

        Assert.DoesNotContain("pw-placeholder-0001", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("id-placeholder-0001", scrubbed, StringComparison.Ordinal);
        Assert.Contains(CredentialRedactor.Mask, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesRepeatedOccurrences()
    {
        var r = new CredentialRedactor("s3cret-value");
        var scrubbed = r.Scrub("a=s3cret-value b=s3cret-value c=s3cret-value");

        Assert.Equal(
            $"a={CredentialRedactor.Mask} b={CredentialRedactor.Mask} c={CredentialRedactor.Mask}",
            scrubbed);
    }

    /// <summary>
    /// 异常对象是主要泄漏通道：预填脚本源码含明文密码，
    /// 而日志写入器会把 ex.ToString()（含消息与堆栈）整段落盘。
    /// </summary>
    [Fact]
    public void ScrubsExceptionTextIncludingTheMessage()
    {
        var r = new CredentialRedactor("hunter2hunter2");
        var ex = new InvalidOperationException("script failed: var __hamPass = \"hunter2hunter2\";");

        var scrubbed = r.Scrub(ex);

        Assert.DoesNotContain("hunter2hunter2", scrubbed, StringComparison.Ordinal);
        Assert.Contains("script failed", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void HandlesNullAndEmptyInput()
    {
        var r = new CredentialRedactor("hunter2hunter2");
        Assert.Equal(string.Empty, r.Scrub((string?)null));
        Assert.Equal(string.Empty, r.Scrub(string.Empty));
        Assert.Equal(string.Empty, r.Scrub((Exception?)null));
    }

    /// <summary>
    /// 过短的片段会被忽略——否则一位数字做全局替换会把日志搅烂，
    /// 反而掩盖真正的信息。
    /// </summary>
    [Fact]
    public void IgnoresSecretsTooShortToBeDistinctive()
    {
        var r = new CredentialRedactor("ab", string.Empty, null);
        Assert.True(r.IsEmpty);
        Assert.Equal("value ab", r.Scrub("value ab"));
    }

    [Fact]
    public void IsANoOpWhenThereIsNothingToHide()
    {
        var r = new CredentialRedactor();
        Assert.True(r.IsEmpty);
        Assert.Equal("untouched", r.Scrub("untouched"));
    }

    [Fact]
    public void ScrubsUrlsTooBecauseSomeSsoPutTicketsInTheQuery()
    {
        var r = new CredentialRedactor("ST-9-abcdef");
        var scrubbed = r.Scrub("已到达教务主机: https://cas.example/login?ticket=ST-9-abcdef");

        Assert.DoesNotContain("ST-9-abcdef", scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 防止后来者把脱敏器"优化"成只处理不脱敏异常。
    /// 真实事故形态：某次异常里带上了凭据，排查时只看了 Message 觉得没问题。
    /// </summary>
    [Fact]
    public void ScrubbingIsNotIdentityForNonEmptySecrets()
    {
        var r = new CredentialRedactor("hunter2hunter2");
        Assert.NotEqual("hunter2hunter2", r.Scrub("hunter2hunter2"));
    }
}
