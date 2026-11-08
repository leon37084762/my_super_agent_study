// Infra.cs —— 计算 / 状态 / 环境分离，以及外部 Watchdog
//
// 对应讲义：
//   第 8 讲（原文 5.1）  计算与状态分离：Agent Loop 放进无状态弹性容器，Session 状态存数据库
//   第 8 讲（原文 5.2）  计算与执行环境分离：重状态环境留在原地，端侧 daemon 执行命令并返回结果
//   第 8 讲（BYOM）      可接入个人开发机、托管机器或按需环境；支持间歇在线设备
//   第 9 讲（原文 6）    独立的 Watchdog Session，由定时消息驱动，构成外部 Control Loop

using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperAgentDemo;

// ---------------------------------------------------------------------------
// 1. SessionStore：持久化会话状态（数据库的替身）
// ---------------------------------------------------------------------------

public sealed class RouterSnapshotDto
{
    public int Replies { get; set; }
    public int Rounds { get; set; }
    public int Decisions { get; set; }
    public int CacheReadTokens { get; set; }
    public int GlobalStateTokens { get; set; }
}

public sealed class ThreadSnapshotDto
{
    public string Id { get; set; } = "";
    public string? Source { get; set; }
    public string? Title { get; set; }
    public int Events { get; set; }
    public List<string> Subscribers { get; set; } = new();
}

public sealed class ObjectsSnapshotDto
{
    public int Count { get; set; }
    public List<ThreadSnapshotDto> Threads { get; set; } = new();
}

public sealed class SessionSnapshot
{
    public int Version { get; set; } = 1;
    public double SavedAt { get; set; }
    public string Label { get; set; } = "checkpoint";
    public RouterSnapshotDto Router { get; set; } = new();
    public List<WorkerSnapshot> Workers { get; set; } = new();
    public ObjectsSnapshotDto Objects { get; set; } = new();
}

/// <summary>数据库不可用（检查点写入失败）时抛出，供演示 catch。</summary>
public sealed class StoreUnavailableException : Exception
{
    public StoreUnavailableException(string message) : base(message) { }
}

public sealed class SessionStore
{
    private readonly Clock _clock;
    private readonly Trace _trace;
    private readonly string? _file;
    public readonly List<SessionSnapshot> Snapshots = new();
    public int Failures;      // 供 Watchdog 检查“状态恢复失败”
    public bool Unavailable;

    /// <param name="file">传入路径则真正落盘，演示“状态在计算实例之外”。</param>
    public SessionStore(Clock clock, Trace trace, string? file = null)
    {
        _clock = clock;
        _trace = trace;
        _file = file;
    }

    /// <summary>把 Router 与 Worker 的会话状态序列化保存。</summary>
    public SessionSnapshot Save(Router router, IReadOnlyList<Worker> workers, ObjectStore store, string label = "checkpoint")
    {
        if (Unavailable)
        {
            Failures += 1;
            throw new StoreUnavailableException("数据库不可用，检查点写入失败");
        }
        var snapshot = new SessionSnapshot
        {
            Version = 1,
            SavedAt = _clock.Now(),
            Label = label,
            Router = new RouterSnapshotDto
            {
                Replies = router.Replies.Count,
                Rounds = router.Rounds.Count,
                Decisions = router.Rounds.Sum(r => r.BatchSize),
                CacheReadTokens = router.Rounds.Sum(r => r.CacheReadTokens),
                GlobalStateTokens = router.GlobalStateTokens,
            },
            Workers = workers.Select(w => w.ToSnapshot()).ToList(),
            Objects = new ObjectsSnapshotDto
            {
                Count = store.Objects.Count,
                Threads = store.Threads.Values.Select(t => new ThreadSnapshotDto
                {
                    Id = t.Id,
                    Source = t.Source,
                    Title = t.Title,
                    Events = t.EventIds.Count,
                    Subscribers = new List<string>(t.SubscriberIds),
                }).ToList(),
            },
        };
        Snapshots.Add(snapshot);
        if (_file != null)
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot, Json.Pretty));
        }
        _trace.Log("STORE", $"检查点「{label}」已持久化：{workers.Count} 个 Worker，{store.Objects.Count} 个对象{(_file != null ? $"，落盘 {_file}" : "")}");
        return snapshot;
    }

    public SessionSnapshot? Load()
    {
        if (_file != null && File.Exists(_file))
            return JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(_file), Json.Compact);
        return Snapshots.Count > 0 ? Snapshots[^1] : null;
    }
}

// ---------------------------------------------------------------------------
// 2. Daemon：留在原地的执行环境（BYOM）
// ---------------------------------------------------------------------------

/// <summary>执行环境（端侧设备）不可用；Worker 捕获后挂起而非丢失进度。</summary>
public sealed class EnvironmentUnavailableException : Exception
{
    public EnvironmentUnavailableException(string message) : base(message) { }
}

public sealed class DaemonExecRecord
{
    public string Cmd = "";
    public double At;
    public string Out = "";
}

public sealed class Daemon
{
    public string Id;
    public string Machine;
    private readonly Clock _clock;
    private readonly Trace _trace;
    public bool Online;
    private readonly EventBus? _bus;
    private readonly string _threadId;
    public readonly List<DaemonExecRecord> ExecLog = new();
    public List<string> Pending = new(); // 离线期间挂起、等设备上线后自动触发的命令
    public int Errors;

    /// <param name="machine">设备标识，例如 'laptop-01'（个人开发机）/ 'cloud-runner'。</param>
    /// <param name="online">间歇在线设备可初始为离线。</param>
    public Daemon(string machine, Clock clock, Trace trace, string? id = null, bool online = true, EventBus? bus = null, string threadId = "H-DEV")
    {
        Id = id ?? $"D-{machine}";
        Machine = machine;
        _clock = clock;
        _trace = trace;
        Online = online;
        _bus = bus;
        _threadId = threadId;
    }

    /// <summary>daemon 不保存会话状态，只执行命令并返回结果（第 8 讲 5.1）。</summary>
    public async Task<string> ExecAsync(string cmd, double simSeconds = 0.6, bool allowQueue = true)
    {
        if (!Online)
        {
            Errors += 1;
            if (allowQueue) Pending.Add(cmd);
            throw new EnvironmentUnavailableException($"设备 {Machine} 离线，无法执行「{cmd}」");
        }
        await _clock.Sleep(simSeconds);
        var outp = $"ok({cmd.Split(' ')[0]})";
        ExecLog.Add(new DaemonExecRecord { Cmd = cmd, At = _clock.Now(), Out = outp });
        _trace.Log("DAEMON", $"[{Machine}] 执行「{cmd}」-> {outp}");
        return outp;
    }

    public void GoOffline(string reason = "设备休眠")
    {
        Online = false;
        _trace.Log("DAEMON", $"[{Machine}] 离线（{reason}）；云端 Loop 与状态不受影响，但该环境上的命令无法执行");
    }

    /// <summary>第 8 讲 5.3：设备重新上线后，由云端 Agent 自动触发需要在该设备上完成的任务。</summary>
    public Trigger? GoOnline()
    {
        Online = true;
        _trace.Log("DAEMON", $"[{Machine}] 重新上线，挂起命令 {Pending.Count} 条");
        if (_bus != null && Pending.Count > 0)
        {
            var ev = Trigger.Make(
                Source.Internal,
                _threadId,
                $"设备 {Machine} 已上线，可继续执行 {Pending.Count} 条挂起命令",
                "system",
                payload: new Dictionary<string, object?> { ["kind"] = "device-online", ["machine"] = Machine, ["pending"] = new List<string>(Pending) },
                clock: _clock);
            Pending = new List<string>();
            _ = _bus.PublishAsync(ev); // 与 JS 一致：goOnline 是同步方法，publish 的续体回到单线程上下文
            return ev;
        }
        return null;
    }
}

// ---------------------------------------------------------------------------
// 3. Watchdog：主系统之外的独立 Control Loop
// ---------------------------------------------------------------------------

public sealed record TickItem(string Id, Evidence Ev, ProgressVerdict Verdict, Worker Worker);

public sealed class WatchdogCheckResult
{
    public string Id = "";
    public string Verdict = "";
    public string Reason = "";
}

public sealed class WatchdogCheck
{
    public int Round;
    public double At;
    public int InfraErrors;
    public List<WatchdogCheckResult> Results = new();
}

public sealed class WatchdogIntervention
{
    public string WorkerId = "";
    public string Verdict = "";
    public string Action = "";
    public double At;
}

public sealed class AskSnapshot
{
    public int Checks { get; set; }
    public List<string> OpenIssues { get; set; } = new();
    public List<string> Interventions { get; set; } = new();
    public List<string> OfflineDaemons { get; set; } = new();
}

public sealed record AskResult(string Question, AskSnapshot Snapshot, string Answer);

public sealed class WatchdogSummary
{
    public int Rounds;
    public Dictionary<string, int> ByVerdict = new();
    public int Interventions;
    public int OpenIssues;
}

public sealed class Watchdog
{
    private readonly Clock _clock;
    private readonly Trace _trace;
    private readonly MockLlm _llm; // 独立的 Watchdog Session，自带工具能力
    private readonly Router _router;
    private readonly List<Daemon> _daemons;
    private readonly SessionStore? _store;
    private readonly double _intervalSimSec;
    private readonly int _maxRounds;

    public bool Running;
    public readonly List<WatchdogCheck> Checks = new();
    public readonly List<WatchdogIntervention> Interventions = new();
    /// <summary>workerId -&gt; 上一次结论，用于“持续复查直到恢复正常”。</summary>
    public readonly Dictionary<string, ProgressVerdict> OpenIssues = new();
    public Task? Done;

    /// <param name="intervalSimSec">定时消息周期（原文未给出固定检查周期，这里是教学设定）。</param>
    public Watchdog(
        Clock clock,
        Trace trace,
        MockLlm llm,
        Router router,
        EventBus? bus = null,
        SessionStore? store = null,
        List<Daemon>? daemons = null,
        double intervalSimSec = 6,
        int maxRounds = 40)
    {
        _clock = clock;
        _trace = trace;
        _llm = llm;
        _router = router;
        _store = store;
        _daemons = daemons ?? new List<Daemon>();
        _intervalSimSec = intervalSimSec;
        _maxRounds = maxRounds;
    }

    /// <summary>启动定时驱动的外部 Control Loop（不阻塞）。需要等待退出时用 await watchdog.Done。</summary>
    public Watchdog Start()
    {
        if (Done != null) return this;
        Running = true;
        _trace.Log("WATCHDOG", $"独立 Session 启动，每 {_intervalSimSec.ToString("0.##", Fmt.Inv)}s 由定时消息驱动（外部 Control Loop）");
        Done = LoopAsync();
        return this;
    }

    public void Stop() => Running = false;

    private async Task LoopAsync()
    {
        var round = 0;
        while (Running && round < _maxRounds)
        {
            await _clock.Sleep(_intervalSimSec);
            round += 1;
            await TickAsync(round);
            if (_router.Workers.Count > 0 && _router.Workers.Values.All(w => w.Status == "closed") && OpenIssues.Count == 0)
                Running = false;
        }
    }

    private async Task TickAsync(int round)
    {
        var workers = _router.Workers.Values.ToList();
        // 基础设施异常 = 离线设备数 + 状态存储写入失败数（防御性兜底避免 NaN）
        var infraErrors = _daemons.Count(d => !d.Online) + (_store?.Failures ?? 0);

        var evidenceList = workers.Select(w => (w.Id, Ev: w.ProgressEvidence())).ToList();
        var promptTokens = 1800 + evidenceList.Count * 260;

        var call = await _llm.CallAsync(
            promptTokens,
            40 + evidenceList.Count * 30,
            () => evidenceList.Select(e => new TickItem(e.Id, e.Ev, Progress.JudgeProgress(e.Ev), workers.First(w => w.Id == e.Id))).ToList());

        Checks.Add(new WatchdogCheck
        {
            Round = round,
            At = _clock.Now(),
            InfraErrors = infraErrors,
            Results = call.Result.Select(r => new WatchdogCheckResult { Id = r.Id, Verdict = r.Verdict.Verdict, Reason = r.Verdict.Reason }).ToList(),
        });
        _trace.Log("WATCHDOG", $"第 {round} 轮检查：基础设施异常 {infraErrors}，Worker {workers.Count} 个");

        foreach (var r in call.Result)
        {
            var hasPrev = OpenIssues.TryGetValue(r.Id, out var prev);
            if (r.Verdict.Verdict == "healthy" || r.Verdict.Verdict == "watch")
            {
                if (hasPrev)
                {
                    _trace.Log("WATCHDOG", $"  {r.Id} 已恢复正常（此前：{prev!.Verdict}）—— 复查通过，解除关注");
                    OpenIssues.Remove(r.Id);
                }
                else
                {
                    _trace.Log("WATCHDOG", $"  {r.Id} {(r.Verdict.Verdict == "watch" ? "观察中" : "正常")}：{r.Verdict.Reason}");
                }
                continue;
            }
            OpenIssues[r.Id] = r.Verdict;
            _trace.Log("WATCHDOG", $"  ! {r.Id} 异常[{r.Verdict.Verdict}]：{r.Verdict.Reason}");
            Intervene(r);
        }

        if (infraErrors > 0 && workers.Count == 0)
            _trace.Log("WATCHDOG", $"  ! 基础设施异常 {infraErrors}（设备离线/状态写入失败），持续复查直到恢复");
    }

    /// <summary>
    /// 介入修复。第 9 讲：Watchdog 本身也是有完整工具能力的 Agent Session，
    /// 可以诊断并推动恢复，而不只是发出告警。
    /// </summary>
    private void Intervene(TickItem r)
    {
        var worker = r.Worker;
        var action = r.Verdict.Verdict == "infra"
            ? "retry-later"
            : r.Verdict.Verdict == "drift"
                ? "inject-constraint"
                : "inject-hint";

        var text = action == "inject-constraint"
            ? "请立即回到任务范围，停止无关重构（Watchdog 介入）"
            : action == "inject-hint"
                ? "检测到重复调用且无新证据，请更换假设或上报阻塞（Watchdog 介入）"
                : "基础设施暂不可用，先挂起等待环境恢复（Watchdog 介入）";

        var ev = Trigger.Make(
            Source.Internal,
            worker.Subscriptions.Count > 0 ? worker.Subscriptions[0] : "H-WD",
            text,
            "watchdog",
            payload: new Dictionary<string, object?> { ["kind"] = "watchdog", ["action"] = action },
            clock: _clock);
        Interventions.Add(new WatchdogIntervention { WorkerId = r.Id, Verdict = r.Verdict.Verdict, Action = action, At = _clock.Now() });
        _trace.Log("WATCHDOG", $"    介入 {r.Id}：{action} -> 注入事件 {ev.Id}");
        // 注入到 Worker 收件箱：这是“在线修正主系统行为”的一种最小形式
        worker.Receive(ev, "watchdog");
        if (action == "inject-constraint") worker.Constraints.Add(text);
    }

    /// <summary>维护者用自然语言询问故障原因（第 9 讲第六节）。</summary>
    public async Task<AskResult> AskAsync(string question)
    {
        var snapshot = new AskSnapshot
        {
            Checks = Checks.Count,
            OpenIssues = OpenIssues.Select(kv => $"{kv.Key}:{kv.Value.Verdict}").ToList(),
            Interventions = Interventions.Select(i => $"{i.WorkerId}/{i.Action}").ToList(),
            OfflineDaemons = _daemons.Where(d => !d.Online).Select(d => d.Machine).ToList(),
        };
        var call = await _llm.CallAsync(
            900 + JsonSerializer.Serialize(snapshot, Json.Compact).Length,
            80,
            () => new AskResult(question, snapshot, Answer(question, snapshot)));
        _trace.Log("WATCHDOG", $"维护者提问：{question}");
        _trace.Log("WATCHDOG", $"诊断回答：{call.Result.Answer}");
        return call.Result;
    }

    private static string Answer(string question, AskSnapshot snapshot)
    {
        if (snapshot.OfflineDaemons.Count > 0 && Regex.IsMatch(question, "为什么|原因|故障"))
            return $"当前 {string.Join("、", snapshot.OfflineDaemons)} 离线，依赖该环境的命令挂起；云端 Loop 与状态仍可用，设备上线后会自动触发。";
        if (snapshot.OpenIssues.Count > 0)
            return $"仍有 {snapshot.OpenIssues.Count} 个未恢复问题：{string.Join("；", snapshot.OpenIssues)}。已介入 {snapshot.Interventions.Count} 次。";
        if (Regex.IsMatch(question, "进展|停滞"))
            return $"已执行 {snapshot.Checks} 轮检查，未发现停滞或目标偏离。";
        return $"已完成 {snapshot.Checks} 轮检查，介入 {snapshot.Interventions.Count} 次，当前无未恢复问题。";
    }

    public WatchdogSummary Summary()
    {
        var byVerdict = new Dictionary<string, int>();
        foreach (var c in Checks)
            foreach (var r in c.Results)
                byVerdict[r.Verdict] = (byVerdict.TryGetValue(r.Verdict, out var n) ? n : 0) + 1;
        return new WatchdogSummary { Rounds = Checks.Count, ByVerdict = byVerdict, Interventions = Interventions.Count, OpenIssues = OpenIssues.Count };
    }
}
