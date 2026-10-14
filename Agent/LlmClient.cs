using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeekPet.Config;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent;

/// <summary>发给 LLM 的一条消息（OpenAI 兼容格式）</summary>
public sealed class LlmMessage
{
    public string Role { get; set; } = "user";          // system / user / assistant / tool
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }             // role=tool 时必填
    public List<LlmToolCall>? ToolCalls { get; set; }   // role=assistant 且要发起工具调用时

    public static LlmMessage System(string content) => new() { Role = "system", Content = content };
    public static LlmMessage User(string content) => new() { Role = "user", Content = content };
    public static LlmMessage Assistant(string? content) => new() { Role = "assistant", Content = content };
    public static LlmMessage Tool(string toolCallId, string content) => new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

/// <summary>LLM 返回的工具调用请求</summary>
public sealed class LlmToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>一次请求的结果</summary>
public sealed class LlmResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = "";
    public List<LlmToolCall> ToolCalls { get; set; } = new();
    public string? Error { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public bool Canceled { get; set; }
}

/// <summary>
/// OpenAI 兼容 LLM 客户端（默认 DeepSeek，可切任意兼容端点，含本地 Ollama）。
/// - 流式输出：SSE 逐 token 回调（onDelta），UI 增量追加；
/// - 支持 Function Calling（tools）；
/// - HttpClient 进程级单例复用；全部异步，不阻塞 UI 线程；
/// - 网络/服务异常统一返回失败结果，由上层兜底（绝不抛出到 UI）。
/// </summary>
public sealed class LlmClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private AppConfig _cfg;

    public LlmClient(AppConfig cfg) => _cfg = cfg;

    public void UpdateConfig(AppConfig cfg) => _cfg = cfg;

    // ---------------- 非流式（状态更新 / 提醒语等小请求） ----------------

    public async Task<LlmResult> CompleteAsync(
        List<LlmMessage> messages,
        CancellationToken ct,
        double? temperature = null,
        int? maxTokens = null,
        string? modelOverride = null)
        => await RequestAsync(messages, null, null, ct, temperature, maxTokens, stream: false, modelOverride);

    // ---------------- 流式 ----------------

    public async Task<LlmResult> StreamAsync(
        List<LlmMessage> messages,
        Action<string>? onDelta,
        List<JsonObject>? tools,
        CancellationToken ct,
        double? temperature = null)
        => await RequestAsync(messages, onDelta, tools, ct, temperature, null, stream: true, null);

    public bool HasApiKey => _cfg.HasApiKey;
    public string ModelName => _cfg.Model;

    private async Task<LlmResult> RequestAsync(
        List<LlmMessage> messages,
        Action<string>? onDelta,
        List<JsonObject>? tools,
        CancellationToken ct,
        double? temperature,
        int? maxTokens,
        bool stream,
        string? modelOverride)
    {
        var result = new LlmResult();
        if (!_cfg.HasApiKey)
        {
            result.Error = "尚未配置 API Key（appconfig.json 或环境变量 DEEPSEEK_PET_API_KEY）";
            return result;
        }

        var body = BuildBody(messages, tools, temperature, maxTokens, stream, modelOverride);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSeconds));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.BaseUrl}/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await SafeReadAsync(resp, cts.Token);
                result.Error = $"服务返回 {(int)resp.StatusCode}：{Shorten(errText, 200)}";
                AppLogger.Warn($"LLM 请求失败：{result.Error}");
                return result;
            }

            if (!stream)
                await ReadNonStreamAsync(resp, result, cts.Token);
            else
                await ReadStreamAsync(resp, result, onDelta, cts.Token);

            result.Success = true;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Canceled = true;
            result.Error = "已取消";
            return result;
        }
        catch (OperationCanceledException)
        {
            // 超时（不是用户取消）
            result.Error = $"请求超时（{_cfg.TimeoutSeconds}s）";
            AppLogger.Warn("LLM 请求超时");
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"网络异常：{ex.Message}";
            AppLogger.Error("LLM 请求异常", ex);
            return result;
        }
    }

    // ---------------- 请求体构建 ----------------

    private string BuildBody(
        List<LlmMessage> messages, List<JsonObject>? tools,
        double? temperature, int? maxTokens, bool stream, string? modelOverride)
    {
        var msgArr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            if (m.Content != null) o["content"] = m.Content;
            else if (m.Role == "assistant") o["content"] = null;

            if (m.ToolCallId != null) o["tool_call_id"] = m.ToolCallId;

            if (m.ToolCalls is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var tc in m.ToolCalls)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.Arguments
                        }
                    });
                }
                o["tool_calls"] = arr;
            }
            msgArr.Add(o);
        }

        var root = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(modelOverride) ? _cfg.Model : modelOverride,
            ["messages"] = msgArr,
            ["stream"] = stream,
            ["temperature"] = temperature ?? _cfg.Temperature
        };
        if (maxTokens is { } mt) root["max_tokens"] = mt;
        if (stream) root["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (tools is { Count: > 0 })
        {
            var toolArr = new JsonArray();
            foreach (var t in tools) toolArr.Add(t.DeepClone());
            root["tools"] = toolArr;
            root["tool_choice"] = "auto";
        }
        return root.ToJsonString();
    }

    // ---------------- 非流式解析 ----------------

    private static async Task ReadNonStreamAsync(HttpResponseMessage resp, LlmResult result, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(text);
        var msg = node?["choices"]?[0]?["message"];
        if (msg == null) { result.Error = "服务返回格式异常"; return; }
        result.Content = msg["content"]?.GetValue<string>() ?? "";
        if (msg["tool_calls"] is JsonArray tcs)
        {
            foreach (var t in tcs)
            {
                var f = t?["function"];
                if (f == null) continue;
                result.ToolCalls.Add(new LlmToolCall
                {
                    Id = t?["id"]?.GetValue<string>() ?? "",
                    Name = f["name"]?.GetValue<string>() ?? "",
                    Arguments = f["arguments"]?.GetValue<string>() ?? "{}"
                });
            }
        }
    }

    // ---------------- 流式解析（SSE） ----------------

    private static async Task ReadStreamAsync(
        HttpResponseMessage resp, LlmResult result, Action<string>? onDelta, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var toolAcc = new SortedDictionary<int, ToolAcc>();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(':')) continue;                        // SSE 注释心跳
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch { continue; }                                        // 忽略坏行，保持流

            // 用量统计（stream_options.include_usage）
            if (node?["usage"] is JsonObject usage)
            {
                result.PromptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? result.PromptTokens;
                result.CompletionTokens = usage["completion_tokens"]?.GetValue<int>() ?? result.CompletionTokens;
            }

            var delta = node?["choices"]?[0]?["delta"];
            if (delta == null) continue;

            var text = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
            {
                content.Append(text);
                try { onDelta?.Invoke(text); } catch { }
            }

            if (delta["tool_calls"] is JsonArray tcs)
            {
                foreach (var t in tcs)
                {
                    int idx = t?["index"]?.GetValue<int>() ?? 0;
                    if (!toolAcc.TryGetValue(idx, out var acc))
                    {
                        acc = new ToolAcc();
                        toolAcc[idx] = acc;
                    }
                    if (t?["id"]?.GetValue<string>() is { Length: > 0 } id) acc.Id = id;
                    var fn = t?["function"];
                    if (fn?["name"]?.GetValue<string>() is { Length: > 0 } name) acc.Name = name;
                    if (fn?["arguments"]?.GetValue<string>() is { Length: > 0 } argFrag) acc.Args.Append(argFrag);
                }
            }
        }

        result.Content = content.ToString();
        foreach (var acc in toolAcc.Values)
        {
            if (string.IsNullOrEmpty(acc.Name)) continue;
            result.ToolCalls.Add(new LlmToolCall
            {
                Id = string.IsNullOrEmpty(acc.Id) ? $"call_{Guid.NewGuid():N}" : acc.Id,
                Name = acc.Name,
                Arguments = acc.Args.Length == 0 ? "{}" : acc.Args.ToString()
            });
        }
    }

    private sealed class ToolAcc
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    // ---------------- 工具 ----------------

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    public void Dispose() { /* HttpClient 为静态单例，随进程结束释放 */ }
}
