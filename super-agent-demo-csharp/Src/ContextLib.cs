// ContextLib.cs —— 上下文组织：Inline / Spill / 局部性 / 链式召回
//
// 对应讲义第 5 讲（原文第 3.3 节）：
//   Inline：把近期很可能使用的信息直接嵌入当前上下文，减少工具往返
//   Spill ：完整内容放在窗口外，窗口内保留索引或摘要 + 可访问入口
//   局部性：从最少信息开始，逐步 Inline 关联对象，超限则 Spill
//   链式召回：为 Trigger / Thread / Worker Session 赋予唯一 ID，
//             注入时携带紧密关联对象的 ID，构成引用图，按需逐跳展开
//
// 关键约束（讲义自测题 2）：把长内容截断并不等于 Spill，
// 必须同时保留“读回完整内容”的入口，否则模型无法补回被截断的部分。

namespace SuperAgentDemo;

public static class Kind
{
    public const string Trigger = "trigger";
    public const string Thread = "thread";
    public const string Session = "session";
    public const string PR = "pr";
    public const string Comment = "comment";
    public const string Log = "log";
}

public static class KindLabel
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        [Kind.Trigger] = "事件",
        [Kind.Thread] = "话题流",
        [Kind.Session] = "Worker",
        [Kind.PR] = "PR",
        [Kind.Comment] = "评论",
        [Kind.Log] = "日志",
    };

    public static string Of(string kind) => Map.TryGetValue(kind, out var v) ? v : kind;
}

/// <summary>
/// 引用图里的一个对象。事件、Thread、PR、评论、日志都用同一结构（仿 JS 的鸭子类型）；
/// Thread 额外用到 Source / EventIds / SubscriberIds。
/// Links 与 SubscriberIds 用“有序去重列表”复刻 JS Set 的插入顺序，保证输出稳定。
/// </summary>
public sealed class CtxObject
{
    public string Id = "";
    public string Kind = "";
    public string? Title;
    public string? Summary;
    public string Body = "";
    public List<string> Links = new();
    public Dictionary<string, object?> Meta = new();
    public long At;

    // 仅 Thread 使用
    public string? Source;
    public List<string> EventIds = new();
    public List<string> SubscriberIds = new();

    public void AddLink(string id) { if (!Links.Contains(id)) Links.Add(id); }
    public void AddSubscriber(string id) { if (!SubscriberIds.Contains(id)) SubscriberIds.Add(id); }
}

public sealed record NextHop(string Id, string? Kind = null, string? Summary = null);
public sealed record ReadResult(bool Ok, string Text, List<NextHop> NextHop);
public sealed record RecallCost(int InjectedTokens, int ReadCalls, string? Text = null);

// ---------------------------------------------------------------------------
// 1. 对象库
// ---------------------------------------------------------------------------

public sealed class ObjectStore
{
    /// <summary>单条正文 Inline 的字符上限，超出部分 Spill（原文举例“前 50 字”）。</summary>
    public int InlineCharLimit { get; }
    public readonly Dictionary<string, CtxObject> Objects = new();
    /// <summary>threadId -> Thread 对象（与 Objects 共享同一实例）。</summary>
    public readonly Dictionary<string, CtxObject> Threads = new();
    /// <summary>统计“读回完整内容”的工具往返次数。</summary>
    public int ReadCount;

    public ObjectStore(int inlineCharLimit = 50) { InlineCharLimit = inlineCharLimit; }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // -- 写入 ---------------------------------------------------------------

    public CtxObject Put(
        string kind,
        string? title = null,
        string? summary = null,
        string body = "",
        IEnumerable<string>? links = null,
        Dictionary<string, object?>? meta = null,
        string? id = null)
    {
        var oid = id ?? Ids.Next(kind == Kind.Trigger ? "T" : kind == Kind.Thread ? "H" : "O");
        var obj = new CtxObject
        {
            Id = oid,
            Kind = kind,
            Title = title,
            Summary = summary,
            Body = body,
            Meta = meta ?? new Dictionary<string, object?>(),
            At = NowMs(),
        };
        foreach (var l in links ?? Enumerable.Empty<string>()) obj.AddLink(l);
        Objects[oid] = obj;
        return obj;
    }

    /// <summary>取得或创建一条 Thread（第 3.2 节：同一 PR / 话题 / 邮件往来下的连续事件）。</summary>
    public CtxObject EnsureThread(string id, string source, string title)
    {
        if (Threads.TryGetValue(id, out var t)) return t;
        t = new CtxObject { Id = id, Kind = Kind.Thread, Source = source, Title = title, Summary = title, Body = "" };
        Threads[id] = t;
        Objects[id] = t;
        return t;
    }

    /// <summary>把一个事件挂到 Thread 上，并建立双向引用（引用图的一条边）。</summary>
    public CtxObject AppendEvent(string threadId, string triggerId)
    {
        var t = EnsureThread(threadId, "unknown", threadId);
        t.EventIds.Add(triggerId);
        if (Objects.TryGetValue(triggerId, out var ev))
        {
            ev.AddLink(threadId);
            t.AddLink(triggerId);
            // 局部性：被回复消息 / 相邻消息作为紧密关联对象
            if (ev.Meta.TryGetValue("replyTo", out var rt) && rt is string rts) Link(triggerId, rts);
            if (t.EventIds.Count >= 2) Link(triggerId, t.EventIds[^2]);
        }
        return t;
    }

    /// <summary>建立双向关联。</summary>
    public void Link(string aId, string bId)
    {
        if (Objects.TryGetValue(aId, out var a)) a.AddLink(bId);
        if (Objects.TryGetValue(bId, out var b)) b.AddLink(aId);
    }

    public CtxObject? Get(string id) => Objects.TryGetValue(id, out var o) ? o : null;

    // -- Inline：注入上下文时看到的那一小块 ---------------------------------

    /// <summary>
    /// 生成对象的“窗口内视图”：摘要 + 紧密关联对象 ID + 完整内容入口。
    /// 正文超过 InlineCharLimit 时执行 Spill，只留前缀并标注读取入口。
    /// </summary>
    public string Brief(string id)
    {
        var o = Objects.TryGetValue(id, out var obj) ? obj : null;
        if (o == null) return $"{id} <缺失对象>";
        var label = KindLabel.Of(o.Kind);
        var lines = new List<string> { $"{o.Id} {label} | {o.Summary ?? o.Title ?? ""}" };

        var body = o.Body ?? "";
        if (body.Length > InlineCharLimit)
            lines.Add($"     正文: {body.Substring(0, InlineCharLimit)}… [已 Spill，read(\"{o.Id}\") 取回完整 {body.Length} 字]");
        else if (body.Length > 0)
            lines.Add($"     正文: {body}");

        var links = o.Links.Where(x => x != o.Id).ToList();
        if (links.Count > 0)
        {
            var parts = links.Select(lid => Objects.TryGetValue(lid, out var lo) ? $"{lid}({KindLabel.Of(lo.Kind)})" : lid);
            lines.Add($"     关联: {string.Join(" ", parts)}");
        }
        if (o.Kind == Kind.Thread && o.EventIds.Count > 0)
        {
            lines.Add($"     含 {o.EventIds.Count} 条事件: {string.Join(" ", o.EventIds.TakeLast(6))}{(o.EventIds.Count > 6 ? " …" : "")}");
            if (o.SubscriberIds.Count > 0) lines.Add($"     订阅者: {string.Join(" ", o.SubscriberIds)}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Spill 的可访问入口：工具调用读回完整内容。
    /// 读回的结果同样应用 Inline / Spill，并带回下一跳关联对象的摘要与 ID。
    /// </summary>
    public ReadResult ReadFull(string id)
    {
        ReadCount += 1;
        if (!Objects.TryGetValue(id, out var o)) return new ReadResult(false, $"{id} 不存在", new List<NextHop>());
        var links = o.Links.Where(x => x != id).ToList();
        var nextHop = links
            .Select(lid => Objects.TryGetValue(lid, out var lo) ? new NextHop(lid, lo.Kind, lo.Summary ?? lo.Title ?? "") : new NextHop(lid))
            .ToList();
        var body = o.Body ?? "";
        if (o.Kind == Kind.Thread && o.EventIds.Count > 0)
        {
            body = string.Join("\n", o.EventIds.Select(eid => Objects.TryGetValue(eid, out var ev)
                ? $"- {ev.Id} [{(ev.Meta.TryGetValue("source", out var s) ? s?.ToString() ?? "" : "")}] {ev.Summary ?? ""}"
                : $"- {eid} <缺失>"));
        }
        return new ReadResult(true, body.Length > 0 ? body : "<无正文>", nextHop);
    }

    /// <summary>对照方案 A：把某条 Thread 的全部历史一次性 Inline 进上下文。</summary>
    public RecallCost FlattenWholeHistory(string threadId)
    {
        var t = Get(threadId);
        if (t == null) return new RecallCost(0, 0);
        var text = "";
        foreach (var eid in t.EventIds)
        {
            var ev = Get(eid);
            if (ev != null) text += $"{ev.Summary ?? ""}\n{ev.Body ?? ""}\n";
        }
        return new RecallCost(Tokenizer.EstimateTokens(text), 0);
    }

    /// <summary>对照方案 B：上下文里只放一个 ID，任何信息都要靠工具读回（讲义自测题 1）。</summary>
    public RecallCost IdOnlyView(string id)
    {
        var full = ReadFull(id);
        return new RecallCost(Tokenizer.EstimateTokens(id), 1, full.Text);
    }
}

// ---------------------------------------------------------------------------
// 2. 链式召回：模型按需沿引用图逐跳展开
// ---------------------------------------------------------------------------

public sealed class RecallHop
{
    public int Hop;
    public string Id = "";
    public string? Kind;
    public string Action = "";
    public int InjectedTokens;
    public string Preview = "";
    public string? Reason;
}

public sealed class RecallResult
{
    public int Hops;
    public int ReadCalls;
    public int InjectedTokens;
    public bool Aborted;
    public bool Hit;
    public string HitText = "";
    public string Path = "";
}

/// <summary>
/// 一次召回过程的可观察记录。讲义第 5 讲的对比：链式召回 vs 把所有历史一次性拼进提示词。
/// </summary>
public sealed class RecallSession
{
    private readonly ObjectStore _store;
    public int TokenBudget;
    public readonly List<RecallHop> Hops = new();
    public int InjectedTokens;
    public int ReadCalls;
    public bool Aborted;
    public string HitText = "";

    public RecallSession(ObjectStore store, int tokenBudget = 4000)
    {
        _store = store;
        TokenBudget = tokenBudget;
    }

    /// <summary>
    /// 从起点对象开始沿引用图逐跳展开，直到命中或超出预算。
    /// 关键设计：每一跳只注入 brief（摘要 + 关联 ID），只有命中的对象才 readFull 取回完整内容。
    /// </summary>
    public RecallResult Walk(string startId, Func<CtxObject?, bool>? matches = null, int maxHops = 6, Action<RecallHop>? onHop = null)
    {
        var frontier = new List<string> { startId };
        var seen = new HashSet<string>();
        for (var hop = 1; hop <= maxHops && frontier.Count > 0; hop += 1)
        {
            var next = new List<string>();
            foreach (var id in frontier)
            {
                if (seen.Contains(id)) continue;
                seen.Add(id);
                var obj = _store.Get(id);

                // 从最少信息开始：先只看摘要与关联 ID
                var brief = _store.Brief(id);
                InjectedTokens += Tokenizer.EstimateTokens(brief);
                if (InjectedTokens > TokenBudget)
                {
                    Aborted = true;
                    var abort = new RecallHop { Hop = hop, Id = id, Kind = obj?.Kind, Action = "abort", InjectedTokens = InjectedTokens, Reason = $"超出上下文预算 {TokenBudget} token" };
                    Hops.Add(abort);
                    onHop?.Invoke(abort);
                    return Result();
                }

                var hit = matches != null && matches(obj);
                var preview = "";
                if (hit)
                {
                    // 命中之后才把完整内容读回（Spill 的可访问入口）
                    var full = _store.ReadFull(id);
                    ReadCalls += 1;
                    preview = full.Text.Substring(0, Math.Min(46, full.Text.Length)).Replace("\n", " ");
                }
                var h = new RecallHop { Hop = hop, Id = id, Kind = obj?.Kind, Action = hit ? "hit" : "expand", InjectedTokens = InjectedTokens, Preview = preview };
                Hops.Add(h);
                onHop?.Invoke(h);
                if (hit)
                {
                    HitText = preview;
                    return Result();
                }

                foreach (var lid in obj?.Links ?? new List<string>()) if (!seen.Contains(lid)) next.Add(lid);
            }
            frontier = next;
        }
        return Result();
    }

    public RecallResult Result() => new()
    {
        Hops = Hops.Count,
        ReadCalls = ReadCalls,
        InjectedTokens = InjectedTokens,
        Aborted = Aborted,
        Hit = Hops.Any(h => h.Action == "hit"),
        HitText = HitText,
        Path = string.Join(" -> ", Hops.Select(h => h.Id)),
    };
}
