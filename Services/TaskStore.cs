using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using DeepSeekPet.Models;

namespace DeepSeekPet.Services;

/// <summary>
/// 任务与设置的持久化（v1.1 起使用 SQLite，替代旧 JSON 文件）。
///
/// 磁盘写入策略（与 v1.0 一致，且更适合数据库）：
///  - 普通变更（增删改、勾选完成）：防抖 2 秒合并写一次（避免连续操作反复写盘）；
///  - 关键变更（提醒触发）：立即落盘（保证掉电/崩溃后同一时刻绝不重复提醒）；
///  - 整批写入放在单个事务中，原子提交，绝不半写；
///  - 空闲时零写入。
///
/// 首次运行自动迁移旧版 tasks.json / settings.json（迁移后旧文件保留为 .migrated 备份）。
/// 公开 API 与 v1.0 完全兼容，上层（Scheduler / UI）无需改动。
/// </summary>
public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true, // 容忍手工编辑的数据文件大小写差异
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string SettingsKvKey = "app_settings";

    private readonly Db _db;
    private readonly string _legacyTasksPath;
    private readonly string _legacySettingsPath;
    private readonly DispatcherTimer _flushTimer;
    private readonly Dispatcher _ui;
    private bool _tasksDirty;
    private bool _settingsDirty;

    public string DataDir { get; }
    public List<TaskItem> Tasks { get; } = new();
    public AppSettings Settings { get; private set; } = new();

    /// <summary>任务集合或设置发生变更（UI 线程回调）</summary>
    public event Action? Changed;

    public TaskStore(Db db, string? dataDir)
    {
        _db = db;
        DataDir = dataDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekPet");
        _legacyTasksPath = Path.Combine(DataDir, "tasks.json");
        _legacySettingsPath = Path.Combine(DataDir, "settings.json");

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _flushTimer.Tick += (_, _) =>
        {
            _flushTimer.Stop();
            if (!SaveNow()) EnsureFlushTimer(); // 失败待重试
        };

        // 供 AI 工具在后台线程安全访问：所有变更/读取统一 marshal 到 UI 线程执行
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>底层数据库（对话记录 / 动态状态 / 搜索缓存共用同一连接）</summary>
    public Db Database => _db;

    // ---------------- UI 线程 marshal ----------------

    private void Ui(Action action)
    {
        if (_ui.CheckAccess()) action();
        else _ui.Invoke(action);
    }

    private T Ui<T>(Func<T> f)
    {
        return _ui.CheckAccess() ? f() : _ui.Invoke(f);
    }

    /// <summary>任务列表快照（线程安全，供 AI 工具/摘要读取）</summary>
    public List<TaskItem> Snapshot() => Ui(() => Tasks.ToList());

    // ---------------- 读取 ----------------

    public void Load()
    {
        try { Directory.CreateDirectory(DataDir); }
        catch (Exception ex) { AppLogger.Error("创建数据目录失败", ex); }

        if (!_db.Healthy())
            AppLogger.Warn("数据库自检未通过（quick_check 非 ok），仍继续尝试使用");

        LoadSettings();
        LoadTasks();
    }

    private void LoadSettings()
    {
        try
        {
            var json = _db.GetKv(SettingsKvKey);
            if (json != null)
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                return;
            }

            // 无数据库记录：尝试从旧 settings.json 迁移
            if (File.Exists(_legacySettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_legacySettingsPath, Encoding.UTF8), JsonOpts);
                if (s != null)
                {
                    Settings = s;
                    _settingsDirty = true;
                    MarkMigrated(_legacySettingsPath);
                    AppLogger.Info("已迁移旧版 settings.json → SQLite");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("读取设置失败，使用默认值", ex);
            TryQuarantine(_legacySettingsPath, onlyLegacy: true);
        }
    }

    private void LoadTasks()
    {
        try
        {
            var rows = _db.QueryList("""
                SELECT id,title,mode,interval_days,week_mask,hour,minute,enabled,anchor_date,last_fired_key,done_date,once_date
                FROM tasks ORDER BY sort_order, rowid
                """, MapTask);

            if (rows.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int dup = 0;
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                foreach (var t in rows)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.Title)) continue;
                    if (!seen.Add(Signature(t))) { dup++; continue; }   // 载入即自动去重
                    if (t.DoneDate != null && t.DoneDate != today) t.DoneDate = null;
                    Tasks.Add(t);
                }
                if (dup > 0) AppLogger.Info($"载入时自动去重 {dup} 个重复任务（数据库中存在重复行）");
                return;
            }

            // 数据库无任务：尝试从旧 tasks.json 迁移
            MigrateLegacyTasks();
        }
        catch (Exception ex)
        {
            AppLogger.Error("读取任务失败", ex);
            TryQuarantine(_legacyTasksPath, onlyLegacy: true);
        }
    }

    private void MigrateLegacyTasks()
    {
        if (!File.Exists(_legacyTasksPath)) return;
        try
        {
            var wrapper = JsonSerializer.Deserialize<LegacyTasksFile>(
                File.ReadAllText(_legacyTasksPath, Encoding.UTF8), JsonOpts);
            var list = wrapper?.Tasks ?? new List<TaskItem>();
            if (list.Count == 0) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int dup = 0;
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            foreach (var t in list)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Title)) continue;
                if (!seen.Add(Signature(t))) { dup++; continue; }
                if (t.DoneDate != null && t.DoneDate != today) t.DoneDate = null;
                Tasks.Add(t);
            }

            _tasksDirty = true;
            SaveNowBestEffort();
            MarkMigrated(_legacyTasksPath);
            AppLogger.Info($"已迁移旧版 tasks.json → SQLite：{Tasks.Count} 个任务" + (dup > 0 ? $"（自动去重 {dup} 个）" : ""));
        }
        catch (Exception ex)
        {
            AppLogger.Error("迁移旧任务数据失败（保留原文件，界面从空列表开始）", ex);
        }
    }

    private sealed class LegacyTasksFile
    {
        public int Version { get; set; } = 1;
        public List<TaskItem> Tasks { get; set; } = new();
    }

    private static TaskItem MapTask(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Mode = (RepeatMode)r.GetInt32(2),
        IntervalDays = r.GetInt32(3),
        WeekMask = r.GetInt32(4),
        Hour = r.GetInt32(5),
        Minute = r.GetInt32(6),
        Enabled = r.GetInt32(7) != 0,
        AnchorDate = r.GetString(8),
        LastFiredKey = r.IsDBNull(9) ? null : r.GetString(9),
        DoneDate = r.IsDBNull(10) ? null : r.GetString(10),
        OnceDate = r.IsDBNull(11) ? null : r.GetString(11)
    };

    private static void MarkMigrated(string legacyPath)
    {
        try
        {
            if (File.Exists(legacyPath))
                File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
        }
        catch { }
    }

    // ---------------- 变更 ----------------

    public static string Signature(TaskItem t) =>
        $"{t.Title.Trim().ToLowerInvariant()}|{t.Mode}|{t.IntervalDays}|{t.WeekMask}|{t.Hour:D2}:{t.Minute:D2}|{t.OnceDate}";

    /// <summary>返回与之重复的现有任务标题；无重复返回 null（线程安全）</summary>
    public string? FindDuplicate(TaskItem t, string? exceptId = null) => Ui(() =>
    {
        var sig = Signature(t);
        foreach (var other in Tasks)
        {
            if (exceptId != null && other.Id == exceptId) continue;
            if (Signature(other) == sig) return other.Title;
        }
        return null;
    });

    public void Upsert(TaskItem item) => Ui(() => UpsertCore(item));

    private void UpsertCore(TaskItem item)
    {
        var now = DateTime.Now;
        var existing = Tasks.Find(x => x.Id == item.Id);
        if (existing == null)
        {
            if (string.IsNullOrWhiteSpace(item.AnchorDate))
                item.AnchorDate = DateTime.Today.ToString("yyyy-MM-dd");
            // 防止“新建后倒补历史提醒”：把最近一次已过时刻标记为已处理
            var occ = item.LatestOccurrenceAtOrBefore(now);
            if (occ != null) item.LastFiredKey = TaskItem.OccurrenceKey(occ.Value);
            Tasks.Add(item);
        }
        else
        {
            existing.Title = item.Title;
            existing.Mode = item.Mode;
            existing.IntervalDays = item.IntervalDays;
            existing.WeekMask = item.WeekMask;
            existing.Hour = item.Hour;
            existing.Minute = item.Minute;
            existing.Enabled = item.Enabled;
            existing.OnceDate = item.OnceDate;   // 支持在“重复任务 ↔ 仅一次”之间切换编辑的语义
            var occ = existing.LatestOccurrenceAtOrBefore(now);
            if (occ != null && existing.LastFiredKey == null)
                existing.LastFiredKey = TaskItem.OccurrenceKey(occ.Value);
        }
        TouchTasks();
    }

    public void Remove(TaskItem item) => Ui(() =>
    {
        Tasks.Remove(item);
        TouchTasks();
    });

    public void ToggleDone(TaskItem t) => Ui(() =>
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        t.DoneDate = t.DoneDate == today ? null : today;
        TouchTasks();
    });

    /// <summary>清理跨天残留的“今日完成”标记</summary>
    public void NormalizeDoneDates() => Ui(() =>
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        bool changed = false;
        foreach (var t in Tasks)
        {
            if (t.DoneDate != null && t.DoneDate != today)
            {
                t.DoneDate = null;
                changed = true;
            }
        }
        if (changed) TouchTasks();
    });

    /// <summary>提醒触发后立即落盘（关键路径，单条 UPDATE，微秒级）</summary>
    public void PersistFired()
    {
        _tasksDirty = true;
        SaveNow();
    }

    public void TouchTasks()
    {
        _tasksDirty = true;
        EnsureFlushTimer();
        Changed?.Invoke();
    }

    public void TouchSettings()
    {
        _settingsDirty = true;
        EnsureFlushTimer();
    }

    private void EnsureFlushTimer()
    {
        if (_flushTimer.IsEnabled) return;
        if (_flushTimer.Dispatcher.CheckAccess())
        {
            _flushTimer.Start();
        }
        else
        {
            // 后台线程（AI 工具）触发：切回 UI 线程启动防抖计时器
            try { _flushTimer.Dispatcher.BeginInvoke(new Action(() => { if (!_flushTimer.IsEnabled) _flushTimer.Start(); })); }
            catch { }
        }
    }

    // ---------------- 写盘 ----------------

    public void SaveNowBestEffort()
    {
        try { SaveNow(); } catch { }
    }

    public bool SaveNow()
    {
        if (!_tasksDirty && !_settingsDirty) return true;
        try
        {
            if (_tasksDirty)
            {
                WriteTasks();
                _tasksDirty = false;
            }
            if (_settingsDirty)
            {
                _db.SetKv(SettingsKvKey, JsonSerializer.Serialize(Settings, JsonOpts));
                _settingsDirty = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存数据失败（稍后自动重试）", ex);
            return false;
        }
    }

    /// <summary>整表对齐写入：单事务内删除多余行 + 更新/插入当前集合</summary>
    private void WriteTasks()
    {
        var snapshot = Tasks.ToList();
        _db.ExecBatch((conn, tx) =>
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tasks";
                del.ExecuteNonQuery();
            }
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO tasks
                      (id,title,mode,interval_days,week_mask,hour,minute,enabled,anchor_date,last_fired_key,done_date,once_date,sort_order)
                    VALUES ($id,$title,$mode,$iv,$wm,$h,$m,$en,$anchor,$lf,$dd,$once,$ord)
                    """;
                var pId = ins.CreateParameter(); pId.ParameterName = "$id";
                var pTitle = ins.CreateParameter(); pTitle.ParameterName = "$title";
                var pMode = ins.CreateParameter(); pMode.ParameterName = "$mode";
                var pIv = ins.CreateParameter(); pIv.ParameterName = "$iv";
                var pWm = ins.CreateParameter(); pWm.ParameterName = "$wm";
                var pH = ins.CreateParameter(); pH.ParameterName = "$h";
                var pM = ins.CreateParameter(); pM.ParameterName = "$m";
                var pEn = ins.CreateParameter(); pEn.ParameterName = "$en";
                var pAnchor = ins.CreateParameter(); pAnchor.ParameterName = "$anchor";
                var pLf = ins.CreateParameter(); pLf.ParameterName = "$lf";
                var pDd = ins.CreateParameter(); pDd.ParameterName = "$dd";
                var pOnce = ins.CreateParameter(); pOnce.ParameterName = "$once";
                var pOrd = ins.CreateParameter(); pOrd.ParameterName = "$ord";
                ins.Parameters.AddRange(new[] { pId, pTitle, pMode, pIv, pWm, pH, pM, pEn, pAnchor, pLf, pDd, pOnce, pOrd });

                int ord = 0;
                foreach (var t in snapshot)
                {
                    pId.Value = t.Id;
                    pTitle.Value = t.Title;
                    pMode.Value = (int)t.Mode;
                    pIv.Value = t.IntervalDays;
                    pWm.Value = t.WeekMask;
                    pH.Value = t.Hour;
                    pM.Value = t.Minute;
                    pEn.Value = t.Enabled ? 1 : 0;
                    pAnchor.Value = t.AnchorDate;
                    pLf.Value = (object?)t.LastFiredKey ?? DBNull.Value;
                    pDd.Value = (object?)t.DoneDate ?? DBNull.Value;
                    pOnce.Value = (object?)t.OnceDate ?? DBNull.Value;
                    pOrd.Value = ord++;
                    ins.ExecuteNonQuery();
                }
            }
        });
    }

    private void TryQuarantine(string path, bool onlyLegacy = false)
    {
        try
        {
            if (!File.Exists(path)) return;
            var q = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, q);
            AppLogger.Warn($"损坏文件已隔离：{q}");
        }
        catch { }
    }

    /// <summary>退出流程调用：最后一次落盘（数据库连接由 App 统一释放）</summary>
    public void Dispose()
    {
        try { SaveNow(); } catch { }
    }
}
