// Channels.cs —— 多信道交互：按渠道组织表达（原文 4.2）
//
// 对应讲义第 7 讲第六、七节：
//   IM 等即时通信        -> 轻量、低打扰；需要决策或验收时才通知；直接回答、给简短结论
//   GitHub / 邮件 / 文档 -> 结构化、高密度；交代背景、改动与验证结果
//
// 原文给出的“单次回复不超过 100 词”是其实践中有效的 IM 硬性规范；
// 讲义提醒不能机械换算为所有中文消息的统一字数上限，
// 因此这里把中日韩字符按“1 字 = 1 词”计入同一预算，仅作教学示范。

namespace SuperAgentDemo;

/// <summary>通知目的：决定在低打扰渠道是否值得发出消息。</summary>
public static class Purpose
{
    public const string Progress = "progress";     // 中间进展
    public const string Decision = "decision";     // 需要人做决定
    public const string Acceptance = "acceptance"; // 需要验收
    public const string Answer = "answer";         // 用户直接提问的回答
    public const string Alert = "alert";           // 异常告警
}

public sealed class ChannelNorm
{
    public string Label = "";
    public string Density = "";
    public double MaxWords = double.PositiveInfinity;
    public List<string>? Sections;
    public List<string> NotifyOn = new();
}

public static class ChannelNorms
{
    private static readonly List<string> All = new()
    {
        Purpose.Progress, Purpose.Decision, Purpose.Acceptance, Purpose.Answer, Purpose.Alert,
    };
    private static readonly List<string> NoProgress = new()
    {
        Purpose.Decision, Purpose.Acceptance, Purpose.Answer, Purpose.Alert,
    };

    public static readonly IReadOnlyDictionary<string, ChannelNorm> Map = new Dictionary<string, ChannelNorm>
    {
        ["im"] = new ChannelNorm { Label = "IM 即时通信", Density = "轻量低打扰", MaxWords = 100, NotifyOn = NoProgress },
        ["github_pr"] = new ChannelNorm { Label = "GitHub PR 描述", Density = "结构化高密度", Sections = new List<string> { "问题背景", "改动", "验证", "待确认" }, NotifyOn = All },
        ["email"] = new ChannelNorm { Label = "邮件", Density = "结构化高密度", Sections = new List<string> { "背景", "结论", "依据", "待确认" }, NotifyOn = All },
        ["terminal"] = new ChannelNorm { Label = "终端日志", Density = "完整过程", NotifyOn = All },
    };

    public static ChannelNorm Of(string channel) => Map.TryGetValue(channel, out var n) ? n : Map["im"];
}

public sealed record ChannelFact(string Topic, string Change, string Verification, string? Background = null, string? Pending = null, string? Ref = null);
public sealed record ChannelAudit(bool Ok, int Words, string Reason);

public static class Channels
{
    /// <summary>词数统计：中日韩字符按字计，其余按空白分词。</summary>
    public static int CountWords(string? text)
    {
        var wide = 0;
        var rest = new List<char>();
        foreach (var ch in text ?? "")
        {
            if ((ch >= '\u2E80' && ch <= '\u9FFF') || (ch >= '\uF900' && ch <= '\uFAFF') || (ch >= '\uFF00' && ch <= '\uFFEF')) wide += 1;
            else rest.Add(ch);
        }
        var latin = new string(rest.ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return wide + latin;
    }

    /// <summary>低打扰判断：这条消息现在值得发给该渠道吗？（第 6 讲第五节：把“是否值得尝试”与“是否值得打断用户”当作不同问题）</summary>
    public static bool ShouldNotify(string channel, string purpose) => ChannelNorms.Of(channel).NotifyOn.Contains(purpose);

    /// <summary>同一个经过验证的事实，按渠道渲染成不同密度的表达。</summary>
    public static string RenderForChannel(string channel, ChannelFact fact)
    {
        switch (channel)
        {
            case "im":
            {
                var refPart = string.IsNullOrEmpty(fact.Ref) ? "" : fact.Ref + " ";
                return $"{fact.Topic}已处理，{fact.Verification}。{refPart}请确认是否采用。";
            }
            case "github_pr":
                return string.Join("\n", new[]
                {
                    $"问题背景：{fact.Background ?? fact.Topic}",
                    $"改动：{fact.Change}",
                    $"验证：{fact.Verification}",
                    $"待确认：{fact.Pending ?? "无"}",
                });
            case "email":
                return string.Join("\n", new[]
                {
                    $"背景：{fact.Background ?? fact.Topic}",
                    $"结论：{fact.Change}",
                    $"依据：{fact.Verification}",
                    $"待确认：{fact.Pending ?? "无"}",
                });
            default:
                return $"[{fact.Topic}] 改动={fact.Change} 验证={fact.Verification} 待确认={fact.Pending ?? "-"}";
        }
    }

    /// <summary>规范审计：是否超出该渠道的表达约束。</summary>
    public static ChannelAudit AuditChannel(string channel, string text)
    {
        var norm = ChannelNorms.Of(channel);
        var words = CountWords(text);
        if (words > norm.MaxWords)
            return new ChannelAudit(false, words, $"超出 {norm.Label} 规范：{words} 词 > {norm.MaxWords.ToString("0", Fmt.Inv)} 词上限");
        if (norm.Sections != null)
        {
            var missing = norm.Sections.Where(s => !text.Contains(s)).ToList();
            if (missing.Count > 0) return new ChannelAudit(false, words, $"{norm.Label} 缺少结构化小节：{string.Join("、", missing)}");
        }
        return new ChannelAudit(true, words, $"符合 {norm.Label}「{norm.Density}」规范");
    }
}
