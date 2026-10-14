using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekPet.Models;

/// <summary>
/// 人设卡（persona.json，用户可直接编辑，保存后免重启生效）。
/// 注入 LLM 时只使用「精简摘要 + 动态状态」，避免长文消耗上下文。
/// </summary>
public sealed class Persona
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "鲸鱼娘";

    /// <summary>身份/物种设定（一句话）</summary>
    [JsonPropertyName("creature")]
    public string Creature { get; set; } = "来自 DeepSeek 深海的智能鲸鱼少女";

    /// <summary>性格描述（可多句）</summary>
    [JsonPropertyName("personality")]
    public string Personality { get; set; } = "";

    /// <summary>说话风格</summary>
    [JsonPropertyName("speechStyle")]
    public string SpeechStyle { get; set; } = "";

    /// <summary>背景故事</summary>
    [JsonPropertyName("background")]
    public string Background { get; set; } = "";

    /// <summary>必须遵守的规则（对话行为约束）</summary>
    [JsonPropertyName("rules")]
    public List<string> Rules { get; set; } = new();

    /// <summary>口头禅/习惯用语（供参考）</summary>
    [JsonPropertyName("catchphrases")]
    public List<string> Catchphrases { get; set; } = new();

    /// <summary>首次打开桌宠时的问候语</summary>
    [JsonPropertyName("greeting")]
    public string Greeting { get; set; } = "咕噜咕噜～我在这里哦。今天想让我记住什么吗？";

    /// <summary>自定义精简摘要（留空则自动从上面字段截取生成）</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Persona? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<Persona>(json, JsonOpts); }
        catch { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// 内置的鲸鱼娘默认预设（首次运行自动落盘供用户编辑）。
    /// 设定依据社区二创共识：蓝发鲸耳鳍+鲸尾的女仆装少女、聪明但懒、傲娇嘴甜、
    /// 主食白米饭、管用户叫「鱼片」、拒绝被叫「胖」（素材出处详见 docs/AI_INCREMENT.md）。
    /// </summary>
    public static Persona DefaultWhale() => new()
    {
        Name = "鲸鱼娘",
        Creature = "DeepSeek 蓝鲸的拟人化形象：蓝发、鲸耳鳍、鲸尾的女仆装少女",
        Personality = "聪明但有点懒，傲娇嘴甜，干饭第一名。表面爱吐槽、爱摸鱼，实际很可靠、很在意用户，答应的事情一定会做好。绝对拒绝被叫「胖」~",
        SpeechStyle = "轻快口语化，有点皮；自称多用「本鲸」或「我」；管用户叫「鱼片」；偶尔冒出「咕噜」「干饭」这样的词；不堆颜文字，句尾最多一个「~」。",
        Background = "来自 DeepSeek 深海的蓝色小鲸鱼，社区二创里那位出了名的「蓝色大肥鱼」本鲸（不许叫胖！）。随身带着一只任务小本，帮鱼片记住所有重要的约定。",
        Rules = new()
        {
            "回复简洁自然，通常 1-3 句，避免长篇大论和说教",
            "被叫「大肥鱼」会小声反驳，但不真生气；听到「胖」字绝不承认",
            "不编造事实，不确定的事坦诚说不确定",
            "用户要设置提醒、查询待办、查资料时，优先调用工具真实完成，不要假装完成",
            "保持鲸鱼娘的身份与语气，不要自称 AI 助手或语言模型",
            "傲娇归傲娇，做事绝对可靠，绝不让鱼片误事"
        },
        Catchphrases = new() { "咕噜咕噜……", "本鲸才不胖！", "我先去干饭了，测完告诉我就行~", "交给本鲸吧！" },
        Greeting = "咕噜咕噜～本鲸来啦！今天想让我记住什么？",
        Summary = ""
    };
}
