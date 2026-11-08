// 演示 04 —— 事件路由与订阅直通
//
// 对应讲义第 4 讲（原文第 3.2 节）。
//
// 第 3 讲回答“值不值得处理”，本讲回答“交给谁、怎么送达”。核心是两条投递路径：
//   未订阅的 Thread -> 交 Router 判断归属（Slow Path，要调用模型）
//   已订阅的 Thread -> 程序直通到 Worker（Fast Path，不调用模型）
//
// 演示四件事：
//   1. 同一条 Thread，首个事件走 Router 分派，后续事件沿订阅直通，Router 不再花模型调用
//   2. 直通不等于 Router 失明：Router 仍 observe 事件、维护全局状态
//   3. CI 失败沿订阅直通到 Worker，Worker 自行追加“修改-重测”，无须 Router 逐条转发
//   4. 超出任务范围的事件，Worker 取消订阅并交回 Router 重新分派
//   5. 订阅是多对多：一个 Worker 可同时订阅工作 Thread 与 PR Thread

namespace SuperAgentDemo;

public static class Demo04
{
    public static Task RunAsync() => MainAsync();

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 04 · 事件路由与订阅直通");

        var p1 = await PartDeliveryPaths(trace);
        var p2 = await PartCiDirect(trace);
        var p3 = await PartOutOfScope(trace);

        trace.Section("三部分小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "观察到的现象", "数值" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "两级投递路径", "未订阅交 Router，已订阅直通", $"交Router {p1.Funnel.ToRouter} / 直通 {p1.Funnel.DirectDelivered}" },
                new List<object?> { "直通省下的模型调用", "Router 轮数不因直通事件增加", $"{p1.RoundsAfterDispatch} -> {p1.RoundsAfterDirect}" },
                new List<object?> { "CI 自我修正", "直通触发追加修复步骤", $"计划 {p2.PlanBefore} -> {p2.PlanAfter} 步" },
                new List<object?> { "范围检查", "超范围事件交回 Router 重新分派", $"派生第二个 Worker：{(p3.SpawnedW2 ? "是" : "否")}" },
            }));

        Takeaways.Print(trace, new[]
        {
            "路由要分两条路径：已订阅 Thread 走程序直通（Fast Path，不调用模型），未订阅 Thread 才交 Router 判断归属（Slow Path）。",
            "直通省下的是“每条后续消息都要重新判断归属”的模型调用；Thread 越长、消息越多，节省越明显。",
            "直通不代表 Router 失去全局信息：Router 仍然 observe 直通事件、更新摘要级全局状态，需要时才能做跨任务关联。",
            "CI 失败、评审意见这类后续事件沿订阅直达 Worker，Worker 自行决定如何回应（追加修复-重测），无须 Router 逐条转发。",
            "订阅是多对多且有边界：Worker 发现事件超出任务范围时取消订阅并交回 Router，避免把无关工作硬塞进当前上下文。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一部分：两级投递路径对比
    // -----------------------------------------------------------------------

    private static async Task<(Funnel Funnel, int RoundsAfterDispatch, int RoundsAfterDirect, Worker W1)> PartDeliveryPaths(Trace trace)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "第一部分：未订阅交 Router（Slow Path） vs 已订阅直通（Fast Path）",
            MsPerSimSecond = 40,
            GlobalStateTokens = 6000,
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        router.Start();

        // 1) H01 尚无任何订阅者：事件只能交 Router 判断归属
        var r1 = await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群：登录故障")),
        });
        trace.Note($"首个事件 -> stage={r1.Stage}（{r1.Reason}）");

        await Demo.WaitUntil(clock, () => router.Workers.Count >= 1);
        var w1 = router.Workers.Values.First();
        var roundsAfterDispatch = router.Rounds.Count;
        trace.Note($"Router 花 {roundsAfterDispatch} 轮模型调用判定归属 -> 分派 {w1.Id} 并订阅 H01");

        // 2) H01 现在有了订阅者 W01：同 Thread 的后续事件走 Fast Path 直通
        var r2 = await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "登录超时又出现一次，频率变高了，附上新日志",
            Author = "user-1",
        });
        trace.Note($"后续事件 -> stage={r2.Stage}（{r2.Reason}）");
        await clock.Sleep(0.6);
        var roundsAfterDirect = router.Rounds.Count;

        trace.Section("第一部分结果：投递路径与模型调用");
        trace.Block(Table.RenderTable(
            new[] { "事件", "目标 Thread", "投递路径", "是否触发 Router 模型调用" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "首条工作请求", "H01（未订阅）", r1.Stage, $"是（rounds {roundsAfterDispatch}）" },
                new List<object?> { "同 Thread 补充", "H01（已订阅 W01）", r2.Stage, $"否（rounds 仍 {roundsAfterDirect}）" },
            }));
        var funnel = bus.Funnel();
        trace.Note($"总线漏斗：{funnel.ToJson()}");
        trace.Note($"直通时 Router 仍观察事件、维护全局状态：observedWhileDirect={router.ObservedWhileDirect}（第 4 讲自测题：直通≠Router 失明）");

        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;

        // 多对多：W01 收尾前创建了 PR，于是同时订阅工作 Thread 与 PR Thread
        trace.Section("第一部分补充：订阅是多对多");
        trace.Note($"{w1.Id} 当前订阅的 Thread：{(w1.Subscriptions.Count > 0 ? string.Join("、", w1.Subscriptions) : "无")}");
        trace.Note("一个 Worker 可订阅多个 Thread（工作群 + PR）；反过来一个 Thread 也可被多个 Worker 订阅。");
        return (funnel, roundsAfterDispatch, roundsAfterDirect, w1);
    }

    // -----------------------------------------------------------------------
    // 第二部分：CI 失败沿订阅直通，Worker 自我修正
    // -----------------------------------------------------------------------

    private static async Task<(int PlanBefore, int PlanAfter, int FixSteps)> PartCiDirect(Trace trace)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "第二部分：CI 失败事件直通到 Worker，自行追加“修改-重测”",
            MsPerSimSecond = 40,
            GlobalStateTokens = 6000,
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        router.Start();

        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群：登录故障")),
        });
        await Demo.WaitUntil(clock, () => router.Workers.Count >= 1);
        var w1 = router.Workers.Values.First();
        var planBefore = w1.Plan.Count;
        trace.Note($"{w1.Id} 已启动，初始计划 {planBefore} 步；此刻它刚进入执行，尚未跑完");

        // CI 失败发布在 W01 订阅的 H01 上 -> 直通，不经过 Router 决策
        var ci = await bus.PublishAsync(new EventInput
        {
            Source = Source.GhCi,
            ThreadId = "H01",
            Text = "CI 运行结果：构建通过，登录相关测试失败 2 项",
            Author = "ci-bot",
            Payload = Demo.Payload(("status", "failure"), ("detail", "auth 超时断言失败")),
        });
        trace.Note($"CI 失败事件 -> stage={ci.Stage}（沿订阅直通，Router 未调用模型）");

        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;

        var planAfter = w1.Plan.Count;
        var fixSteps = w1.Plan.Where(s => s.Tag == "fix").ToList();
        trace.Section("第二部分结果：直通触发的自我修正闭环");
        trace.Note($"计划从 {planBefore} 步增长到 {planAfter} 步，其中由 CI 直通追加的修复步骤 {fixSteps.Count} 个");
        foreach (var s in fixSteps) trace.Note($"  追加：{s.Tool}({s.Target}) // {s.Note}");
        trace.Note("关键点：CI 事件不需要 Router 逐条转发，Worker 沿订阅直接收到并决定如何回应。");
        return (planBefore, planAfter, fixSteps.Count);
    }

    // -----------------------------------------------------------------------
    // 第三部分：超出任务范围 -> 取消订阅并交回 Router
    // -----------------------------------------------------------------------

    private static async Task<(List<string> W1Subs, bool SpawnedW2)> PartOutOfScope(Trace trace)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "第三部分：超出任务范围的事件，Worker 取消订阅并交回 Router 重新分派",
            MsPerSimSecond = 40,
            GlobalStateTokens = 6000,
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        router.Start();

        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群：登录故障")),
        });
        await Demo.WaitUntil(clock, () => router.Workers.Count >= 1);
        var w1 = router.Workers.Values.First();
        trace.Note($"{w1.Id}（登录任务）已订阅 H01");

        // 一条与登录无关的“导出”请求发到同一个 Thread：会先直通到 W01
        var r = await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "导出大文件时失败，日志显示内存溢出，需要优化导出功能",
            Author = "user-2",
        });
        trace.Note($"导出请求 -> stage={r.Stage}（先沿 H01 订阅直通到 {w1.Id}）");

        await Demo.WaitUntil(clock, () => router.Workers.Count >= 2, 40);
        var w2 = router.Workers.Values.FirstOrDefault(w => w.Id != w1.Id);

        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;

        trace.Section("第三部分结果：范围检查与重新分派");
        trace.Note($"{w1.Id} 判定“导出”超出本任务范围 -> 取消订阅 H01，把事件交回 Router");
        trace.Note($"{w1.Id} 现存订阅：{(w1.Subscriptions.Count > 0 ? string.Join("、", w1.Subscriptions) : "（已退出 H01）")}");
        if (w2 != null) trace.Note($"Router 重新分派 {w2.Id}「{Demo.Trunc(w2.Task, 16)}」处理导出请求");
        else trace.Note("注意：未观察到第二个 Worker，导出请求可能仍在队列中");
        trace.Note("这就是“直通”与“绑死”的区别：Worker 收到不属于自己的事件时，能把它退回全局协调层。");
        return (new List<string>(w1.Subscriptions), w2 != null);
    }
}
