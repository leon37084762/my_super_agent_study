// 演示 07 —— 面向人的交互：异步全双工 + 多信道表达
//
// 对应讲义第 7 讲（原文第 4.1、4.2 节）。
//
// 4.1 异步 ReAct Loop：
//   - 工具用 start() 而非阻塞式 run()：立即拿到 Started 回执，后台完成后再作为新事件回投
//   - Started 只说明“启动成功”，不代表完成，更不代表成功
//   - 新输入在“当前推理步结束后”处理，不能随时打断一次正在进行的推理
//   - 因此耗时工具运行期间仍能接收并处理用户输入 = 全双工体验
// 4.2 多信道表达：
//   - Worker 不直接回复用户，由 Router 统一按渠道组织表达
//   - IM 轻量低打扰：需要决策/验收/回答/告警才通知，抑制中间进展；单次回复有词数上限
//   - GitHub / 邮件 结构化高密度：交代背景、改动、验证、待确认
//
// 三部分：全双工对比 / start·Started·stop 语义 / 多信道表达与低打扰抑制

namespace SuperAgentDemo;

public static class Demo07
{
    public static Task RunAsync() => MainAsync();

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 07 · 面向人的交互：异步全双工 + 多信道表达");

        var duplex = await PartDuplex(trace);
        await PartRunnerSemantics(trace);
        var ch = await PartChannels(trace);

        trace.Section("全演示小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "演示结果" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "同步 run() 阻塞输入", $"{duplex.Sync.BlockedInputs} 条，阻塞 {duplex.Sync.BlockedSeconds.Fixed(1)}s" },
                new List<object?> { "异步 start() 全双工", $"{duplex.Asyn.BlockedInputs} 条被阻塞（测试期间即处理补充消息）" },
                new List<object?> { "Started 语义", "回执=启动成功，真实结果以完成事件为准（本例 failed）" },
                new List<object?> { "多信道表达", "同一事实渲染成 IM / GitHub / 邮件 三种密度" },
                new List<object?> { "低打扰抑制", $"中间进展被抑制 {ch.Suppressed} 条，验收回复统一由 Router 发出 {ch.RepliesBefore} 条" },
            }));

        Takeaways.Print(trace, new[]
        {
            "把阻塞式 run() 换成 start()，工具在后台跑、完成后作为新事件回投，耗时操作期间会话仍能吸收新输入，这就是全双工。",
            "Started 只表示“启动成功”，既不代表完成也不代表成功；真实结果必须等完成事件，否则会误报。",
            "全双工不等于随时打断：新输入在当前推理步结束后才被处理，一次正在进行的模型推理不可被抢占。",
            "Worker 不直接回复用户，由 Router 统一按渠道组织表达，避免把内部推理与工具过程外泄给人。",
            "渠道规范决定表达密度与是否打扰：IM 轻量低打扰、抑制中间进展且有词数上限；GitHub/邮件结构化高密度、交代背景与验证。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一部分：同步 run() vs 异步 start()（全双工）
    // -----------------------------------------------------------------------

    private sealed class DuplexResult
    {
        public string Mode = "";
        public int BlockedInputs;
        public double BlockedSeconds;
        public double UserWait;
        public string ProcessedDuringTool = "";
    }

    private static async Task<DuplexResult> RunDuplex(bool asyncTools)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions { Title = "", MsPerSimSecond = 40, GlobalStateTokens = 6000, AsyncTools = asyncTools });
        var bus = sys.Bus;
        var router = sys.Router;
        var clock = sys.Clock;
        router.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群：登录故障")),
        });
        await Demo.WaitUntil(clock, () => router.Workers.Count >= 1);
        var w = router.Workers.Values.First();

        // 等 Worker 进入那段耗时测试（状态 waiting-tool），此时用户又发来一条补充
        await Demo.WaitUntil(clock, () => w.Status == "waiting-tool", 40);
        var tSent = clock.Now();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "登录超时又出现一次，频率变高了，附新日志",
            Author = "user-1",
        });

        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;

        return new DuplexResult
        {
            Mode = asyncTools ? "异步 start()" : "同步 run()",
            BlockedInputs = w.BlockedInputs,
            BlockedSeconds = w.BlockedSeconds,
            // 同步模式下补充消息要等测试跑完才被处理；异步模式下几乎即时被吸收
            UserWait = asyncTools ? clock.Now() - tSent - w.BlockedSeconds : w.BlockedSeconds,
            ProcessedDuringTool = asyncTools ? "是（测试运行期间即纳入上下文）" : "否（阻塞到 run() 返回）",
        };
    }

    private static async Task<(DuplexResult Sync, DuplexResult Asyn)> PartDuplex(Trace trace)
    {
        trace.Section("第一部分 · 同步 run() vs 异步 start()：耗时工具运行期间能否响应用户");
        trace.Note("场景：Worker 正在跑一段 5 秒的测试，期间用户又补充了一条与登录相关的信息。");
        var sync = await RunDuplex(false);
        var asyn = await RunDuplex(true);
        trace.Block(Table.RenderTable(
            new[] { "工具调用方式", "期间被阻塞的输入数", "阻塞时长", "补充消息何时被处理" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { sync.Mode, sync.BlockedInputs, $"{sync.BlockedSeconds.Fixed(1)}s", sync.ProcessedDuringTool },
                new List<object?> { asyn.Mode, asyn.BlockedInputs, $"{asyn.BlockedSeconds.Fixed(1)}s", asyn.ProcessedDuringTool },
            },
            new[] { 'l', 'r', 'r', 'l' }));
        trace.Note("同步 run()：调用后整个会话被占住，测试返回前无法吸收任何新输入，用户补充只能排队。");
        trace.Note("异步 start()：立即拿到回执，推理循环在等待完成事件的同时持续排空收件箱 = 全双工。");
        trace.Note("注意：即便异步，新输入也是在“当前推理步结束后”处理，不会打断一次正在进行的模型推理。");
        return (sync, asyn);
    }

    // -----------------------------------------------------------------------
    // 第二部分：start() / Started 回执 / stop() 的语义
    // -----------------------------------------------------------------------

    private static async Task PartRunnerSemantics(Trace trace)
    {
        trace.Section("第二部分 · start() 立即回执、完成事件回投、stop() 可中止");
        var clock = new Clock(60);
        var runner = new AsyncToolRunner(clock, trace);

        ToolHandle? completion = null;
        var h = runner.Start("run_tests", "npm test -- flaky", 3, "failed", "2 项断言失败", onDone: x => completion = x);
        trace.Note($"start() 立即返回回执：id={h.Id}，status={h.Status}（这只是“启动成功”，不代表完成，更不代表通过）");
        await clock.Sleep(0.5);
        var mid = runner.Status(h.Id);
        trace.Note($"0.5s 后查询：status={mid?.Status}，已运行 {(mid?.Elapsed ?? 0).Fixed(1)}s —— 仍在后台跑，主循环没有被占住");
        await clock.Sleep(3);
        trace.Note($"完成事件回投：status={completion?.Status}，output={completion?.Output} —— 真实结果以完成事件为准，这里是 failed");

        var h2 = runner.Start("build", "npm run build", 5, "succeeded");
        await clock.Sleep(0.3);
        var stopRunning = runner.Stop(h2.Id);
        trace.Note($"对仍在运行的 {h2.Id} 调用 stop() -> ok={(stopRunning.Ok ? "true" : "false")}（{stopRunning.Reason}），status={h2.Status}");
        var stopFinished = runner.Stop(h.Id);
        trace.Note($"对已结束的 {h.Id} 再调用 stop() -> ok={(stopFinished.Ok ? "true" : "false")}（{stopFinished.Reason}）");
        trace.Note("第 7 讲自测题：把 Started 当成成功，就会在测试其实失败时误报“已完成”。");
    }

    // -----------------------------------------------------------------------
    // 第三部分：多信道表达 + 低打扰抑制
    // -----------------------------------------------------------------------

    private static readonly ChannelFact Fact = new(
        Topic: "登录超时修复",
        Change: "补充超时处理并复位界面加载状态",
        Verification: "auth 相关 12 项测试通过",
        Background: "线上登录偶发超时，用户反馈附了复现步骤",
        Pending: "超时提示文案待产品确认",
        Ref: "P01");

    private static async Task<(int RepliesBefore, int Suppressed)> PartChannels(Trace trace)
    {
        trace.Section("第三部分 · 同一个经过验证的事实，按渠道渲染成不同密度");
        foreach (var ch in new[] { "im", "github_pr", "email" })
        {
            var text = Channels.RenderForChannel(ch, Fact);
            var audit = Channels.AuditChannel(ch, text);
            var norm = ChannelNorms.Of(ch);
            trace.Note($"渠道：{norm.Label}（{norm.Density}，词数上限 {(double.IsPositiveInfinity(norm.MaxWords) ? "无" : norm.MaxWords.ToString("0", Fmt.Inv))}）");
            trace.Block(text);
            trace.Note($"字数 {Channels.CountWords(text)}，规范审计：{(audit.Ok ? "通过" : "不通过")} —— {audit.Reason}");
        }

        trace.Section("第三部分 · 低打扰：不同“通知目的”是否值得在该渠道发出");
        var purposeLabel = new Dictionary<string, string>
        {
            [Purpose.Progress] = "中间进展",
            [Purpose.Decision] = "需要决策",
            [Purpose.Acceptance] = "需要验收",
            [Purpose.Answer] = "回答问题",
            [Purpose.Alert] = "异常告警",
        };
        var purposes = new[] { Purpose.Progress, Purpose.Decision, Purpose.Acceptance, Purpose.Answer, Purpose.Alert };
        trace.Block(Table.RenderTable(
            new[] { "通知目的", "IM 即时通信", "GitHub PR", "邮件" },
            purposes.Select(p => (IReadOnlyList<object?>)new List<object?>
            {
                purposeLabel.TryGetValue(p, out var lbl) ? lbl : p,
                Channels.ShouldNotify("im", p) ? "通知" : "抑制",
                Channels.ShouldNotify("github_pr", p) ? "通知" : "抑制",
                Channels.ShouldNotify("email", p) ? "通知" : "抑制",
            }).ToList()));
        var longIm = string.Concat(Enumerable.Repeat("登录超时问题已经修复，相关测试全部通过，请确认是否采用本次改动。", 6));
        var longAudit = Channels.AuditChannel("im", longIm);
        trace.Note($"把一段超长说明发到 IM（{Channels.CountWords(longIm)} 词）：审计 ok={(longAudit.Ok ? "true" : "false")} —— {longAudit.Reason}");
        trace.Note("讲义提醒：原文“单次不超过 100 词”是其 IM 实践，不能机械换算成所有中文消息的统一字数上限，这里仅作教学示范。");

        // 真实运行一次，展示 Router 统一对外回复，并抑制一条中间进展
        var sys = SystemBuilder.BuildSystem(new SystemOptions { Title = "", MsPerSimSecond = 30, GlobalStateTokens = 6000, UserChannel = "im" });
        var bus = sys.Bus;
        var router = sys.Router;
        router.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群")),
        });
        await router.QuiesceAsync(timeoutSimSec: 60);
        var w = router.Workers.Values.First();
        var repliesBefore = router.Replies.Count;
        // 模拟 Worker 汇报一条“中间进展”：在 IM 低打扰渠道应被抑制
        router.Report(w.Id, new ReportArgs
        {
            Purpose = Purpose.Progress,
            Fact = new ChannelFact(Topic: "登录超时", Change: "正在跑测试", Verification: "-"),
        });
        trace.Section("第三部分 · Worker 不直接回复用户，中间进展在 IM 被抑制");
        trace.Note($"任务收尾时 Router 已统一发出验收回复 {repliesBefore} 条（Worker 的推理与工具过程不外泄）");
        trace.Note($"再模拟汇报一条“中间进展”：replies {repliesBefore} -> {router.Replies.Count}，suppressedProgress={router.SuppressedProgress}（IM 低打扰，未打断用户）");
        router.Stop();
        await router.Done!;
        return (repliesBefore, router.SuppressedProgress);
    }
}
