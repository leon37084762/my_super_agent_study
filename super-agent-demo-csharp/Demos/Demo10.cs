// 演示 10 —— 初步实验与证据解读：批处理压力测试 + 消融对比
//
// 对应讲义第 10 讲（原文第 7 节）。
//
// 原文的实验思路：
//   7.1 用合成负载（约 300 条消息、其中约 20 条工作请求，其余闲聊/跟进/叫停/CI）压测系统；
//       并给出若干“基线方案”：单 Session、单 Session + Subagent 工具（中性提示 / 优先委派提示）；
//   7.2 批处理压力测试关注三个指标：batch 大小、单次请求耗时、每决策分摊时间；
//       用“读缓存 Token（全部 Session 合计）”衡量上下文重读成本；
//   具体编码工作用“只消耗时间的工具”代替；数值服务于理解机制方向，不是性能基准。
//
// 四部分：
//   1. 批处理压力测试：突发流量涌入时的漏斗与三个核心指标
//   2. 消融对比：A 关闭批处理（同一突发负载） / B 去掉直通（真实合成流）
//   3. 与“单 Session + Subagent”基线对比（解析模型 vs 本系统实测）
//   4. 证据解读：这些数字能说明什么、不能说明什么

namespace SuperAgentDemo;

public static class Demo10
{
    public static Task RunAsync() => MainAsync();

    // -----------------------------------------------------------------------
    // 负载：突发流量（用于批处理压测）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 构造一次“突发流量”：n 条消息在极短时间窗口内涌入（模拟早高峰 / 告警风暴）。
    /// 全部落在无人订阅的闲聊话题上，因此都会交给 Router 判断——正好用来观察批处理。
    /// </summary>
    private static List<StreamMsg> MakeBurst(int n = 30)
    {
        var texts = new[] { "午饭吃什么", "今天下班早点走", "周末有人打球吗", "哈哈哈这个表情太好笑了", "恭喜发版，辛苦了", "收到，稍后看", "这个链接谁发的", "+1" };
        var msgs = new List<StreamMsg>();
        for (var i = 0; i < n; i += 1)
        {
            msgs.Add(new StreamMsg
            {
                At = Math.Round(i * 0.03 * 100, MidpointRounding.AwayFromZero) / 100, // 每 0.03 模拟秒来一条，30 条约 0.9s 内涌完
                Kind = "burst",
                Input = new EventInput
                {
                    Source = Source.ImWork,
                    ThreadId = $"H-B{1 + (i % 3)}",
                    Text = texts[i % texts.Length],
                    Author = $"user-{1 + (i % 5)}",
                },
            });
        }
        return msgs;
    }

    // -----------------------------------------------------------------------
    // 在给定负载上跑一遍双层系统，并采集指标
    // -----------------------------------------------------------------------

    private sealed class DualResult
    {
        public RouterSummary Summary = null!;
        public Funnel Funnel = null!;
        public LlmStatsSnapshot Stats = null!;
        public List<double> Times = new();
        public int Workers;
    }

    private static async Task<DualResult> RunDualLayer(
        List<StreamMsg> stream,
        int maxBatch = 8,
        bool directDelivery = true,
        double msPerSimSecond = 18,
        int globalStateTokens = 9000)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "",
            MsPerSimSecond = msPerSimSecond,
            MaxBatch = maxBatch,
            DirectDelivery = directDelivery,
            GlobalStateTokens = globalStateTokens,
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        var stats = sys.Stats;
        router.Start();

        // 并发跟踪每个 Worker 的“到达 -> 完成”时间（用于与单会话基线对比完成时间分布）。
        // 用局部 async 函数以 fire-and-forget 方式启动，禁用 Task.Run（会脱离单线程虚拟调度器导致死锁）。
        var completions = new List<(string Id, string? OriginTriggerId, double DispatchedAt, double DoneAt)>();
        var seen = new List<string>();
        var stopTrack = false;
        async Task TrackerLoop()
        {
            while (!stopTrack)
            {
                foreach (var w in router.Workers.Values.ToList())
                {
                    if (w.Status == "closed" && !seen.Contains(w.Id))
                    {
                        seen.Add(w.Id);
                        completions.Add((w.Id, w.OriginTriggerId, w.DispatchedAt, clock.Now()));
                    }
                }
                await clock.Sleep(0.05);
            }
        }
        var tracker = TrackerLoop();

        var published = await SystemBuilder.ReplayStream(bus, stream, clock);
        var arrivalById = new Dictionary<string, double>();
        foreach (var p in published)
            if (p.Result?.Trigger != null) arrivalById[p.Result.Trigger.Id] = p.At;

        await router.QuiesceAsync(timeoutSimSec: 200);
        stopTrack = true;
        await tracker;
        router.Stop();
        await router.Done!;

        var times = completions
            .Select(c => c.DoneAt - (c.OriginTriggerId != null && arrivalById.TryGetValue(c.OriginTriggerId, out var a) ? a : c.DispatchedAt))
            .Where(x => x >= 0)
            .ToList();
        return new DualResult
        {
            Summary = router.Summary(),
            Funnel = bus.Funnel(),
            Stats = stats.Snapshot(),
            Times = times,
            Workers = router.Workers.Count,
        };
    }

    // -----------------------------------------------------------------------
    // 第一部分：批处理压力测试（突发流量）
    // -----------------------------------------------------------------------

    private static void PartStress(Trace trace, DualResult r)
    {
        trace.Section("第一部分 · 批处理压力测试：30 条消息在 ~1 秒内涌入");
        trace.Block(Table.RenderTable(
            new[] { "漏斗阶段", "事件数" },
            r.Funnel.Rows().Select(kv => (IReadOnlyList<object?>)new List<object?> { kv.Key, kv.Value }).ToList(),
            new[] { 'l', 'r' }));
        var s = r.Summary;
        trace.Block(Table.RenderTable(
            new[] { "指标（第 7.2 节）", "数值", "含义" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "① 平均 batch 大小", s.MeanBatch.Fixed(2), "单轮合并处理的事件数（第 2 讲批处理）" },
                new List<object?> { "② 单轮平均耗时", $"{s.MeanRoundLatency.Fixed(2)}s", "一次 Router 推理：长输入、短输出" },
                new List<object?> { "③ 每决策分摊时间", $"{s.ServicePerDecision.Fixed(3)}s", "把一轮耗时摊到本轮多条事件上" },
                new List<object?> { "Router 轮数", s.RouterRounds, "常驻层被唤醒并推理的次数" },
                new List<object?> { "决策数", s.Decisions, "实际判断过的事件条数" },
                new List<object?> { "读缓存 Token(Router)", SystemBuilder.FmtTok(s.CacheReadTokens), "每轮都要重读常驻全局状态摘要" },
            },
            new[] { 'l', 'r', 'l' }));
        trace.Note($"突发 30 条只用 {s.RouterRounds} 轮就消化完（平均 batch {s.MeanBatch.Fixed(2)}）：上一轮推理期间到达的事件积压，下一轮合并处理。");
        trace.Note($"关键：每决策分摊时间 {s.ServicePerDecision.Fixed(3)}s 远小于单轮耗时 {s.MeanRoundLatency.Fixed(2)}s——批处理把\"重读全局状态\"的成本摊薄到多条事件上。");
    }

    // -----------------------------------------------------------------------
    // 第二部分 A：批处理消融（同一突发负载）
    // -----------------------------------------------------------------------

    private static void PartAblationBatch(Trace trace, DualResult batched, DualResult serial)
    {
        trace.Section("第二部分 · 消融 A：关闭批处理（maxBatch=1，同一突发负载）");
        IReadOnlyList<object?> Row(string name, DualResult r) => new List<object?>
        {
            name, r.Summary.Decisions, r.Summary.RouterRounds, r.Summary.MeanBatch.Fixed(2), $"{r.Summary.ServicePerDecision.Fixed(3)}s", SystemBuilder.FmtTok(r.Summary.CacheReadTokens),
        };
        trace.Block(Table.RenderTable(
            new[] { "配置", "决策数", "Router轮数", "平均batch", "每决策分摊", "读缓存Token(Router)" },
            new List<IReadOnlyList<object?>> { Row("批处理开启（maxBatch=8）", batched), Row("关闭批处理（maxBatch=1）", serial) },
            new[] { 'l', 'r', 'r', 'r', 'r', 'r' }));
        trace.Note(
            $"同样的 30 条突发消息：开启批处理只用 {batched.Summary.RouterRounds} 轮、每决策分摊 {batched.Summary.ServicePerDecision.Fixed(3)}s；" +
            $"关闭后变成 {serial.Summary.RouterRounds} 轮、每决策分摊 {serial.Summary.ServicePerDecision.Fixed(3)}s（一轮只处理一条，全局状态被重读 {serial.Summary.RouterRounds} 次）。");
        trace.Note($"读缓存 Token：{SystemBuilder.FmtTok(batched.Summary.CacheReadTokens)} → {SystemBuilder.FmtTok(serial.Summary.CacheReadTokens)}。这就是批处理压测要看的成本差。");
    }

    // -----------------------------------------------------------------------
    // 第二部分 B：直通消融（真实合成流）
    // -----------------------------------------------------------------------

    private static void PartAblationDirect(Trace trace, DualResult full, DualResult noDirect)
    {
        trace.Section("第二部分 · 消融 B：去掉订阅直通（directDelivery=false，真实合成流）");
        IReadOnlyList<object?> Row(string name, DualResult r) => new List<object?>
        {
            name, r.Summary.Decisions, r.Summary.RouterRounds, r.Funnel.DirectDelivered, r.Funnel.ToRouter, SystemBuilder.FmtTok(r.Stats.CacheReadTokens), r.Workers,
        };
        trace.Block(Table.RenderTable(
            new[] { "配置", "决策数", "Router轮数", "直通投递", "交Router", "读缓存Token合计", "Worker数" },
            new List<IReadOnlyList<object?>> { Row("完整（含直通）", full), Row("去掉直通", noDirect) },
            new[] { 'l', 'r', 'r', 'r', 'r', 'r', 'r' }));
        trace.Note(
            $"去掉直通后，已订阅 Thread 的事件（CI 结果、跟进补充）也要经 Router 判断归属：交 Router 事件 {full.Funnel.ToRouter} → {noDirect.Funnel.ToRouter}，" +
            $"Router 轮数 {full.Summary.RouterRounds} → {noDirect.Summary.RouterRounds}。");
        trace.Note($"全部 Session 读缓存 Token 合计 {SystemBuilder.FmtTok(full.Stats.CacheReadTokens)} → {SystemBuilder.FmtTok(noDirect.Stats.CacheReadTokens)}：直通让高频事件绕开 Router，省下大量重复的上下文重读。");
        trace.Note("消融（ablation）的意义：不是\"加了机制变好了\"，而是\"拿掉机制确实变差了\"，从而确认该机制在起作用。");
    }

    // -----------------------------------------------------------------------
    // 第三部分：与“单 Session + Subagent 工具”基线对比
    // -----------------------------------------------------------------------

    private static void PartBaseline(Trace trace, List<StreamMsg> stream, DualResult dual)
    {
        trace.Section("第三部分 · 与\"单 Session + Subagent 工具\"基线对比（第 10 讲 7.1 方案三、四）");
        var neutral = SystemBuilder.SimulateSingleSession(stream, "neutral", 90, 2.5, 12000, 7);
        var prefer = SystemBuilder.SimulateSingleSession(stream, "prefer", 90, 2.5, 12000, 7, 0.8);
        var dP50 = SystemBuilder.Percentile(dual.Times, 50);
        var dP95 = SystemBuilder.Percentile(dual.Times, 95);
        trace.Block(Table.RenderTable(
            new[] { "方案", "委派/Worker", "读缓存Token合计", "完成p50", "完成p95", "决策通道是否被任务占住" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "单 Session + 中性提示", neutral.Delegated, SystemBuilder.FmtTok(neutral.CacheReadTokens), SystemBuilder.FmtMin(neutral.P50), SystemBuilder.FmtMin(neutral.P95), $"是·串行执行·排队 {neutral.Interruptions} 次" },
                new List<object?> { "单 Session + 优先委派", prefer.Delegated, SystemBuilder.FmtTok(prefer.CacheReadTokens), SystemBuilder.FmtMin(prefer.P50), SystemBuilder.FmtMin(prefer.P95), $"部分·偶发自己做·排队 {prefer.Interruptions} 次" },
                new List<object?> { "双层架构（本系统实测）", $"{dual.Workers} Worker", SystemBuilder.FmtTok(dual.Stats.CacheReadTokens), SystemBuilder.FmtMin(dP50), SystemBuilder.FmtMin(dP95), "否·Router 只决策不执行" },
            },
            new[] { 'l', 'r', 'r', 'r', 'r', 'l' }));
        trace.Note($"① 读缓存 Token（尺度一致、最硬的对比）：单会话每条消息都重读整段上下文 -> {SystemBuilder.FmtTok(neutral.CacheReadTokens)}；双层架构 Router 只读摘要、Worker 上下文彼此隔离 -> {SystemBuilder.FmtTok(dual.Stats.CacheReadTokens)}，约 {(neutral.CacheReadTokens / (double)Math.Max(1, dual.Stats.CacheReadTokens)).Fixed(1)}× 差距。");
        trace.Note($"② 决策通道：单会话一旦亲自执行 90s 的任务就无法响应新消息（排队 {neutral.Interruptions} 次）；双层架构里 Router 从不执行任务，Worker 并发跑，新消息始终能被决策（排队 0 次）。");
        trace.Note($"③ 委派稳定性：中性提示下模型几乎不主动委派（{neutral.Delegated} 次）；优先委派也可能\"任务小就自己做\"（{prefer.Delegated} 次）。双层架构由 Router 强制分派，不依赖模型自觉。");
        trace.Note("口径提示：单 Session 是解析模型（workSec=90 代表一次真实编码），双层是合成负载下的实测且教学计划被压缩；完成时间只看量级与趋势，重点比①②③。");
    }

    // -----------------------------------------------------------------------
    // 第四部分：证据解读
    // -----------------------------------------------------------------------

    private static void PartEvidence(Trace trace)
    {
        trace.Section("第四部分 · 证据解读：这些数字能说明什么、不能说明什么");
        trace.Block(Table.RenderTable(
            new[] { "观察到的现象", "能支持的结论", "不能推出的结论" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "突发流量下批处理减少轮数与重读", "合并决策能摊薄全局状态的重读成本", "不能给出真实模型的绝对延迟" },
                new List<object?> { "去掉直通后交 Router 事件与 Token 上升", "订阅直通确实减少了 Router 的介入", "不能量化真实 IM 环境下的收益" },
                new List<object?> { "单会话读缓存 Token 远高于双层", "串行主会话反复重读整段上下文", "不代表某个具体产品的真实表现" },
                new List<object?> { "MockLLM 延迟随输入/输出变化", "保留了对比所需的定性性质", "不是真实模型的 Token/延迟曲线" },
            },
            new[] { 'l', 'l', 'l' }));
        trace.Note("第 10 讲的态度：先做\"能跑起来的最小实验\"，用消融与对照确认机制的方向，再谈规模与真实性。");
        trace.Note("教学替身的数值服务于\"理解机制为何有效\"，不应被当成性能基准；真实结论需在真实模型与真实负载上复做。");
    }

    // -----------------------------------------------------------------------

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 10 · 初步实验与证据解读");

        // 负载一：突发流量（批处理压测）；负载二：真实合成流（直通消融 + 基线对比）
        var burst = MakeBurst(30);
        var stream = SystemBuilder.MakeStream(total: 120, workCount: 8, durationSec: 120, seed: 20260711, workSeconds: 6);
        var kinds = new Dictionary<string, int>();
        foreach (var m in stream) kinds[m.Kind] = kinds.TryGetValue(m.Kind, out var c) ? c + 1 : 1;
        trace.Section("实验负载：突发流量 + 合成消息流（按比例缩放的教学替身）");
        trace.Note($"突发负载：{burst.Count} 条消息在 ~0.9 模拟秒内涌入，全部落在无人订阅的话题上（都需 Router 判断）。");
        trace.Note($"合成流：{stream.Count} 条消息跨越约 {stream.Max(m => m.At).Fixed(0)} 模拟秒；构成：{string.Join("，", kinds.Select(kv => $"{kv.Key}×{kv.Value}"))}。");
        trace.Note("原文约 300 条消息 / 20 条工作请求，这里按比例缩小以便几秒内跑完；比例与角色保持一致。");

        var burstBatched = await RunDualLayer(burst, maxBatch: 8, msPerSimSecond: 30);
        var burstSerial = await RunDualLayer(burst, maxBatch: 1, msPerSimSecond: 30);
        var full = await RunDualLayer(stream, maxBatch: 8, directDelivery: true);
        var noDirect = await RunDualLayer(stream, maxBatch: 8, directDelivery: false);

        PartStress(trace, burstBatched);
        PartAblationBatch(trace, burstBatched, burstSerial);
        PartAblationDirect(trace, full, noDirect);
        PartBaseline(trace, stream, full);
        PartEvidence(trace);

        trace.Section("全演示小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "演示结果" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "批处理", $"突发 30 条用 {burstBatched.Summary.RouterRounds} 轮消化（平均 batch {burstBatched.Summary.MeanBatch.Fixed(2)}），每决策分摊 {burstBatched.Summary.ServicePerDecision.Fixed(3)}s" },
                new List<object?> { "关闭批处理消融", $"Router 轮数 {burstBatched.Summary.RouterRounds} → {burstSerial.Summary.RouterRounds}，读缓存 Token {SystemBuilder.FmtTok(burstBatched.Summary.CacheReadTokens)} → {SystemBuilder.FmtTok(burstSerial.Summary.CacheReadTokens)}" },
                new List<object?> { "去掉直通消融", $"交 Router 事件 {full.Funnel.ToRouter} → {noDirect.Funnel.ToRouter}，读缓存 Token {SystemBuilder.FmtTok(full.Stats.CacheReadTokens)} → {SystemBuilder.FmtTok(noDirect.Stats.CacheReadTokens)}" },
                new List<object?> { "对比单会话基线", $"读缓存 Token {SystemBuilder.FmtTok(full.Stats.CacheReadTokens)}（双层） vs {SystemBuilder.FmtTok(SystemBuilder.SimulateSingleSession(stream, "neutral", contextTokens: 12000).CacheReadTokens)}（单会话中性）" },
            }));

        Takeaways.Print(trace, new[]
        {
            "用合成负载做\"能跑起来的最小实验\"：约 300 条消息里只有约 20 条工作请求，其余是闲聊、跟进、叫停与 CI。",
            "批处理压力测试看三个指标——batch 大小、单次请求耗时、每决策分摊时间；突发流量下批处理把一轮推理的成本摊到多条事件上。",
            "消融实验通过\"关掉机制看指标变差\"来验证机制有效：关闭批处理推高轮数与重读，去掉直通推高交 Router 事件与读缓存 Token。",
            "与\"单 Session + Subagent\"基线相比，双层架构最硬的收益是读缓存 Token 量级更低、决策通道永不被任务占住、分派不依赖模型自觉。",
            "证据要会解读：教学替身的数值只说明机制方向，不能当性能基准；真实结论需在真实模型与真实负载上复做。",
        });
    }
}
