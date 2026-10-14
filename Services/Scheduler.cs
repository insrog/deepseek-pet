using System.Windows.Threading;
using DeepSeekPet.Models;

namespace DeepSeekPet.Services;

/// <summary>
/// 提醒调度器（单定时器，动态间隔）。
///  - 不空转：距离下次提醒 > 30s 时 30s 一跳；临近时精确到毫秒级再跳；
///  - 低延迟：到点触发误差通常在 150ms 以内（日志记录实际毫秒）；
///  - 不丢失：休眠/时钟跳变自动重扫；启动时对 2 小时内的错过项补提醒；
///  - 不重复：按 (任务, 精确到分钟的触发时刻) 记账，触发即落盘，重启不会重发。
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly TaskStore _store;
    private readonly DispatcherTimer _timer;
    private DateTime _lastTick = DateTime.Now;
    private int _tickCount;
    private bool _disposed;

    /// <summary>有一批任务到点（可能多个同时）</summary>
    public event Action<List<TaskItem>>? ReminderDue;

    public Scheduler(TaskStore store)
    {
        _store = store;
        _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        // 休眠唤醒即时补发：不依赖定时器轮询，系统一恢复立刻检查错过的提醒
        try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; }
        catch (Exception ex) { AppLogger.Warn($"电源事件订阅失败（仍有时钟跳变兜底）：{ex.Message}"); }
    }

    public void Start()
    {
        _lastTick = DateTime.Now;
        FireDue(TimeSpan.FromMinutes(120), "补提醒");
        Rearm();
        _timer.Start();
    }

    /// <summary>重新计算下一个跳点（任务变更后调用）</summary>
    public void Rearm()
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.Now;
            double minMs = 30000;
            foreach (var t in _store.Tasks)
            {
                var next = t.NextOccurrence(now);
                if (next == null) continue;
                var ms = (next.Value - now).TotalMilliseconds;
                if (ms < minMs) minMs = ms;
            }
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(minMs, 100, 30000));
        }
        catch
        {
            _timer.Interval = TimeSpan.FromSeconds(30);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var now = DateTime.Now;

        // 休眠恢复 / 系统时钟调整检测
        bool jumped = (now - _lastTick).TotalMinutes > 2 || _lastTick - now > TimeSpan.FromMinutes(1);
        _lastTick = now;

        FireDue(jumped ? TimeSpan.FromMinutes(120) : TimeSpan.FromSeconds(90), jumped ? "休眠恢复" : null);
        Rearm();

        if (++_tickCount >= 8)
        {
            _tickCount = 0;
            try { _store.NormalizeDoneDates(); } catch { }
            try { App.Current.Pet?.HousekeepingTick(); } catch { }
            MemoryGuard.Check();
        }
    }

    private void FireDue(TimeSpan window, string? tag)
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");
        List<TaskItem>? due = null;

        foreach (var t in _store.Tasks)
        {
            if (!t.Enabled || t.DoneDate == today) continue;
            var occ = t.LatestOccurrenceAtOrBefore(now);
            if (occ == null) continue;
            var late = now - occ.Value;
            if (late < TimeSpan.Zero || late > window) continue;
            var key = TaskItem.OccurrenceKey(occ.Value);
            if (t.LastFiredKey == key) continue;

            t.LastFiredKey = key;
            (due ??= new List<TaskItem>()).Add(t);
        }

        if (due == null) return;

        _store.PersistFired(); // 关键：先记账落盘，再展示，保证不重复/不丢失

        foreach (var t in due)
        {
            var delayMs = (now - now.Date.AddHours(t.Hour).AddMinutes(t.Minute)).TotalMilliseconds;
            AppLogger.Info($"提醒触发{(tag == null ? "" : $"[{tag}]")}：{t.Title} 计划 {t.Hour:D2}:{t.Minute:D2} 实际 {now:HH:mm:ss.fff}");
        }

        try { ReminderDue?.Invoke(due); }
        catch (Exception ex) { AppLogger.Error("提醒回调异常", ex); }
    }

    /// <summary>系统电源事件：从休眠/睡眠恢复时，立即补发错过的提醒</summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || _disposed) return;
        AppLogger.Info("系统从休眠恢复，立即检查错过的提醒");
        try
        {
            if (_timer.Dispatcher.CheckAccess())
                ResumeCatchup();
            else
                _timer.Dispatcher.BeginInvoke(new Action(ResumeCatchup));
        }
        catch { }
    }

    private void ResumeCatchup()
    {
        try
        {
            _lastTick = DateTime.Now; // 该次跳变已由补发逻辑消化
            FireDue(TimeSpan.FromMinutes(120), "休眠唤醒");
            Rearm();
        }
        catch (Exception ex)
        {
            AppLogger.Error("休眠唤醒补发失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
    }
}
