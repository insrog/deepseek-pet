using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeepSeekPet.Config;

namespace DeepSeekPet.Services;

/// <summary>一条搜索结果（只保留结构化字段）</summary>
public sealed class SearchHit
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
}

/// <summary>搜索返回值</summary>
public sealed class SearchResult
{
    public bool Ok { get; set; }
    public string Query { get; set; } = "";
    public string? Answer { get; set; }              // Tavily 的聚合回答（若有）
    public List<SearchHit> Hits { get; set; } = new();
    public string? Error { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>
/// 联网搜索客户端：Tavily（默认）/ SearXNG 双后端。
/// - Key 只从配置/环境变量读取（零硬编码）；
/// - 结果写入 SQLite 本地缓存（默认 30 分钟），同查询不重复打网络；
/// - 失败返回明确的 Error，绝不编造结果（上层如实告知用户）。
/// </summary>
public sealed class SearchClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Db _db;
    private AppConfig _cfg;

    public SearchClient(Db db, AppConfig cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;

    public async Task<SearchResult> SearchAsync(string query, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new SearchResult { Ok = false, Error = "空查询" };

        var provider = _cfg.SearchProvider;
        if (provider == "none")
            return new SearchResult { Ok = false, Query = query, Error = "搜索功能已关闭（appconfig.json searchProvider=none）" };
        if (!_cfg.SearchUsable(provider))
            return new SearchResult
            {
                Ok = false, Query = query,
                Error = provider == "tavily"
                    ? "未配置 Tavily API Key（appconfig.json searchApiKey 或环境变量 TAVILY_API_KEY）"
                    : "未配置 SearXNG 地址（appconfig.json searxngUrl）"
            };

        // ---- 本地缓存 ----
        var cacheKey = "search_cache:" + Sha1($"{provider}|{query}|{_cfg.SearchMaxResults}");
        var cached = TryLoadCache(cacheKey);
        if (cached != null) return cached;

        try
        {
            var result = provider == "searxng"
                ? await SearchSearxngAsync(query, ct)
                : await SearchTavilyAsync(query, ct);

            if (result.Ok) SaveCache(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new SearchResult { Ok = false, Query = query, Error = "搜索超时或被取消" };
        }
        catch (Exception ex)
        {
            AppLogger.Error("搜索失败", ex);
            return new SearchResult { Ok = false, Query = query, Error = $"网络异常：{ex.Message}" };
        }
    }

    // ---------------- Tavily ----------------

    private async Task<SearchResult> SearchTavilyAsync(string query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["api_key"] = _cfg.SearchApiKey,
            ["query"] = query,
            ["max_results"] = _cfg.SearchMaxResults,
            ["search_depth"] = "basic",
            ["include_answer"] = true,
            ["include_raw_content"] = false
        }.ToJsonString();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            AppLogger.Warn($"Tavily 返回 {(int)resp.StatusCode}");
            return new SearchResult { Ok = false, Query = query, Error = $"搜索服务返回 {(int)resp.StatusCode}" };
        }

        var node = JsonNode.Parse(text);
        var result = new SearchResult { Ok = true, Query = query };
        result.Answer = node?["answer"]?.GetValue<string>();

        if (node?["results"] is JsonArray arr)
        {
            foreach (var r in arr)
            {
                if (r == null) continue;
                result.Hits.Add(new SearchHit
                {
                    Title = r["title"]?.GetValue<string>() ?? "",
                    Url = r["url"]?.GetValue<string>() ?? "",
                    Snippet = Shorten(r["content"]?.GetValue<string>() ?? "", 300)
                });
            }
        }
        return result;
    }

    // ---------------- SearXNG ----------------

    private async Task<SearchResult> SearchSearxngAsync(string query, CancellationToken ct)
    {
        var baseUrl = _cfg.SearxngUrl.TrimEnd('/');
        var url = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json&language=zh-CN";

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            AppLogger.Warn($"SearXNG 返回 {(int)resp.StatusCode}");
            return new SearchResult { Ok = false, Query = query, Error = $"搜索服务返回 {(int)resp.StatusCode}（SearXNG 需允许 JSON 输出）" };
        }

        var node = JsonNode.Parse(text);
        var result = new SearchResult { Ok = true, Query = query };
        if (node?["results"] is JsonArray arr)
        {
            foreach (var r in arr)
            {
                if (r == null) continue;
                result.Hits.Add(new SearchHit
                {
                    Title = r["title"]?.GetValue<string>() ?? "",
                    Url = r["url"]?.GetValue<string>() ?? "",
                    Snippet = Shorten(r["content"]?.GetValue<string>() ?? "", 300)
                });
                if (result.Hits.Count >= _cfg.SearchMaxResults) break;
            }
        }
        return result;
    }

    // ---------------- 缓存 ----------------

    private SearchResult? TryLoadCache(string key)
    {
        try
        {
            var json = _db.GetKv(key);
            if (json == null) return null;
            var node = JsonNode.Parse(json);
            long expires = node?["expires"]?.GetValue<long>() ?? 0;
            if (DateTimeOffset.Now.ToUnixTimeMilliseconds() > expires)
            {
                _db.DeleteKv(key);
                return null;
            }
            var r = new SearchResult
            {
                Ok = true,
                FromCache = true,
                Query = node?["query"]?.GetValue<string>() ?? "",
                Answer = node?["answer"]?.GetValue<string>()
            };
            if (node?["hits"] is JsonArray hits)
            {
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    r.Hits.Add(new SearchHit
                    {
                        Title = h["title"]?.GetValue<string>() ?? "",
                        Url = h["url"]?.GetValue<string>() ?? "",
                        Snippet = h["snippet"]?.GetValue<string>() ?? ""
                    });
                }
            }
            return r;
        }
        catch { return null; }
    }

    private void SaveCache(string key, SearchResult r)
    {
        try
        {
            if (_cfg.SearchCacheMinutes <= 0) return;
            var hits = new JsonArray();
            foreach (var h in r.Hits)
                hits.Add(new JsonObject { ["title"] = h.Title, ["url"] = h.Url, ["snippet"] = h.Snippet });

            var node = new JsonObject
            {
                ["expires"] = DateTimeOffset.Now.AddMinutes(_cfg.SearchCacheMinutes).ToUnixTimeMilliseconds(),
                ["query"] = r.Query,
                ["answer"] = r.Answer,
                ["hits"] = hits
            };
            _db.SetKv(key, node.ToJsonString());
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"写入搜索缓存失败：{ex.Message}");
        }
    }

    // ---------------- 工具 ----------------

    private static string Sha1(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }
}
