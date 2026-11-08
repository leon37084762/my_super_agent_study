// Async.cs —— 单线程协作式调度上下文 + 虚拟时钟（离散事件仿真）
//
// Node.js 的示例跑在单线程事件循环上：共享可变状态只在 await 点之间被原子访问，
// 多个 Worker / Router / Watchdog 的“并发”其实是续体在同一线程上串行交错，
// 因此数值可复现、无数据竞争。C# 要忠实复刻，需要做两件事：
//
//   1) 单线程：用 SynchronizationContext 把每个 await 之后的续体 Post 回本线程串行执行，
//      而不是丢到线程池并行跑（否则会打乱交错顺序、产生数据竞争）。
//
//   2) 虚拟时钟：时间不取自墙上时间，而是“离散事件”推进。
//      Clock.Sleep(s) 不再真的等待，而是登记一个“到期时刻 = 当前虚拟时刻 + s”的定时器；
//      调度泵在“没有就绪续体”时，把虚拟时刻直接推进到最近的定时器并触发它。
//
// 为什么放弃墙上时间（Task.Delay）？因为 Windows 默认定时器粒度约 15.6ms：
// Clock.Sleep(0.05)（本意 0.05 模拟秒）会被放大成约 0.6 模拟秒，模型调用的等待同样被放大，
// 于是“批处理”“订阅直通”这类依赖时序的对比被扭曲，且每次数值都不同、还混入 JIT / 调度抖动。
// 虚拟时钟让 Sleep(s) 精确推进 s 模拟秒：输出完全可复现、跨平台，且瞬间跑完。

using System.Collections.Concurrent;

namespace SuperAgentDemo;

public sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    // 就绪续体：Post 进来、可以立刻运行的续体
    private readonly ConcurrentQueue<(SendOrPostCallback Cb, object? State)> _ready = new();

    // 虚拟定时器最小堆：优先级为 (到期时刻, 登记序)，保证同一到期时刻按登记先后 FIFO 触发
    private readonly PriorityQueue<Action, (double Due, long Seq)> _timers = new();
    private long _seq;
    private double _now;

    [ThreadStatic] private static SingleThreadSynchronizationContext? t_current;

    /// <summary>当前线程上正在驱动的虚拟调度上下文；不在泵内时为 null。</summary>
    public static SingleThreadSynchronizationContext? CurrentVirtual => t_current;

    /// <summary>当前虚拟时刻（模拟秒）。</summary>
    public double VirtualNow => _now;

    public override void Post(SendOrPostCallback d, object? state) => _ready.Enqueue((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>登记一个虚拟定时器：经过 simSeconds 模拟秒后完成所返回的 Task。</summary>
    public Task Delay(double simSeconds)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _timers.Enqueue(() => tcs.TrySetResult(), (_now + Math.Max(0, simSeconds), _seq++));
        return tcs.Task;
    }

    /// <summary>
    /// 在当前线程安装本上下文并驱动 work() 直到其完成（含它 await 掉的全部后台任务）。
    /// 泵：优先跑就绪续体；没有就绪续体时，把虚拟时刻推进到最近的定时器并触发它；
    /// 两者皆空而 root 仍未完成，说明无法再推进（死锁），抛错而非忙等。
    /// 与 Node“事件循环清空才退出”一致：调用方需保证 work() 会 await 掉它启动的后台任务
    /// （router.Done / worker.Done / watchdog.Done），并在收尾前 Stop 掉常驻循环。
    /// </summary>
    public void Run(Func<Task> work)
    {
        var prevCtx = Current;
        var prevVirtual = t_current;
        SetSynchronizationContext(this);
        t_current = this;
        try
        {
            var root = work();
            while (!root.IsCompleted)
            {
                // 1) 先跑所有就绪续体（等价 Node 的微任务/事件回调）
                if (_ready.TryDequeue(out var item)) { item.Cb(item.State); continue; }
                // 2) 没有就绪续体时，把虚拟时间推进到最近的定时器（等价 Node 的宏任务定时器）
                if (_timers.TryDequeue(out var fire, out var prio))
                {
                    if (prio.Due > _now) _now = prio.Due;
                    fire();
                    continue;
                }
                // 3) 既无就绪续体也无定时器，root 却无法完成 -> 无法推进
                throw new InvalidOperationException(
                    "调度泵无法推进：主任务未完成，但既无就绪续体也无待触发的虚拟定时器。" +
                    "常见原因：某个常驻循环未 Stop / 未被 await，或代码里误用了真实 Task.Delay、Task.Run、Thread.Sleep。");
            }
            root.GetAwaiter().GetResult(); // 观察并抛出主任务里的异常
        }
        finally
        {
            SetSynchronizationContext(prevCtx);
            t_current = prevVirtual;
        }
    }

    public void Dispose() { }
}
