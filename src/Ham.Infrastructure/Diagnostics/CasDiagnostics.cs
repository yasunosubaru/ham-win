using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Ham.Core.Models;
using Ham.Infrastructure.Cas;
using Ham.Infrastructure.Education;

namespace Ham.Infrastructure.Diagnostics;

/// <summary>诊断步骤的结论。</summary>
public enum DiagnosticStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed,
    Skipped,
}

/// <summary>一个诊断步骤。</summary>
public sealed record DiagnosticStep(
    string Name,
    string Description,
    DiagnosticStatus Status,
    string Detail);

/// <summary>诊断报告。</summary>
public sealed class DiagnosticReport
{
    public required IReadOnlyList<DiagnosticStep> Steps { get; init; }
    public required bool AllPassed { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public int PassedCount => Steps.Count(s => s.Status == DiagnosticStatus.Passed);
    public int FailedCount => Steps.Count(s => s.Status == DiagnosticStatus.Failed);

    public string Summary => FailedCount == 0
        ? $"{PassedCount}/{Steps.Count} 项通过"
        : $"{FailedCount} 项失败，{PassedCount}/{Steps.Count} 项通过";
}

/// <summary>
/// 信息门户 / 教务系统连接诊断。
/// </summary>
/// <remarks>
/// 目的：把"登录失败"这种笼统反馈拆成可定位的步骤。凭据相关问题占绝大多数，
/// 但用户真正需要知道的是"到底卡在哪一步"——是网络不通、页面改版、
/// 账号密码错误、还是教务会话过期。
/// <para>
/// 无需凭据即可执行前 4 步（可达性、页面结构、登录窗口、SSO 地址），
/// 因此可以在拿到账号之前就验证环境是否就绪。
/// </para>
/// </remarks>
public sealed partial class CasDiagnostics
{
    private readonly HttpClient _http;

    public CasDiagnostics(HttpClient? http = null)
        => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>执行无需凭据的诊断步骤。</summary>
    public async Task<DiagnosticReport> RunNetworkStepsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<DiagnosticStep>();

        steps.Add(await CheckCasReachableAsync(ct).ConfigureAwait(false));
        steps.Add(await CheckLoginFormAsync(ct).ConfigureAwait(false));
        steps.Add(await CheckEducationReachableAsync(ct).ConfigureAwait(false));
        steps.Add(CheckSsoUrlShape());
        steps.Add(CheckClientCapability());

        sw.Stop();
        return new DiagnosticReport
        {
            Steps = steps,
            AllPassed = steps.All(s => s.Status is DiagnosticStatus.Passed or DiagnosticStatus.Warning),
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>校验学号与密码格式（本地，不发网络请求）。</summary>
    public static DiagnosticStep ValidateCredentials(string studentId, string password)
    {
        if (string.IsNullOrWhiteSpace(studentId))
            return new DiagnosticStep("凭据格式", "学号与信息门户密码", DiagnosticStatus.Failed,
                "未填写学号。");

        if (string.IsNullOrEmpty(password))
            return new DiagnosticStep("凭据格式", "学号与信息门户密码", DiagnosticStatus.Failed,
                "未填写信息门户密码。");

        // 学号长度只提示不拦截：官方实现是客户端硬拦截，但服务端才是权威规则。
        if (!CasClient.IsLikelyAcceptedStudentIdLength(studentId))
        {
            return new DiagnosticStep("凭据格式", "学号与信息门户密码", DiagnosticStatus.Warning,
                $"学号为 {studentId.Length} 位；超星 CAS 移动端页面常见 13 位或 8 位。"
                + "长度不寻常但不阻断登录，若提交失败请先确认学号。");
        }

        return new DiagnosticStep("凭据格式", "学号与信息门户密码", DiagnosticStatus.Passed,
            $"学号 {studentId.Length} 位、密码已填写。");
    }

    private async Task<DiagnosticStep> CheckCasReachableAsync(CancellationToken ct)
    {
        const string name = "CAS 可达性";
        const string desc = "连接武大信息门户登录页";

        try
        {
            using var response = await _http
                .GetAsync(CasEndpoints.MobileLoginUrl, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new DiagnosticStep(name, desc, DiagnosticStatus.Failed,
                    $"HTTP {(int)response.StatusCode}。若在学校外网，可能需要接入校园网。");
            }

            return new DiagnosticStep(name, desc, DiagnosticStatus.Passed,
                $"HTTP {(int)response.StatusCode}，登录页可访问。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiagnosticStep(name, desc, DiagnosticStatus.Failed, "连接超时。");
        }
        catch (HttpRequestException ex)
        {
            return new DiagnosticStep(name, desc, DiagnosticStatus.Failed,
                $"网络错误：{ex.Message}");
        }
    }

    private async Task<DiagnosticStep> CheckLoginFormAsync(CancellationToken ct)
    {
        const string name = "登录页结构";
        const string desc = "核对登录表单元素（与 ham-rn 选择器一致）";

        try
        {
            var html = await _http.GetStringAsync(CasEndpoints.MobileLoginUrl, ct).ConfigureAwait(false);

            var required = new (string Id, string Label)[]
            {
                ("pwdFromId", "表单"),
                ("username", "学号输入框"),
                ("password", "密码输入框"),
                ("login_submit", "登录按钮"),
            };

            var missing = required
                .Where(r => !html.Contains($"id=\"{r.Id}\"", StringComparison.Ordinal))
                .Select(r => r.Label)
                .ToList();

            if (missing.Count == required.Length)
            {
                return new DiagnosticStep(name, desc, DiagnosticStatus.Failed,
                    "未找到任何登录表单元素，登录页结构可能已改版。");
            }

            if (missing.Count > 0)
            {
                return new DiagnosticStep(name, desc, DiagnosticStatus.Failed,
                    $"缺少：{string.Join("、", missing)}。登录页结构可能已改版，需要调整注入脚本。");
            }

            return new DiagnosticStep(name, desc, DiagnosticStatus.Passed,
                "pwdFromId / username / password / login_submit 全部存在。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticStep(name, desc, DiagnosticStatus.Failed, ex.Message);
        }
    }

    private async Task<DiagnosticStep> CheckEducationReachableAsync(CancellationToken ct)
    {
        const string name = "教务可达性";
        const string desc = "连接正方教务系统";

        try
        {
            using var response = await _http
                .GetAsync(EducationEndpoints.BaseUrl + EducationEndpoints.HomePath, ct)
                .ConfigureAwait(false);

            return new DiagnosticStep(name, desc,
                response.IsSuccessStatusCode ? DiagnosticStatus.Passed : DiagnosticStatus.Warning,
                $"HTTP {(int)response.StatusCode}。未登录时被重定向到登录页属正常现象。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiagnosticStep(name, desc, DiagnosticStatus.Failed, "连接超时。");
        }
        catch (HttpRequestException ex)
        {
            return new DiagnosticStep(name, desc, DiagnosticStatus.Failed, $"网络错误：{ex.Message}");
        }
    }

    private static DiagnosticStep CheckSsoUrlShape()
    {
        const string name = "SSO 地址";
        const string desc = "校验 service 参数只编码一次";

        var url = CasEndpoints.BuildSsoLoginUrl();
        var problems = new List<string>();

        if (url.Contains("%25", StringComparison.Ordinal))
            problems.Add("service 被重复编码（含 %25）");

        if (!url.Contains(CasEndpoints.EducationServiceEncoded, StringComparison.Ordinal))
            problems.Add("service 值与官方实现不一致");

        if (!EducationClient.IsWhuHost(CasEndpoints.EducationServiceDecoded))
            problems.Add("service 目标域名不是 whu.edu.cn");

        return new DiagnosticStep(name, desc,
            problems.Count == 0 ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
            problems.Count == 0
                ? "service 已编码一次且指向 jwgl.whu.edu.cn。"
                : string.Join("；", problems));
    }

    private static DiagnosticStep CheckClientCapability()
    {
        const string name = "浏览器内核";
        const string desc = "信息门户登录需要 WebView2 Runtime";

        // WebView2 Runtime 实际安装目录（64 位 / 32 位 / 用户级）
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "EdgeWebView", "Application"),
        };

        var found = candidates.FirstOrDefault(Directory.Exists);

        if (found is not null)
        {
            var version = Directory.GetDirectories(found)
                .Select(Path.GetFileName)
                .FirstOrDefault();

            return new DiagnosticStep(name, desc, DiagnosticStatus.Passed,
                version is null ? "已安装 WebView2 Runtime。" : $"已安装 WebView2 Runtime {version}。");
        }

        return new DiagnosticStep(name, desc, DiagnosticStatus.Failed,
            "未检测到 WebView2 Runtime，信息门户登录将无法进行。"
            + "请安装 Microsoft Edge WebView2 Runtime（Windows 11 自带；Windows 10 需另行下载）。");
    }

    /// <summary>把一次真实的同步结果转成诊断结论，便于统一展示。</summary>
    public static DiagnosticStep FromSyncResult(string name, string desc, (bool Ok, string Message) result)
        => new(name, desc,
            result.Ok ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
            result.Message);
}
