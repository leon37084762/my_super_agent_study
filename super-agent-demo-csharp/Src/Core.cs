// Core.cs —— 通用基础设施：事件模型、模拟时钟、可读日志
//
// 对应讲义：
//   第 3 讲   Trigger（环境事件）与三个阶段的区分
//   第 3.2 节 Thread（把相关事件组织成一条流）
//   第 5 讲   为 Trigger / Thread / Worker Session 赋予唯一 ID，构成引用图
//   第 10 讲  合成负载：消息间隔与工作时长可按比例缩放
//
// 教学说明：这里的一切都是“教学替身”。MockLlm 不是真实模型，
// 事件源不是真实 IM / GitHub 接入，目的是把设计机制变得可观察、可测量。

using System.Globalization;
using System.Text;

namespace SuperAgentDemo;

/// <summary>全局格式化约定：一律用不变文化，避免小数点/千分位随区域变化。</summary>
public static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string F(this double v, string format) => v.ToString(format, Inv);

    /// <summary>等价于 JS 的 Number.toFixed(n)。</summary>
    public static string Fixed(this double v, int digits) => v.ToString("0." + new string('0', digits), Inv);
}

// ---------------------------------------------------------------------------
// 1. 事件来源与程序层策略
// ---------------------------------------------------------------------------

/// <summary>原文第 3 节列举的事件来源（Trigger），加上系统内部事件。</summary>
public static class Source
{
    public const string ImWork = "im:work";     // 工作群 A：允许观察问题反馈
    public const string ImSocial = "im:social"; // 闲聊群：不在处理范围
    public const string ImNotify = "im:notify"; // 通知群 B：所有事件直接忽略
    public const string GhPr = "github:pr";     // PR 上的提交与评审意见
    public const string GhCi = "github:ci";     // CI 运行结果
    public const string Email = "email";        // 邮件（会议邀请等）
    public const string Schedule = "schedule";  // 日程提醒
    public const string Internal = "internal";  // 系统内部：工具完成事件、Watchdog 定时消息
}

/// <summary>原文第 3.1 节：程序层策略可按来源设置为“仅响应明确请求”“作为候选事件”或“直接忽略”。</summary>
public static class Policy
{
    public const string Ignore = "ignore";
    public const string ExplicitOnly = "explicit_only";
    public const string Observe = "observe";
}

public static class PolicyLabel
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        [Policy.Ignore] = "直接忽略",
        [Policy.ExplicitOnly] = "仅响应明确请求",
        [Policy.Observe] = "作为候选事件",
    };
}

/// <summary>一份示例配置：教学设定，不是原文给出的规则表。</summary>
public static class DefaultSourcePolicy
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        [Source.ImWork] = Policy.Observe,
        [Source.ImSocial] = Policy.Ignore,
        [Source.ImNotify] = Policy.Ignore,
        [Source.GhPr] = Policy.Observe,
        [Source.GhCi] = Policy.Observe,
        [Source.Email] = Policy.ExplicitOnly,
        [Source.Schedule] = Policy.Observe,
        [Source.Internal] = Policy.Observe,
    };
}

// ---------------------------------------------------------------------------
// 2. 唯一 ID：Trigger(T) / Thread(H) / Worker(W) / PR(P) / 评论(C) / 日志(L)
// ---------------------------------------------------------------------------

public static class Ids
{
    private static readonly Dictionary<string, int> Counters = new();
    private static readonly object Gate = new();

    /// <summary>生成讲义风格的教学 ID：T01、H02、W03……</summary>
    public static string Next(string prefix)
    {
        lock (Gate)
        {
            Counters.TryGetValue(prefix, out var n);
            n += 1;
            Counters[prefix] = n;
            return prefix + (n < 10 ? "0" + n : n.ToString(Inv()));
        }
    }

    public static void Reset()
    {
        lock (Gate) Counters.Clear();
    }

    private static CultureInfo Inv() => CultureInfo.InvariantCulture;
}

// ---------------------------------------------------------------------------
// 3. 模拟时钟
// ---------------------------------------------------------------------------

/// <summary>
/// 演示用的时间轴：真实墙上时间按比例换算成“模拟秒”。
/// 第 10 讲原文注明消息间隔与工作时长按 1/4 缩放、模型调用耗时为真实值；
/// 这里为了几秒内跑完，默认 1 模拟秒 = 80 毫秒，并允许每个 demo 单独调整。
/// 时钟读数由墙上时间推导，因此并发的多个任务看到的时刻是一致的。
/// </summary>
public sealed class Clock
{
    public double K { get; }
    private readonly long _t0;
    // 虚拟时钟下的“纪元”：本 Clock 创建时的虚拟时刻，使 Now() 从 0 起算。
    // 等价 Node 每个 Clock 各自持有 t0：一个 demo 里多次 buildSystem（如演示 03 的三遍），
    // 每遍的时间戳都从 0 重新开始，而不是累加上一遍的虚拟时刻。
    private readonly double _epoch;

    public Clock(double msPerSimSecond = 80)
    {
        K = msPerSimSecond;
        _t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _epoch = SingleThreadSynchronizationContext.CurrentVirtual?.VirtualNow ?? 0;
    }

    private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>当前模拟时间（秒）。优先取虚拟调度上下文推进的离散事件时刻（减去本 Clock 的纪元）；
    /// 不在演示调度器内时回退到墙上时间（便于单独构造/测试）。</summary>
    public double Now()
    {
        var vc = SingleThreadSynchronizationContext.CurrentVirtual;
        return vc != null ? vc.VirtualNow - _epoch : (NowMs() - _t0) / K;
    }

    /// <summary>睡眠若干模拟秒。在虚拟调度器内登记一个到期定时器（精确推进 s 模拟秒）；
    /// 否则回退到真实 Task.Delay（按 K 换算成毫秒）。</summary>
    public Task Sleep(double simSeconds)
    {
        var vc = SingleThreadSynchronizationContext.CurrentVirtual;
        return vc != null ? vc.Delay(simSeconds) : Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, simSeconds * K)));
    }

    /// <summary>日志时间戳。</summary>
    public string Stamp() => "+" + Now().Fixed(1).PadLeft(5) + "s";
}

// ---------------------------------------------------------------------------
// 4. Trigger：一个环境事件
// ---------------------------------------------------------------------------

public sealed class Trigger
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Author { get; set; } = "someone";
    public List<string> Mentions { get; set; } = new();
    public Dictionary<string, object?> Payload { get; set; } = new();
    public double At { get; set; }

    public static Trigger Make(
        string source,
        string threadId,
        string text,
        string author = "someone",
        List<string>? mentions = null,
        Dictionary<string, object?>? payload = null,
        Clock? clock = null) => new()
        {
            Id = Ids.Next("T"),
            Source = source,
            ThreadId = threadId,
            Text = text,
            Author = author,
            Mentions = mentions ?? new List<string>(),
            Payload = payload ?? new Dictionary<string, object?>(),
            At = clock?.Now() ?? 0,
        };
}

// ---------------------------------------------------------------------------
// 5. 中日韩字符宽度与表格渲染（让控制台输出对齐）
// ---------------------------------------------------------------------------

public static class Table
{
    private static bool IsWide(char c) =>
        (c >= '\u1100' && c <= '\u115F') ||
        (c >= '\u2E80' && c <= '\uA4CF') ||
        (c >= '\uA960' && c <= '\uA97F') ||
        (c >= '\uAC00' && c <= '\uD7A3') ||
        (c >= '\uF900' && c <= '\uFAFF') ||
        (c >= '\uFE10' && c <= '\uFE19') ||
        (c >= '\uFE30' && c <= '\uFE6F') ||
        (c >= '\uFF00' && c <= '\uFF60') ||
        (c >= '\uFFE0' && c <= '\uFFE6');

    public static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var ch in s) w += IsWide(ch) ? 2 : 1;
        return w;
    }

    public static string PadEnd(string s, int width)
    {
        var d = width - DisplayWidth(s);
        return d > 0 ? s + new string(' ', d) : s;
    }

    public static string PadStart(string s, int width)
    {
        var d = width - DisplayWidth(s);
        return d > 0 ? new string(' ', d) + s : s;
    }

    private static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString(Inv2()),
        float f => f.ToString(Inv2()),
        _ => v.ToString() ?? "",
    };

    private static CultureInfo Inv2() => CultureInfo.InvariantCulture;

    /// <summary>渲染一张 ASCII 表格。aligns 每列 'l' 或 'r'，默认左对齐。</summary>
    public static string RenderTable(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        IReadOnlyList<char>? aligns = null)
    {
        aligns ??= Array.Empty<char>();
        var cells = rows
            .Select(r => Enumerable.Range(0, headers.Count)
                .Select(i => i < r.Count ? Cell(r[i]) : "")
                .ToList())
            .ToList();

        var widths = Enumerable.Range(0, headers.Count).Select(i =>
        {
            var m = Math.Max(DisplayWidth(headers[i]), 1);
            foreach (var row in cells) m = Math.Max(m, DisplayWidth(row[i]));
            return m;
        }).ToList();

        string Line(IReadOnlyList<string> row)
        {
            var sb = new StringBuilder("| ");
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(i < aligns.Count && aligns[i] == 'r' ? PadStart(row[i], widths[i]) : PadEnd(row[i], widths[i]));
            }
            sb.Append(" |");
            return sb.ToString();
        }

        var sep = "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        var outLines = new List<string> { sep, Line(headers), sep };
        outLines.AddRange(cells.Select(Line));
        outLines.Add(sep);
        return string.Join("\n", outLines);
    }
}

// ---------------------------------------------------------------------------
// 6. Trace：把系统内部动作打印成可读时间线
// ---------------------------------------------------------------------------

public sealed class Trace
{
    private readonly Clock? _clock;
    private readonly List<string> _lines = new();

    public Trace(Clock? clock = null, string title = "")
    {
        _clock = clock;
        if (!string.IsNullOrEmpty(title)) Section(title);
    }

    /// <summary>分节标题。</summary>
    public void Section(string text)
    {
        Raw("");
        Raw($"=== {text} ===");
    }

    /// <summary>教学旁白：解释正在演示的机制，不属于系统输出。</summary>
    public void Note(string text) => Raw($"  # {text}");

    /// <summary>系统动作日志。</summary>
    public void Log(string actor, string text)
    {
        var stamp = _clock != null ? _clock.Stamp() : "        ";
        Raw($"[{stamp}] {Table.PadEnd(actor, 9)}| {text}");
    }

    /// <summary>多行块（表格、上下文快照等）。</summary>
    public void Block(string text)
    {
        foreach (var l in text.Split('\n')) Raw("           " + l);
    }

    private void Raw(string line)
    {
        _lines.Add(line);
        Console.WriteLine(line);
    }

    public string Dump() => string.Join("\n", _lines);
}

/// <summary>demo 结束时打印结论要点。</summary>
public static class Takeaways
{
    public static void Print(Trace trace, IReadOnlyList<string> items)
    {
        trace.Section("本讲要点");
        for (var i = 0; i < items.Count; i++) trace.Note($"{i + 1}. {items[i]}");
    }
}
