// Llm.cs —— Mock 模型：延迟模型 + Token 计费 + 规则化“语义判断”
//
// 对应讲义：
//   第 2.1 节  Router 常“输入较长、输出较短”，可用较轻量的模型
//   第 2.2 节  Worker 直接决定任务结果，通常配能力最强的模型
//   第 3.1 节  语义层策略（Slow Path）：由模型结合提示词与上下文判断是否行动
//   第 7.2 节  批处理压力测试的三个指标：batch 大小、单次请求耗时、每决策分摊时间
//   第 10 讲   读缓存 Token（全部 Session 合计）
//
// 教学说明：真实模型不可用也不可控，因此这里的“模型”是规则驱动的替身。
// 它保留两件对理解设计至关重要的性质：
//   (1) 调用有延迟，且延迟随输入/输出长度变化（Prefill 可并行、Decode 串行）；
//   (2) 调用有 Token 成本，可以被统计和比较。

using static SuperAgentDemo.Signals;

namespace SuperAgentDemo;

// ---------------------------------------------------------------------------
// 1. 模型档位
// ---------------------------------------------------------------------------

public sealed record ModelProfile(double FixedOverheadSec, double PrefillTokensPerSec, double DecodeTokensPerSec);

public static class ModelProfiles
{
    public static readonly IReadOnlyDictionary<string, ModelProfile> Map = new Dictionary<string, ModelProfile>
    {
        // Router：轻量模型，Prefill 快、输出短，适合“输入长输出短”的路由决策
        ["lite-router"] = new ModelProfile(0.3, 42000, 170),
        // Worker：最强模型，单位算力更贵、推理更慢，但决定任务质量
        ["strong-worker"] = new ModelProfile(0.5, 14000, 62),
        // Watchdog：独立 Session，中等档位
        ["watchdog"] = new ModelProfile(0.3, 30000, 140),
    };
}

public static class Tokenizer
{
    /// <summary>估算一段文本的 Token 数：中文按 1 字/token，其余按 4 字符/token。教学近似。</summary>
    public static int EstimateTokens(string? text)
    {
        int wide = 0, narrow = 0;
        foreach (var ch in text ?? "")
        {
            if ((ch >= '\u2E80' && ch <= '\u9FFF') || (ch >= '\uF900' && ch <= '\uFAFF') || (ch >= '\uFF00' && ch <= '\uFFEF')) wide += 1;
            else narrow += 1;
        }
        return (int)Math.Ceiling(wide + narrow / 4.0);
    }
}

// ---------------------------------------------------------------------------
// 2. 统计：跨 Session 汇总
// ---------------------------------------------------------------------------

public sealed record LlmStatsSnapshot(int Calls, int PromptTokens, int CacheReadTokens, int CompletionTokens, double BusySeconds);

public sealed class ActorStat
{
    public int Calls;
    public int PromptTokens;
    public int CacheReadTokens;
    public int CompletionTokens;
    public double BusySeconds;
}

public sealed class LlmStats
{
    private readonly object _gate = new();

    public int Calls;
    public int PromptTokens;      // 本轮输入总 Token
    public int CacheReadTokens;   // 其中命中缓存读取的部分（第 10 讲的统计口径）
    public int CompletionTokens;
    public double BusySeconds;    // 模型被占用的模拟时间
    public readonly Dictionary<string, ActorStat> ByActor = new();

    public void Record(string actor, int promptTokens, int cacheReadTokens, int completionTokens, double latency)
    {
        lock (_gate)
        {
            Calls += 1;
            PromptTokens += promptTokens;
            CacheReadTokens += cacheReadTokens;
            CompletionTokens += completionTokens;
            BusySeconds += latency;
            if (!ByActor.TryGetValue(actor, out var a))
            {
                a = new ActorStat();
                ByActor[actor] = a;
            }
            a.Calls += 1;
            a.PromptTokens += promptTokens;
            a.CacheReadTokens += cacheReadTokens;
            a.CompletionTokens += completionTokens;
            a.BusySeconds += latency;
        }
    }

    public LlmStatsSnapshot Snapshot()
    {
        lock (_gate) return new LlmStatsSnapshot(Calls, PromptTokens, CacheReadTokens, CompletionTokens, BusySeconds);
    }
}

// ---------------------------------------------------------------------------
// 3. MockLlm
// ---------------------------------------------------------------------------

public sealed record LlmCall<T>(T Result, double Latency, int PromptTokens);

public sealed class MockLlm
{
    private readonly Clock _clock;
    public string Actor { get; }
    public string Model { get; }
    public ModelProfile Profile { get; }
    public LlmStats Stats { get; }

    public MockLlm(Clock clock, string actor, string model = "lite-router", LlmStats? stats = null)
    {
        _clock = clock;
        Actor = actor;
        Model = model;
        Profile = ModelProfiles.Map.TryGetValue(model, out var p) ? p : ModelProfiles.Map["lite-router"];
        Stats = stats ?? new LlmStats();
    }

    /// <summary>纯函数：给定输入输出 Token，返回本次调用的模拟耗时（秒）。</summary>
    public double LatencySeconds(int promptTokens, int completionTokens)
    {
        var prefill = promptTokens / Profile.PrefillTokensPerSec;
        var decode = completionTokens / Profile.DecodeTokensPerSec;
        return Profile.FixedOverheadSec + prefill + decode;
    }

    /// <summary>
    /// 发起一次“模型调用”。先算出结果，再等待等长的时间：模拟“这一轮推理期间系统被占用”。
    /// </summary>
    public async Task<LlmCall<T>> CallAsync<T>(int promptTokens, int outputTokens, Func<T> produce, int? cacheReadTokens = null)
    {
        var latency = LatencySeconds(promptTokens, outputTokens);
        var result = produce();
        await _clock.Sleep(latency); // 走虚拟时钟：精确推进 latency 模拟秒（等价 Node 的 delay(latency*k)，但可复现）
        Stats.Record(Actor, promptTokens, cacheReadTokens ?? promptTokens, outputTokens, latency);
        return new LlmCall<T>(result, latency, promptTokens);
    }
}

// ---------------------------------------------------------------------------
// 4. 规则化的“语义判断”：把提示词策略写成可复现的代码
// ---------------------------------------------------------------------------

public static class Signals
{
    /// <summary>工作请求线索：出现即认为可能存在需要处理的问题。</summary>
    public static readonly string[] WorkSignals = { "报错", "失败", "修复", "优化", "排查", "复现", "卡顿", "超时", "崩溃", "bug", "异常", "挂了" };
    /// <summary>叫停 / 约束线索：需要改变已有任务的方向。</summary>
    public static readonly string[] SteerSignals = { "先不要", "先不用", "暂缓", "暂停", "别改", "不要动", "叫停", "先停", "等评审" };
    /// <summary>询问线索：需要沿引用图召回上下文再回答。</summary>
    public static readonly string[] AskSignals = { "进度", "到哪", "怎么样了", "上次那个", "评审到", "有没有" };
    /// <summary>明确的闲聊：语义层通常判断为无须行动。</summary>
    public static readonly string[] ChitchatSignals = { "午饭", "吃什么", "下班", "周末", "哈哈", "恭喜", "辛苦了" };
    /// <summary>需求线索：在“需求收集”策略下才值得行动。</summary>
    public static readonly string[] IdeaSignals = { "以后", "如果能", "希望可以", "建议", "也许可以", "要不要考虑" };

    public static bool ContainsAny(string? text, IEnumerable<string> words)
    {
        var t = text ?? "";
        return words.Any(t.Contains);
    }
}

public sealed record Judgement(bool Act, string Reason, string Kind);

/// <summary>Router 注入上下文后能“看见”的单个 Worker 摘要（第 5 讲的 Inline 摘要）。</summary>
public sealed class WorkerSummary
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public List<string> ThreadIds { get; set; } = new();
    public string Status { get; set; } = "";
}

/// <summary>Router 的全局视图：Worker 摘要 + 当前语义策略。</summary>
public sealed class RoutingView
{
    public List<WorkerSummary> Workers { get; set; } = new();
    public string? Policy { get; set; }
}

public sealed record RoutingDecision(string Action, string? WorkerId, string Reason, string? Kind = null);

public static class Semantic
{
    /// <summary>
    /// 语义层策略（第 3.1 节 Slow Path）：这条候选事件是否值得进入后续处理？
    /// policy = "collect-ideas" 时换成需求收集策略，用于演示第 3 讲的结论：
    /// 同一条事件是否值得处理，取决于系统目标与策略。
    /// </summary>
    public static Judgement JudgeWorthActing(Trigger trigger, int activeTaskCount = 0, string policy = "default")
    {
        var text = trigger.Text;
        if (Signals.ContainsAny(text, SteerSignals))
        {
            return activeTaskCount > 0
                ? new Judgement(true, "要求暂缓，且有正在进行的任务需要调整", "steer")
                : new Judgement(false, "要求暂缓，但当前没有相关任务", "steer");
        }
        if (Signals.ContainsAny(text, AskSignals)) return new Judgement(true, "用户在询问任务进展，需要召回上下文", "ask");
        if (Signals.ContainsAny(text, WorkSignals)) return new Judgement(true, "包含具体故障/需求信息", "work");
        if (Signals.ContainsAny(text, ChitchatSignals)) return new Judgement(false, "日常闲聊，无须 Agent 介入", "chitchat");
        if (policy == "collect-ideas" && Signals.ContainsAny(text, IdeaSignals))
            return new Judgement(true, "需求收集策略：这是一条产品改进线索", "work");
        return new Judgement(false, "当前策略下没有明确处理价值", "other");
    }

    /// <summary>Router 的路由决策（第 3.2 节 Slow Path）：交给谁处理？</summary>
    public static RoutingDecision JudgeRouting(Trigger trigger, RoutingView view)
    {
        var judgement = JudgeWorthActing(trigger,
            activeTaskCount: view.Workers.Count(w => w.Status == "running"),
            policy: view.Policy ?? "default");

        if (!judgement.Act) return new RoutingDecision("ignore", null, judgement.Reason, judgement.Kind);

        // 是否已有 Worker 在做同一件事？（依据注入的 Worker 摘要 + 关键词重叠）
        var related = view.Workers.FirstOrDefault(w => w.Status != "closed" && TaskOverlap(w.Task, trigger.Text));

        if (judgement.Kind == "steer")
        {
            if (related != null) return new RoutingDecision("steer", related.Id, $"约束已投递给 {related.Id}");
            // 全局约束（例如“先不要改接口”）往往不含具体任务关键词，但对所有在跑任务都生效。
            var running = view.Workers.Where(w => w.Status != "closed").ToList();
            return running.Count > 0
                ? new RoutingDecision("steer-all", null, $"未指向具体任务，作为全局约束广播给 {string.Join(" ", running.Select(w => w.Id))}")
                : new RoutingDecision("ignore", null, "没有可调整的在跑任务");
        }
        if (judgement.Kind == "ask")
            return new RoutingDecision("recall-and-reply", related?.Id, "需要沿引用图召回后回复用户");
        if (related != null) return new RoutingDecision("attach", related.Id, $"已有相关任务 {related.Id}，事件并入其 Thread");
        return new RoutingDecision("dispatch", null, "新任务，创建 Worker 并订阅该 Thread");
    }

    /// <summary>教学用的“任务相关性”判断：共享关键词即视为相关。</summary>
    public static bool TaskOverlap(string taskText, string eventText)
    {
        string[] keywords = { "登录", "导出", "渲染", "超时", "性能", "卡顿", "配置", "文档", "波浪号" };
        var hit = keywords.Where(taskText.Contains).ToList();
        return hit.Count > 0 && hit.Any(eventText.Contains);
    }
}

/// <summary>Watchdog 进展判断的证据输入（第 6 节）。</summary>
public sealed record Evidence(int RepeatedSameCall, int NewEvidenceCount, int ScopeViolations, int InfraErrors, string Stage);

public sealed record ProgressVerdict(string Verdict, string Reason);

public static class Progress
{
    /// <summary>Watchdog 的进展判断：区分“活动量”与“实质进展”。</summary>
    public static ProgressVerdict JudgeProgress(Evidence evidence)
    {
        if (evidence.InfraErrors > 0) return new ProgressVerdict("infra", $"基础设施报错 {evidence.InfraErrors} 次");
        if (evidence.ScopeViolations > 0) return new ProgressVerdict("drift", $"越出任务范围 {evidence.ScopeViolations} 次");
        // 关键：长时间等待工具不等于停滞（第 9 讲自测题 3）
        if (evidence.Stage == "waiting-tool" && evidence.NewEvidenceCount == 0)
            return new ProgressVerdict("watch", "正在等待耗时工具，暂不判定为停滞");
        if (evidence.RepeatedSameCall >= 3 && evidence.NewEvidenceCount == 0)
            return new ProgressVerdict("stalled", $"连续 {evidence.RepeatedSameCall} 次相同调用且无新证据");
        return new ProgressVerdict("healthy", "有新证据或调用在变化");
    }
}
