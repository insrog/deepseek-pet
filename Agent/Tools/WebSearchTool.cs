using System.Text;
using System.Text.Json.Nodes;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent.Tools;

/// <summary>
/// 工具：联网搜索（Tavily / SearXNG）。
/// 只把结构化结果（标题/摘要/链接）回给 LLM；失败时明确告知，让模型如实回复用户而非编造。
/// </summary>
public sealed class WebSearchTool : AgentTool
{
    private readonly SearchClient _search;

    public WebSearchTool(SearchClient search) => _search = search;

    public override string Name => "web_search";

    public override string Description =>
        "联网搜索实时信息（新闻、天气、价格、资料等）。当用户询问需要最新或外部信息的问题时调用。" +
        "获得结果后，用你的话概括回答，并注明来源链接；若搜索失败就如实说明，不要编造。";

    public override JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "搜索关键词（简洁、聚焦，如“上海 明天 天气”）"
            }
        },
        ["required"] = new JsonArray("query")
    };

    public override async Task<string> ExecuteAsync(JsonNode? args, CancellationToken ct)
    {
        var query = (args?["query"]?.GetValue<string>() ?? "").Trim();
        if (query.Length == 0) return "错误：query 不能为空。";

        var r = await _search.SearchAsync(query, ct);
        if (!r.Ok)
            return $"搜索失败：{r.Error}。请如实告知用户暂时查不到，不要编造内容。";

        if (r.Hits.Count == 0 && string.IsNullOrWhiteSpace(r.Answer))
            return $"没有找到与「{query}」相关的结果。请如实告知用户没有找到。";

        var sb = new StringBuilder();
        sb.Append($"搜索「{query}」的结果");
        if (r.FromCache) sb.Append("（本地缓存）");
        sb.Append('：');

        if (!string.IsNullOrWhiteSpace(r.Answer))
            sb.Append($"\n【摘要】{r.Answer}");

        int i = 1;
        foreach (var h in r.Hits)
        {
            sb.Append($"\n{i++}. {h.Title}");
            if (!string.IsNullOrWhiteSpace(h.Snippet)) sb.Append($"\n   {h.Snippet}");
            if (!string.IsNullOrWhiteSpace(h.Url)) sb.Append($"\n   来源：{h.Url}");
        }
        sb.Append("\n（回答时请概括要点并附上来源链接）");
        return sb.ToString();
    }
}
