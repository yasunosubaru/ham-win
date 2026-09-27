using System.IO;
using System.Text;

namespace Ham.Infrastructure.Logging;

/// <summary>
/// 基础设施层的诊断出口。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：<c>Ham.Infrastructure</c> 不能引用 <c>Ham.App</c>（那会造成分层倒置），
/// 但接口层的异常响应必须留下现场——教务系统用的是<b>自定义状态码</b>
/// （901/910 等，均非 HTTP 标准码），只看数字无法判断原因，正文里才写得清楚。
/// </para>
/// <para>
/// 默认实现写入 <c>%LOCALAPPDATA%\Ham\logs\ham.log</c>，与 <c>App.LogCritical</c> 同一文件，
/// 因此用户报障时只需提供一个日志。宿主可调用 <see cref="Sink"/> 改写落点。
/// </para>
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>自定义落点。返回 <c>true</c> 表示已处理，框架不再写默认文件。</summary>
    public static Func<string, bool>? Sink { get; set; }

    /// <summary>日志文件路径。</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ham", "logs", "ham.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        if (Sink is not null)
        {
            try
            {
                if (Sink(line)) return;
            }
            catch
            {
                // 自定义落点失败不能影响主流程。
            }
        }

        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath,
                    line + Environment.NewLine
                    + (ex is null ? string.Empty : ex + Environment.NewLine + Environment.NewLine),
                    Utf8NoBom);
            }
        }
        catch
        {
            // 日志失败不能影响主流程。
        }
    }
}
