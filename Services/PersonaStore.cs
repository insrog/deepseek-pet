using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeepSeekPet.Models;

namespace DeepSeekPet.Services;

/// <summary>
/// 人设卡服务：加载 persona.json（不存在时生成鲸鱼娘默认预设）、
/// 监听文件变更免重启生效、维护动态状态（心情/好感度，持久化在数据库）。
///
/// 注入 LLM 的只有「精简摘要 + 动态状态」（见 BuildSummary / AttachState）。
/// </summary>
public sealed class PersonaStore : IDisposable
{
    private const string StateKvKey = "persona_state";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Db _db;
    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private DateTime _lastSelfWrite = DateTime.MinValue;

    public string PersonaPath { get; }
    public Persona Current { get; private set; } = Persona.DefaultWhale();

    /// <summary>人设文件或动态状态变更（后台线程触发）</summary>
    public event Action? Changed;

    public PersonaStore(Db db, string? customPath, string dataDir)
    {
        _db = db;
        PersonaPath = string.IsNullOrWhiteSpace(customPath)
            ? Path.Combine(dataDir, "persona.json")
            : customPath!;
        Load();
        StartWatch();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(PersonaPath))
            {
                var p = Persona.FromJson(File.ReadAllText(PersonaPath, Encoding.UTF8));
                if (p != null)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "鲸鱼娘";
                    if (string.IsNullOrWhiteSpace(p.Greeting)) p.Greeting = "咕噜咕噜～我在这里哦。";
                    lock (_lock) Current = p;
                    return;
                }
                AppLogger.Warn("人设卡 JSON 解析失败，使用内置默认预设");
            }
            else
            {
                // 首次运行：落盘一份鲸鱼娘预设，方便用户直接编辑
                var def = Persona.DefaultWhale();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PersonaPath)!);
                    _lastSelfWrite = DateTime.Now;
                    File.WriteAllText(PersonaPath, AddComment(def.ToJson()), Utf8NoBom);
                }
                catch (Exception ex) { AppLogger.Warn($"写入默认人设卡失败：{ex.Message}"); }
                lock (_lock) Current = def;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("读取人设卡失败，使用内置默认预设", ex);
            lock (_lock) Current = Persona.DefaultWhale();
        }
    }

    /// <summary>写入文件的版本带一行使用说明注释（读取时自动忽略）</summary>
    private static string AddComment(string json)
    {
        const string note = "// 鲸鱼娘人设卡：直接编辑本文件，保存后立即生效（免重启）。summary 留空则自动生成精简摘要。\n";
        return note + json;
    }

    private void StartWatch()
    {
        try
        {
            var dir = Path.GetDirectoryName(PersonaPath)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(PersonaPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
            _watcher.Renamed += (_, _) => DebouncedReload();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"人设卡监听启动失败（热更新不可用）：{ex.Message}");
        }
    }

    private readonly object _debounceLock = new();
    private System.Timers.Timer? _debounce;

    private void DebouncedReload()
    {
        lock (_debounceLock)
        {
            _debounce?.Stop();
            _debounce?.Dispose();
            _debounce = new System.Timers.Timer(600) { AutoReset = false };
            _debounce.Elapsed += (_, _) =>
            {
                if ((DateTime.Now - _lastSelfWrite).TotalSeconds < 2) return;
                AppLogger.Info("检测到人设卡变更，热更新");
                Load();
                Changed?.Invoke();
            };
            _debounce.Start();
        }
    }

    /// <summary>保存人设卡（设置界面 / 程序化修改用）：写文件 + 立即生效，返回是否成功</summary>
    public bool Save(Persona p)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "鲸鱼娘";
            if (string.IsNullOrWhiteSpace(p.Greeting)) p.Greeting = "咕噜咕噜～我在这里哦。";

            _lastSelfWrite = DateTime.Now;
            var dir = Path.GetDirectoryName(PersonaPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(PersonaPath, AddComment(p.ToJson()), Utf8NoBom);

            lock (_lock) Current = p;
            Changed?.Invoke();
            AppLogger.Info("人设卡已保存（立即生效）");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存人设卡失败", ex);
            return false;
        }
    }

    // ---------------- 动态状态 ----------------

    public PersonaState GetState()
    {
        try
        {
            var json = _db.GetKv(StateKvKey);
            if (json != null)
                return JsonSerializer.Deserialize<PersonaState>(json, Persona.JsonOpts) ?? new PersonaState();
        }
        catch { }
        return new PersonaState();
    }

    public void SaveState(PersonaState state)
    {
        try
        {
            state.UpdatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _db.SetKv(StateKvKey, JsonSerializer.Serialize(state, Persona.JsonOpts));
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存动态状态失败", ex);
        }
    }

    // ---------------- 摘要构建 ----------------

    /// <summary>生成注入 LLM 的人设精简摘要（总长控制在 ~500 字内）</summary>
    public string BuildSummary()
    {
        Persona p;
        lock (_lock) p = Current;

        if (!string.IsNullOrWhiteSpace(p.Summary))
            return p.Summary.Trim();

        var sb = new StringBuilder();
        sb.Append($"你是「{p.Name}」——{p.Creature.Trim()}。");
        if (!string.IsNullOrWhiteSpace(p.Personality))
            sb.Append($"\n【性格】{Clip(p.Personality, 180)}");
        if (!string.IsNullOrWhiteSpace(p.SpeechStyle))
            sb.Append($"\n【说话风格】{Clip(p.SpeechStyle, 150)}");
        if (!string.IsNullOrWhiteSpace(p.Background))
            sb.Append($"\n【背景】{Clip(p.Background, 120)}");
        if (p.Rules.Count > 0)
            sb.Append("\n【必须遵守】\n- " + string.Join("\n- ", p.Rules.Take(6)).Replace("\n", "\n  "));
        if (p.Catchphrases.Count > 0)
            sb.Append($"\n【口头禅参考】{string.Join("；", p.Catchphrases.Take(4))}");
        return sb.ToString();
    }

    private static string Clip(string s, int max)
    {
        s = s.Trim().Replace("\r", "");
        return s.Length <= max ? s : s[..max] + "…";
    }

    public string Greeting
    {
        get { lock (_lock) return Current.Greeting; }
    }

    public string Name
    {
        get { lock (_lock) return Current.Name; }
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        lock (_debounceLock) { try { _debounce?.Dispose(); } catch { } }
    }
}
