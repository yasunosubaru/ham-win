using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ham.Core.Models;

namespace Ham.Infrastructure.Storage;

/// <summary>应用设置。</summary>
public sealed class AppSettings
{
    public string StudentId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string College { get; set; } = string.Empty;
    public string Major { get; set; } = string.Empty;

    /// <summary>信息门户密码。仅本机保存，且不参与任何日志输出。</summary>
    public string PortalPassword { get; set; } = string.Empty;

    public int SemesterYear { get; set; } = DateTime.Now.Year;
    public int SemesterNumber { get; set; } = 1;
    public string SemesterStartDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int TotalWeeks { get; set; } = 20;

    // 默认值必须**恰好等于** GpaScale.Standard4_0.Name。
    // 原来写的是字面量 "Standard 4.0"（英文），而预设名是中文的「标准 4.0 制」，
    // 于是按名字查找永远匹配不上——只是因为回落分支恰好也是 Standard4_0
    // 才一直没暴露出来。改成直接引用，杜绝这类漂移。
    public string GpaScaleName { get; set; } = GpaScale.Standard4_0.Name;
    public string ComprehensiveMethod { get; set; } = "NewF2";
    public string? CustomB2CourseIds { get; set; }
    public string? SelectedScoreCalcScriptId { get; set; }

    public bool RequireBiometricForScores { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    public bool EnableCourseReminder { get; set; } = true;
    public bool EnableScheduleReminder { get; set; } = true;
    public int CourseReminderMinutes { get; set; } = 10;
    public int LibraryAutoRefreshMinutes { get; set; } = 15;

    public string Theme { get; set; } = "Light";
    public bool AllowOfflineDemoData { get; set; } = true;

    // Ham 互联 OAuth2 客户端凭据（自行申请获得；可留空，仅影响平台相关功能）。
    public string OAuthClientId { get; set; } = string.Empty;
    public string OAuthClientSecret { get; set; } = string.Empty;
    public string OAuthAccessToken { get; set; } = string.Empty;
    public string OAuthTokenExpiry { get; set; } = string.Empty;
    public string OpenId { get; set; } = string.Empty;

    // 开放平台 API Key（用于 MCP / 只读给分查询）。
    public string ApiKey { get; set; } = string.Empty;

    public string? PreferredLibraryRoom { get; set; }
    public string? PreferredLibrarySeat { get; set; }
    public string? PreferredSportVenue { get; set; }
    public string? PreferredSportSlot { get; set; }
    public string BusStopName { get; set; } = string.Empty;
}

/// <summary>持久化到本地的完整应用数据。</summary>
public sealed class AppData
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<Core.Models.Course> Courses { get; set; } = [];
    public List<Core.Models.ScoreRecord> Scores { get; set; } = [];
    public List<Core.Models.ScheduleItem> Schedules { get; set; } = [];
    public List<Core.Models.ScheduleGroup> ScheduleGroups { get; set; } = [];

    public List<Library.Models.LibraryBooking> LibraryBookings { get; set; } = [];
    public List<Library.Models.LibrarySeat> PreferredSeats { get; set; } = [];

    public List<Sport.Models.SportBooking> SportBookings { get; set; } = [];
    public List<Sport.Models.FavoriteSportBooking> FavoriteSportBookings { get; set; } = [];

    public List<Rating.Models.CourseRating> CourseRatings { get; set; } = [];
    public List<Rating.Models.CourseReview> CourseReviews { get; set; } = [];
    public List<Rating.Models.CourseWish> CourseWishes { get; set; } = [];

    public DateTimeOffset? LastEducationSync { get; set; }
    public DateTimeOffset? LastLibrarySync { get; set; }
}

/// <summary>
/// JSON 文件存储。
/// </summary>
/// <remarks>
/// 采用"先写临时文件再替换"的原子写入策略：直接覆盖写入时若进程在写一半被杀，
/// 用户的课表与成绩会损坏，而这类数据无法从服务器恢复。
/// </remarks>
public sealed class DataStore
{
    /// <summary>
    /// 磁盘格式契约。公开以便调用方（含测试）直接校验/复用同一套序列化选项，
    /// 避免"读用一套选项、写用另一套"导致的不对称。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataStore(string? path = null)
    {
        FilePath = path ?? DefaultPath();
    }

    /// <summary>默认数据路径：<c>%LOCALAPPDATA%\Ham\appdata.json</c>。</summary>
    public static string DefaultPath()
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ham", "appdata.json");

    /// <summary>数据文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>最近一次加载失败的原因；正常时为 null。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 读取本地数据。
    /// </summary>
    /// <remarks>
    /// <b>本方法绝不抛异常。</b>数据文件一旦损坏或格式不兼容，用户的课表与成绩就会全部丢失，
    /// 此时"打不开应用"远比"数据重来"糟糕。因此任何失败都退化为空数据，并把现场备份为
    /// <c>.corrupt-时间戳</c>，保证应用总能启动。
    /// <para>
    /// 这里曾只捕获 <see cref="JsonException"/> 与 <see cref="IOException"/>，
    /// 而 <see cref="NotSupportedException"/>（目标类型无法实例化）会穿透，
    /// 导致启动在 <c>Show()</c> 之前中断，用户看不到任何窗口。
    /// </para>
    /// </remarks>
    public async Task<AppData> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath)) return new AppData();

            var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new AppData();

            var data = JsonSerializer.Deserialize<AppData>(json, Options);
            if (data is not null) return data;

            LastLoadError = "数据文件内容为空，已重置。";
            TryBackupCorrupt();
            return new AppData();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastLoadError = ex.Message;
            TryBackupCorrupt();
            return new AppData();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(data, Options);
            var dir = System.IO.Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var backup = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(FilePath, backup, overwrite: true);
        }
        catch
        {
            // 备份失败（例如文件被占用）不阻断启动。
        }
    }
}
