// 演示 06 —— 提前行动与推测执行
//
// 对应讲义第 6 讲（原文第 3.4 节）。
//
// 推测执行 = 在用户还没要求时，先在后台悄悄尝试；有价值才交付，无价值就安静丢弃。
// 讲义强调必须把三个问题分开，不能混为一谈：
//   闸门一 ShouldAttempt  ：这条线索值不值得先在后台试一把？（看副作用范围与预算）
//   闸门二 ShouldContinue ：要不要继续投入？（看有没有新证据）
//   闸门三 ShouldDeliver  ：要不要交付、打断用户？（只有经过验证的结果才呈现）
//
// 本演示分两部分：
//   第一部分：分别驱动三道闸门，说明它们回答的是三个不同的问题
//   第二部分：真实运行两个推测任务，对比“验证通过才交付” vs “未复现则安静丢弃”，
//             再与“关闭推测模式”对照，看它如何避免把猜测写成结论、制造注意力负担

namespace SuperAgentDemo;

public static class Demo06
{
    public static Task RunAsync() => MainAsync();

    private static string Yn(GateResult g) => g.Go ? "是" : "否";

    /// <summary>把可空布尔渲染成 JS 风格的小写 true/false（对齐 Node 输出）。</summary>
    private static string JsBool(bool? b) => b.HasValue ? (b.Value ? "true" : "false") : "undefined";

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 06 · 提前行动与推测执行");

        PartGates(trace);
        await PartRuns(trace);

        Takeaways.Print(trace, new[]
        {
            "推测执行把“提前尝试”和“打断用户”解耦：可以先在后台悄悄做，但不等于做出来就要告诉用户。",
            "三道闸门回答三个不同问题——是否后台尝试（副作用/预算）、是否继续投入（有无新证据）、是否交付（是否验证通过）。",
            "只有经过验证的结果才主动交付；未复现或未验证就安静丢弃，避免用注意力负担抵消省下的等待时间。",
            "不能把“后台进程退出”写成“所有功能正确”：关闭推测模式的对照里，未复现的卡顿任务照样汇报了“已处理”。",
            "例外是用户主动询问：即使结果不确定，也应如实说明当前状态，而不是继续沉默。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一部分：三道闸门分别回答三个问题
    // -----------------------------------------------------------------------

    private static void PartGates(Trace trace)
    {
        trace.Section("第一部分 · 闸门一 ShouldAttempt：值不值得先在后台试一把？");
        var attemptCases = new (string Name, double Clue, string SideEffect, double Budget)[]
        {
            ("有线索 + 只读 + 有预算", 0.6, "read-only", 1),
            ("没有任何线索", 0, "read-only", 1),
            ("涉及不可撤销的外部影响", 0.8, "destructive", 1),
            ("推测预算已用尽", 0.6, "read-only", 0),
        };
        trace.Block(Table.RenderTable(
            new[] { "场景", "后台尝试？", "理由" },
            attemptCases.Select(c =>
            {
                var g = SpeculationGate.ShouldAttempt(c.Clue, c.SideEffect, c.Budget);
                return (IReadOnlyList<object?>)new List<object?> { c.Name, Yn(g), g.Reason };
            }).ToList()));
        trace.Note("关注点：副作用范围与预算。破坏性操作即使线索很强，也必须先取得授权，不能擅自在后台执行。");

        trace.Section("第一部分 · 闸门二 ShouldContinue：要不要继续投入？");
        var contCases = new (string Name, int NewEvidence, int Attempts, int MaxAttempts)[]
        {
            ("出现新证据", 1, 2, 3),
            ("无新证据但未到上限", 0, 1, 3),
            ("无新证据且已到上限", 0, 3, 3),
        };
        trace.Block(Table.RenderTable(
            new[] { "场景", "继续投入？", "理由" },
            contCases.Select(c =>
            {
                var g = SpeculationGate.ShouldContinue(c.NewEvidence, c.Attempts, c.MaxAttempts);
                return (IReadOnlyList<object?>)new List<object?> { c.Name, Yn(g), g.Reason };
            }).ToList()));
        trace.Note("关注点：信息增量。“活动量”不等于“进展”——没有新证据的重复尝试应当止损。");

        trace.Section("第一部分 · 闸门三 ShouldDeliver：要不要交付、打断用户？");
        var deliverCases = new (string Name, bool Reproduced, bool Verified, double Confidence, bool UserAsked)[]
        {
            ("已复现且验证通过", true, true, 0.86, false),
            ("已复现但未验证", true, false, 0.5, false),
            ("未能复现", false, false, 0.2, false),
            ("未复现，但用户主动问进度", false, false, 0.2, true),
        };
        trace.Block(Table.RenderTable(
            new[] { "场景", "交付用户？", "理由" },
            deliverCases.Select(c =>
            {
                var g = SpeculationGate.ShouldDeliver(c.Reproduced, c.Verified, c.Confidence, c.UserAsked);
                return (IReadOnlyList<object?>)new List<object?> { c.Name, Yn(g), g.Reason };
            }).ToList()));
        trace.Note("关注点：只有经过验证的结果才主动呈现；未验证就保持安静。但用户主动询问时，即使不确定也要如实说明当前状态。");
        trace.Note("三道闸门是三个独立判断：可以“值得尝试”却“不值得交付”，也可以“停止投入”但“仍交付已有结论”。");
    }

    // -----------------------------------------------------------------------
    // 第二部分：真实运行推测任务，对比交付与安静丢弃
    // -----------------------------------------------------------------------

    private sealed record SpecRun(Worker? Worker, int Replies, string? ReplyText, Findings? Findings, bool Delivered);

    private static async Task<SpecRun> RunSpeculative(string taskText, string threadId, bool speculative)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "",
            MsPerSimSecond = 25,
            GlobalStateTokens = 6000,
            Speculative = speculative,
        });
        var bus = sys.Bus;
        var router = sys.Router;
        router.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = threadId,
            Text = taskText,
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群")),
        });
        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;
        var w = router.Workers.Values.FirstOrDefault();
        return new SpecRun(
            w,
            router.Replies.Count,
            router.Replies.Count > 0 ? router.Replies[0].Text : null,
            w?.FindingsState,
            router.Replies.Count > 0);
    }

    private static async Task PartRuns(Trace trace)
    {
        trace.Section("第二部分 · 真实运行：验证通过才交付");
        var tilde = await RunSpeculative("Markdown 里的波浪号删除线渲染出错了，帮忙复现并修复", "H01", speculative: true);
        trace.Note($"任务「波浪号渲染」后台尝试：复现={JsBool(tilde.Findings?.Reproduced)}，验证={JsBool(tilde.Findings?.Verified)}，置信度={((tilde.Findings?.Confidence) ?? 0).Fixed(2)}");
        trace.Note($"交付闸门放行 -> 向用户发出 {tilde.Replies} 条回复：{tilde.ReplyText ?? "（无）"}");

        trace.Section("第二部分 · 真实运行：未复现则安静丢弃");
        var lag = await RunSpeculative("打开大文件时编辑器卡顿明显，帮忙看看性能", "H02", speculative: true);
        trace.Note($"任务「大文件卡顿」后台尝试：复现={JsBool(lag.Findings?.Reproduced)}，验证={JsBool(lag.Findings?.Verified)}，置信度={((lag.Findings?.Confidence) ?? 0).Fixed(2)}");
        trace.Note($"交付闸门拦截 -> 向用户发出 {lag.Replies} 条回复（安静丢弃，不制造注意力负担）");

        trace.Section("第二部分 · 对照：关闭推测模式（speculative=false）");
        var lagNoSpec = await RunSpeculative("打开大文件时编辑器卡顿明显，帮忙看看性能", "H03", speculative: false);
        trace.Note($"同样未复现的卡顿任务，关闭推测模式后 -> 向用户发出 {lagNoSpec.Replies} 条回复：{lagNoSpec.ReplyText ?? "（无）"}");
        trace.Note("这正是讲义警告的反面：把“后台跑完了/进程退出了”当成“问题解决了”去打扰用户，等于把猜测写成结论。");

        trace.Section("第二部分结果对比");
        trace.Block(Table.RenderTable(
            new[] { "任务", "推测模式", "复现", "验证", "置信度", "是否打扰用户" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "波浪号渲染", "开", JsBool(tilde.Findings?.Reproduced), JsBool(tilde.Findings?.Verified), ((tilde.Findings?.Confidence) ?? 0).Fixed(2), tilde.Delivered ? $"是（{tilde.Replies} 条）" : "否" },
                new List<object?> { "大文件卡顿", "开", JsBool(lag.Findings?.Reproduced), JsBool(lag.Findings?.Verified), ((lag.Findings?.Confidence) ?? 0).Fixed(2), lag.Delivered ? $"是（{lag.Replies} 条）" : "否（安静丢弃）" },
                new List<object?> { "大文件卡顿", "关", JsBool(lagNoSpec.Findings?.Reproduced), JsBool(lagNoSpec.Findings?.Verified), ((lagNoSpec.Findings?.Confidence) ?? 0).Fixed(2), lagNoSpec.Delivered ? $"是（{lagNoSpec.Replies} 条）" : "否" },
            }));
    }
}
