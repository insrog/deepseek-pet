using System.Text.Json.Nodes;
using DeepSeekPet.Config;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent.Tools;

/// <summary>
/// Agent 工具基类：定义 JSON Schema（发给 LLM）与本地执行逻辑（与现有任务系统直接打通）。
/// </summary>
public abstract class AgentTool
{
    /// <summary>工具名（LLM 调用标识）</summary>
    public abstract string Name { get; }

    /// <summary>给 LLM 的功能描述</summary>
    public abstract string Description { get; }

    /// <summary>参数 JSON Schema</summary>
    public abstract JsonObject ParametersSchema { get; }

    /// <summary>本地执行，返回给 LLM 观察的结果文本</summary>
    public abstract Task<string> ExecuteAsync(JsonNode? args, CancellationToken ct);

    /// <summary>组装为 OpenAI tools 数组元素</summary>
    public JsonObject BuildSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = ParametersSchema.DeepClone()
        }
    };
}

/// <summary>工具注册表：集中登记、按配置裁剪可用集、统一容错执行</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, AgentTool> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Register(AgentTool tool) => _map[tool.Name] = tool;

    public bool Contains(string name) => _map.ContainsKey(name);

    /// <summary>构建本轮可用的 tools schema（如搜索未配置则不下发 web_search）</summary>
    public List<JsonObject> BuildSchemas(AppConfig cfg)
    {
        var list = new List<JsonObject>();
        foreach (var t in _map.Values)
        {
            if (t.Name == "web_search" &&
                (!cfg.EnableWebSearch || cfg.SearchProvider == "none" || !cfg.SearchUsable(cfg.SearchProvider)))
                continue;
            list.Add(t.BuildSchema());
        }
        return list;
    }

    /// <summary>执行工具；任何异常都被捕获并转为给 LLM 的错误描述（绝不影响对话主流程）</summary>
    public async Task<string> ExecuteAsync(string name, string argsJson, CancellationToken ct)
    {
        if (!_map.TryGetValue(name, out var tool))
        {
            AppLogger.Warn($"LLM 请求了未知工具：{name}");
            return $"错误：不存在名为 {name} 的工具。";
        }
        try
        {
            JsonNode? args = null;
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                try { args = JsonNode.Parse(argsJson); }
                catch { args = null; }
            }
            var result = await tool.ExecuteAsync(args, ct);
            AppLogger.Info($"工具 {name} 执行完成：{Shorten(result)}");
            return result;
        }
        catch (OperationCanceledException)
        {
            return "错误：操作已取消。";
        }
        catch (Exception ex)
        {
            AppLogger.Error($"工具 {name} 执行失败", ex);
            return $"错误：{name} 执行失败（{ex.Message}）。请把这一情况如实告诉用户。";
        }
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
