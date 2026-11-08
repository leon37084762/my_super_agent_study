// 演示 09 —— Watchdog 与异常恢复
//
// 对应讲义第 9 讲（原文第 6 节）。
//
// 核心思想：
//   - 主系统之外再放一个独立的 Watchdog Session，由“定时消息”驱动，构成外部 Control Loop；
//   - 它区分“活动量”（有没有在调用工具）与“实质进展”（有没有新证据、有没有越界、基础设施是否可用）；
//   - 判定分五档：healthy / watch / stalled / drift / infra；
//   - Watchdog 本身也是有完整工具能力的 Agent，能诊断并“在线介入”（注入约束/提示、延后重试），不只是告警；
//   - 维护者可以用自然语言向它询问故障原因；异常解除后持续复查直到恢复正常。
//
// 四部分：
//   1. 判定逻辑表（确定性）：同样在“调用工具”，进展却可能完全不同
//   2. 目标偏离 drift：为“小修复”不断扩大到无关模块 -> 注入约束
//   3. 停滞 stalled：连续相同调用且无新证据 -> 注入提示；换新证据后复查恢复正常
//   4. 基础设施异常 infra：设备离线导致挂起 -> 延后重试 + 维护者自然语言诊断

namespace SuperAgentDemo;

public static class Demo09
{
    public static Task RunAsync() => MainAsync();

    /// <summary>把 Watchdog 每轮检查渲染成时间线，体现“定时消息驱动的外部 Control Loop”。</summary>
    private static string Timeline(Watchdog wd)
    {
        var lines = wd.Checks.Select(c =>
        {
            var results = string.Join("  ", c.Results.Select(r => $"{r.Id}={r.Verdict}"));
            if (results.Length == 0) results = "（暂无 Worker）";
            return $"第 {c.Round.ToString().PadLeft(2, ' ')} 轮 @{c.At.Fixed(1).PadLeft(5, ' ')}s | infra {c.InfraErrors} | {results}";
        }).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : "（尚未产生检查记录）";
    }

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 09 · Watchdog 与异常恢复");

        var logic = PartVerdictLogic(trace);
        var drift = await PartDrift(trace);
        var stalled = await PartStalled(trace);
        var infra = await PartInfra(trace);

        trace.Section("全演示小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "演示结果" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "五档判定逻辑", $"确定性覆盖 {string.Join(" / ", logic.Select(r => r[3]))}" },
                new List<object?> { "目标偏离 drift", $"越界 {drift.Violations} 次被捕获={(drift.Detected ? "是" : "否")}，注入约束 {drift.Interventions} 次（复查 {drift.Rounds} 轮）" },
                new List<object?> { "停滞 stalled", $"捕获并恢复={(stalled.Detected ? "是" : "否")}，注入提示 {stalled.Interventions} 次，恢复后 openIssues={stalled.OpenIssues}" },
                new List<object?> { "基础设施 infra", $"离线挂起判定={(infra.InfraSeen ? "infra" : "-")}，retry-later {infra.Interventions} 次，上线续跑->{infra.Resumed}/{infra.PrId ?? "-"}" },
                new List<object?> { "自然语言诊断", Demo.Trunc(infra.Answer, 30) + "…" },
            }));

        Takeaways.Print(trace, new[]
        {
            "Watchdog 是主系统之外的独立 Session，由定时消息驱动，构成一个外部 Control Loop，不依赖主循环“自觉”。",
            "它区分“活动量”与“实质进展”：一直在调用工具不等于在推进；关键看有没有新证据、有没有越界、基础设施是否可用。",
            "判定分五档 healthy/watch/stalled/drift/infra，且“等待耗时工具(watch)”与“反复相同调用无新证据(stalled)”必须分清，避免误杀长跑任务。",
            "Watchdog 本身是有完整工具能力的 Agent，可在线介入（注入约束/提示、延后重试）推动恢复，而不只是发告警。",
            "异常解除后要持续复查直到恢复正常才解除关注；维护者还能用自然语言向它询问故障原因，得到基于快照的诊断。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一部分：判定逻辑（纯函数，确定性）
    // -----------------------------------------------------------------------

    private static List<IReadOnlyList<object?>> PartVerdictLogic(Trace trace)
    {
        trace.Section("第一部分 · 判定逻辑：区分“活动量”与“实质进展”");
        trace.Note("同样都在“调用工具”，进展却可能完全不同——Watchdog 不看热闹，看证据。");
        var cases = new (string Label, int ToolCalls, int RepeatedSameCall, int NewEvidenceCount, int ScopeViolations, int InfraErrors, string Stage)[]
        {
            ("正常推进（不断有新证据）", 3, 0, 2, 0, 0, "iterating"),
            ("等待耗时工具（自测题 3）", 1, 0, 0, 0, 0, "waiting-tool"),
            ("原地打转（重复调用无新证据）", 6, 4, 0, 0, 0, "iterating"),
            ("目标偏离（越出任务范围）", 5, 0, 1, 2, 0, "iterating"),
            ("基础设施异常（设备/依赖报错）", 2, 0, 0, 0, 3, "iterating"),
            ("调用在变化（虽少但在推进）", 4, 1, 1, 0, 0, "iterating"),
        };
        var rows = cases.Select(c =>
        {
            var v = Progress.JudgeProgress(new Evidence(c.RepeatedSameCall, c.NewEvidenceCount, c.ScopeViolations, c.InfraErrors, c.Stage));
            return (IReadOnlyList<object?>)new List<object?>
            {
                c.Label,
                $"{c.ToolCalls} / {c.RepeatedSameCall}",
                $"新证据{c.NewEvidenceCount}·越界{c.ScopeViolations}·infra{c.InfraErrors}·{c.Stage}",
                v.Verdict,
                v.Reason,
            };
        }).ToList();
        trace.Block(Table.RenderTable(
            new[] { "场景", "活动量(调用/重复)", "实质进展信号", "判定", "理由" },
            rows,
            new[] { 'l', 'r', 'l', 'l', 'l' }));
        trace.Note("关键区分：watch（等待耗时工具）不等于 stalled（反复相同调用且无新证据）——长跑测试不该被误判为卡死。");
        trace.Note("优先级：infra > drift > watch > stalled > healthy；越界即使“有新证据”也算偏离。");
        return rows;
    }

    // -----------------------------------------------------------------------
    // 通用：启动一个被 Watchdog 盯住的系统
    // -----------------------------------------------------------------------

    private sealed class WatchedCtx
    {
        public BuiltSystem Sys = null!;
        public Watchdog Wd = null!;
        public Router Router = null!;
        public Clock Clock = null!;
    }

    private sealed class WatchedResult
    {
        public WatchedCtx Ctx = null!;
        public bool Ok;
        public Worker? Worker;
    }

    private static async Task<WatchedResult> RunWatched(
        string taskText,
        Func<WatchedCtx, bool> until,
        Func<string, List<Step>>? planResolver = null,
        bool asyncTools = true,
        double intervalSimSec = 1.5,
        int maxRounds = 24,
        double msPerSimSecond = 40)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "",
            MsPerSimSecond = msPerSimSecond,
            GlobalStateTokens = 6000,
            AsyncTools = asyncTools,
            PlanResolver = planResolver,
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        var stats = sys.Stats;
        // Watchdog 监视的是“状态存储”（SessionStore），用它的 failures 计数判断状态恢复失败
        var sessionStore = new SessionStore(clock, sys.Trace);
        var wd = new Watchdog(clock, sys.Trace, new MockLlm(clock, "WATCHDOG", "watchdog", stats), router, bus, sessionStore, sys.Daemons, intervalSimSec, maxRounds);
        router.Start();
        wd.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = taskText,
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群")),
        });
        var ctx = new WatchedCtx { Sys = sys, Wd = wd, Router = router, Clock = clock };
        var ok = await Demo.WaitUntil(clock, () => until(ctx), 60);
        await router.QuiesceAsync(timeoutSimSec: 30);
        wd.Stop();
        router.Stop();
        await wd.Done!;
        await router.Done!;
        return new WatchedResult { Ctx = ctx, Ok = ok, Worker = router.Workers.Values.FirstOrDefault() };
    }

    // -----------------------------------------------------------------------
    // 第二部分：目标偏离 drift
    // -----------------------------------------------------------------------

    private sealed class DriftResult { public bool Detected; public int Violations; public int Interventions; public int Rounds; }

    private static async Task<DriftResult> PartDrift(Trace trace)
    {
        trace.Section("第二部分 · 目标偏离(drift)：一个“小修复”不断扩散到无关模块");
        trace.Note("任务范围 src/config，但计划里顺手重构了 theme / api-client / api-router（越界）。");
        var res = await RunWatched(
            "配置读取的默认值合并有 bug，帮忙排查修复一下",
            until: c => c.Wd.Interventions.Any(i => i.Verdict == "drift") && c.Router.Workers.Values.All(w => w.Status == "closed"),
            intervalSimSec: 2);
        var wd = res.Ctx.Wd;
        var worker = res.Worker;
        trace.Note($"Watchdog 是否捕获 drift：{(res.Ok ? "是" : "否（超时）")}；越界次数 scopeViolations={worker?.ScopeViolations}");
        trace.Block(Timeline(wd));
        var s = wd.Summary();
        trace.Note($"介入 {s.Interventions} 次（drift -> inject-constraint：注入“立即回到任务范围”的约束事件到 Worker 收件箱）。");
        trace.Note("第 9 讲：Watchdog 是有完整工具能力的独立 Session，能在线修正主系统行为，而不只是发一条告警。");
        return new DriftResult { Detected = res.Ok, Violations = worker?.ScopeViolations ?? 0, Interventions = s.Interventions, Rounds = s.Rounds };
    }

    // -----------------------------------------------------------------------
    // 第三部分：停滞 stalled 与恢复
    // -----------------------------------------------------------------------

    private sealed class StalledResult { public bool Detected; public int Interventions; public int OpenIssues; public Dictionary<string, int> ByVerdict = new(); public int ToolCalls; }

    private static async Task<StalledResult> PartStalled(Trace trace)
    {
        trace.Section("第三部分 · 停滞(stalled)：连续相同调用且无新证据 -> 注入提示 -> 恢复");
        trace.Note("计划：反复跑同一条失败测试（无新证据），最后更换假设做一次带新证据的修改。");
        // 自定义计划：6 次完全相同、无 evidence 的失败测试，随后一次带新证据的修改
        Func<string, List<Step>> stalledPlan = _ =>
        {
            var steps = new List<Step>();
            for (var i = 0; i < 6; i += 1)
                steps.Add(new Step { Tool = "run_tests", Cmd = "npm test -- scheduler", Sim = 0.15, Outcome = "failed", Output = "同一处断言失败" });
            steps.Add(new Step { Tool = "edit", Target = "src/core/scheduler.js", Sim = 0.3, Note = "更换假设：修正竞态", Evidence = "ev:st-fix" });
            return steps;
        };
        var res = await RunWatched(
            "偶发的调度测试反复失败，帮忙排查定位",
            until: c => c.Wd.Interventions.Any(i => i.Verdict == "stalled") && c.Wd.OpenIssues.Count == 0,
            planResolver: stalledPlan,
            asyncTools: false, // 同步 run()：两次测试之间状态回到 running，才可能被判为 stalled 而非 watch
            intervalSimSec: 1);
        var wd = res.Ctx.Wd;
        trace.Note($"Watchdog 是否捕获 stalled 并在恢复后解除关注：{(res.Ok ? "是" : "否（超时）")}");
        trace.Block(Timeline(wd));
        var s = wd.Summary();
        trace.Note($"介入 {s.Interventions} 次（stalled -> inject-hint：提示“更换假设或上报阻塞”）；最终未恢复问题数 openIssues={s.OpenIssues}。");
        trace.Note("恢复判定：Worker 换假设后产生新证据 -> 下一轮复查变 healthy -> Watchdog 解除关注（持续复查直到恢复正常）。");
        return new StalledResult { Detected = res.Ok, Interventions = s.Interventions, OpenIssues = s.OpenIssues, ByVerdict = s.ByVerdict, ToolCalls = res.Worker?.ToolLog.Count ?? 0 };
    }

    // -----------------------------------------------------------------------
    // 第四部分：基础设施异常 infra + 维护者自然语言诊断
    // -----------------------------------------------------------------------

    private sealed class InfraResult { public bool InfraSeen; public int Interventions; public string Answer = ""; public string Resumed = ""; public string? PrId; }

    private static async Task<InfraResult> PartInfra(Trace trace)
    {
        trace.Section("第四部分 · 基础设施异常(infra)：设备离线 -> 延后重试 + 自然语言诊断");
        var sys = SystemBuilder.BuildSystem(new SystemOptions { Title = "", MsPerSimSecond = 40, GlobalStateTokens = 6000, Daemons = new List<Daemon>() });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        var stats = sys.Stats;
        var daemon = new Daemon("laptop-01", clock, sys.Trace, online: false, bus: bus, threadId: "H-DEV");
        sys.Daemons.Add(daemon);
        var sessionStore = new SessionStore(clock, sys.Trace);
        var wd = new Watchdog(clock, sys.Trace, new MockLlm(clock, "WATCHDOG", "watchdog", stats), router, bus, sessionStore, sys.Daemons, 1.5, 20);
        router.Start();
        wd.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "帮我处理笔记本上未提交的改动，本地构建报错，先排查再提 PR",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群")),
        });

        await Demo.WaitUntil(clock, () => router.Workers.Count >= 1 && router.Workers.Values.First().Status == "blocked-env", 40);
        var worker = router.Workers.Values.FirstOrDefault();
        var infraSeen = await Demo.WaitUntil(clock, () => wd.Checks.Any(c => c.Results.Any(r => r.Verdict == "infra")), 30);
        trace.Note($"Worker 因 laptop-01 离线挂起（infraErrors={worker?.InfraErrors ?? 0}，状态 {worker?.Status ?? "-"}）；Watchdog 判定 infra={(infraSeen ? "是" : "否")}");
        trace.Block(Timeline(wd));
        trace.Note($"介入动作：infra -> retry-later（先挂起等待环境恢复，不无谓重试）。介入共 {wd.Summary().Interventions} 次。");

        // 维护者用自然语言询问故障原因（第 9 讲第六节）
        var ans = await wd.AskAsync("为什么这个任务一直卡住不动？");
        trace.Note($"维护者提问「{ans.Question}」-> Watchdog 诊断：{ans.Answer}");
        var interventions = wd.Summary().Interventions;

        // Watchdog 已完成“检测 + 诊断”职责，先停止它：worker.infraErrors 是累计计数，
        // 若继续跑会在恢复过程中反复复述同一个历史 infra 信号，反而模糊“已恢复”的事实。
        wd.Stop();
        await wd.Done!;
        trace.Note("Watchdog 完成本轮职责后停止；下面演示环境恢复（设备上线）+ 云端从挂起处续跑。");

        // 环境恢复：设备上线 + 从挂起处续跑（Watchdog 的检测对应一个真实可恢复的场景）
        daemon.GoOnline();
        worker?.Resume("设备重新上线，云端触发续跑");
        if (worker != null) await worker.Done!;
        trace.Note($"设备上线后续跑：Worker 最终状态 {worker?.Status ?? "-"}，产出 PR={worker?.PrId ?? "-"}（infra 属可恢复异常，环境就绪即续跑）。");

        router.Stop();
        await router.Done!;
        return new InfraResult { InfraSeen = infraSeen, Interventions = interventions, Answer = ans.Answer, Resumed = worker?.Status ?? "-", PrId = worker?.PrId };
    }
}
