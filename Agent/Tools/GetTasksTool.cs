using System.Text;
using System.Text.Json.Nodes;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent.Tools;

/// <summary>
/// 工具：查询用户现有待办/提醒（读现有任务系统，与面板显示同源）。
/// </summary>
public sealed class GetTasksTool : AgentTool
{
    private readonly TaskStore _store;

    public GetTasksTool(TaskStore store) => _store = store;

    public override string Name => "get_tasks";

    public override string Description =>
        "查询用户的任务/提醒列表。当用户问“我有什么待办/今天要做什么/几点提醒/我的提醒列表”时调用。" +
        "返回内容按 今天 / 其他 分组，包含时间、重复规则、启用状态与今日完成情况。";

    public override JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["filter"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("all", "today", "enabled"),
                ["description"] = "all=全部（默认）；today=今天会提醒的；enabled=仅已启用"
            }
        }
    };

    public override Task<string> ExecuteAsync(JsonNode? args, CancellationToken ct)
    {
        var filter = (args?["filter"]?.GetValue<string>() ?? "all").Trim().ToLowerInvariant();
        var now = DateTime.Now;
        var today = now.Date;

        var sb = new StringBuilder();
        var todayItems = new List<TaskItem>();
        var otherItems = new List<TaskItem>();

        foreach (var t in _store.Snapshot())
        {
            if (filter == "enabled" && !t.Enabled) continue;

            bool firesToday =
                t.Enabled &&
                (t.NextOccurrence(now.AddSeconds(-1))?.Date == today   // 今天还有下一次
                 || (t.LatestOccurrenceAtOrBefore(now)?.Date == today && t.DoneDate != today.ToString("yyyy-MM-dd")));

            if (filter == "today" && !firesToday) continue;
            (firesToday ? todayItems : otherItems).Add(t);
        }

        if (todayItems.Count == 0 && otherItems.Count == 0)
            return Task.FromResult(filter == "today"
                ? "用户今天没有需要提醒的任务。"
                : "用户当前没有任何任务/提醒。");

        var todayStr = today.ToString("yyyy-MM-dd");

        void AppendGroup(string header, List<TaskItem> items)
        {
            if (items.Count == 0) return;
            sb.Append(header).Append('\n');
            int i = 1;
            foreach (var t in items.OrderBy(x => x.Hour).ThenBy(x => x.Minute))
            {
                sb.Append($"{i++}. {t.Title} — {t.ScheduleText()} {t.Hour:D2}:{t.Minute:D2}");
                if (!t.Enabled) sb.Append("（已停用）");
                if (t.DoneDate == todayStr) sb.Append("（今日已完成）");
                sb.Append('\n');
            }
        }

        AppendGroup("【今天】", todayItems);
        AppendGroup(filter == "today" ? "" : "【其他】", otherItems);

        sb.Append($"\n（共 {todayItems.Count + otherItems.Count} 条；当前时间 {now:yyyy-MM-dd HH:mm} 周{WeekName(now.DayOfWeek)}）");
        return Task.FromResult(sb.ToString());
    }

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
}
