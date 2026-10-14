using System.Text.RegularExpressions;

namespace DeepSeekPet.Agent;

/// <summary>分流结果</summary>
public enum RouteKind
{
    /// <summary>本地规则完全解析出提醒 → 直接走任务系统（零延迟、无 API Key 也可用）</summary>
    LocalReminder,

    /// <summary>其余一律交给 LLM（带工具）。LLM 自行决定闲聊/调用工具</summary>
    Llm
}

/// <summary>本地解析出的提醒草稿</summary>
public sealed class ReminderDraft
{
    public string Title { get; set; } = "";
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string Repeat { get; set; } = "daily";     // daily / interval / weekly / once
    public int IntervalDays { get; set; } = 2;
    public int WeekMask { get; set; }
    public string? Date { get; set; }                 // repeat=once 时 yyyy-MM-dd

    public string ToArgsJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"title\":\"").Append(Escape(Title)).Append('"');
        sb.Append(",\"hour\":").Append(Hour);
        sb.Append(",\"minute\":").Append(Minute);
        sb.Append(",\"repeat\":\"").Append(Repeat).Append('"');
        if (Repeat == "interval") sb.Append(",\"interval_days\":").Append(IntervalDays);
        if (Repeat == "weekly")
        {
            sb.Append(",\"weekdays\":[");
            for (int i = 1; i <= 7; i++)
                if ((WeekMask & (1 << (i - 1))) != 0) sb.Append(i).Append(',');
            if (sb[^1] == ',') sb.Length--;
            sb.Append(']');
        }
        if (Repeat == "once" && Date != null) sb.Append(",\"date\":\"").Append(Date).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
}

/// <summary>
/// 意图分流（第一层：本地规则预判）。
/// 命中「明确的创建提醒表达 + 可解析的时间 + 非空标题」时走 LocalReminder，直接打通任务系统；
/// 其余（闲聊、查询待办、含糊表述、需要追问）走 LLM Function Calling（第二层）。
/// 单轮工具调用次数由 AgentService 限制（≤ maxToolCallsPerTurn）。
/// </summary>
public static class IntentRouter
{
    // 触发词：创建提醒类表达
    private static readonly Regex TriggerRx = new(
        @"提醒我|提醒一下|提醒|记一下|记下|记住|别忘了|别忘记|记得|待办|叫我|备注一下|备注",
        RegexOptions.Compiled);

    // 时间表达："9点 / 9:30 / 上午9点 / 下午3点半 / 晚上八点"
    private static readonly Regex TimeRx = new(
        @"(?<period>凌晨|早上|上午|中午|下午|傍晚|晚上|夜里)?\s*(?<h>[0-9]{1,2}|[一二三四五六七八九十两]{1,3})\s*(?<sep>[:：点])\s*(?<m>[0-9]{1,2}|半|[一二三四五六七八九十]{1,3})?\s*分?",
        RegexOptions.Compiled);

    private static readonly Regex DailyRx = new(@"每天|天天|每日", RegexOptions.Compiled);
    private static readonly Regex IntervalRx = new(@"每隔?\s*(?<n>[0-9]{1,3}|[一二三四五六七八九十两]{1,3})\s*天", RegexOptions.Compiled);
    private static readonly Regex WeeklyRx = new(@"每\s*(?:周|星期|礼拜)\s*(?<days>[一二三四五六日天0-9、,，和及\s]{1,12})", RegexOptions.Compiled);
    private static readonly Regex OnceWeekRx = new(@"(?:这|下)?\s*(?:周|星期|礼拜)\s*(?<d>[一二三四五六日天])", RegexOptions.Compiled);
    private static readonly Regex TomorrowRx = new(@"明天|明日", RegexOptions.Compiled);
    private static readonly Regex DayAfterRx = new(@"后天", RegexOptions.Compiled);
    private static readonly Regex TodayRx = new(@"今天|今晚|今早|今夜", RegexOptions.Compiled);

    private static readonly Regex CleanupRx = new(@"[，。！？、,.!?\s]+", RegexOptions.Compiled);

    /// <summary>本地预判：完全可解析 → LocalReminder（含草稿）；否则 → Llm</summary>
    public static RouteKind Classify(string text, out ReminderDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return RouteKind.Llm;
        if (!TriggerRx.IsMatch(text)) return RouteKind.Llm;

        if (!TryParseTime(text, out int hour, out int minute)) return RouteKind.Llm;

        var d = new ReminderDraft { Hour = hour, Minute = minute };

        // ---- 重复规则 ----
        if (IntervalRx.Match(text) is { Success: true } im)
        {
            d.Repeat = "interval";
            d.IntervalDays = Math.Clamp(ParseNum(im.Groups["n"].Value) ?? 2, 2, 365);
        }
        else if (WeeklyRx.Match(text) is { Success: true } wm)
        {
            int mask = ParseWeekMask(wm.Groups["days"].Value);
            if (mask == 0) return RouteKind.Llm;   // “每周”后面没写星期，交给 LLM 追问
            d.Repeat = "weekly";
            d.WeekMask = mask;
        }
        else if (DailyRx.IsMatch(text))
        {
            d.Repeat = "daily";
        }
        else if (DayAfterRx.IsMatch(text))
        {
            d.Repeat = "once";
            d.Date = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd");
        }
        else if (TomorrowRx.IsMatch(text))
        {
            d.Repeat = "once";
            d.Date = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        }
        else if (TodayRx.IsMatch(text))
        {
            d.Repeat = "once";
            d.Date = DateTime.Today.ToString("yyyy-MM-dd");
        }
        else if (OnceWeekRx.Match(text) is { Success: true } ow)
        {
            d.Repeat = "once";
            d.Date = NearestWeekday(ow.Groups["d"].Value, hour, minute).ToString("yyyy-MM-dd");
        }
        else
        {
            d.Repeat = "daily";   // “9点提醒我…” 未指明重复 → 默认每天
        }

        // ---- 标题提取：去掉触发词、时间与重复表达 ----
        var title = text;
        title = TriggerRx.Replace(title, " ");
        title = TimeRx.Replace(title, " ");
        title = IntervalRx.Replace(title, " ");
        title = WeeklyRx.Replace(title, " ");
        title = DailyRx.Replace(title, " ");
        title = DayAfterRx.Replace(title, " ");
        title = TomorrowRx.Replace(title, " ");
        title = TodayRx.Replace(title, " ");
        title = OnceWeekRx.Replace(title, " ");
        title = CleanupRx.Replace(title, " ").Trim(' ', '的', '要', '去', '我', '你');

        if (title.Length == 0) return RouteKind.Llm;    // 没说内容 → 让 LLM 追问

        d.Title = title.Length > 40 ? title[..40] : title;
        draft = d;
        return RouteKind.LocalReminder;
    }

    // ---------------- 时间解析 ----------------

    private static bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        var m = TimeRx.Match(text);
        if (!m.Success) return false;

        var hStr = m.Groups["h"].Value;
        int h = ParseNum(hStr) ?? -1;
        if (h < 0 || h > 23) return false;

        int mi = 0;
        var mStr = m.Groups["m"].Value;
        if (mStr == "半") mi = 30;
        else if (!string.IsNullOrEmpty(mStr))
        {
            mi = ParseNum(mStr) ?? -1;
            if (mi < 0 || mi > 59) return false;
        }

        var period = m.Groups["period"].Value;
        switch (period)
        {
            case "下午" or "傍晚" or "晚上" or "夜里":
                if (h == 12) h = 0;              // 晚上12点 = 次日 0 点
                else if (h < 12) h += 12;
                break;
            case "中午":
                if (h < 12 && h != 12) h += 12;  // 中午1点 = 13 点；中午12点不变
                break;
            case "凌晨" or "早上" or "上午":
                if (h == 12) h = 0;
                break;
        }

        hour = h;
        minute = mi;
        return true;
    }

    /// <summary>解析阿拉伯数字或中文数字（0-99）</summary>
    private static int? ParseNum(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, out var n)) return n;

        var map = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
            ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9
        };
        if (s.Length == 1 && map.TryGetValue(s[0], out var v)) return v;

        int idx = s.IndexOf('十');
        if (idx >= 0)
        {
            int tens = idx == 0 ? 1 : (map.TryGetValue(s[0], out var t) ? t : -1);
            if (tens < 0) return null;
            int ones = 0;
            if (s.Length > idx + 1 && !map.TryGetValue(s[idx + 1], out ones)) return null;
            return tens * 10 + ones;
        }
        return null;
    }

    private static int ParseWeekMask(string days)
    {
        int mask = 0;
        foreach (var ch in days)
        {
            int bit = ch switch
            {
                '一' or '1' => 0,
                '二' or '2' => 1,
                '三' or '3' => 2,
                '四' or '4' => 3,
                '五' or '5' => 4,
                '六' or '6' => 5,
                '日' or '天' or '7' => 6,
                _ => -1
            };
            if (bit >= 0) mask |= 1 << bit;
        }
        return mask;
    }

    /// <summary>最近的周几（若就是今天且时刻未过则今天，否则下一周）</summary>
    private static DateTime NearestWeekday(string d, int hour, int minute)
    {
        int target = d switch
        {
            "一" => 0, "二" => 1, "三" => 2, "四" => 3, "五" => 4, "六" => 5, _ => 6
        };
        int today = ((int)DateTime.Today.DayOfWeek + 6) % 7;  // 周一=0
        int delta = (target - today + 7) % 7;
        var date = DateTime.Today.AddDays(delta);
        if (delta == 0)
        {
            var occ = date.AddHours(hour).AddMinutes(minute);
            if (occ <= DateTime.Now) date = date.AddDays(7);
        }
        return date;
    }
}
