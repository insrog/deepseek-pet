using System.IO;
using System.Text;

namespace DeepSeekPet.Services;

/// <summary>
/// 轻量日志：仅在事件/错误时写盘（空闲零写入）；
/// 单文件超过 1MB 自动归档，保留最近 7 天，避免无限增长。
/// </summary>
public static class AppLogger
{
    private static readonly object Sync = new();
    private static string _dir = "";

    public static void Init(string? dataDirHint)
    {
        try
        {
            var baseDir = dataDirHint
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekPet");
            _dir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(_dir);
            CleanupOld();
        }
        catch
        {
            _dir = "";
        }
    }

    public static void Info(string msg) => Write("信息", msg);
    public static void Warn(string msg) => Write("警告", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("错误", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        try
        {
            if (string.IsNullOrEmpty(_dir)) return;
            lock (Sync)
            {
                var path = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    try { File.Move(path, path + ".old", overwrite: true); } catch { }
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志绝不影响主流程
        }
    }

    private static void CleanupOld()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "app-*.log*"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                    File.Delete(f);
            }
        }
        catch { }
    }
}
