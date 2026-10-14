using System.Globalization;
using System.Text.Json.Nodes;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Agent.Tools;

/// <summary>
/// 工具：创建提醒（直接写入现有任务系统 TaskStore，UI 列表立即同步）。
/// 支持 每天 / 每N天 / 每周几 / 仅一次 四种模式，自动查重。
/// </summary>
public sealed class AddReminderTool : AgentTool
{
    private readonly TaskStore _store;

    public AddReminderTool(TaskStore store) => _store = store;

    public override string Name => "add_reminder";

    public override string Description =>
        "为用户创建一条定时提醒（写入桌宠的任务列表）。当用户要求“提醒我/记一下/别忘了/待办某时刻做事”时调用。" +
        "必须给出 24 小时制的 hour 与 minute。用户说“明天/后天/某月某日”时用 repeat=once 并给出 date；" +
        "说“每天”用 repeat=daily；说“每N天”用 repeat=interval 并给出 interval_days；" +
        "说“每周X”用 repeat=weekly 并给出 weekdays（1=周一…7=周日）。";

    public override JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "提醒内容，简洁明了（如“开会”“吃药”“交周报”）"
            },
            ["hour"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 23,
                ["description"] = "小时（24 小时制）"
            },
            ["minute"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 59,
                ["description"] = "分钟"
            },
            ["repeat"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("daily", "interval", "weekly", "once"),
                ["description"] = "daily=每天；interval=每N天；weekly=每周指定几天；once=仅一次（需 date）"
            },
            ["interval_days"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 2, ["maximum"] = 365,
                ["description"] = "repeat=interval 时生效：每隔几天"
            },
            ["weekdays"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 7 },
                ["description"] = "repeat=weekly 时生效：星期几的列表（1=周一 … 7=周日）"
            },
            ["date"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "repeat=once 时生效：日期 yyyy-MM-dd（按用户本地日历推算“明天”等）"
            }
        },
        ["required"] = new JsonArray("title", "hour", "minute")
    };

    public override Task<string> ExecuteAsync(JsonNode? args, CancellationToken ct)
    {
        if (args == null) return Task.FromResult("错误：缺少参数。");

        var title = (args["title"]?.GetValue<string>() ?? "").Trim();
        if (title.Length == 0) return Task.FromResult("错误：title 不能为空。");
        if (title.Length > 60) title = title[..60];

        if (args["hour"] == null || args["minute"] == null)
            return Task.FromResult("错误：必须提供 hour 与 minute（24 小时制）。");

        int hour, minute;
        try
        {
            hour = Math.Clamp(args["hour"]!.GetValue<int>(), 0, 23);
            minute = Math.Clamp(args["minute"]!.GetValue<int>(), 0, 59);
        }
        catch { return Task.FromResult("错误：hour/minute 必须是整数。"); }

        var repeat = (args["repeat"]?.GetValue<string>() ?? "daily").Trim().ToLowerInvariant();
        var item = new TaskItem { Title = title, Hour = hour, Minute = minute };

        switch (repeat)
        {
            case "once":
            {
                var dateStr = args["date"]?.GetValue<string>()?.Trim();
                var d = ParseDate(dateStr);
                if (d == null)
                    return Task.FromResult("错误：repeat=once 时必须给出合法的 date（yyyy-MM-dd）。");
                // 一次性任务：日期已过则直接拒绝，避免建立永不触发的任务
                if (d.Value.Date < DateTime.Today)
                    return Task.FromResult($"错误：{d.Value:yyyy-MM-dd} 已经过去了，请确认日期。");
                var occ = d.Value.Date.AddHours(hour).AddMinutes(minute);
                if (occ <= DateTime.Now)
                    return Task.FromResult($"错误：{occ:yyyy-MM-dd HH:mm} 已经过去了，请确认时间。");
                item.OnceDate = d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                item.AnchorDate = item.OnceDate;
                break;
            }
            case "interval":
            {
                int n = 2;
                try { n = args["interval_days"]?.GetValue<int>() ?? 2; } catch { }
                item.Mode = RepeatMode.Interval;
                item.IntervalDays = Math.Clamp(n, 2, 365);
                break;
            }
            case "weekly":
            {
                int mask = 0;
                if (args["weekdays"] is JsonArray arr)
                {
                    foreach (var w in arr)
                    {
                        try
                        {
                            int v = w!.GetValue<int>();
                            if (v is >= 1 and <= 7) mask |= 1 << (v - 1);
                        }
                        catch { }
                    }
                }
                if (mask == 0)
                    return Task.FromResult("错误：repeat=weekly 时必须给出 weekdays（1=周一…7=周日）。");
                item.Mode = RepeatMode.Weekly;
                item.WeekMask = mask;
                break;
            }
            default: // daily
                item.Mode = RepeatMode.Interval;
                item.IntervalDays = 1;
                break;
        }

        // 查重：完全相同的规则不重复创建
        var dup = _store.FindDuplicate(item);
        if (dup != null)
            return Task.FromResult($"已存在相同提醒，未重复创建：「{dup}」（{Describe(item)}）。");

        // 一次性任务：若该时刻已过（例如“今天9点”但现在10点），拒绝
        if (item.IsOnce && item.NextOccurrence(DateTime.Now) == null)
            return Task.FromResult("错误：该一次性提醒的时间已经过去，请与用户确认时间。");

        _store.Upsert(item);
        return Task.FromResult($"已创建提醒：「{item.Title}」，{Describe(item)}。");
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToLowerInvariant();
        if (s is "today" or "今天") return DateTime.Today;
        if (s is "tomorrow" or "明天") return DateTime.Today.AddDays(1);
        if (s is "day after tomorrow" or "后天") return DateTime.Today.AddDays(2);
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.Date;
        return null;
    }

    /// <summary>人类可读的规则描述（回给 LLM 用于向用户确认）</summary>
    private static string Describe(TaskItem t)
    {
        var time = $"{t.Hour:D2}:{t.Minute:D2}";
        if (t.IsOnce) return $"仅 {t.OnceDate} 的 {time} 提醒一次";
        if (t.Mode == RepeatMode.Weekly) return $"{t.ScheduleText()} 的 {time} 提醒";
        return $"{(t.IntervalDays <= 1 ? "每天" : $"每 {t.IntervalDays} 天")} {time} 提醒";
    }
}
