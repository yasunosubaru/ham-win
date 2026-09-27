using System.Windows;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Net;

namespace Ham.App.Services;

/// <summary>信息门户（CAS）交互式登录的执行者。</summary>
public interface ICasLoginProvider
{
    /// <summary>
    /// 打开登录界面并等待用户完成认证，随后在<b>已认证的浏览器页面上下文</b>中
    /// 代取 <paramref name="plan"/> 指定的数据。
    /// </summary>
    /// <remarks>
    /// 数据必须由浏览器代取：教务前置安全设备只放行同源、带会话、由页面脚本发起的请求
    /// （详见 <see cref="FetchPlan"/> 的实测记录）。纯 HTTP 客户端稳定拿到 901 或 HTML 首页。
    /// </remarks>
    Task<CasLoginOutcome> LoginAsync(FetchPlan plan, CancellationToken ct = default);
}

/// <summary>当应用运行在没有浏览器内核的环境中时的占位实现。</summary>
public sealed class UnavailableCasLoginProvider : ICasLoginProvider
{
    public Task<CasLoginOutcome> LoginAsync(FetchPlan plan, CancellationToken ct = default)
        => Task.FromResult(CasLoginOutcome.Empty);
}

/// <summary>基于 WebView2 窗口的 CAS 登录提供器。</summary>
/// <remarks>
/// 把已保存的学号 / 密码交给登录窗口预填，避免用户每次手输。
/// 凭据由调用方通过 <see cref="_credentialAccessor"/> 实时提供，不在此处缓存。
/// </remarks>
public sealed class WebView2CasLoginProvider : ICasLoginProvider
{
    private readonly Func<Window?> _ownerAccessor;
    private readonly Func<(string? StudentId, string? Password)> _credentialAccessor;
    private readonly CampusEndpoints _endpoints;

    public WebView2CasLoginProvider(
        Func<Window?> ownerAccessor,
        Func<(string?, string?)> credentialAccessor,
        CampusEndpoints? endpoints = null)
    {
        _ownerAccessor = ownerAccessor;
        _credentialAccessor = credentialAccessor;
        _endpoints = endpoints ?? CampusEndpoints.Default;
    }

    public Task<CasLoginOutcome> LoginAsync(FetchPlan plan, CancellationToken ct = default)
    {
        var (studentId, password) = _credentialAccessor();
        return CasLoginWindow.ShowAsync(
            _ownerAccessor(), studentId, password, ct, _endpoints, plan);
    }
}
