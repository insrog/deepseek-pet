using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DeepSeekPet.Agent;
using DeepSeekPet.Models;
using DeepSeekPet.Services;
using DeepSeekPet.Windows;

namespace DeepSeekPet;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _wakeEvent;
    private TaskPanel? _panel;
    private int _fatalCount;
    private DateTime _fatalWindowStart = DateTime.MinValue;

    public static new App Current => (App)Application.Current;

    // ---- 分层组件（App 统一持有与释放） ----
    public Db Database { get; private set; } = null!;
    public ConfigService Config { get; private set; } = null!;
    public PersonaStore Persona { get; private set; } = null!;
    public TaskStore Store { get; private set; } = null!;
    public AgentService? Agent { get; private set; }
    public Scheduler Scheduler { get; private set; } = null!;
    public TrayService? Tray { get; private set; }
    public PetWindow? Pet { get; private set; }
    public SpeechBubble? SayBubble { get; private set; }
    public bool IsExiting { get; private set; }
    public TaskPanel? PanelOrNull => _panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ---- 单实例：重复启动只唤醒已有实例 ----
        _mutex = new Mutex(true, @"Local\DeepSeekPet_SingleInstance", out bool isNew);
        if (!isNew)
        {
            try
            {
                using var w = EventWaitHandle.OpenExisting(@"Local\DeepSeekPet_Wake");
                w.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        try { _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeepSeekPet_Wake"); }
        catch { }

        // ---- 错误监控：任何层级的异常都不静默丢失 ----
        DispatcherUnhandledException += (_, args) => args.Handled = HandleFatal(args.Exception, "界面线程异常");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Error("未捕获异常", args.ExceptionObject as Exception);
            Store?.SaveNowBestEffort();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("后台任务异常", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Store?.SaveNowBestEffort();

        // ---- 参数：--data <目录>  --delay <秒>  --autostart ----
        string? dataDir = null;
        int delay = 0;
        var args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length) dataDir = args[++i];
            else if (args[i] == "--delay" && i + 1 < args.Length && int.TryParse(args[i + 1], out var d))
            {
                delay = Math.Clamp(d, 0, 120);
                i++;
            }
        }

        var resolvedDataDir = dataDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekPet");

        AppLogger.Init(dataDir);

        // ---- 数据层：SQLite（单连接全应用复用）→ 配置 → 任务 → 人设 ----
        try
        {
            Database = new Db(Path.Combine(resolvedDataDir, "pet.db"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("数据库初始化失败，改用内存库兜底", ex);
            Database = new Db(Path.Combine(Path.GetTempPath(), "DeepSeekPet_fallback.db"));
        }

        Config = new ConfigService(resolvedDataDir);
        Store = new TaskStore(Database, dataDir);
        Store.Load();
        Store.Changed += OnStoreChanged;
        Persona = new PersonaStore(Database, Config.Current.PersonaPath, Store.DataDir);
        AutoStart.RefreshIfEnabled();

        // ---- 对话 Agent（后台线程处理，事件回调切回 UI 线程） ----
        try
        {
            Agent = new AgentService(Config, Persona, Store, Database, Dispatcher);
            Agent.RestoreMemory();
        }
        catch (Exception ex)
        {
            AppLogger.Error("对话 Agent 初始化失败（不影响任务提醒功能）", ex);
            Agent = null;
        }

        // ---- 托盘先出（开机自启时 1-2 秒内即可见） ----
        try
        {
            Tray = new TrayService(this);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"托盘图标创建失败：{ex.Message}");
        }

        AppLogger.Info($"DeepSeekPet v{AppVersion()} 启动，数据目录：{Store.DataDir}，API Key：{(Config.Current.HasApiKey ? "已配置" : "未配置")}");

        if (delay > 0)
        {
            // 开机自启：先静默等待，再弹出界面（低开机感知）
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (s, _) =>
            {
                t.Stop();
                StartUi();
            };
            t.Start();
        }
        else
        {
            StartUi();
        }

        if (_wakeEvent != null)
        {
            ThreadPool.RegisterWaitForSingleObject(_wakeEvent,
                (_, _) =>
                {
                    try { Dispatcher.BeginInvoke(new Action(() => Pet?.Nudge())); } catch { }
                },
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>当前程序集版本（形如 1.2.0），用于日志与关于页</summary>
    public static string AppVersion()
    {
        try { return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.2"; }
        catch { return "1.2"; }
    }

    private void StartUi()
    {
        try
        {
            Pet = new PetWindow(Store);
            MainWindow = Pet;
            Pet.Show();

            try { SayBubble = new SpeechBubble(this); } catch (Exception ex) { AppLogger.Warn($"气泡窗口初始化失败：{ex.Message}"); }

            Scheduler = new Scheduler(Store);
            Scheduler.ReminderDue += OnReminderDue;
            Scheduler.Start();

            AppLogger.Info("界面启动完成");
        }
        catch (Exception ex)
        {
            AppLogger.Error("启动界面失败", ex);
            ExitApp();
        }
    }

    private void OnStoreChanged()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnStoreChanged));
                return;
            }
            Scheduler?.Rearm();
            _panel?.Refresh();
        }
        catch (Exception ex)
        {
            AppLogger.Error("响应数据变更失败", ex);
        }
    }

    private void OnReminderDue(List<TaskItem> tasks)
    {
        try
        {
            if (Store.Settings.Sound)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
            Pet?.Alert(tasks);
            EnsurePanel().ShowHighlight(tasks.ConvertAll(t => t.Id));

            // 1) 模板句立刻"说话"（零延迟，保证提醒必达）
            var quick = tasks.Count == 1
                ? $"叮咚～到「{tasks[0].Title}」的时间啦！"
                : $"叮咚～有 {tasks.Count} 件事到点了：{string.Join("、", tasks.ConvertAll(t => t.Title))}";
            SayBubble?.ShowText(quick);

            // 2) LLM 人设化提醒语（异步替换；失败/未配置自动保持模板句，绝不影响提醒本身）
            if (Config.Current.LlmReminderPhrase && Agent != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var phrase = tasks.Count == 1
                            ? await Agent.GenerateReminderPhraseAsync(tasks[0])
                            : await Agent.GenerateReminderPhraseAsync(tasks[0]) + $"（另外还有 {tasks.Count - 1} 件事也到点啦）";
                        if (!IsExiting)
                            _ = Dispatcher.BeginInvoke(new Action(() => SayBubble?.ShowText(phrase)));
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("提醒展示失败", ex);
        }
    }

    public TaskPanel EnsurePanel()
    {
        return _panel ??= new TaskPanel(Store, this);
    }

    public void TogglePanel()
    {
        var p = EnsurePanel();
        if (p.IsOpen) p.HidePanel();
        else p.ShowPanel(follow: true);
    }

    /// <summary>打开设置窗口并聚焦聊天输入框（托盘 / 桌宠气泡点击入口）</summary>
    public void ShowPanelAndFocus()
    {
        var p = EnsurePanel();
        if (!p.IsOpen) p.ShowPanel(follow: true);
        p.FocusInput();
        Pet?.KeepAwake();
    }

    private SettingsWindow? _settings;

    /// <summary>打开设置窗口（单例复用；tab 可选 ai / search / persona / about）</summary>
    public void ShowSettings(string? tab = null)
    {
        try
        {
            if (_settings == null) _settings = new SettingsWindow(this);
            _settings.ShowTab(tab);
            if (!_settings.IsVisible) _settings.Show();
            _settings.Activate();
            _settings.Topmost = true;   // 与桌宠同为置顶，保证交互时可见
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开设置窗口失败", ex);
        }
    }

    /// <summary>返回 true 表示异常已被吸收，程序继续运行</summary>
    private bool HandleFatal(Exception ex, string title)
    {
        AppLogger.Error(title, ex);
        var now = DateTime.Now;
        if ((now - _fatalWindowStart).TotalSeconds > 60)
        {
            _fatalWindowStart = now;
            _fatalCount = 0;
        }
        if (++_fatalCount > 15)
        {
            AppLogger.Error("界面错误过于频繁，安全退出");
            ExitApp();
        }
        return true;
    }

    /// <summary>系统关机 / 注销：第一时间落盘，防止数据异常丢失</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppLogger.Info("系统会话结束，执行落盘保护");
        try { Store?.SaveNowBestEffort(); } catch { }
        base.OnSessionEnding(e);
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { Store?.SaveNowBestEffort(); } catch { }
        try { Agent?.Dispose(); } catch { }
        try { Scheduler?.Dispose(); } catch { }
        try { _settings?.Close(); } catch { }
        try { _panel?.ForceClose(); } catch { }
        try { SayBubble?.HideBubble(); } catch { }
        try { Pet?.ReleaseForExit(); } catch { }
        AppLogger.Info("正常退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Store?.SaveNowBestEffort(); } catch { }
        try { Agent?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { Config?.Dispose(); } catch { }
        try { Persona?.Dispose(); } catch { }
        try { Database?.Dispose(); } catch { }
        try { _wakeEvent?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        AppLogger.Info("进程退出");
        base.OnExit(e);
    }
}
