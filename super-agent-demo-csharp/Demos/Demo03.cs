// 演示 03 —— 事件驱动与两级事件过滤
//
// 对应讲义第 3 讲（原文第 3 节开头、第 3.1 节）。
//
// 演示四个必须分清的阶段：
//   接收到事件 -> 允许进入候选集 -> 值得后续处理 -> 分派给执行者
// 本演示停在第三个阶段（filterOnly），路由分派见演示 04。
//
// 并演示三件事：
//   1. 程序层 Fast Path 用确定性规则拦掉大量消息，完全不调用模型
//   2. 语义层 Slow Path 才判断“值不值得行动”，且策略可用自然语言调整
//   3. 规则过严会误过滤有价值事件；换一套语义策略，同一条事件结论会不同

namespace SuperAgentDemo;

public static class Demo03
{
    /// <summary>十条事件，覆盖讲义第 3 讲列举的全部来源与判断分支。</summary>
    private sealed record EvDef(string Key, string Source, string ThreadId, string Text, string Author, string[]? Mentions = null);

    private static readonly EvDef[] Events =
    {
        new("发布通知", Source.ImNotify, "H-N1", "版本 v2.3.1 已发布到生产环境", "release-bot"),
        new("告警(有价值)", Source.ImNotify, "H-N2", "告警：导出服务连续失败 12 次，日志已附", "alert-bot"),
        new("闲聊群", Source.ImSocial, "H-S1", "午饭吃什么？", "user-9"),
        new("工作群闲聊", Source.ImWork, "H-W1", "楼下新开了家面馆，周末有人去打球吗", "user-2"),
        new("故障反馈", Source.ImWork, "H-W1", "当前产品登录报错，附复现步骤和日志", "user-1"),
        new("改进建议", Source.ImWork, "H-W2", "以后界面也许可以更简洁一些", "user-3"),
        new("已有人处理", Source.ImWork, "H-W3", "这个问题已经有人在处理，先不用接手", "user-4"),
        new("会议邀请", Source.Email, "H-E1", "周五下午 3 点架构评审会议邀请", "calendar"),
        new("点名邮件", Source.Email, "H-E2", "super-agent 帮我核对这次评审有没有时间冲突", "user-1", new[] { "super-agent" }),
        new("CI 失败", Source.GhCi, "H-C1", "CI 运行结果：构建失败，单元测试 2 项不通过", "ci-bot"),
    };

    // 一条事件经过两级过滤后的结果行
    private sealed class FilterRow
    {
        public string Event = "";
        public string Source = "";
        public string Program = "";   // 程序层 Fast Path
        public string Semantic = "";  // 语义层 Slow Path
        public string ModelCall = ""; // 是否调用模型
    }

    private sealed class PassOpts
    {
        public string Label = "";
        public string SemanticPolicy = "default";
        public IReadOnlyDictionary<string, string>? SourcePolicy;
        public string Note = "";
    }

    private sealed class PassResult
    {
        public BuiltSystem Sys = null!;
        public List<FilterRow> Rows = new();
    }

    public static Task RunAsync() => MainAsync();

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 03 · 事件驱动与两级事件过滤");

        // ---- 第一遍：默认策略 -------------------------------------------------
        var pass1 = await RunPass(new PassOpts
        {
            Label = "第一遍：默认配置（通知群/闲聊群直接忽略，邮件仅响应明确请求，工作群允许观察）",
            Note = "语义策略：关注当前产品的问题反馈；日常闲聊不采取行动；要求暂缓时结合现有任务状态判断。",
        });
        trace.Section("第一遍结果：默认策略");
        trace.Block(Table.RenderTable(
            new[] { "事件", "来源", "程序层 Fast Path", "语义层 Slow Path", "调用模型" },
            pass1.Rows.Select(r => (IReadOnlyList<object?>)new List<object?> { r.Event, r.Source, r.Program, r.Semantic, r.ModelCall }).ToList()));
        var f1 = pass1.Sys.Bus.Funnel();
        trace.Note($"漏斗：{f1.ToJson()}");
        var keepRatio = f1.Received > 0 ? (double)f1.Candidate / f1.Received : 0;
        trace.Note($"教学成本模型：总事件 N={f1.Received}，程序层保留比例 p={keepRatio.Fixed(2)}，需要模型判断的事件从 {f1.Received} 条降到 {f1.Candidate} 条");
        trace.Note($"Router 批处理：{pass1.Sys.Router.Rounds.Count} 轮处理了 {pass1.Sys.Router.Rounds.Sum(r => r.BatchSize)} 条候选事件，读缓存 Token {pass1.Sys.Stats.CacheReadTokens}");
        trace.Note("「已有人处理，先不用接手」被判为无须行动，是因为 filterOnly 模式下没有在跑任务；");
        trace.Note("  在真实运行中它会作为约束投递给相关 Worker（见演示 02 的全局约束广播）。");

        // ---- 第二遍：换一套语义策略 -------------------------------------------
        var pass2 = await RunPass(new PassOpts
        {
            Label = "第二遍：只把语义策略换成「需求收集」，程序层规则不变",
            SemanticPolicy = "collect-ideas",
            Note = "语义策略：除问题反馈外，也收集产品改进线索（“以后…”“如果能…”“建议…”）。",
        });
        trace.Section("第二遍结果：语义策略改为需求收集");
        var changed = pass2.Rows
            .Select((r, i) => (Row: r, Pass1Semantic: pass1.Rows[i].Semantic))
            .Where(x => x.Row.Semantic != x.Pass1Semantic)
            .ToList();
        trace.Block(Table.RenderTable(
            new[] { "事件", "默认策略", "需求收集策略" },
            changed.Select(c => (IReadOnlyList<object?>)new List<object?> { c.Row.Event, c.Pass1Semantic, c.Row.Semantic }).ToList()));
        trace.Note("同一条事件、同一套程序层规则，只改语义策略就得到不同结论：");
        trace.Note("  这正是讲义强调的“事件是否值得处理，取决于系统目标与策略”。");

        // ---- 第三遍：放宽一条被误过滤的规则 -----------------------------------
        var relaxed = new Dictionary<string, string>(DefaultSourcePolicy.Map) { [Source.ImNotify] = Policy.Observe };
        var pass3 = await RunPass(new PassOpts
        {
            Label = "第三遍：把通知群从「直接忽略」放宽为「作为候选事件」",
            SourcePolicy = relaxed,
            Note = "动机：告警类消息虽然来自通知群，却可能携带值得处理的故障信息。",
        });
        trace.Section("第三遍结果：放宽通知群规则");
        var alarm1 = pass1.Rows.First(r => r.Event == "告警(有价值)");
        var alarm3 = pass3.Rows.First(r => r.Event == "告警(有价值)");
        trace.Block(Table.RenderTable(
            new[] { "事件", "放宽前", "放宽后" },
            new List<IReadOnlyList<object?>> { new List<object?> { "告警(有价值)", $"{alarm1.Program} / {alarm1.Semantic}", $"{alarm3.Program} / {alarm3.Semantic}" } }));
        var f3 = pass3.Sys.Bus.Funnel();
        trace.Note($"代价：进入候选集的事件从 {f1.Candidate} 条增加到 {f3.Candidate} 条，语义层调用与 Token 随之上升。");

        trace.Section("三遍对比");
        var f2 = pass2.Sys.Bus.Funnel();
        trace.Block(Table.RenderTable(
            new[] { "配置", "接收", "程序层过滤", "候选事件", "判定值得处理", "Router 轮数", "读缓存 Token" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "默认策略", f1.Received, f1.ProgramFiltered, f1.Candidate, pass1.Sys.Router.FilterResults.Count(r => r.Worth), pass1.Sys.Router.Rounds.Count, pass1.Sys.Stats.CacheReadTokens },
                new List<object?> { "需求收集策略", f1.Received, f2.ProgramFiltered, f2.Candidate, pass2.Sys.Router.FilterResults.Count(r => r.Worth), pass2.Sys.Router.Rounds.Count, pass2.Sys.Stats.CacheReadTokens },
                new List<object?> { "放宽通知群", f3.Received, f3.ProgramFiltered, f3.Candidate, pass3.Sys.Router.FilterResults.Count(r => r.Worth), pass3.Sys.Router.Rounds.Count, pass3.Sys.Stats.CacheReadTokens },
            },
            new[] { 'l', 'r', 'r', 'r', 'r', 'r', 'r' }));

        Takeaways.Print(trace, new[]
        {
            "Trigger 只是“进入系统视野的环境事件”，不等于已确认的任务，更不等于获准执行操作。",
            "程序层用确定性规则（来源、群组范围、白名单）快速缩小候选范围，这一级完全不调用模型。",
            "语义层才判断“值不值得行动”，它是面向人类的自然语言扩展点，改策略就能改行为，但不保证每次判断都准确。",
            "过滤过严会让有价值事件在第一级就被丢弃（本例的告警消息）；过松则把大量无关事件推给模型，抬高成本。",
            "“通过过滤”只回答值不值得继续处理，“交给谁”是下一讲的路由问题，两者都借用 Fast / Slow Path 思想但职责不同。",
        });
    }

    /// <summary>跑一遍过滤流程，返回每条事件的两级结果。</summary>
    private static async Task<PassResult> RunPass(PassOpts o)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = o.Label,
            MsPerSimSecond = 10,
            FilterOnly = true, // 只做过滤，不做分派（第 3 讲到“值得处理”为止）
            SemanticPolicy = o.SemanticPolicy,
            SourcePolicy = o.SourcePolicy,
            GlobalStateTokens = 4000,
            MaxBatch = 8,
        });
        if (!string.IsNullOrEmpty(o.Note)) sys.Trace.Note(o.Note);

        sys.Router.Start();
        var programResults = new List<(string Key, string TriggerId, string Stage, string Reason)>();
        foreach (var e in Events)
        {
            var r = await sys.Bus.PublishAsync(new EventInput
            {
                Source = e.Source,
                ThreadId = e.ThreadId,
                Text = e.Text,
                Author = e.Author,
                Mentions = e.Mentions?.ToList(),
            });
            programResults.Add((e.Key, r.Trigger!.Id, r.Stage, r.Reason));
        }
        await sys.Router.QuiesceAsync(timeoutSimSec: 40);
        sys.Router.Stop();
        await sys.Router.Done!;

        var rows = programResults.Select(p =>
        {
            var ev = Events.First(e => e.Key == p.Key);
            var sem = sys.Router.FilterResults.FirstOrDefault(f => f.Id == p.TriggerId);
            return new FilterRow
            {
                Event = p.Key,
                Source = ev.Source,
                Program = p.Stage == "program-filtered" ? $"过滤（{p.Reason}）" : "进入候选集",
                Semantic = sem != null ? (sem.Worth ? $"值得处理（{sem.Reason}）" : $"无须行动（{sem.Reason}）") : "未调用模型",
                ModelCall = sem != null ? "是" : "否",
            };
        }).ToList();
        return new PassResult { Sys = sys, Rows = rows };
    }
}
