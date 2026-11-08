// 演示 08 —— 计算、状态与执行环境分离
//
// 对应讲义第 8 讲（原文第 5.1、5.2、5.3 节 + BYOM）。
//
// 三个分离：
//   5.1 计算与状态分离：Agent Loop 放进无状态弹性容器，Session 状态存“数据库”（这里用 SessionStore + 落盘文件替身）
//   5.2 计算与执行环境分离：重状态环境留在原地，端侧 daemon 执行命令并回传结果
//   5.3 间歇在线设备：设备离线 -> 任务挂起但不丢进度；设备上线 -> 云端触发在原地续跑
//
// 本演示分三部分：
//   1. 设备离线导致 Worker 挂起，进度停在具体某一步（不丢失）
//   2. 状态序列化落盘 -> “销毁”计算实例 -> 设备上线 -> 用状态在全新实例中恢复并续跑到完成
//   3. BYOM：托管环境一直在线可直接执行；个人开发机间歇在线，离线挂起、上线发布续跑事件

using System.Text.Json;

namespace SuperAgentDemo;

public static class Demo08
{
    public static Task RunAsync() => MainAsync();

    private const string CheckpointFile = "tmp/demo08/checkpoint.json";

    private sealed class SuspendResult
    {
        public BuiltSystem Sys = null!;
        public int BlockedStep;
        public string RestoredStatus = "";
        public string? PrId;
        public string File = "";
    }

    private sealed class ByomResult
    {
        public bool CloudOk;
        public int LaptopQueued;
    }

    private static async Task MainAsync()
    {
        var trace = new Trace(null, "演示 08 · 计算、状态与执行环境分离");

        var r = await PartSuspendAndRestore(trace);
        var byom = await PartByom(r.Sys, trace);

        trace.Section("全演示小结");
        trace.Block(Table.RenderTable(
            new[] { "机制", "演示结果" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "计算与执行环境分离", $"设备离线时 Worker 挂起于第 {r.BlockedStep} 步，进度不丢" },
                new List<object?> { "计算与状态分离", $"状态序列化落盘 {r.File}，实例可回收" },
                new List<object?> { "新实例恢复续跑", $"fromJSON + resume -> 状态 {r.RestoredStatus}，产出 {r.PrId ?? "-"}" },
                new List<object?> { "BYOM 多环境", $"cloud-runner 常驻在线执行成功；laptop-02 离线挂起 {byom.LaptopQueued} 条、上线后续跑" },
            }));

        Takeaways.Print(trace, new[]
        {
            "把 Agent Loop 放进无状态弹性容器、把 Session 状态存进数据库，计算实例就能被随时回收或替换而不丢进度。",
            "把计算与执行环境分离：重状态的环境（开发机、依赖、数据）留在原地，端侧 daemon 只负责执行命令并回传结果。",
            "设备离线不是任务失败，而是“挂起在这台设备的这一步”；进度停在具体 stepIndex，云端 Loop 与状态都还在。",
            "设备重新上线后，云端从检查点恢复出同一个逻辑会话（全新计算实例 + 持久化状态），从挂起的那一步续跑。",
            "BYOM 让个人开发机、托管机器、按需环境都能作为执行环境接入，间歇在线设备靠“挂起队列 + 上线事件”自动补齐。",
        });
    }

    // -----------------------------------------------------------------------
    // 第一、二部分：设备离线挂起 -> 状态持久化 -> 新实例恢复续跑
    // -----------------------------------------------------------------------

    private static async Task<SuspendResult> PartSuspendAndRestore(Trace trace)
    {
        var sys = SystemBuilder.BuildSystem(new SystemOptions
        {
            Title = "第一/二部分：设备离线挂起 -> 状态落盘 -> 销毁实例 -> 上线后在新实例续跑",
            MsPerSimSecond = 40,
            GlobalStateTokens = 6000,
            Daemons = new List<Daemon>(),
        });
        var clock = sys.Clock;
        var bus = sys.Bus;
        var router = sys.Router;
        var store = sys.Store;
        var stats = sys.Stats;
        var runner = sys.Runner;

        // 个人开发机 laptop-01 一开始处于离线（休眠）状态
        var daemon = new Daemon("laptop-01", clock, sys.Trace, online: false, bus: bus, threadId: "H-DEV");
        sys.Daemons.Add(daemon); // 必须在 publish 前：workerFactory 从 opts.Daemons 按 machine 找设备
        var sessionStore = new SessionStore(clock, sys.Trace, CheckpointFile);

        router.Start();
        await bus.PublishAsync(new EventInput
        {
            Source = Source.ImWork,
            ThreadId = "H01",
            Text = "帮我处理笔记本上未提交的改动，本地构建报错，先跑 lint 排查再提个 PR",
            Author = "user-1",
            Payload = Demo.Payload(("threadTitle", "工作群")),
        });

        var blocked = await Demo.WaitUntil(clock, () => router.Workers.Count >= 1 && router.Workers.Values.First().Status == "blocked-env", 40);
        if (!blocked || router.Workers.Count == 0)
        {
            router.Stop();
            await router.Done!;
            throw new Exception("未能观察到 Worker 因设备离线而挂起（任务可能未被分派）");
        }
        var w = router.Workers.Values.First();

        trace.Section("第一部分 · 执行环境留在原地：设备离线则任务挂起，但不丢进度");
        trace.Note($"任务「{Demo.Trunc(w.Task, 16)}」需要在 laptop-01 上执行命令，但设备离线");
        trace.Note($"Worker 挂起于第 {w.StepIndex} 步（状态 {w.Status}）：{w.BlockedReason}");
        trace.Note("关键：挂起的是“这台设备上的这一步”，云端 Loop 与已完成进度都还在，不是整个任务失败重来。");

        // --- 5.1 计算与状态分离：把状态存到“数据库”（落盘），计算实例本身无状态 ---
        trace.Section("第二部分 · 计算与状态分离：状态落盘，计算实例可被替换");
        var snap = sessionStore.Save(router, new[] { w }, store, "设备离线检查点");
        trace.Note($"检查点已写入数据库/磁盘：Worker {snap.Workers.Count} 个、对象 {snap.Objects.Count} 个，文件 {CheckpointFile}");
        var serialized = w.ToSnapshot();
        var jsonText = JsonSerializer.Serialize(serialized, Json.Compact);
        trace.Note($"Worker 状态被序列化为纯数据（{jsonText.Length} 字节）：status={serialized.Status}, stepIndex={serialized.StepIndex}, plan={serialized.Plan.Count} 步, 订阅={string.Join("/", serialized.Subscriptions)}");
        trace.Note("此刻可以认为原计算实例被弹性回收——状态不在实例里，而在数据库/这段 JSON 里。");

        // 从磁盘读回（证明状态确实脱离了计算实例）
        var reloaded = sessionStore.Load();
        trace.Note($"从磁盘读回检查点：label={reloaded?.Label}，savedAt={reloaded?.SavedAt.Fixed(1)}s，Worker[0].status={reloaded?.Workers?[0]?.Status}");

        // --- 5.3 设备重新上线：云端触发在原地续跑 ---
        var pendingBefore = daemon.Pending.Count;
        var onlineEv = daemon.GoOnline();
        trace.Note($"laptop-01 重新上线（此前挂起命令 {pendingBefore} 条）；发布上线事件 {onlineEv?.Id ?? "-"}，云端据此触发续跑");

        // 在一个全新的无状态计算实例中，用持久化状态恢复 Worker
        var deps = new WorkerOptions
        {
            Clock = clock,
            Trace = sys.Trace,
            Store = store,
            Bus = bus,
            Llm = new MockLlm(clock, $"{w.Id}*", "strong-worker", stats),
            Router = router,
            Daemon = daemon,
            Runner = runner,
        };
        var restored = Worker.FromSnapshot(JsonSerializer.Deserialize<WorkerSnapshot>(jsonText, Json.Compact)!, deps);
        trace.Note($"新的计算实例恢复出 {restored.Id}（同一个逻辑会话）：status={restored.Status}, stepIndex={restored.StepIndex} —— 计算是全新的，状态是接着上次的");
        restored.Resume("设备重新上线，云端从检查点触发续跑");
        await restored.Done!;
        trace.Note($"续跑完成：最终状态 {restored.Status}，走完 {restored.StepIndex}/{restored.Plan.Count} 步，产出 PR={restored.PrId ?? "-"}");

        router.Stop();
        await router.Done!;

        trace.Section("第一/二部分结果");
        trace.Block(Table.RenderTable(
            new[] { "阶段", "计算实例", "状态存放位置", "进度" },
            new List<IReadOnlyList<object?>>
            {
                new List<object?> { "设备离线", "原实例（挂起）", "内存中的 Worker", $"停在第 {w.StepIndex} 步" },
                new List<object?> { "检查点落盘", "原实例将被回收", $"数据库/磁盘 {CheckpointFile}", "stepIndex 已持久化" },
                new List<object?> { "设备上线续跑", "全新实例（fromJSON）", "从磁盘读回", $"续跑到第 {restored.StepIndex} 步完成" },
            }));

        return new SuspendResult { Sys = sys, BlockedStep = w.StepIndex, RestoredStatus = restored.Status, PrId = restored.PrId, File = CheckpointFile };
    }

    // -----------------------------------------------------------------------
    // 第三部分：BYOM —— 多种执行环境、间歇在线
    // -----------------------------------------------------------------------

    private static async Task<ByomResult> PartByom(BuiltSystem sys, Trace trace)
    {
        var clock = sys.Clock;
        var bus = sys.Bus;
        trace.Section("第三部分 · BYOM：托管环境常驻在线，个人设备间歇在线");

        var cloud = new Daemon("cloud-runner", clock, sys.Trace, online: true, bus: bus, threadId: "H-CLOUD");
        sys.Daemons.Add(cloud);
        var outp = await cloud.ExecAsync("npm run build", 0.4);
        trace.Note($"托管环境 cloud-runner（按需拉起、常驻在线）执行 build -> {outp}；daemon 不保存会话状态，只执行命令回传结果");

        var laptop = new Daemon("laptop-02", clock, sys.Trace, online: true, bus: bus, threadId: "H-LAP2");
        sys.Daemons.Add(laptop);
        await laptop.ExecAsync("git status", 0.3);
        laptop.GoOffline("合上盖子休眠");
        try
        {
            await laptop.ExecAsync("npm test");
        }
        catch (EnvironmentUnavailableException e)
        {
            trace.Note($"laptop-02 离线后执行命令 -> 抛出「{e.Message}」，命令进入挂起队列（{laptop.Pending.Count} 条），等设备上线");
        }
        var ev = laptop.GoOnline();
        List<string>? pending = null;
        if (ev != null && ev.Payload.TryGetValue("pending", out var pobj)) pending = pobj as List<string>;
        trace.Note($"laptop-02 重新上线 -> 发布事件 {ev?.Id}，携带挂起命令 [{(pending != null ? string.Join(", ", pending) : "-")}]，云端可据此自动续跑");
        trace.Note("BYOM：个人开发机、托管机器、按需环境都能接入；重状态环境留在原地，云端只发命令、收结果。");
        return new ByomResult { CloudOk = !string.IsNullOrEmpty(outp), LaptopQueued = pending?.Count ?? 0 };
    }
}
