using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DeepSeekPet.Agent.Tools;
using DeepSeekPet.Config;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent;

/// <summary>
/// 对话 Agent 编排层：
/// - 意图双层分流：本地规则（命中直接走任务系统）→ LLM Function Calling（兜底/闲聊）；
/// - 全部处理在后台线程（Task.Run），UI 只收事件回调（已切回 UI 线程）；
/// - 单轮工具调用次数受限（maxToolCallsPerTurn，需求 ≤2），超限后强制收尾；
/// - 看门狗：处理超时强制取消，防止线程卡死；
/// - 统一异常吞并转错误提示，绝不让异常冒到 UI。
/// </summary>
public sealed class AgentService : IDisposable
{
    private readonly ConfigService _config;
    private readonly PersonaStore _persona;
    private readonly TaskStore _store;
    private readonly LlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly Dispatcher _ui;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _currentCts;
    private int _userTurns;
    private long _processingSinceTicks;
    private volatile bool _disposed;
    private readonly DispatcherTimer _watchdog;

    // ---- 事件（全部在 UI 线程触发） ----

    /// <summary>流式增量文本（逐 token）</summary>
    public event Action<string>? DeltaReceived;

    /// <summary>一条完整回复就绪</summary>
    public event Action<string>? ResponseCompleted;

    /// <summary>状态提示（"思考中…" / null 表示清除）</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>错误提示（面向用户的人设化文案）</summary>
    public event Action<string>? ErrorOccurred;

    public ChatSession Session { get; }
    public bool IsBusy { get; private set; }
    public bool HasApiKey => _llm.HasApiKey;

    public AgentService(ConfigService config, PersonaStore persona, TaskStore store, Db db, Dispatcher ui)
    {
        _config = config;
        _persona = persona;
        _store = store;
        _ui = ui;
        _llm = new LlmClient(config.Current);
        var search = new SearchClient(db, config.Current);
        _tools = new ToolRegistry();
        _tools.Register(new AddReminderTool(store));
        _tools.Register(new GetTasksTool(store));
        _tools.Register(new WebSearchTool(search));

        Session = new ChatSession(new ChatStore(db)) { MemoryRounds = config.Current.MemoryRounds };

        // 配置热更新 → 同步到客户端
        _config.Changed += () =>
        {
            _llm.UpdateConfig(_config.Current);
            search.UpdateConfig(_config.Current);
            Session.MemoryRounds = _config.Current.MemoryRounds;
        };

        // 看门狗：60s 检查一次，单次处理超过 3 分钟强制取消
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _watchdog.Tick += (_, _) => WatchdogTick();
        _watchdog.Start();
    }

    /// <summary>启动时恢复短期记忆（重启不丢最近对话）</summary>
    public void RestoreMemory()
    {
        try { Session.Restore(); }
        catch (Exception ex) { AppLogger.Error("恢复对话记忆失败", ex); }
    }

    // ---------------- 对外入口（UI 线程调用） ----------------

    /// <summary>
    /// 发送一条用户消息。立即返回；处理在后台线程串行执行。
    /// 若已有请求在处理，会先取消它（对话软件的自然行为）。
    /// </summary>
    public void Send(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length == 0) return;

        try { _currentCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        _currentCts = cts;

        _ = Task.Run(() => ProcessSafeAsync(text, cts));
    }

    /// <summary>取消当前进行中的请求（UI“停止”按钮）</summary>
    public void CancelCurrent()
    {
        try { _currentCts?.Cancel(); } catch { }
    }

    /// <summary>清空对话记忆（内存 + 数据库）</summary>
    public void ClearMemory()
    {
        try { Session.Clear(); } catch (Exception ex) { AppLogger.Error("清空对话记忆失败", ex); }
    }

    private async Task ProcessSafeAsync(string text, CancellationTokenSource cts)
    {
        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { return; }

        try
        {
            if (cts.IsCancellationRequested) return;   // 已被更新的消息取代
            await ProcessAsync(text, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常打断，不提示
        }
        catch (Exception ex)
        {
            AppLogger.Error("Agent 处理异常", ex);
            NotifyError("呜呜，我这边出了点小状况…稍后再试一次吧~");
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _processingSinceTicks, 0);
            SetStatus(null);
            try { _gate.Release(); } catch { }
        }
    }

    // ---------------- 主流程 ----------------

    private async Task ProcessAsync(string text, CancellationToken ct)
    {
        IsBusy = true;
        Interlocked.Exchange(ref _processingSinceTicks, DateTime.Now.Ticks);
        SetStatus("思考中…");

        Session.Append("user", text);

        // ---- 第一层：本地规则（完全可解析 → 直接打通任务系统，零网络延迟） ----
        var route = IntentRouter.Classify(text, out var draft);
        if (route == RouteKind.LocalReminder && draft != null)
        {
            AppLogger.Info($"本地规则分流：直接创建提醒「{draft.Title}」");
            var result = await _tools.ExecuteAsync("add_reminder", draft.ToArgsJson(), ct).ConfigureAwait(false);
            Session.Append("assistant", result);
            CompleteResponse(result);
            return;
        }

        // ---- 未配置 Key：友好提示（提醒创建功能仍可用） ----
        if (!_llm.HasApiKey)
        {
            var tip = "我还没有连接上大脑呢（未配置 API Key）。\n" +
                      "请在 appconfig.json 里填入 apiKey（或设置环境变量 DEEPSEEK_PET_API_KEY）后，我就能陪你聊天、查资料啦~\n" +
                      "不过现在也可以直接对我说「提醒我 9 点喝水」这样的指令哦。";
            Session.Append("assistant", tip);
            CompleteResponse(tip);
            return;
        }

        // ---- 第二层：LLM（带工具） ----
        await RunLlmLoopAsync(ct).ConfigureAwait(false);

        // ---- 低频更新角色动态状态（每 5 轮，后台进行，不阻塞对话） ----
        if (Interlocked.Increment(ref _userTurns) % 5 == 0)
            _ = Task.Run(UpdateStateAsync);
    }

    /// <summary>LLM 对话主循环（含工具调用轮，≤ maxToolCallsPerTurn 次）</summary>
    private async Task RunLlmLoopAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var messages = new List<LlmMessage> { LlmMessage.System(BuildSystemPrompt()) };
        messages.AddRange(Session.BuildHistoryMessages());

        var tools = _tools.BuildSchemas(cfg);
        int maxRounds = cfg.MaxToolCallsPerTurn;
        int round = 0;
        var fullText = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var useTools = tools.Count > 0 && round < maxRounds;
            if (round == 0) SetStatus("思考中…");
            else SetStatus(useTools ? "正在查资料…" : "整理回答…");

            var r = await _llm.StreamAsync(
                messages,
                delta =>
                {
                    fullText.Append(delta);
                    InvokeUi(() => DeltaReceived?.Invoke(delta));
                },
                useTools ? tools : null,
                ct).ConfigureAwait(false);

            if (r.Canceled)
            {
                AppLogger.Info("LLM 请求被取消（新消息打断或用户停止）");
                return;
            }

            if (!r.Success)
            {
                var err = r.Error ?? "未知错误";
                AppLogger.Warn($"LLM 请求失败：{err}");
                var partial = fullText.ToString().Trim();
                var msg = partial.Length > 0
                    ? partial + $"\n\n（呜…后面的话被海浪吞掉了：{err}）"
                    : $"唔…我暂时联系不上大脑（{Shorten(err, 120)}）。检查一下网络或 appconfig.json 里的配置吧~";
                Session.Append("assistant", msg);
                CompleteResponse(msg);
                return;
            }

            // ---- 无工具调用 → 最终回复 ----
            if (r.ToolCalls.Count == 0)
            {
                var reply = fullText.ToString().Trim();
                if (reply.Length == 0) reply = "（咕噜咕噜…我好像走神了，再说一次好吗~）";
                Session.Append("assistant", reply);
                CompleteResponse(reply);
                return;
            }

            // ---- 有工具调用：执行后继续对话 ----
            messages.Add(new LlmMessage
            {
                Role = "assistant",
                Content = fullText.Length > 0 ? fullText.ToString() : null,
                ToolCalls = r.ToolCalls
            });

            foreach (var tc in r.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                SetStatus($"正在使用{FriendlyToolName(tc.Name)}…");
                var result = await _tools.ExecuteAsync(tc.Name, tc.Arguments, ct).ConfigureAwait(false);
                messages.Add(LlmMessage.Tool(tc.Id, result));
            }

            round++;
            if (round >= maxRounds)
            {
                AppLogger.Info($"工具调用达到上限（{maxRounds}），下一轮强制收尾");
                // 追加一条提醒，引导模型基于已有信息总结
                messages.Add(LlmMessage.System("（已达到工具调用次数上限，请基于以上结果直接回复用户，不要再调用工具。）"));
            }
        }
    }

    // ---------------- 系统提示词 ----------------

    private string BuildSystemPrompt()
    {
        var cfg = _config.Current;
        var state = _persona.GetState();
        var now = DateTime.Now;

        var sb = new StringBuilder();
        sb.Append(_persona.BuildSummary());

        sb.Append($"\n\n【当前状态】心情：{state.Mood}｜好感度：{state.Affection}/100");
        sb.Append($"\n【当前时间】{now:yyyy-MM-dd HH:mm} 周{WeekName(now.DayOfWeek)}");

        var overview = TodayOverview();
        if (!string.IsNullOrEmpty(overview))
            sb.Append($"\n【今日提醒】{overview}");

        sb.Append("\n\n【你可以使用工具】");
        sb.Append("\n- add_reminder：帮用户创建定时提醒（支持 每天 / 每N天 / 每周几 / 仅一次）");
        sb.Append("\n- get_tasks：查询用户的待办与提醒列表");
        if (cfg.EnableWebSearch && cfg.SearchProvider != "none" && cfg.SearchUsable(cfg.SearchProvider))
            sb.Append("\n- web_search：联网搜索实时信息（新闻、天气、价格、资料）");
        sb.Append("\n用户请求适合用工具时，直接调用工具完成；不要说「要不要我帮你」；工具结果用户看得到，不要假装完成了未做的事。");
        if (!string.IsNullOrEmpty(state.LastNote))
            sb.Append($"\n【你记得的】{state.LastNote}");

        sb.Append("\n\n【对话要求】");
        sb.Append("\n- 用中文回复，保持人设语气，简短自然（一般 1-3 句）");
        sb.Append("\n- 不确定的事实要如实说明，不编造");
        return sb.ToString();
    }

    /// <summary>今日待提醒任务概览（最多 3 条，帮模型快速回答高频问题）</summary>
    private string? TodayOverview()
    {
        try
        {
            var list = _store.Snapshot();
            var now = DateTime.Now;
            var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            var items = list
                .Where(t => t.Enabled && t.DoneDate != todayStr)
                .Select(t => new { t, next = t.NextOccurrence(now) })
                .Where(x => x.next?.Date == DateTime.Today)
                .OrderBy(x => x.next)
                .Take(3)
                .ToList();

            if (items.Count == 0) return "今天没有待提醒的任务";
            var text = string.Join("、", items.Select(x => $"{x.next:HH:mm} {x.t.Title}"));
            return items.Count == 3 ? text + " 等" : text;
        }
        catch { return null; }
    }

    // ---------------- 动态状态维护 ----------------

    private async Task UpdateStateAsync()
    {
        try
        {
            if (_disposed || !_llm.HasApiKey) return;
            var old = _persona.GetState();
            var recent = Session.BuildHistoryMessages();
            if (recent.Count == 0) return;
            if (recent.Count > 10) recent = recent.Skip(recent.Count - 10).ToList();

            var sys =
                "你是角色状态维护器。阅读以下对话，输出更新后的角色状态 JSON，只输出 JSON、不要任何其他文字：\n" +
                "{\"mood\":\"当前心情（2-6个字）\",\"affection\":好感度0到100整数,\"note\":\"一句对用户的最新印象（不超过20字，可不填）\"}\n" +
                $"当前状态：心情={old.Mood}，好感度={old.Affection}。好感度应缓慢变化（每次不超过±5），用户友好互动则+，被冷落或反感则-。";

            var msgs = new List<LlmMessage> { LlmMessage.System(sys) };
            msgs.AddRange(recent.Select(m => new LlmMessage { Role = m.Role, Content = m.Content }));

            var r = await _llm.CompleteAsync(msgs, CancellationToken.None, temperature: 0.2, maxTokens: 120)
                            .ConfigureAwait(false);
            if (!r.Success) return;

            var json = ExtractJson(r.Content);
            if (json == null) return;
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var mood = node["mood"]?.GetValue<string>()?.Trim();
            int aff = node["affection"]?.GetValue<int>() ?? old.Affection;
            var note = node["note"]?.GetValue<string>()?.Trim();

            // 程序侧再限一次变化幅度，防止跑偏
            aff = Math.Clamp(aff, Math.Max(0, old.Affection - 5), Math.Min(100, old.Affection + 5));

            var ns = new PersonaState
            {
                Mood = string.IsNullOrWhiteSpace(mood) ? old.Mood : (mood!.Length > 12 ? mood[..12] : mood),
                Affection = aff,
                LastNote = string.IsNullOrWhiteSpace(note) ? old.LastNote : note
            };
            _persona.SaveState(ns);
            AppLogger.Info($"角色状态更新：心情={ns.Mood}，好感度={ns.Affection}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"动态状态更新失败（忽略）：{ex.Message}");
        }
    }

    private static string? ExtractJson(string s)
    {
        int a = s.IndexOf('{');
        int b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : null;
    }

    // ---------------- 提醒语生成（定时提醒触发时调用） ----------------

    /// <summary>
    /// 用 LLM 生成人设化的自然提醒语；任何失败自动回退模板（保证提醒永远能发出）。
    /// </summary>
    public async Task<string> GenerateReminderPhraseAsync(TaskItem t)
    {
        var fallback = $"叮咚～到「{t.Title}」的时间啦！";
        try
        {
            var cfg = _config.Current;
            if (!cfg.LlmReminderPhrase || !_llm.HasApiKey) return fallback;

            var state = _persona.GetState();
            var sys = $"{_persona.BuildSummary()}\n\n" +
                      $"【当前状态】心情：{state.Mood}｜好感度：{state.Affection}/100\n" +
                      "现在到了你为提醒用户定好的时间。用一两句符合人设的话提醒用户，直接输出提醒语本身" +
                      "（不要引号、不要表情包堆砌、不超过60字）。";

            var user = $"提醒事项：{t.Title}（原定 {t.Hour:D2}:{t.Minute:D2}，{t.ScheduleText()}）" +
                       $"；当前时间 {DateTime.Now:HH:mm}。";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var r = await _llm.CompleteAsync(
                new List<LlmMessage> { LlmMessage.System(sys), LlmMessage.User(user) },
                cts.Token, temperature: 1.0, maxTokens: 100).ConfigureAwait(false);

            if (r.Success && !string.IsNullOrWhiteSpace(r.Content))
            {
                var s = r.Content.Trim().Trim('"', '\u201c', '\u201d', '\u3002');
                if (s.Length is > 0 and <= 120) return s;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"提醒语生成失败（回退模板）：{ex.Message}");
        }
        return fallback;
    }

    /// <summary>
    /// 连接测试（设置界面用）：用给定配置做一次最小请求，验证密钥 / 端点 / 模型是否可用。
    /// 不影响正在进行的对话，不改变当前运行配置。
    /// </summary>
    public async Task<(bool ok, string message)> TestConnectionAsync(AppConfig cfg)
    {
        try
        {
            if (!cfg.HasApiKey) return (false, "请先填写 API Key");
            var probe = new LlmClient(cfg);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(Math.Max(cfg.TimeoutSeconds, 5), 20)));
            var r = await probe.CompleteAsync(
                new List<LlmMessage> { LlmMessage.User("请只回复两个字：你好") },
                cts.Token, temperature: 0, maxTokens: 16).ConfigureAwait(false);

            if (r.Success)
            {
                var text = (r.Content ?? "").Trim().Replace("\n", " ");
                return (true, text.Length > 0
                    ? $"✓ 连接成功（{cfg.Model}）：{Shorten(text, 16)}"
                    : $"✓ 连接成功（{cfg.Model}）");
            }
            return (false, $"✗ {r.Error ?? "未知错误"}");
        }
        catch (Exception ex)
        {
            return (false, $"✗ {ex.Message}");
        }
    }

    // ---------------- 看门狗 ----------------

    private void WatchdogTick()
    {
        try
        {
            long ticks = Interlocked.Read(ref _processingSinceTicks);
            if (ticks == 0) return;
            var span = DateTime.Now - new DateTime(ticks);
            if (span.TotalSeconds > 180)
            {
                AppLogger.Warn($"看门狗介入：单次处理已超过 {span.TotalSeconds:F0}s，强制取消并复位");
                try { _currentCts?.Cancel(); } catch { }
                Interlocked.Exchange(ref _processingSinceTicks, DateTime.Now.Ticks); // 防重复触发
            }
        }
        catch { }
    }

    // ---------------- 辅助 ----------------

    private void InvokeUi(Action action)
    {
        try
        {
            if (_ui.CheckAccess()) action();
            else _ui.BeginInvoke(action);
        }
        catch { }
    }

    private void SetStatus(string? status) => InvokeUi(() => StatusChanged?.Invoke(status));

    private void CompleteResponse(string text)
    {
        AppLogger.Info($"Agent 回复完成（{text.Length} 字）");
        InvokeUi(() => ResponseCompleted?.Invoke(text));
    }

    private void NotifyError(string text) => InvokeUi(() => ErrorOccurred?.Invoke(text));

    private static string FriendlyToolName(string name) => name switch
    {
        "add_reminder" => "任务本",
        "get_tasks" => "任务本",
        "web_search" => "搜索引擎",
        _ => "工具"
    };

    private static string WeekName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    private static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watchdog.Stop(); } catch { }
        try { _currentCts?.Cancel(); } catch { }
    }
}
