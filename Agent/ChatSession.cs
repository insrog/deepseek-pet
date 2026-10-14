using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent;

/// <summary>
/// 短期记忆会话：内存中维护最近 N 轮对话（一轮 = 用户 + 助手），
/// - 组装 LLM 上下文时按 memoryRounds 裁剪（默认 24 轮，需求 20-30）；
/// - 每条对话结束后写入 SQLite，重启后自动恢复最近若干轮；
/// - 不做长期向量记忆（需求明确）。
/// 线程模型：所有方法由 AgentService 串行调用（同一后台线程），内部不再加锁。
/// </summary>
public sealed class ChatSession
{
    private readonly ChatStore _store;
    private readonly List<ChatMessage> _history = new();

    public ChatSession(ChatStore store) => _store = store;

    public int MemoryRounds { get; set; } = 24;

    /// <summary>恢复最近对话（应用启动时调用一次）</summary>
    public void Restore()
    {
        _history.Clear();
        var recent = _store.LoadRecent(MemoryRounds * 2);
        _history.AddRange(recent);
        if (_history.Count > 0)
            AppLogger.Info($"已恢复最近 {_history.Count} 条对话记录（{_history.Count / 2} 轮）");
    }

    /// <summary>追加一条并持久化</summary>
    public void Append(string role, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        var msg = new ChatMessage
        {
            Role = role,
            Content = content,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds()
        };
        _history.Add(msg);
        _store.Append(role, content);
        TrimMemory();
    }

    /// <summary>仅追加到内存（不落库；用于流式失败丢弃的半截回复）</summary>
    public void AppendMemoryOnly(string role, string content)
    {
        _history.Add(new ChatMessage { Role = role, Content = content, CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        TrimMemory();
    }

    /// <summary>内存裁剪：最多保留 memoryRounds 轮（数据库侧另有更大的环形上限）</summary>
    private void TrimMemory()
    {
        int max = MemoryRounds * 2;
        if (_history.Count > max)
            _history.RemoveRange(0, _history.Count - max);
    }

    /// <summary>构建发给 LLM 的历史消息（时间正序，老→新）</summary>
    public List<LlmMessage> BuildHistoryMessages()
    {
        var list = new List<LlmMessage>(_history.Count);
        foreach (var m in _history)
        {
            if (string.IsNullOrEmpty(m.Content)) continue;
            list.Add(new LlmMessage { Role = m.Role, Content = m.Content });
        }
        return list;
    }

    public int Count => _history.Count;

    public List<ChatMessage> Snapshot() => _history.ToList();

    /// <summary>清空历史（内存 + 数据库）</summary>
    public void Clear()
    {
        _history.Clear();
        _store.Clear();
    }

    /// <summary>最近一条助手消息（UI 首次展开时用于气泡回显）</summary>
    public string? LastAssistantMessage()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
            if (_history[i].Role == "assistant" && !string.IsNullOrWhiteSpace(_history[i].Content))
                return _history[i].Content;
        return null;
    }
}
