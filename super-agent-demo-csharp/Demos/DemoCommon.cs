// DemoCommon.cs —— 演示公共基础设施：单线程运行器、轮询等待、事件构造简写
//
// 每个演示都跑在 SingleThreadSynchronizationContext 上，复刻 Node 单线程协作式事件循环：
// 多个 Worker / Router / Watchdog 的“并发”其实是续体在同一线程上串行交错，
// 因此数值可复现、共享状态无数据竞争。

namespace SuperAgentDemo;

public static class Demo
{
    /// <summary>在单线程协作式调度上下文上运行一个演示主体，返回进程退出码。</summary>
    public static int Run(Func<Task> body)
    {
        using var ctx = new SingleThreadSynchronizationContext();
        try
        {
            ctx.Run(body);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>轮询等待某个条件成立（模拟秒超时）。</summary>
    public static async Task<bool> WaitUntil(Clock clock, Func<bool> pred, double timeoutSec = 60)
    {
        var t0 = clock.Now();
        while (clock.Now() - t0 < timeoutSec)
        {
            if (pred()) return true;
            await clock.Sleep(0.1);
        }
        return false;
    }

    /// <summary>构造一个待发布事件输入（对齐 JS 里 bus.publish({...}) 的写法）。</summary>
    public static EventInput Ev(
        string source,
        string threadId,
        string text,
        string author = "someone",
        List<string>? mentions = null,
        Dictionary<string, object?>? payload = null) =>
        new() { Source = source, ThreadId = threadId, Text = text, Author = author, Mentions = mentions, Payload = payload };

    /// <summary>把若干键值拼成 payload 字典的简写。</summary>
    public static Dictionary<string, object?> Payload(params (string Key, object? Val)[] items)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }

    public static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

    /// <summary>把一个对象列表渲染成表格行（每行取若干列）。</summary>
    public static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<char>? aligns = null)
        => SuperAgentDemo.Table.RenderTable(headers, rows, aligns);
}
