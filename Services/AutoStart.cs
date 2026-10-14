using Microsoft.Win32;

namespace DeepSeekPet.Services;

/// <summary>
/// 开机自启（HKCU Run 键，无需管理员）。
/// 使用 --autostart --delay 3：开机登录后静默延迟 3 秒再出现，
/// 避开登录高峰期的磁盘/CPU 竞争，降低开机“存在感”。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeepSeekPet";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0)
                    key.SetValue(ValueName, $"\"{exe}\" --autostart --delay 3");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("设置开机自启失败", ex);
        }
    }

    /// <summary>若已启用自启，启动时刷新一次路径（程序被移动后自动纠正）</summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            if (IsEnabled()) Set(true);
        }
        catch { }
    }
}
