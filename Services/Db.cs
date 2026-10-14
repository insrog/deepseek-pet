using System.IO;
using Microsoft.Data.Sqlite;

namespace DeepSeekPet.Services;

/// <summary>
/// 全局共享的 SQLite 连接（进程内单实例，全应用复用，不重复开关连接）。
/// - WAL 日志模式 + synchronous=NORMAL：写入快、掉电安全性良好；
/// - 所有访问经内部锁串行化（SQLite 本身单写入者，任务/设置/对话写入频率都极低）；
/// - 表：tasks / kv（设置、动态状态、搜索缓存等通用键值）。
/// </summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    private bool _disposed;

    public string DbPath { get; }

    public Db(string dbPath)
    {
        DbPath = dbPath;
        try { Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!); } catch { }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _conn = new SqliteConnection(csb.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA busy_timeout=4000");
        Exec("PRAGMA temp_store=MEMORY");
        CreateSchema();
    }

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS tasks (
                id             TEXT PRIMARY KEY,
                title          TEXT NOT NULL,
                mode           INTEGER NOT NULL,
                interval_days  INTEGER NOT NULL,
                week_mask      INTEGER NOT NULL,
                hour           INTEGER NOT NULL,
                minute         INTEGER NOT NULL,
                enabled        INTEGER NOT NULL,
                anchor_date    TEXT NOT NULL,
                last_fired_key TEXT,
                done_date      TEXT,
                sort_order     INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS kv (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_messages (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                role         TEXT NOT NULL,
                content      TEXT NOT NULL,
                tool_name    TEXT,
                tool_call_id TEXT,
                created_at   INTEGER NOT NULL
            );
            """);

        // 兼容旧库补列（v1.1.x 增量开发期间可能已有 pet.db）
        try
        {
            var cols = QueryList("PRAGMA table_info(tasks)", r => r.GetString(1));
            if (!cols.Contains("once_date"))
                Exec("ALTER TABLE tasks ADD COLUMN once_date TEXT");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"tasks 表结构检查失败：{ex.Message}");
        }
    }

    // ---------------- 基础执行 ----------------

    public void Exec(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>在一个事务中执行一批语句（要么全成功，要么全不写）</summary>
    public void ExecBatch(Action<SqliteConnection, SqliteTransaction> batch)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                batch(_conn, tx);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }

    public T Query<T>(Func<SqliteConnection, T> f)
    {
        lock (_lock) return f(_conn);
    }

    public List<T> QueryList<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand>? bind = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            var list = new List<T>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public T? QueryScalar<T>(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            var v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return default;
            try { return (T)Convert.ChangeType(v, typeof(T)); }
            catch { return default; }
        }
    }

    public string? GetKv(string key)
        => QueryScalar<string>("SELECT value FROM kv WHERE key=$k", c => c.Parameters.AddWithValue("$k", key));

    public void SetKv(string key, string value)
        => Exec("INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
            c => { c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value); });

    public void DeleteKv(string key)
        => Exec("DELETE FROM kv WHERE key=$k", c => c.Parameters.AddWithValue("$k", key));

    /// <summary>完整性自检（WAL 快速检查，不阻塞长时间）</summary>
    public bool Healthy()
    {
        try { return QueryScalar<string>("PRAGMA quick_check") is "ok"; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            try { _conn.Close(); } catch { }
            try { _conn.Dispose(); } catch { }
        }
    }
}
