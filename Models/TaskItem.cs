using System.Globalization;

namespace DeepSeekPet.Models;

/// <summary>重复方式：按天间隔 / 每周指定星期</summary>
public enum RepeatMode
{
    Interval = 0,
    Weekly = 1
}

/// <summary>一条提醒任务（本地墙上时间语义）</summary>
public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public RepeatMode Mode { get; set; } = RepeatMode.Interval;

    /// <summary>按天模式：每 N 天提醒一次（1 = 每天）</summary>
    public int IntervalDays { get; set; } = 1;

    /// <summary>每周模式：bit0=周一 … bit6=周日</summary>
    public int WeekMask { get; set; }

    public int Hour { get; set; } = 9;
    public int Minute { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>锚点日期（yyyy-MM-dd），用于按天模式稳定推算，不随打开次数漂移</summary>
    public string AnchorDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>最近一次已触发的事件键（yyyy-MM-dd HH:mm），用于“同一时刻绝不重复提醒”</summary>
    public string? LastFiredKey { get; set; }

    /// <summary>“今日完成”日期（yyyy-MM-dd），当天不再提醒</summary>
    public string? DoneDate { get; set; }

    /// <summary>
    /// 一次性任务日期（yyyy-MM-dd）；非空时仅在当天指定时刻提醒一次（AI 创建“明天9点提醒我”用）。
    /// 不影响原三种重复模式的任何行为。
    /// </summary>
    public string? OnceDate { get; set; }

    public bool IsOnce => !string.IsNullOrEmpty(OnceDate);

    public static string OccurrenceKey(DateTime occ) =>
        occ.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private DateTime? ParseAnchor()
    {
        if (DateTime.TryParseExact(AnchorDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)) return d.Date;
        return null;
    }

    /// <summary>严格晚于 after 的下一次提醒时刻；无（未启用星期/异常数据）返回 null</summary>
    public DateTime? NextOccurrence(DateTime after)
    {
        if (!Enabled) return null;

        // 一次性任务：仅当天该时刻
        if (IsOnce)
        {
            if (!DateTime.TryParseExact(OnceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var od)) return null;
            var once = od.Date.AddHours(Hour).AddMinutes(Minute);
            return once > after ? once : null;
        }

        if (Mode == RepeatMode.Weekly)
        {
            if (WeekMask == 0) return null;
            for (int i = 0; i <= 8; i++)
            {
                var day = after.Date.AddDays(i);
                int bit = 1 << (((int)day.DayOfWeek + 6) % 7); // 周一=bit0
                if ((WeekMask & bit) == 0) continue;
                var cand = day.AddHours(Hour).AddMinutes(Minute);
                if (cand > after) return cand;
            }
            return null;
        }

        if (IntervalDays < 1) return null;
        var anchor = ParseAnchor() ?? after.Date;
        var first = anchor.AddHours(Hour).AddMinutes(Minute);
        if (first > after) return first;
        if (anchor > after.Date) return first;

        int days = (int)(after.Date - anchor).TotalDays;
        int k = Math.Max(0, days / IntervalDays);
        var cand2 = anchor.AddDays((long)k * IntervalDays).AddHours(Hour).AddMinutes(Minute);
        int guard = 0;
        while (cand2 <= after && guard++ < 10000)
        {
            k++;
            cand2 = anchor.AddDays((long)k * IntervalDays).AddHours(Hour).AddMinutes(Minute);
        }
        return cand2;
    }

    /// <summary>不晚于 now 的最近一次提醒时刻；尚未开始返回 null</summary>
    public DateTime? LatestOccurrenceAtOrBefore(DateTime now)
    {
        if (!Enabled) return null;

        // 一次性任务：仅当天该时刻
        if (IsOnce)
        {
            if (!DateTime.TryParseExact(OnceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var od)) return null;
            var once = od.Date.AddHours(Hour).AddMinutes(Minute);
            return once <= now ? once : null;
        }

        if (Mode == RepeatMode.Weekly)
        {
            if (WeekMask == 0) return null;
            for (int i = 0; i <= 8; i++)
            {
                var day = now.Date.AddDays(-i);
                int bit = 1 << (((int)day.DayOfWeek + 6) % 7);
                if ((WeekMask & bit) == 0) continue;
                var cand = day.AddHours(Hour).AddMinutes(Minute);
                if (cand <= now) return cand;
            }
            return null;
        }

        if (IntervalDays < 1) return null;
        var anchor = ParseAnchor() ?? now.Date;
        if (anchor > now.Date) return null;
        var first = anchor.AddHours(Hour).AddMinutes(Minute);
        if (first > now) return null; // 还没到首次提醒

        int days = (int)(now.Date - anchor).TotalDays;
        int k = days / IntervalDays;
        var occ = anchor.AddDays((long)k * IntervalDays).AddHours(Hour).AddMinutes(Minute);
        int guard = 0;
        while (occ > now && guard++ < 10000)
        {
            k--;
            occ = anchor.AddDays((long)k * IntervalDays).AddHours(Hour).AddMinutes(Minute);
        }
        return occ;
    }

    /// <summary>面板显示的重复规则文本</summary>
    public string ScheduleText()
    {
        if (IsOnce)
        {
            if (DateTime.TryParseExact(OnceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var od))
                return $"仅 {od:M月d日}";
            return "仅一次";
        }
        if (Mode == RepeatMode.Weekly)
        {
            var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
            var parts = new List<string>();
            for (int i = 0; i < 7; i++)
                if ((WeekMask & (1 << i)) != 0) parts.Add(names[i]);
            return parts.Count == 0 ? "未选择" : "周" + string.Join("/", parts);
        }
        return IntervalDays <= 1 ? "每天" : $"每{IntervalDays}天";
    }
}
