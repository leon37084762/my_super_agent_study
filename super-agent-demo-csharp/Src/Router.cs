// Router.cs —— Router 层：常驻全局协调 + 批处理 + 统一对外回复
//
// 对应讲义：
//   第 2 讲（原文 2.1）  Router 是全局序点、协调点和记忆；不执行具体任务；可用较轻量模型
//   第 2 讲第五节        批处理：上一轮推理期间到达的事件积压，下一轮合并处理
//   第 4 讲（原文 3.2）  未订阅事件由 Router 判断分派；已订阅事件直通，Router 仍可观察
//   第 5 讲（原文 3.3）  Router 不携带全部历史，只保留摘要与关联 ID，靠链式召回补回
//   第 7 讲（原文 4.2）  Worker 不直接回复用户，由 Router 统一按渠道组织表达

namespace SuperAgentDemo;

/// <summary>创建 Worker 的入参（避免模块环依赖：Router 不直接 new Worker）。</summary>
public sealed class WorkerSpec
{
    public string Task = "";
    public Trigger? OriginTrigger;
    public string? OriginThread;
    public Router? Router;
}

public delegate Worker WorkerFactory(WorkerSpec spec);

/// <summary>Worker 汇报给 Router 的参数（第 7 讲：Worker 不直接回复用户）。</summary>
public sealed class ReportArgs
{
    public string Purpose = "progress";
    public ChannelFact? Fact;
    public string? Text;
    public string? Channel;
}

public sealed record QueueItem(Trigger Trigger, double ArrivedAt);
public sealed record DecidedItem(QueueItem Item, RoutingDecision Decision);

public sealed class RoundRecord
{
    public double At;
    public int BatchSize;
    public double Latency;
    public int PromptTokens;
    public int CacheReadTokens;
    public int OverflowAtStart;
    public int ContextChars;
}

public sealed class Reply
{
    public string Channel = "";
    public string Purpose = "";
    public string Text = "";
    public ChannelAudit? Audit;
    public double At;
    public string? WorkerId;
    public Trigger? Trigger;
}

public sealed class DecisionLatency
{
    public string Id = "";
    public double Waited;
    public string? Action;
}

public sealed class FilterResult
{
    public string Id = "";
    public string Source = "";
    public string Text = "";
    public bool Worth;
    public string Reason = "";
}

public sealed class RouterSummary
{
    public int RouterRounds;
    public int Decisions;
    public double MeanBatch;
    public double MeanRoundLatency;
    public double ServicePerDecision;
    public int CacheReadTokens;
    public int PromptTokens;
    public int ObservedWhileDirect;
    public int SuppressedProgress;
    public int Replies;
}

public sealed class Router
{
    public const string SystemPrompt =
        "你是常驻的全局路由层（Router）。\n" +
        "职责：承接人类交互、维护跨任务关联、分派独立任务、统一对外回复。\n" +
        "禁止：亲自执行代码修改、测试与调试；不要介入 Worker 内部的步骤拆解。\n" +
        "判断顺序：这条事件是否值得处理 -> 是否已有相关任务 -> 交给谁 -> 是否需要回复用户。";

    private readonly Clock _clock;
    private readonly Trace _trace;
    private readonly ObjectStore _store;
    private readonly EventBus _bus;
    private readonly MockLlm _llm;
    private readonly WorkerFactory _workerFactory;
    private readonly double _pollSimSec;
    private readonly string _userChannel;
    private readonly string _semanticPolicy;

    public readonly bool FilterOnly;
    public readonly int MaxBatch;
    public int GlobalStateTokens;

    /// <summary>filterOnly 模式下记录每条事件的语义判断结果。</summary>
    public readonly List<FilterResult> FilterResults = new();
    public readonly List<QueueItem> Queue = new();     // 待决策事件（Slow Path）
    public readonly Dictionary<string, Worker> Workers = new();
    public bool Running;
    /// <summary>本轮是否正在推理：batch 已取出但决策尚未落地的空窗期为 true；quiesce 必须避开它。</summary>
    public bool Busy;
    public readonly List<RoundRecord> Rounds = new();
    public readonly List<Reply> Replies = new();
    public readonly List<DecisionLatency> DecisionLatencies = new();
    public int ObservedWhileDirect;  // 直通时 Router 仍观察到的事件数
    public int SuppressedProgress;   // 低打扰规范下被抑制的中间进展
    public Task? Done;

    public Router(
        Clock clock,
        Trace trace,
        ObjectStore store,
        EventBus bus,
        MockLlm llm,
        WorkerFactory workerFactory,
        bool filterOnly = false,
        int maxBatch = 8,
        int globalStateTokens = 9000,
        double pollSimSec = 0.05,
        string userChannel = "im",
        string semanticPolicy = "default")
    {
        _clock = clock;
        _trace = trace;
        _store = store;
        _bus = bus;
        _llm = llm;
        _workerFactory = workerFactory;
        _pollSimSec = pollSimSec;
        _userChannel = userChannel;
        _semanticPolicy = semanticPolicy; // 语义层策略（第 3.1 节）：可用自然语言调整的扩展点
        FilterOnly = filterOnly;
        MaxBatch = maxBatch;
        GlobalStateTokens = globalStateTokens;
    }

    // -- 事件入口 -----------------------------------------------------------

    public void Enqueue(Trigger trigger) => Queue.Add(new QueueItem(trigger, trigger.At));

    /// <summary>直通投递时由 Bus 调用：Worker 直接收事件，Router 不花模型调用。</summary>
    public bool Deliver(string workerId, Trigger trigger, string how = "router")
    {
        if (!Workers.TryGetValue(workerId, out var w)) return false;
        w.Receive(trigger, how);
        return true;
    }

    /// <summary>
    /// 第 4 讲自测题 1：直通不等于 Router 失去全局信息。
    /// 这里只做程序化的状态更新（摘要级别），不调用模型。
    /// </summary>
    public void Observe(Trigger trigger)
    {
        ObservedWhileDirect += 1;
        foreach (var w in Workers.Values)
            if (w.Subscriptions.Contains(trigger.ThreadId)) w.NoteObserved(trigger);
    }

    // -- 主循环 -------------------------------------------------------------

    /// <summary>启动常驻循环（不阻塞）。需要等待退出时用 await router.Done。</summary>
    public Router Start()
    {
        if (Done != null) return this;
        Running = true;
        Done = LoopAsync();
        return this;
    }

    public void Stop() => Running = false;

    private async Task LoopAsync()
    {
        while (Running)
        {
            if (Queue.Count == 0) { await _clock.Sleep(_pollSimSec); continue; }
            // 批处理：一次取走当前积压，推理期间新到的事件自然积到下一轮
            var batch = Queue.Take(MaxBatch).ToList();
            Queue.RemoveRange(0, batch.Count);
            var overflow = Queue.Count;
            Busy = true; // 取出即置忙：覆盖“队列已空但 Worker 尚未创建”的空窗
            try { await RoundAsync(batch, overflow); }
            finally { Busy = false; }
        }
    }

    private async Task RoundAsync(List<QueueItem> batch, int overflowAtStart)
    {
        var ctx = BuildContext(batch);
        var outputTokens = 30 + batch.Count * 42; // Router 输出短：每条事件一小段决策
        var startedAt = _clock.Now();

        var call = await _llm.CallAsync(
            ctx.PromptTokens,
            outputTokens,
            () =>
            {
                var view = GlobalView(); // 本轮开始时的全局快照（批内各条事件看到同一视图）
                return batch.Select(item => new DecidedItem(item, Semantic.JudgeRouting(item.Trigger, view))).ToList();
            },
            cacheReadTokens: ctx.CachedTokens);

        Rounds.Add(new RoundRecord
        {
            At = startedAt,
            BatchSize = batch.Count,
            Latency = call.Latency,
            PromptTokens = ctx.PromptTokens,
            CacheReadTokens = ctx.CachedTokens,
            OverflowAtStart = overflowAtStart,
            ContextChars = ctx.Text.Length,
        });
        _trace.Log("ROUTER", $"批处理 {batch.Count} 条事件，单轮耗时 {call.Latency.Fixed(2)}s（输入 {ctx.PromptTokens} tok，其中读缓存 {ctx.CachedTokens} tok）");

        foreach (var item in call.Result) Apply(item);
    }

    /// <summary>组装本轮注入的上下文：系统提示 + 全局状态摘要 + 每条事件的 brief。</summary>
    private (string Text, int CachedTokens, int PromptTokens) BuildContext(List<QueueItem> batch)
    {
        var global = Workers.Count > 0
            ? string.Join("\n", Workers.Values.Select(w => w.Brief()))
            : "（当前没有在跑任务）";
        // 常驻全局状态被抽象成固定规模：真实系统里它随任务数增长，但只放摘要
        var padded = global + new string(' ', Math.Max(0, GlobalStateTokens * 3 - global.Length));
        var cachedPart = $"{SystemPrompt}\n[全局状态]\n{padded}\n";
        var batchPart = $"[本批事件 {batch.Count} 条]\n{string.Join("\n", batch.Select(b => _store.Brief(b.Trigger.Id)))}\n";
        var cachedTokens = Math.Max(Tokenizer.EstimateTokens(cachedPart), GlobalStateTokens);
        return (cachedPart + batchPart, cachedTokens, cachedTokens + Tokenizer.EstimateTokens(batchPart));
    }

    /// <summary>Router 眼中的全局视图：只有摘要级信息（第 5 讲：不是全部历史）。</summary>
    public RoutingView GlobalView() => new()
    {
        Policy = _semanticPolicy,
        Workers = Workers.Values.Select(w => new WorkerSummary
        {
            Id = w.Id,
            Task = w.Task,
            Status = w.Status,
            ThreadIds = new List<string>(w.Subscriptions),
        }).ToList(),
    };

    // -- 决策落地 -----------------------------------------------------------

    private void Apply(DecidedItem di)
    {
        var trigger = di.Item.Trigger;
        var decision = di.Decision;
        DecisionLatencies.Add(new DecisionLatency { Id = trigger.Id, Waited = _clock.Now() - di.Item.ArrivedAt, Action = decision.Action });
        _bus.MarkRouted(trigger);

        if (FilterOnly)
        {
            // 第 3 讲到“值得处理”为止，不进入路由分派
            var worth = decision.Action != "ignore";
            FilterResults.Add(new FilterResult { Id = trigger.Id, Source = trigger.Source, Text = trigger.Text, Worth = worth, Reason = decision.Reason });
            _trace.Log("ROUTER", $"语义层判断 {trigger.Id}: {(worth ? "值得处理" : "无须行动")} —— {decision.Reason}");
            return;
        }

        switch (decision.Action)
        {
            case "ignore":
                _trace.Log("ROUTER", $"语义层判断 {trigger.Id}: 无须行动 —— {decision.Reason}");
                return;

            case "dispatch":
            {
                var w = SpawnWorker(trigger);
                _trace.Log("ROUTER", $"分派新任务 {w.Id}「{w.Task}」并订阅 {trigger.ThreadId}（{decision.Reason}）");
                w.Receive(trigger, "dispatch");
                return;
            }

            case "attach":
                _trace.Log("ROUTER", $"事件 {trigger.Id} 并入已有任务 {decision.WorkerId}（{decision.Reason}）");
                Deliver(decision.WorkerId!, trigger, "router");
                return;

            case "steer":
                _trace.Log("ROUTER", $"约束投递 {trigger.Id} -> {decision.WorkerId}：{trigger.Text}");
                Deliver(decision.WorkerId!, trigger, "steer");
                return;

            case "steer-all":
            {
                // 第 2 讲：跨任务的高层关联由 Router 维护。
                // 一条不含任务关键词的全局约束，仍能关联到所有在跑任务。
                var targets = Workers.Values.Where(w => w.Status != "closed").ToList();
                _trace.Log("ROUTER", $"全局约束广播 {trigger.Id} -> {(targets.Count > 0 ? string.Join(" ", targets.Select(w => w.Id)) : "无")}：{trigger.Text}");
                foreach (var w in targets) Deliver(w.Id, trigger, "steer");
                return;
            }

            case "recall-and-reply":
                RecallAndReply(trigger);
                return;

            default:
                _trace.Log("ROUTER", $"未知决策 {decision.Action}");
                return;
        }
    }

    private Worker SpawnWorker(Trigger trigger)
    {
        var w = _workerFactory(new WorkerSpec { Task = trigger.Text, OriginTrigger = trigger, OriginThread = trigger.ThreadId, Router = this });
        Workers[w.Id] = w;
        w.DispatchedAt = _clock.Now();
        _store.Put(
            Kind.Session,
            id: w.Id,
            title: w.Task,
            summary: $"任务：{Trunc(w.Task, 24)}",
            body: "",
            meta: new Dictionary<string, object?> { ["worker"] = true });
        _bus.Subscribe(w.Id, trigger.ThreadId, $"{trigger.Source} 话题", trigger.Source);
        w.Start(); // 异步启动，不阻塞 Router（第 2 讲：Router 不陷入具体执行）
        return w;
    }

    /// <summary>
    /// 链式召回后回复用户（第 5 讲完整案例）。
    /// Router 不常驻任务历史，靠 ID 引用图按需逐跳找回。
    /// </summary>
    private void RecallAndReply(Trigger trigger)
    {
        var recall = new RecallSession(_store, tokenBudget: 1200);
        var res = recall.Walk(
            trigger.Id,
            matches: obj => obj != null && (obj.Kind == Kind.PR || (obj.Kind == Kind.Thread && obj.Source == Source.GhPr)),
            maxHops: 5,
            onHop: h => _trace.Log("ROUTER", $"  召回第 {h.Hop} 跳 {h.Id}({h.Kind ?? "?"}) {(h.Action == "hit" ? "命中" : "展开")} 累计注入 {h.InjectedTokens} tok"));

        var lastHit = recall.Hops.LastOrDefault(h => h.Action == "hit");
        var target = lastHit != null ? _store.Get(lastHit.Id) : null;
        var answer = target != null
            ? $"{Trunc(trigger.Text, 12)}… 已找到：{target.Summary ?? target.Title}（沿 {res.Path} 召回，{res.ReadCalls} 次读取，注入 {res.InjectedTokens} tok）"
            : $"未能沿引用图定位到相关对象（召回 {res.Hops} 跳，注入 {res.InjectedTokens} tok）";

        Say("im", Purpose.Answer, answer, trigger: trigger);
    }

    // -- 对外表达 -----------------------------------------------------------

    /// <summary>
    /// Worker 汇报 -&gt; Router 统一回复用户。
    /// 低打扰渠道会抑制中间进展（第 6 讲：不必每次尝试都通知用户）。
    /// </summary>
    public void Report(string workerId, ReportArgs args)
    {
        if (!Workers.TryGetValue(workerId, out var w)) return;
        w.LastReport = args.Fact?.Topic ?? args.Text ?? "";

        var channel = args.Channel ?? _userChannel;
        if (!Channels.ShouldNotify(channel, args.Purpose))
        {
            SuppressedProgress += 1;
            _trace.Log("ROUTER", $"抑制 {workerId} 的中间进展（{channel} 低打扰规范，不打断用户）");
            return;
        }
        var body = args.Text ?? (args.Fact != null ? Channels.RenderForChannel(channel, args.Fact) : "");
        Say(channel, args.Purpose, body, workerId: workerId);
    }

    private void Say(string channel, string purpose, string text, string? workerId = null, Trigger? trigger = null)
    {
        var audit = Channels.AuditChannel(channel, text);
        Replies.Add(new Reply { Channel = channel, Purpose = purpose, Text = text, Audit = audit, At = _clock.Now(), WorkerId = workerId, Trigger = trigger });
        _trace.Log("TO-USER", $"[{channel}] {text.Replace("\n", " / ")}");
        if (!audit.Ok) _trace.Log("TO-USER", $"  ! 规范审计不通过：{audit.Reason}");
    }

    // -- 统计 ---------------------------------------------------------------

    public RouterSummary Summary()
    {
        var batches = Rounds.Select(r => (double)r.BatchSize).ToList();
        var lat = Rounds.Select(r => r.Latency).ToList();
        var totalDecisions = (int)batches.Sum();
        var perDecision = new List<double>();
        for (var i = 0; i < lat.Count; i++) perDecision.Add(batches[i] > 0 ? lat[i] / batches[i] : 0);
        return new RouterSummary
        {
            RouterRounds = Rounds.Count,
            Decisions = totalDecisions,
            MeanBatch = Mean(batches),
            MeanRoundLatency = Mean(lat),
            ServicePerDecision = Mean(perDecision),
            CacheReadTokens = Rounds.Sum(r => r.CacheReadTokens),
            PromptTokens = Rounds.Sum(r => r.PromptTokens),
            ObservedWhileDirect = ObservedWhileDirect,
            SuppressedProgress = SuppressedProgress,
            Replies = Replies.Count,
        };
    }

    /// <summary>等待队列排空且所有 Worker 结束（closed / idle）。超时而未结束则返回 false。</summary>
    public async Task<bool> QuiesceAsync(double timeoutSimSec = 120)
    {
        bool IsDone(Worker w) => w.Status == "closed" || w.Status == "idle";
        bool IsSettled() => !Busy && Queue.Count == 0 && Workers.Values.All(IsDone);
        var t0 = _clock.Now();
        while (_clock.Now() - t0 < timeoutSimSec)
        {
            if (IsSettled())
            {
                // 再等一小段，确认没有尾巴事件（例如后台工具的完成事件）
                await _clock.Sleep(0.3);
                if (IsSettled()) return true;
            }
            await _clock.Sleep(0.1);
        }
        return false;
    }

    private static double Mean(List<double> a) => a.Count > 0 ? a.Sum() / a.Count : 0;
    private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
}
