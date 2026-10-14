using System.IO;
using DeepSeekPet.Config;

namespace DeepSeekPet.Services;

/// <summary>
/// 配置服务：加载 appconfig.json（不存在时生成带注释说明的默认文件）、
/// 环境变量覆盖、监听文件变更热更新（免重启生效）。
/// 线程模型：Load/Save 在调用线程执行；Changed 事件在后台线程触发，
/// 订阅方自行切回 UI 线程。
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private DateTime _lastSelfWrite = DateTime.MinValue;

    public AppConfig Current { get; private set; } = new();
    public string ConfigPath => _path;

    /// <summary>配置发生变更（热更新或手动保存后触发）</summary>
    public event Action? Changed;

    public ConfigService(string dataDir)
    {
        _path = Path.Combine(dataDir, "appconfig.json");
        Load();
        StartWatch();
    }

    public void Load()
    {
        try
        {
            AppConfig cfg;
            if (File.Exists(_path))
            {
                cfg = AppConfig.FromJson(File.ReadAllText(_path)) ?? new AppConfig();
            }
            else
            {
                cfg = new AppConfig();
                // 首次生成：写一份带字段说明的默认配置，方便用户直接编辑
                try { File.WriteAllText(_path, BuildDefaultTemplate(cfg)); }
                catch (Exception ex) { AppLogger.Warn($"生成默认配置失败：{ex.Message}"); }
            }
            cfg.Normalize();
            cfg.ApplyEnvironment();
            lock (_lock) Current = cfg;
        }
        catch (Exception ex)
        {
            AppLogger.Error("读取配置失败，使用默认值", ex);
            var cfg = new AppConfig();
            cfg.Normalize();
            cfg.ApplyEnvironment();
            lock (_lock) Current = cfg;
        }
    }

    /// <summary>手动保存（UI 修改配置时调用）；会触发 Changed</summary>
    public void Save(AppConfig cfg)
    {
        try
        {
            cfg.Normalize();
            _lastSelfWrite = DateTime.Now;
            File.WriteAllText(_path, cfg.ToJson());
            lock (_lock) Current = cfg;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存配置失败", ex);
        }
    }

    private void StartWatch()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            // 编辑器保存常触发多次事件：统一交给防抖处理
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
            _watcher.Renamed += (_, _) => DebouncedReload();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"配置文件监听启动失败（热更新不可用）：{ex.Message}");
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
                // 忽略自己保存触发的回声
                if ((DateTime.Now - _lastSelfWrite).TotalSeconds < 2) return;
                AppLogger.Info("检测到 appconfig.json 变更，热更新配置");
                Load();
                Changed?.Invoke();
            };
            _debounce.Start();
        }
    }

    private static string BuildDefaultTemplate(AppConfig cfg)
    {
        return $$"""
        {
          "_说明": "配置修改后自动生效（免重启）。API Key 也可用环境变量：DEEPSEEK_PET_API_KEY / TAVILY_API_KEY。",
          "apiKey": "{{cfg.ApiKey}}",
          "baseUrl": "{{cfg.BaseUrl}}",
          "model": "{{cfg.Model}}",
          "timeoutSeconds": {{cfg.TimeoutSeconds}},
          "temperature": {{cfg.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "memoryRounds": {{cfg.MemoryRounds}},
          "_搜索说明": "searchProvider 可选 tavily / searxng / none；searxng 只需填 searxngUrl",
          "searchProvider": "{{cfg.SearchProvider}}",
          "searchApiKey": "{{cfg.SearchApiKey}}",
          "searxngUrl": "{{cfg.SearxngUrl}}",
          "searchCacheMinutes": {{cfg.SearchCacheMinutes}},
          "searchMaxResults": {{cfg.SearchMaxResults}},
          "enableWebSearch": {{(cfg.EnableWebSearch ? "true" : "false")}},
          "maxToolCallsPerTurn": {{cfg.MaxToolCallsPerTurn}},
          "llmReminderPhrase": {{(cfg.LlmReminderPhrase ? "true" : "false")}},
          "personaPath": "{{cfg.PersonaPath}}"
        }
        """;
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        lock (_debounceLock) { try { _debounce?.Dispose(); } catch { } }
    }
}
