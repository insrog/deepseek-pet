using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekPet.Config;

/// <summary>
/// 应用配置（appconfig.json + 环境变量，代码中零密钥硬编码）。
/// 取值优先级：环境变量 &gt; appconfig.json &gt; 内置默认。
/// 修改文件后无需重启（ConfigService 监听文件变更热更新）。
/// </summary>
public sealed class AppConfig
{
    // ---------------- LLM（OpenAI 兼容） ----------------

    /// <summary>API Key（也可用环境变量 DEEPSEEK_PET_API_KEY / DEEPSEEK_API_KEY）</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    /// <summary>接口地址（OpenAI 兼容；默认 DeepSeek，可改任意兼容服务，如 Ollama http://127.0.0.1:11434/v1）</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>模型名（deepseek-chat / deepseek-reasoner / 其他兼容模型）</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>单次请求超时（秒）</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>采样温度</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 1.0;

    /// <summary>短期记忆保留轮数（一轮 = 一条用户 + 一条助手；建议 20-30）</summary>
    [JsonPropertyName("memoryRounds")]
    public int MemoryRounds { get; set; } = 24;

    // ---------------- 联网搜索 ----------------

    /// <summary>搜索后端：tavily | searxng | none</summary>
    [JsonPropertyName("searchProvider")]
    public string SearchProvider { get; set; } = "tavily";

    /// <summary>Tavily API Key（也可用环境变量 TAVILY_API_KEY）</summary>
    [JsonPropertyName("searchApiKey")]
    public string SearchApiKey { get; set; } = "";

    /// <summary>SearXNG 实例地址（自建/公共实例，如 http://127.0.0.1:8888）</summary>
    [JsonPropertyName("searxngUrl")]
    public string SearxngUrl { get; set; } = "";

    /// <summary>搜索结果本地缓存时长（分钟）</summary>
    [JsonPropertyName("searchCacheMinutes")]
    public int SearchCacheMinutes { get; set; } = 30;

    /// <summary>单次搜索返回条数</summary>
    [JsonPropertyName("searchMaxResults")]
    public int SearchMaxResults { get; set; } = 5;

    // ---------------- Agent 行为 ----------------

    /// <summary>是否允许 LLM 联网搜索工具</summary>
    [JsonPropertyName("enableWebSearch")]
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>单轮问答最多工具调用次数（需求：≤1-2 次）</summary>
    [JsonPropertyName("maxToolCallsPerTurn")]
    public int MaxToolCallsPerTurn { get; set; } = 2;

    /// <summary>定时提醒是否先调 LLM 生成自然提醒语（失败自动回退模板）</summary>
    [JsonPropertyName("llmReminderPhrase")]
    public bool LlmReminderPhrase { get; set; } = true;

    /// <summary>人设卡文件路径（留空 = 数据目录下 persona.json）</summary>
    [JsonPropertyName("personaPath")]
    public string PersonaPath { get; set; } = "";

    /// <summary>未知字段保留（如模板里的 "_说明"）：读写往返不丢，方便用户手工备注</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtraFields { get; set; }

    // ---------------- 派生 / 校验 ----------------

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasSearchKey => !string.IsNullOrWhiteSpace(SearchApiKey) || !string.IsNullOrWhiteSpace(SearxngUrl);

    /// <summary>是否具备联网搜索可用条件</summary>
    public bool SearchUsable(string provider) => provider switch
    {
        "tavily" => !string.IsNullOrWhiteSpace(SearchApiKey),
        "searxng" => !string.IsNullOrWhiteSpace(SearxngUrl),
        _ => false
    };

    /// <summary>环境变量覆盖（进程级，改完需重启进程；文件配置可热更新）</summary>
    public void ApplyEnvironment()
    {
        string? Env(params string[] names)
        {
            foreach (var n in names)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return null;
        }

        if (Env("DEEPSEEK_PET_API_KEY", "DEEPSEEK_API_KEY") is { } k) ApiKey = k;
        if (Env("DEEPSEEK_PET_BASE_URL") is { } b) BaseUrl = b;
        if (Env("DEEPSEEK_PET_MODEL") is { } m) Model = m;
        if (Env("TAVILY_API_KEY") is { } t) SearchApiKey = t;
        if (Env("DEEPSEEK_PET_SEARXNG_URL") is { } s) SearxngUrl = s;
    }

    /// <summary>规整取值，避免非法配置导致运行异常</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = "https://api.deepseek.com/v1";
        BaseUrl = BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(Model)) Model = "deepseek-chat";
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MemoryRounds = Math.Clamp(MemoryRounds, 4, 60);
        Temperature = Math.Clamp(Temperature, 0, 2);
        MaxToolCallsPerTurn = Math.Clamp(MaxToolCallsPerTurn, 0, 4);
        SearchCacheMinutes = Math.Clamp(SearchCacheMinutes, 0, 1440);
        SearchMaxResults = Math.Clamp(SearchMaxResults, 1, 10);
        SearchProvider = (SearchProvider ?? "").Trim().ToLowerInvariant();
        if (SearchProvider is not ("tavily" or "searxng" or "none")) SearchProvider = "tavily";
    }

    // ---------------- JSON 读写 ----------------

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static AppConfig? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts); }
        catch { return null; }
    }
}
