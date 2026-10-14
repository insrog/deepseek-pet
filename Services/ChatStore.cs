using DeepSeekPet.Models;

namespace DeepSeekPet.Services;

/// <summary>
/// 对话记录持久化（SQLite）。重启后按需恢复最近若干条，供短期记忆使用。
/// 存储上限做环形裁剪（默认保留 400 条，避免数据库无限增长）。
/// </summary>
public sealed class ChatStore
{
    private const int MaxStoredMessages = 400;
    private readonly Db _db;
    private int _appended;

    public ChatStore(Db db) => _db = db;

    public void Append(string role, string content)
    {
        try
        {
            _db.Exec(
                "INSERT INTO chat_messages(role,content,created_at) VALUES($r,$c,$t)",
                c =>
                {
                    c.Parameters.AddWithValue("$r", role);
                    c.Parameters.AddWithValue("$c", content);
                    c.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                });

            // 每 50 条做一次裁剪，避免每条都执行删除检查
            if (++_appended >= 50)
            {
                _appended = 0;
                Trim();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("写入对话记录失败", ex);
        }
    }

    /// <summary>加载最近 maxMessages 条（返回时间正序）</summary>
    public List<ChatMessage> LoadRecent(int maxMessages)
    {
        try
        {
            var list = _db.QueryList(
                "SELECT id,role,content,created_at FROM chat_messages ORDER BY id DESC LIMIT $n",
                r => new ChatMessage
                {
                    Id = r.GetInt64(0),
                    Role = r.GetString(1),
                    Content = r.GetString(2),
                    CreatedAt = r.GetInt64(3)
                },
                c => c.Parameters.AddWithValue("$n", Math.Max(1, maxMessages)));
            list.Reverse();
            return list;
        }
        catch (Exception ex)
        {
            AppLogger.Error("读取对话记录失败", ex);
            return new List<ChatMessage>();
        }
    }

    public int Count()
        => _db.QueryScalar<int>("SELECT COUNT(*) FROM chat_messages");

    /// <summary>清空对话历史（UI 提供入口）</summary>
    public void Clear()
    {
        try { _db.Exec("DELETE FROM chat_messages"); }
        catch (Exception ex) { AppLogger.Error("清空对话记录失败", ex); }
    }

    private void Trim()
    {
        try
        {
            _db.Exec("""
                DELETE FROM chat_messages WHERE id <= (
                    SELECT id FROM chat_messages ORDER BY id DESC LIMIT 1 OFFSET $keep
                )
                """, c => c.Parameters.AddWithValue("$keep", MaxStoredMessages));
        }
        catch (Exception ex)
        {
            AppLogger.Error("对话记录裁剪失败", ex);
        }
    }
}
