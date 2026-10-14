using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepSeekPet.Services;

/// <summary>
/// 资源守护：每 ~2 分钟巡检一次（跟随调度器捎带执行，不额外起线程）。
///  - 内存持续偏高时做一次轻量回收并记录日志；
///  - GDI / USER 句柄数量超阈值时告警，便于发现句柄泄漏。
/// 空闲时不打扰、不产生可感知开销。
/// </summary>
public static class MemoryGuard
{
    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags); // 0=GDI, 1=USER

    private const long WarnMemoryBytes = 150L * 1024 * 1024;
    private const long ReclaimMemoryBytes = 160L * 1024 * 1024;
    private const int WarnGdi = 350;
    private const int WarnUser = 500;
    private static DateTime _lastReclaim = DateTime.MinValue;

    public static void Check()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            long ws = p.WorkingSet64;
            int gdi = GetGuiResources(p.Handle, 0);
            int user = GetGuiResources(p.Handle, 1);

            // 高位回收（带 8 分钟冷却，避免频繁 GC；回收在后台线程做，UI 无感）
            if (ws > ReclaimMemoryBytes && (DateTime.Now - _lastReclaim).TotalMinutes > 8)
            {
                _lastReclaim = DateTime.Now;
                AppLogger.Warn($"内存 {ws / 1048576}MB 偏高，执行一次优化回收（GDI {gdi} / USER {user}）");
                Task.Run(() =>
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                        GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    }
                    catch { }
                });
            }
            else if (ws > WarnMemoryBytes || gdi > WarnGdi || user > WarnUser)
            {
                AppLogger.Warn($"资源监控：内存 {ws / 1048576}MB，GDI 句柄 {gdi}，USER 句柄 {user}");
            }
        }
        catch
        {
            // 巡检本身绝不影响主流程
        }
    }
}
