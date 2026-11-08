// 演示 05 —— 上下文组织与链式召回
//
// 对应讲义第 5 讲（原文第 3.3 节）。
//
// Router 不常驻全部历史，只保留摘要与关联 ID，需要时靠“链式召回”沿引用图逐跳找回。
// 本演示分四部分：
//   1. 先跑一个真实任务，让系统自然留下一张引用图（Trigger/Thread/Worker/PR/评论），
//      再补入若干条带长日志的历史消息，模拟“累积了很久的工作群”
//   2. Inline 与 Spill：窗口内放摘要 + 关联 ID + 读取入口，完整内容留在窗口外
//   3. 关键区分：把长内容“截断”并不等于 Spill —— 必须保留读回完整内容的入口
//   4. 链式召回 vs 两种对照方案（一次性拼历史 / 只放 ID 全靠读回）的 Token 与往返对比

namespace SuperAgentDemo;

public static class Demo05
{
    // 一条“很长”的历史日志正文：模拟工作群里粘贴的完整排查记录，用于让 Spill 发挥省 Token 的作用。
    // 正文远长于 Inline 上限（50 字）时，链式召回只注入前缀，而“一次性拼历史”要注入全文，差距才明显。
    private const string LongLog =
        "调用链采样：网关->鉴权->会话服务，鉴权段 P99 从 120ms 抬升到 900ms，会话服务连接池等待队列积压，峰值时段出现 504；已导出对应时段全部请求的时间戳、耗时分布与重试次数，逐段核对超时与重试配置，完整日志体积较大，此处仅摘录开头部分，其余见附件。";

    public static Task RunAsync() => MainAsync();

    /// <summary>向 Thread 补入 n 条历史消息（含较长正文），模拟长期累积的讨论。</summary>
    private static int SeedHistory(ObjectStore store, string threadId, int n = 8)
    {
        for (var i = 0; i < n; i++)
        {
            var body = $"{LongLog}{LongLog.Substring(0, 60)}（第 {i + 1} 条历史消息，完整日志见附件）";
            var id = $"E{(i + 1).ToString().PadLeft(2, '0')}";
            store.Put(Kind.Trigger, title: $"历史消息 {i + 1}", summary: $"排查记录 {i + 1}：登录网关高峰期 504", body: body, id: id);
            store.AppendEvent(threadId, id);
        }
        return n;
    }

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 05 · 上下文组织与链式召回");

        var sys = await BuildReferenceGraph(trace);
        var spill = PartInlineSpill(sys, trace);
        var recall = PartChainRecall(sys, trace);

        trace.Section("全演示小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "演示结果" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "唯一 ID + 引用图", $"{sys.Store.Objects.Count} 个对象互相以 ID 关联" },
                new List<object?> { "Spill 保留读回入口", $"{spill.CommentId} 正文 {spill.BodyLen} 字，窗口内只放前缀 + read() 入口" },
                new List<object?> { "链式召回注入", $"{recall.Chain} tok，读回 {recall.Reads} 次" },
                new List<object?> { "对比：一次性拼历史", $"{recall.Flatten} tok（约 {recall.Ratio} 倍，含大量无关正文）" },
                new List<object?> { "对比：只放 ID", $"{recall.IdOnly} tok，但往返读回次数最多" },
            }));
        trace.Note($"口径核对：estimateTokens('登录超时 login timeout') = {Tokenizer.EstimateTokens("登录超时 login timeout")}（中英混排示例）。");

        Takeaways.Print(trace, new[]
        {
            "为 Trigger / Thread / Worker Session 赋予唯一 ID，注入时带上紧密关联对象的 ID，就构成一张可按需行走的引用图。",
            "Inline 把近期很可能用的信息直接嵌入窗口，减少工具往返；Spill 把完整内容留在窗口外，只在窗口内保留摘要与读取入口。",
            "把长内容截断并不等于 Spill：截断丢掉了后半段且无法补回，Spill 的关键是保留“读回完整内容”的入口。",
            "链式召回从最少信息开始，逐跳只注入摘要，命中目标才读回全文；Thread 历史越长、单条正文越大，相对“一次性拼历史”省得越多。",
            "两个极端都不划算：一次性拼全部历史注入过大且混入无关内容；只放一个 ID 注入最省，却把成本转嫁成反复读回的执行步数。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一部分：跑一个真实任务 + 补入历史，得到一张引用图
    // -----------------------------------------------------------------------

    private static async Task<BuiltSystem> BuildReferenceGraph(Trace trace)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions { Title = "第一部分：一个真实任务 + 历史消息留下的引用图", MsPerSimSecond = 30, GlobalStateTokens = 6000 });
        var bus = sys.Bus;
        var router = sys.Router;
        var store = sys.Store;
        router.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群：登录故障")),
        });
        await router.QuiesceAsync(timeoutSimSec: 60);
        router.Stop();
        await router.Done!;

        var seeded = SeedHistory(store, "H01", 8);
        trace.Note($"任务跑完后，又向 H01 补入 {seeded} 条历史消息（含较长日志正文），模拟一条“聊了很久”的工作群。");
        trace.Note("对象库里现在是一张引用图（每个对象都有唯一 ID，并携带紧密关联对象的 ID）：");
        var rows = store.Objects.Values.Select(o =>
        {
            var links = string.Join(" ", o.Links.Where(x => x != o.Id).Take(5));
            return (IReadOnlyList<object?>)new List<object?>
            {
                o.Id,
                KindLabel.Of(o.Kind),
                Demo.Trunc(o.Summary ?? o.Title ?? "", 20),
                (o.Body ?? "").Length,
                links.Length > 0 ? links : "-",
            };
        }).ToList();
        trace.Block(Table.RenderTable(new[] { "ID", "类型", "摘要", "正文字数", "关联 ID（引用图的边）" }, rows));
        trace.Note($"对象总数 {store.Objects.Count}；这张图就是链式召回可以逐跳行走的“地图”。");
        return sys;
    }

    // -----------------------------------------------------------------------
    // 第二、三部分：Inline / Spill，以及“截断 ≠ Spill”
    // -----------------------------------------------------------------------

    private static (string CommentId, int BodyLen) PartInlineSpill(BuiltSystem sys, Trace trace)
    {
        var store = sys.Store;
        var commentId = store.Objects.Values.FirstOrDefault(o => o.Kind == Kind.Comment)?.Id ?? "P01-C1";
        var obj = store.Get(commentId)!;
        var bodyLen = (obj.Body ?? "").Length;

        trace.Section("第二部分：Inline（窗口内视图）与 Spill（完整内容留窗口外）");
        trace.Note($"对象 {commentId} 的完整正文有 {bodyLen} 字，超过 Inline 上限 {store.InlineCharLimit} 字。");
        trace.Note("注入上下文时看到的是 brief()——摘要 + 被 Spill 的前缀 + 读回入口 + 关联 ID：");
        trace.Block(store.Brief(commentId));

        trace.Section("第三部分：关键区分 —— “截断”不等于 Spill");
        var naive = (obj.Body ?? "").Substring(0, Math.Min(store.InlineCharLimit, bodyLen)) + "…";
        trace.Note("做法甲（错误）：只把长内容截断塞进窗口，模型永远拿不到后半段：");
        trace.Block($"正文: {naive}   <-- 到此为止，没有入口，被截掉的内容无法补回");
        trace.Note("做法乙（正确 = Spill）：同样只放前缀，但保留 read(\"...\") 入口，需要时能取回完整内容：");
        var full = store.ReadFull(commentId);
        var nextHop = string.Join(" ", full.NextHop.Select(n => $"{n.Id}({(n.Kind != null ? KindLabel.Of(n.Kind) : "?")})"));
        trace.Block($"read(\"{commentId}\") -> 取回完整 {full.Text.Length} 字；下一跳关联：{nextHop}");
        trace.Note("差别不在“放多少字”，而在“有没有留一条把完整内容读回来的路”。这是第 5 讲自测题的核心。");
        return (commentId, bodyLen);
    }

    // -----------------------------------------------------------------------
    // 第四部分：链式召回 vs 两种对照方案
    // -----------------------------------------------------------------------

    private static (int Chain, int Flatten, int IdOnly, int Reads, string Ratio) PartChainRecall(BuiltSystem sys, Trace trace)
    {
        var store = sys.Store;

        // 用户在原 Thread 追问评审进度：把它挂进 H01，使它与引用图相连
        const string askId = "T99";
        store.Put(Kind.Trigger, title: "追问进度", summary: "用户询问上次登录修复的评审进度", body: "上次那个登录超时的修复，评审到哪一步了？", id: askId);
        store.AppendEvent("H01", askId);
        trace.Section("第四部分：从追问事件出发，沿引用图链式召回");
        trace.Note($"起点 {askId}「上次那个登录超时的修复，评审到哪一步了？」，目标：找到相关的 PR / 评审对象。");

        var recall = new RecallSession(store, tokenBudget: 4000);
        var res = recall.Walk(
            askId,
            matches: o => o != null && (o.Kind == Kind.PR || (o.Kind == Kind.Thread && o.Source == Source.GhPr)),
            maxHops: 5,
            onHop: h => trace.Note($"  第 {h.Hop} 跳 {h.Id}({(h.Kind != null ? KindLabel.Of(h.Kind) : "?")}) {(h.Action == "hit" ? "命中，读回全文" : "展开，仅注入摘要+关联ID")} 累计 {h.InjectedTokens} tok"));

        trace.Note($"召回路径：{res.Path}{(res.Aborted ? "（超出预算中止）" : "")}");
        var hit = recall.Hops.LastOrDefault(h => h.Action == "hit");
        trace.Note($"命中 {hit?.Id}：{(hit != null ? store.Get(hit.Id)?.Summary ?? "" : "")}");
        trace.Note("注意：沿途的历史消息只注入了摘要与被 Spill 的前缀，没有把它们的完整日志正文读进窗口。");

        // 对照方案 A：把整条 Thread 的全部历史（含完整正文）一次性拼进上下文
        var flatten = store.FlattenWholeHistory("H01");
        // 对照方案 B：上下文只放一个 ID，任何信息都要靠工具逐跳读回
        var idOnly = store.IdOnlyView("H01");
        var idOnlyReads = res.Path.Split(" -> ").Length; // 走完整条链大约要这么多次读回

        var ratio = ((double)flatten.InjectedTokens / Math.Max(1, res.InjectedTokens)).Fixed(1);
        trace.Section("第四部分结果：三种上下文组织方式对比");
        trace.Block(Table.RenderTable(
            new[] { "方案", "注入 Token", "工具读回次数", "特点" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "链式召回（本讲）", res.InjectedTokens, res.ReadCalls, "逐跳只注入摘要，命中才读全文" },
                new List<object?> { "A 一次性拼全部历史", flatten.InjectedTokens, flatten.ReadCalls, "把所有历史消息的完整正文都塞进窗口" },
                new List<object?> { "B 只放一个 ID", idOnly.InjectedTokens, idOnlyReads, "注入最小，但每跳都要读回、步数膨胀" },
            },
            new[] { 'l', 'r', 'r', 'l' }));
        trace.Note($"链式召回注入 {res.InjectedTokens} tok，只读回命中的 {res.ReadCalls} 个对象；");
        trace.Note($"一次性拼历史注入 {flatten.InjectedTokens} tok，约为链式召回的 {ratio} 倍——因为它连每条历史消息的完整日志正文都注入了，其中多数与本次追问无关；");
        trace.Note($"只放 ID 注入 {idOnly.InjectedTokens} tok 最省，但要沿链读回约 {idOnlyReads} 次，把成本换成了执行步数与延迟（第 5 讲自测题 1）。");
        trace.Note("说明：Token 用 estimateTokens 教学近似（中文按字、其余按 4 字符折算），仅用于比较相对量级。");
        return (res.InjectedTokens, flatten.InjectedTokens, idOnly.InjectedTokens, res.ReadCalls, ratio);
    }
}
