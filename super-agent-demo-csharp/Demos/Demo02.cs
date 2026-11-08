// 演示 02 —— 双层架构：连续性与吞吐量的权衡
//
// 对应讲义第 2 讲（原文第 2、2.1、2.2 节）。
//
// 三种会话组织方式跑同一个场景：
//   方案 A  所有事件共用一个 Session   -> 上下文连续，但串行、多任务过程混杂
//   方案 B  每个事件新建一个 Session   -> 有利于并发，但缺少全局上下文、交互割裂
//   方案 C  Router / Worker 双层架构   -> 全局连续性与并发执行分开组织（真实运行）
//
// 场景（取自讲义第 2 讲补充案例）：
//   t=0.0s  用户提出「修复登录超时」
//   t=1.5s  用户补充「先不要改接口」        <- 必须与正在执行的任务建立联系
//   t=4.0s  用户提出「优化导出功能」        <- 新任务不必等待旧任务完成
//
// 方法说明：先真实运行方案 C，量出“单个任务从分派到回复”的执行成本，
// 再用同一成本参数计算 A / B 的数值模型，避免三套方案使用不一致的假设。

namespace SuperAgentDemo;

public static class Demo02
{
    private const double Rework = 8;   // 叫停没有及时关联到任务时的返工成本（模拟秒，教学设定）
    private const double T1 = 0;       // 「修复登录超时」到达时刻
    private const double TStop = 1.5;  // 「先不要改接口」到达时刻
    private const double T2 = 4;       // 「优化导出功能」到达时刻

    private sealed class SchemeRow
    {
        public string Scheme = "";
        public double Task1Done;
        public double Task2Done;
        public string Concurrency = "";
        public string StopLinked = "";
        public string Context = "";
        public string Replies = "";
    }

    public static Task RunAsync() => MainAsync();

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 02 · 双层架构：连续性与吞吐量的权衡");

        var (c, exec1, exec2) = await RunDualLayer();
        var a = SimulateSharedSession(trace, exec1, exec2);
        var b = SimulateSessionPerEvent(trace, exec1, exec2);

        trace.Section("三种方案对比（时间为从 t=0 起的模拟秒）");
        var rows = new List<SchemeRow> { a, b, c }.Select(r => (IReadOnlyList<object?>)new List<object?>
        {
            r.Scheme,
            $"{r.Task1Done.Fixed(1)}s",
            $"{r.Task2Done.Fixed(1)}s",
            $"{((r.Task1Done + r.Task2Done) / 2).Fixed(1)}s",
            r.Concurrency,
            r.StopLinked,
            r.Replies,
        }).ToList();
        trace.Block(Table.RenderTable(new[] { "方案", "任务1完成", "任务2完成", "平均", "并发", "叫停关联", "对外回复" }, rows));
        trace.Note($"方案 C 为真实运行；A / B 使用 C 实测的单任务执行成本（{exec1.Fixed(1)}s / {exec2.Fixed(1)}s）与 {Rework}s 返工假设。");
        trace.Note("方案 B 的任务2 看起来最快，是因为它跳过了全局协调；代价体现在任务1 的返工与三条各自发出的回复上。");

        Takeaways.Print(trace, new[]
        {
            "共用一个 Session 保住连续性，代价是串行执行与过程混杂；每事件新建 Session 换来并发，代价是全局上下文与交互的割裂。",
            "双层架构把两类职责分开：Router 常驻负责全局关联、交互与分派；Worker 在隔离上下文中并发执行具体任务。",
            "「先不要改接口」之所以生效，不是因为它进了同一个会话，而是因为 Router 维护任务关联，把这条不含任务关键词的全局约束广播给了在跑 Worker。",
            "Router 不直接执行任务，所以在 Worker 跑测试的十几秒里仍能接收并分派新任务；这就是它保持轻量的原因。",
            "两层是逻辑职责而非 Session 数量：Worker 内部还能开 Subagent 或 Agent Team，对 Router 透明。",
        });
    }

    /// <summary>方案 C：Router / Worker 双层架构，真实运行。</summary>
    private static async Task<(SchemeRow Row, double Exec1, double Exec2)> RunDualLayer()
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions { Title = "方案 C：Router / Worker 双层架构（真实运行）", MsPerSimSecond = 25, GlobalStateTokens = 9000 });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        var trace = sys.Trace;

        trace.Note("Router 只做交互与分派；Worker 在隔离上下文中并发执行，不直接回复用户");
        router.Start(); // 常驻循环，不阻塞

        async Task<PublishResult> PublishAt(double at, EventInput input)
        {
            var wait = at - clock.Now();
            if (wait > 0) await clock.Sleep(wait);
            return await bus.PublishAsync(input);
        }

        await PublishAt(T1, Demo.Ev(Source.ImWork, "H01", "线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复", "user-1",
            payload: Demo.Payload(("threadTitle", "工作群：登录故障"))));
        // 关键：叫停发生在主对话 H00（未被任何 Worker 订阅），必须由 Router 关联到在跑任务
        await PublishAt(TStop, Demo.Ev(Source.ImWork, "H00", "先不要改接口，等评审结论", "user-1",
            payload: Demo.Payload(("threadTitle", "与用户的主对话"))));
        await PublishAt(T2, Demo.Ev(Source.ImWork, "H02", "导出大文件时失败，日志显示内存溢出，需要优化导出功能", "user-2",
            payload: Demo.Payload(("threadTitle", "工作群：导出问题"))));

        var settled = await router.QuiesceAsync(timeoutSimSec: 90);
        router.Stop();
        await router.Done!;

        var w1 = router.Workers.Values.FirstOrDefault(w => w.Task.StartsWith("线上登录"));
        var w2 = router.Workers.Values.FirstOrDefault(w => w.Task.StartsWith("导出大文件"));
        var done1 = DoneAt(router, w1);
        var done2 = DoneAt(router, w2);

        trace.Section("方案 C 结果");
        trace.Note($"Router 轮数 {router.Rounds.Count}，决策 {router.Rounds.Sum(r => r.BatchSize)} 条，Worker {router.Workers.Count} 个");
        trace.Note($"{w1?.Id} 于 t={(w1?.DispatchedAt ?? 0).Fixed(1)}s 分派，t={done1.Fixed(1)}s 回复；中途接收全局约束 {w1?.Constraints.Count ?? 0} 条，接口改动步骤被移除 -> 无返工");
        trace.Note($"{w2?.Id} 于 t={(w2?.DispatchedAt ?? 0).Fixed(1)}s 分派，t={done2.Fixed(1)}s 回复；与 {w1?.Id} 并发推进，不必等前者结束");
        trace.Note($"对外回复统一由 Router 发出 {router.Replies.Count} 条（Worker 的推理与工具过程不外泄）");
        if (!settled) trace.Note("注意：quiesce 超时，仍有 Worker 未结束");

        var row = new SchemeRow
        {
            Scheme = "C 双层架构",
            Task1Done = done1,
            Task2Done = done2,
            Concurrency = $"是（{router.Workers.Count} 个 Worker）",
            StopLinked = (w1?.Constraints.Count ?? 0) > 0 ? "是（Router 广播到在跑任务）" : "否",
            Context = "按任务隔离",
            Replies = $"统一 {router.Replies.Count} 条",
        };
        return (row, done1 - (w1?.DispatchedAt ?? 0), done2 - (w2?.DispatchedAt ?? 0));
    }

    private static double DoneAt(Router router, Worker? w) =>
        w != null ? router.Replies.FirstOrDefault(r => r.WorkerId == w.Id)?.At ?? 0 : 0;

    /// <summary>方案 A：所有事件进入同一个 Session，串行执行。执行成本用方案 C 的实测值。</summary>
    private static SchemeRow SimulateSharedSession(Trace trace, double exec1, double exec2)
    {
        trace.Section("方案 A：所有事件共用一个 Session（数值模型，执行成本取自方案 C 实测）");
        var stopSeen = T1 + exec1; // 串行会话要等当前任务跑完才能看新事件
        var work1Done = stopSeen + Rework;
        var work2Done = work1Done + exec2;
        trace.Note($"t={T1}s 任务1 开始，独占唯一的 ReAct Loop（实测执行成本 {exec1.Fixed(1)}s）");
        trace.Note($"t={TStop}s「先不要改接口」到达，但会话正忙 -> 排队到 t={stopSeen.Fixed(1)}s 才被看到，接口已改完 -> 返工 {Rework}s");
        trace.Note($"t={work1Done.Fixed(1)}s 任务1 完成；t={T2}s 到达的任务2 一直排队，t={work2Done.Fixed(1)}s 才完成");
        trace.Note("两个任务的日志、测试结果不断进入同一上下文，互相稀释注意力");
        return new SchemeRow { Scheme = "A 共用一个 Session", Task1Done = work1Done, Task2Done = work2Done, Concurrency = "否（串行）", StopLinked = "否（迟到，返工）", Context = "混杂", Replies = "统一 1 个会话" };
    }

    /// <summary>方案 B：每个事件都新建独立 Session。</summary>
    private static SchemeRow SimulateSessionPerEvent(Trace trace, double exec1, double exec2)
    {
        trace.Section("方案 B：每个事件新建一个 Session（数值模型，执行成本取自方案 C 实测）");
        var work1Done = T1 + exec1 + Rework;
        var work2Done = T2 + exec2;
        trace.Note($"t={T1}s S1 启动执行任务1");
        trace.Note($"t={TStop}s S2 启动处理「先不要改接口」—— S2 是全新会话，不知道 S1 在做什么，只能回一句「已记录」");
        trace.Note($"t={(T1 + exec1).Fixed(1)}s S1 按旧要求改完接口，事后发现冲突 -> 返工 {Rework}s，t={work1Done.Fixed(1)}s 收尾");
        trace.Note($"t={T2}s S3 启动执行任务2，与 S1 并发，t={work2Done.Fixed(1)}s 完成（没有分派开销，单任务反而更快）");
        trace.Note("S1 / S2 / S3 各自向用户回复，用户面对三个互不知情的会话");
        return new SchemeRow { Scheme = "B 每事件新建 Session", Task1Done = work1Done, Task2Done = work2Done, Concurrency = "是", StopLinked = "否（无全局上下文）", Context = "隔离但割裂", Replies = "各自回复 3 条" };
    }
}
