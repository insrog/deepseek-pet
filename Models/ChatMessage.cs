namespace DeepSeekPet.Models;

/// <summary>一条对话消息（持久化到 SQLite，重启后恢复）</summary>
public sealed class ChatMessage
{
    public long Id { get; set; }
    public string Role { get; set; } = "user";   // user / assistant
    public string Content { get; set; } = "";
    public long CreatedAt { get; set; }          // Unix 毫秒

    public DateTime LocalTime => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).LocalDateTime;
}

/// <summary>Agent 动态状态（心情 / 好感度，由 Agent 对话后维护，持久化在数据库）</summary>
public sealed class PersonaState
{
    public string Mood { get; set; } = "平静";
    public int Affection { get; set; } = 50;      // 0-100
    public string? LastNote { get; set; }         // Agent 记录的最近互动印象（一句话）
    public long UpdatedAt { get; set; }
}
