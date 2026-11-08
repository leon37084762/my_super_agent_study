// Bus.cs —— 事件总线：两级过滤 + Thread 订阅直通
//
// 对应讲义：
//   第 3 讲（原文 3.1）  程序层策略 Fast Path：来源、群组范围、白名单
//   第 4 讲（原文 3.2）  Thread、订阅、已订阅直通 / 未订阅交 Router
//
// 四个阶段必须分清（第 3 讲学习检查标准）：
//   接收到事件 -> 允许进入候选集 -> 值得后续处理 -> 分派给执行者
// 本文件负责前两个阶段与“投递路径选择”，第三、四阶段由 Router 完成。

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperAgentDemo;

/// <summary>程序层准入检查结果（Fast Path）。</summary>
public sealed record Admission(bool Pass, string Stage, string Reason, string? Policy = null);

/// <summary>Bus 的程序层策略配置。</summary>
public sealed class BusConfig
{
    public IReadOnlyDictionary<string, string> SourcePolicy = DefaultSourcePolicy.Map;
    public HashSet<string>? Whitelist;
    public string BotName = "super-agent";
}

/// <summary>发布事件的输入。Id 为空时由 Bus 用 Clock 构造 Trigger。</summary>
public sealed class EventInput
{
    public string? Id;
    public string Source = "";
    public string ThreadId = "";
    public string Text = "";
    public string Author = "someone";
    public List<string>? Mentions;
    public Dictionary<string, object?>? Payload;
}

public sealed record PublishResult(string Stage, string Reason, Trigger? Trigger);

/// <summary>Bus 的四阶段计数与漏斗视图（第 3 讲学习检查标准的量化）。</summary>
public sealed class Funnel
{
    public int Received;
    public int ProgramFiltered;
    public int Candidate;
    public int DirectDelivered;
    public int ToRouter;

    public List<KeyValuePair<string, int>> Rows() => new()
    {
        new KeyValuePair<string, int>("接收事件", Received),
        new KeyValuePair<string, int>("程序层过滤掉", ProgramFiltered),
        new KeyValuePair<string, int>("进入候选集", Candidate),
        new KeyValuePair<string, int>("直通投递", DirectDelivered),
        new KeyValuePair<string, int>("交Router决策", ToRouter),
    };

    /// <summary>等价于 JS 的 JSON.stringify(funnel())：保留中文键与插入顺序（不转义非 ASCII）。</summary>
    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, int>
    {
        ["接收事件"] = Received,
        ["程序层过滤掉"] = ProgramFiltered,
        ["进入候选集"] = Candidate,
        ["直通投递"] = DirectDelivered,
        ["交Router决策"] = ToRouter,
    }, JsonOpts);

    // JSON.stringify 不会把中文转义成 \uXXXX，这里用 UnsafeRelaxedJsonEscaping 对齐（仅输出到控制台，无 HTML 注入风险）
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

public sealed class EventBus
{
    private readonly Clock _clock;
    private readonly Trace _trace;
    private readonly ObjectStore _store;
    private readonly bool _directDelivery;
    private readonly BusConfig _cfg;
    private Router? _router;

    public readonly Funnel Counters = new();
    /// <summary>事件到达时间 -> 处理完成时间，用于统计端到端等待。</summary>
    public readonly List<double> Latencies = new();

    public EventBus(
        Clock clock,
        Trace trace,
        ObjectStore store,
        bool directDelivery = true,
        IReadOnlyDictionary<string, string>? sourcePolicy = null,
        HashSet<string>? whitelist = null,
        string botName = "super-agent")
    {
        _clock = clock;
        _trace = trace;
        _store = store;
        _directDelivery = directDelivery; // false = 消融「去掉直通」（第 10 讲方案二）
        _cfg = new BusConfig { SourcePolicy = sourcePolicy ?? DefaultSourcePolicy.Map, Whitelist = whitelist, BotName = botName };
    }

    public void AttachRouter(Router router) => _router = router;

    /// <summary>
    /// 程序层准入检查（Fast Path）。纯代码，不调用模型。
    /// </summary>
    public static Admission ProgramFilter(Trigger trigger, BusConfig cfg)
    {
        var policy = cfg.SourcePolicy.TryGetValue(trigger.Source, out var p) ? p : Policy.Ignore;

        if (policy == Policy.Ignore)
            return new Admission(false, "来源策略", $"{trigger.Source} 配置为「直接忽略」");

        if (cfg.Whitelist != null && cfg.Whitelist.Count > 0 && trigger.Source != Source.Internal && !cfg.Whitelist.Contains(trigger.Author))
            return new Admission(false, "白名单", $"发出者 {trigger.Author} 不在白名单");

        if (policy == Policy.ExplicitOnly && !(trigger.Mentions ?? new List<string>()).Contains(cfg.BotName))
            return new Admission(false, "明确请求", $"来源为「{PolicyLabel.Map[Policy.ExplicitOnly]}」，但未提及 {cfg.BotName}");

        return new Admission(true, "准入通过", $"来源策略：{(PolicyLabel.Map.TryGetValue(policy, out var lbl) ? lbl : policy)}", policy);
    }

    /// <summary>订阅关系：Worker &lt;-&gt; Thread 是多对多（第 4 讲第五节）。</summary>
    public CtxObject Subscribe(string workerId, string threadId, string? title = null, string source = "unknown")
    {
        var t = _store.EnsureThread(threadId, source, title ?? threadId);
        t.AddSubscriber(workerId);
        _store.Link(threadId, workerId);
        return t;
    }

    public bool Unsubscribe(string workerId, string threadId)
    {
        var t = _store.Get(threadId);
        if (t == null) return false;
        t.SubscriberIds.Remove(workerId);
        return true;
    }

    /// <summary>发布一个普通输入（无 Id 时用 Clock 构造 Trigger）。</summary>
    public Task<PublishResult> PublishAsync(EventInput input)
    {
        var trigger = input.Id != null
            ? new Trigger
            {
                Id = input.Id,
                Source = input.Source,
                ThreadId = input.ThreadId,
                Text = input.Text,
                Author = input.Author,
                Mentions = input.Mentions ?? new List<string>(),
                Payload = input.Payload ?? new Dictionary<string, object?>(),
                At = _clock.Now(),
            }
            : Trigger.Make(input.Source, input.ThreadId, input.Text, input.Author, input.Mentions, input.Payload, _clock);
        return PublishAsync(trigger);
    }

    /// <summary>
    /// 发布一个事件，走完“程序过滤 -&gt; 直通 / 交 Router”的完整路径。
    /// 逻辑全部同步（无真实 await），返回已完成的 Task 以对齐 JS 的 async 签名。
    /// </summary>
    public Task<PublishResult> PublishAsync(Trigger trigger) => Task.FromResult(PublishCore(trigger));

    private PublishResult PublishCore(Trigger trigger)
    {
        Counters.Received += 1;

        // -- 阶段 1/2：程序层 Fast Path -------------------------------------
        var admission = ProgramFilter(trigger, _cfg);
        if (!admission.Pass)
        {
            Counters.ProgramFiltered += 1;
            _trace.Log("BUS", $"程序层过滤 {trigger.Id} <- {trigger.Source}: {admission.Reason}（未调用模型）");
            return new PublishResult("program-filtered", admission.Reason, trigger);
        }

        // 进入候选集：登记到对象库，构成引用图（第 5 讲的 ID 与关联）
        Counters.Candidate += 1;
        var threadTitle = trigger.Payload.TryGetValue("threadTitle", out var tt) ? tt?.ToString() : null;
        var thread = _store.EnsureThread(trigger.ThreadId, trigger.Source, threadTitle ?? $"{trigger.Source} 话题 {trigger.ThreadId}");
        var replyTo = trigger.Payload.TryGetValue("replyTo", out var rt) ? rt : null;
        _store.Put(
            Kind.Trigger,
            id: trigger.Id,
            title: Trunc(trigger.Text, 20),
            summary: Summarize(trigger.Text),
            body: trigger.Text,
            meta: new Dictionary<string, object?> { ["source"] = trigger.Source, ["author"] = trigger.Author, ["replyTo"] = replyTo });
        _store.AppendEvent(trigger.ThreadId, trigger.Id);
        _trace.Log("BUS", $"候选事件 {trigger.Id} @{thread.Id}「{Summarize(trigger.Text)}」({admission.Reason})");

        // -- 阶段 3：已订阅 Thread -> 程序直通，不再经 Router 分派 ------------
        if (_directDelivery && thread.SubscriberIds.Count > 0)
        {
            Counters.DirectDelivered += 1;
            var subs = thread.SubscriberIds.ToList();
            foreach (var wid in subs) _router?.Deliver(wid, trigger, "direct");
            _trace.Log("BUS", $"Fast Path 直通 {trigger.Id} -> {string.Join(" ", subs)}（Router 不调用模型）");
            _router?.Observe(trigger); // Router 仍可观察事件、维护全局状态
            MarkHandled(trigger);
            return new PublishResult("direct-delivered", $"沿订阅投递给 {string.Join(" ", subs)}", trigger);
        }

        // -- 阶段 4：未订阅 -> Slow Path，交 Router 判断归属 ------------------
        Counters.ToRouter += 1;
        if (_router == null) return new PublishResult("dropped", "未挂载 Router", trigger);
        _router.Enqueue(trigger);
        return new PublishResult("to-router", "未订阅，交 Router 判断与分派", trigger);
    }

    private void MarkHandled(Trigger trigger) => Latencies.Add(_clock.Now() - (trigger.At));

    /// <summary>Router 完成决策后回填端到端等待时间。</summary>
    public void MarkRouted(Trigger trigger) => MarkHandled(trigger);

    public Funnel Funnel() => Counters;

    private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

    /// <summary>教学用的摘要：取前 n 字，模拟“注入摘要而非全文”。</summary>
    public static string Summarize(string? text, int n = 24)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length > n ? t.Substring(0, n) + "…" : t;
    }
}
