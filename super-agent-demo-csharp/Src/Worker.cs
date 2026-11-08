// Worker.cs —— Worker 层：隔离上下文执行、异步工具、推测执行
//
// 对应讲义：
//   第 2 讲（原文 2.2）  Worker 上下文彼此隔离、挂载完整执行工具、向 Router 汇报状态，
//                        不直接对外回复用户；同层 Worker 通过订阅 Thread 发现彼此
//   第 4 讲（原文 3.2）  已订阅事件直通到达；超出任务范围可取消订阅或交回 Router
//   第 6 讲（原文 3.4）  推测执行：先在后台尝试，有价值才交付，无价值则安静丢弃
//   第 7 讲（原文 4.1）  异步 ReAct Loop：start() 立即回执、完成后作为新事件回投、可 stop()；
//                        新输入在“当前推理步结束后”处理，不能随时打断一次正在进行的推理

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuperAgentDemo;

/// <summary>共享的 JSON 序列化选项：camelCase + 忽略 null + 不转义非 ASCII（完全贴近 JS 的 JSON.stringify）。</summary>
public static class Json
{
    // JS 的 JSON.stringify 会把中文原样输出，而 System.Text.Json 默认转义成 \uXXXX。
    // 序列化结果会被 EstimateTokens / .Length 计入 Token，故必须用 UnsafeRelaxedJsonEscaping 对齐，
    // 否则一个中文字会被当成 6 个 ASCII 字符计数，系统性抬高上下文 Token。
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Worker 计划里的一步（教学替身：真实 Worker 由模型自行规划）。</summary>
public sealed class Step
{
    public string Tool { get; set; } = "";
    public string? Target { get; set; }
    public string? Cmd { get; set; }
    public double? Sim { get; set; }
    public string? Note { get; set; }
    public string? Tag { get; set; }
    public string? Evidence { get; set; }
    public string? Machine { get; set; }
    public bool? Spilled { get; set; }
    public bool? Reproduced { get; set; }
    public bool? Verified { get; set; }
    public double? Confidence { get; set; }
    public string? ThreadId { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Verification { get; set; }
    public string? Outcome { get; set; }
    public string? Output { get; set; }
    public bool? AsyncTools { get; set; }

    [JsonIgnore]
    public Regex? CancelOn { get; set; }
}

public sealed class Findings
{
    public bool? Reproduced { get; set; }
    public bool? Verified { get; set; }
    public double? Confidence { get; set; }
}

public sealed record WorkerResult(string WorkerId, string Task, string? PrId, bool? Reproduced, bool? Verified, double? Confidence, int ToolCalls, int ContextTokens);

// ---------------------------------------------------------------------------
// 1. 异步工具运行器：start() / stop()
// ---------------------------------------------------------------------------

public sealed class ToolHandle
{
    public string Id = "";
    public string Name = "";
    public string Cmd = "";
    public string Status = "started"; // started -> running -> succeeded | failed | stopped
    public double StartedAt;
    public double SimSeconds;
    public string Outcome = "succeeded";
    public string Output = "";
    public double? FinishedAt;
}

public sealed record ToolStatus(string Status, double Elapsed);
public sealed record StopResult(bool Ok, string Reason);

public sealed class AsyncToolRunner
{
    private readonly Clock _clock;
    private readonly Trace _trace;
    public readonly Dictionary<string, ToolHandle> Handles = new();

    public AsyncToolRunner(Clock clock, Trace trace) { _clock = clock; _trace = trace; }

    /// <summary>
    /// 启动一个耗时命令，立即返回 Started 回执。
    /// 注意（第 7 讲自测题 1）：Started 只说明启动成功，不代表任务完成或成功。
    /// </summary>
    public ToolHandle Start(string name, string cmd, double simSeconds, string outcome = "succeeded", string output = "", Action<ToolHandle>? onDone = null)
    {
        var handle = new ToolHandle
        {
            Id = Ids.Next("J"),
            Name = name,
            Cmd = cmd,
            Status = "started",
            StartedAt = _clock.Now(),
            SimSeconds = simSeconds,
            Outcome = outcome,
            Output = output,
        };
        Handles[handle.Id] = handle;
        handle.Status = "running";

        // 后台执行：不占用调用方的推理循环（fire-and-forget，续体仍回到单线程上下文）
        _ = RunAsync(handle, onDone);
        return handle;
    }

    private async Task RunAsync(ToolHandle handle, Action<ToolHandle>? onDone)
    {
        await _clock.Sleep(handle.SimSeconds);
        if (handle.Status != "running") return; // 已被 stop()
        handle.Status = handle.Outcome;
        handle.FinishedAt = _clock.Now();
        onDone?.Invoke(handle);
    }

    /// <summary>停止一个仍在运行的后台命令。</summary>
    public StopResult Stop(string id)
    {
        if (!Handles.TryGetValue(id, out var h)) return new StopResult(false, "句柄不存在");
        if (h.Status != "running") return new StopResult(false, $"已结束（{h.Status}），无法停止");
        h.Status = "stopped";
        h.FinishedAt = _clock.Now();
        return new StopResult(true, $"已停止 {h.Cmd}");
    }

    public ToolStatus? Status(string id) =>
        Handles.TryGetValue(id, out var h) ? new ToolStatus(h.Status, _clock.Now() - h.StartedAt) : null;
}

// ---------------------------------------------------------------------------
// 2. 推测执行的三道闸门（第 6 讲第五节）
// ---------------------------------------------------------------------------

public sealed record GateResult(bool Go, string Reason);

/// <summary>讲义强调这是三个不同的问题，不能混为一谈：是否后台尝试 / 是否继续投入 / 是否交付（打断用户）。</summary>
public static class SpeculationGate
{
    /// <summary>线索是否值得先在后台试一把？关注副作用范围与预算。</summary>
    public static GateResult ShouldAttempt(double clueStrength = 0, string sideEffect = "read-only", double budgetLeft = 1)
    {
        if (budgetLeft <= 0) return new GateResult(false, "推测预算已用尽");
        if (clueStrength <= 0) return new GateResult(false, "没有可用线索");
        if (sideEffect == "destructive") return new GateResult(false, "涉及不可撤销的外部影响，须先取得授权");
        return new GateResult(true, $"线索强度 {clueStrength.ToString("0.##", Fmt.Inv)}，副作用范围 {sideEffect}，可在后台尝试");
    }

    /// <summary>是否继续投入？看是否有新证据。</summary>
    public static GateResult ShouldContinue(int newEvidence = 0, int attempts = 0, int maxAttempts = 3)
    {
        if (newEvidence > 0) return new GateResult(true, "出现新证据，值得继续");
        if (attempts >= maxAttempts) return new GateResult(false, $"已尝试 {attempts} 次且无新证据，停止投入");
        return new GateResult(true, "尚未达到尝试上限");
    }

    /// <summary>是否交付给用户？只有经过验证的结果才呈现，否则保持安静。</summary>
    public static GateResult ShouldDeliver(bool reproduced = false, bool verified = false, double confidence = 0, bool userAsked = false)
    {
        if (userAsked) return new GateResult(true, "用户主动询问，即使结果不确定也应说明当前状态");
        if (reproduced && verified && confidence >= 0.7) return new GateResult(true, "已复现且验证通过，可交付");
        if (!reproduced) return new GateResult(false, "未能复现，不把猜测写成结论 —— 保持安静");
        return new GateResult(false, "结果未经验证 —— 保持安静，避免制造注意力负担");
    }
}

// ---------------------------------------------------------------------------
// 3. Worker
// ---------------------------------------------------------------------------

public sealed record InboxItem(Trigger Trigger, string How, double At, bool MidStep);

public sealed class ToolRecord
{
    public double At;
    public string Fingerprint = "";
    public string? Evidence;
    public bool Repeated;
    public bool IsNewEvidence;
}

/// <summary>创建 Worker 所需的外部依赖（用于 fromSnapshot 复用）。</summary>
public sealed class WorkerOptions
{
    public string? Id;
    public string Task = "";
    public string? Title;
    public Clock Clock = null!;
    public Trace Trace = null!;
    public ObjectStore Store = null!;
    public EventBus? Bus;
    public MockLlm Llm = null!;
    public Router? Router;
    public Daemon? Daemon;
    public AsyncToolRunner? Runner;
    public List<Step> Plan = new();
    public List<string> Scope = new();
    public bool Speculative;
    public bool AsyncTools = true;
    public string? OriginThread;
}

/// <summary>持久化用的 Worker 状态快照（可 JSON 序列化）。</summary>
public sealed class WorkerSnapshot
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int StepIndex { get; set; }
    public List<Step> Plan { get; set; } = new();
    public List<string> Scope { get; set; } = new();
    public List<string> Subscriptions { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public List<string> Evidence { get; set; } = new();
    // JS 的 toJSON 用 `findings ?? null` / `prId ?? null` 强制把这两个字段写成 null（而非省略），
    // 故这里用 Condition=Never 覆盖全局的 WhenWritingNull，保证序列化字节数与 JSON.stringify(toJSON()) 完全一致。
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Findings? Findings { get; set; }
    public int ContextTokens { get; set; }
    public int ToolCalls { get; set; }
    public bool Speculative { get; set; }
    public bool AsyncTools { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrId { get; set; }
}

public sealed class Worker
{
    private static readonly string[] ConstraintWords = { "先不要", "暂缓", "别改", "不要动" };
    private static readonly string[] ScopeKeywords = { "登录", "超时", "导出", "渲染", "波浪号", "性能", "卡顿" };

    public string Id;
    public string Task;
    public string Title;
    public readonly Clock Clock;
    public readonly Trace Trace;
    public readonly ObjectStore Store;
    public readonly EventBus? Bus;
    public readonly MockLlm Llm;
    public readonly Router? Router;
    public readonly Daemon? Daemon;
    public readonly AsyncToolRunner Runner;
    public List<Step> Plan;
    public List<string> Scope;
    public bool Speculative;
    public bool AsyncTools;

    public string Status = "idle"; // idle | running | waiting-tool | closed | blocked-env
    public int StepIndex;
    public readonly List<InboxItem> Inbox = new();
    public List<string> Subscriptions = new();
    public List<string> Constraints = new();
    public readonly List<ToolRecord> ToolLog = new();
    public List<string> EvidenceKeys = new();
    public int ScopeViolations;
    public int InfraErrors;
    public int BlockedInputs;
    public double BlockedSeconds;
    public int ObservedCount;
    public string LastReport = "";
    public WorkerResult? Result;
    public int ContextTokens = 400; // 隔离上下文：只含本任务信息
    public Task? Done;
    public Findings? FindingsState;
    public string? PrId;
    public string? PrThread;
    public string? BlockedReason;
    public Step? PendingDeviceStep;
    public double DispatchedAt;
    /// <summary>由 SystemBuilder 的 workerFactory 回填：本任务的源事件 Id（供 demo 10 统计端到端时延）。</summary>
    public string? OriginTriggerId;

    public Worker(WorkerOptions o)
    {
        Id = o.Id ?? Ids.Next("W");
        Task = o.Task;
        Title = o.Title ?? Trunc(o.Task, 12);
        Clock = o.Clock;
        Trace = o.Trace;
        Store = o.Store;
        Bus = o.Bus;
        Llm = o.Llm;
        Router = o.Router;
        Daemon = o.Daemon;
        Runner = o.Runner ?? new AsyncToolRunner(o.Clock, o.Trace);
        Plan = o.Plan;
        Scope = o.Scope;
        Speculative = o.Speculative;
        AsyncTools = o.AsyncTools;
        if (o.OriginThread != null) AddSub(o.OriginThread);
    }

    private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
    private void AddSub(string id) { if (!Subscriptions.Contains(id)) Subscriptions.Add(id); }
    private void AddEvidence(string k) { if (!EvidenceKeys.Contains(k)) EvidenceKeys.Add(k); }
    private bool HasEvidence(string k) => EvidenceKeys.Contains(k);
    private static string? PayloadStr(Trigger t, string key) => t.Payload.TryGetValue(key, out var v) ? v?.ToString() : null;

    // -- 事件入口 -----------------------------------------------------------

    /// <summary>收到事件（直通 / Router 分派 / 内部工具完成）。</summary>
    public void Receive(Trigger trigger, string how = "direct")
    {
        // 分派事件是任务的起点，不算“推理步中途插入”
        var midStep = how != "dispatch" && Status == "running";
        Inbox.Add(new InboxItem(trigger, how, Clock.Now(), midStep));
        if (how == "direct" && Speculative) Trace.Log(Id, $"直通收到 {trigger.Id}（推测模式：先不打扰用户）");
    }

    /// <summary>Router 在直通时观察到事件后同步给 Worker 的全局提示。</summary>
    public void NoteObserved(Trigger trigger) => ObservedCount += 1;

    // -- 主循环 -------------------------------------------------------------

    /// <summary>启动 Worker 自己的 ReAct 循环（不阻塞）。需要等待结束时用 await worker.Done。</summary>
    public Worker Start()
    {
        if (Done != null) return this;
        Status = "running";
        Trace.Log(Id, $"启动，任务「{Trunc(Task, 28)}」，订阅 {(Subscriptions.Count > 0 ? string.Join(" ", Subscriptions) : "无")}，上下文隔离（{ContextTokens} tok 起）");
        Done = LoopAsync();
        return this;
    }

    private async Task LoopAsync()
    {
        while (Status == "running")
        {
            // 全双工边界：新输入在“当前推理步结束后”处理；必须先排空收件箱再取步骤（约束可能增删步骤）
            DrainInbox();
            if (StepIndex >= Plan.Count) { await ConcludeAsync(); break; }
            var step = Plan[StepIndex];
            try
            {
                await ReasonAndActAsync(step);
                StepIndex += 1;
            }
            catch (EnvironmentUnavailableException err)
            {
                // 执行环境不可用时任务挂起，而不是丢失进度（第 8 讲）
                Status = "blocked-env";
                BlockedReason = err.Message;
                Trace.Log(Id, $"挂起于第 {StepIndex} 步：{err.Message}");
                return;
            }
        }
    }

    /// <summary>设备重新上线后由云端触发续跑：状态仍在，计算实例可以是全新的。</summary>
    public Worker Resume(string reason = "设备重新上线")
    {
        if (Status != "blocked-env") return this;
        Status = "running";
        Trace.Log(Id, $"从第 {StepIndex} 步续跑（{reason}）");
        Done = LoopAsync();
        return this;
    }

    /// <summary>排空收件箱：这是订阅直通的信息进入隔离上下文的唯一入口。</summary>
    private void DrainInbox()
    {
        while (Inbox.Count > 0)
        {
            var item = Inbox[0];
            Inbox.RemoveAt(0);
            var trigger = item.Trigger;
            var how = item.How;
            if (item.MidStep) Trace.Log(Id, $"输入 {trigger.Id} 到达时正在推理步中 -> 等本步结束后处理（不能随时打断推理）");
            ContextTokens += Tokenizer.EstimateTokens(trigger.Text) + 30;

            if (how == "steer" || Signals.ContainsAny(trigger.Text, ConstraintWords))
            {
                Constraints.Add(trigger.Text);
                AddEvidence($"constraint:{trigger.Id}");
                Trace.Log(Id, $"接收约束：{trigger.Text}");
                ApplyConstraints(trigger);
                continue;
            }
            if (trigger.Source == Source.GhCi)
            {
                // 第 4 讲：CI 失败事件直通到达，Worker 自行继续修改，无须 Router 逐条转发
                var status = PayloadStr(trigger, "status");
                var failed = status == "failure";
                AddEvidence($"ci:{trigger.Id}:{status}");
                Trace.Log(Id, $"CI 事件 {trigger.Id} 状态={status ?? "?"}");
                if (failed && !Speculative) AppendFixCycle(trigger);
                continue;
            }
            if (trigger.Source == Source.Internal && PayloadStr(trigger, "kind") == "tool-done")
            {
                AddEvidence($"tool:{trigger.Id}");
                continue;
            }
            // 超出任务范围 -> 交回 Router 重新分派（第 4 讲）
            if (how != "dispatch" && !InScope(trigger))
            {
                Trace.Log(Id, $"{trigger.Id} 超出本任务范围 -> 交回 Router 重新分派");
                Bus?.Unsubscribe(Id, trigger.ThreadId);
                Subscriptions.Remove(trigger.ThreadId);
                Router?.Enqueue(trigger);
                continue;
            }
            AddEvidence($"event:{trigger.Id}");
            Trace.Log(Id, $"纳入上下文 {trigger.Id}「{Trunc(trigger.Text, 20)}」");
        }
    }

    private bool InScope(Trigger trigger)
    {
        var mine = ScopeKeywords.Where(Task.Contains).ToList();
        return mine.Count == 0 || mine.Any(trigger.Text.Contains);
    }

    /// <summary>用户中途叫停某类改动：从计划里剔除相关步骤。</summary>
    private void ApplyConstraints(Trigger trigger)
    {
        var banned = trigger.Text.Contains("接口") ? "api" : trigger.Text.Contains("测试") ? "test" : null;
        if (banned == null) return;
        var before = Plan.Count;
        // 已经执行完的步骤被移除时，要把游标往前拉，否则会跳过下一步
        var removedBefore = Plan.Take(StepIndex).Count(s => s.Tag == banned);
        Plan = Plan.Where(s => s.Tag != banned).ToList();
        StepIndex = Math.Max(0, StepIndex - removedBefore);
        if (Plan.Count != before)
            Trace.Log(Id, $"按约束调整计划：移除 {before - Plan.Count} 个 {banned} 相关步骤，下一步为 {(StepIndex < Plan.Count ? Plan[StepIndex].Tool : "收尾")}");
    }

    /// <summary>CI 失败后追加一轮“修改-重测”（体现直通带来的自我修正闭环）。</summary>
    private void AppendFixCycle(Trigger trigger)
    {
        var target = Scope.Count > 0 ? Scope[0] : "src/unknown.js";
        Plan.Insert(StepIndex + 1, new Step
        {
            Tool = "edit",
            Target = target,
            Sim = 1.2,
            Note = $"根据 {trigger.Id} 的失败信息修正",
            Tag = "fix",
            Evidence = $"failure:{PayloadStr(trigger, "detail") ?? "ci"}",
        });
        Trace.Log(Id, $"追加一轮修复-重测（由 {trigger.Id} 直通触发）");
    }

    // -- 单步推理与工具执行 -------------------------------------------------

    private async Task ReasonAndActAsync(Step step)
    {
        var promptTokens = ContextTokens + Tokenizer.EstimateTokens(JsonSerializer.Serialize(step, Json.Compact));
        const int outputTokens = 60;
        var call = await Llm.CallAsync(promptTokens, outputTokens, () => step);
        ContextTokens = (int)Math.Round(promptTokens * 0.82, MidpointRounding.AwayFromZero); // 上下文随任务推进增长
        Trace.Log(Id, $"推理 {call.Latency.Fixed(2)}s -> {step.Tool}({step.Target ?? step.Cmd ?? ""}){(step.Note != null ? $" // {step.Note}" : "")}");

        RecordTool(step);
        switch (step.Tool)
        {
            case "read": await ToolReadAsync(step); break;
            case "edit": await ToolEditAsync(step); break;
            case "run_tests": await ToolRunTestsAsync(step); break;
            case "daemon_exec": await ToolDaemonAsync(step); break;
            case "create_pr": await ToolCreatePrAsync(step); break;
            case "reproduce": await ToolReproduceAsync(step); break;
            default: await Clock.Sleep(step.Sim ?? 0.5); break;
        }
    }

    /// <summary>Watchdog 用的执行记录：相同调用重复且无新证据 = 停滞信号（第 9 讲）。</summary>
    private void RecordTool(Step step)
    {
        var fingerprint = $"{step.Tool}:{step.Target ?? step.Cmd ?? ""}";
        var last = ToolLog.Count > 0 ? ToolLog[^1] : null;
        var evKey = step.Evidence;
        // 关键区分：“活动量”不等于“信息增量”。只有第一次出现的证据才算新证据。
        var isNewEvidence = evKey != null && !HasEvidence(evKey);
        ToolLog.Add(new ToolRecord { At = Clock.Now(), Fingerprint = fingerprint, Evidence = evKey, Repeated = last?.Fingerprint == fingerprint && last?.Evidence == evKey, IsNewEvidence = isNewEvidence });
        if (evKey != null) AddEvidence(evKey);
    }

    /// <summary>供 Watchdog 读取的进展证据。</summary>
    public Evidence ProgressEvidence()
    {
        var recent = ToolLog.Count > 6 ? ToolLog.Skip(ToolLog.Count - 6).ToList() : ToolLog.ToList();
        var repeatedSameCall = 0;
        for (var i = recent.Count - 1; i > 0; i -= 1) { if (recent[i].Repeated) repeatedSameCall += 1; else break; }
        var newEvidenceCount = recent.Count(r => r.IsNewEvidence);
        var stage = Status == "waiting-tool" ? "waiting-tool" : Status == "closed" ? "closed" : "iterating";
        return new Evidence(repeatedSameCall, newEvidenceCount, ScopeViolations, InfraErrors, stage);
    }

    private async Task ToolReadAsync(Step step)
    {
        await Clock.Sleep(step.Sim ?? 0.6);
        var obj = step.Target != null ? Store.Get(step.Target) : null;
        var text = obj != null ? Store.ReadFull(step.Target!).Text : $"读取 {step.Target}：{step.Evidence ?? ""}";
        ContextTokens += Tokenizer.EstimateTokens(text.Length > 400 ? text.Substring(0, 400) : text);
        if (step.Spilled == true) Trace.Log(Id, $"  内容超预算 -> Spill 到窗口外，保留入口 read(\"{step.Target}\")");
    }

    private async Task ToolEditAsync(Step step)
    {
        await Clock.Sleep(step.Sim ?? 0.8);
        var target = step.Target ?? "";
        if (Scope.Count > 0 && !Scope.Any(s => target.Contains(s) || s.Contains(target)))
        {
            ScopeViolations += 1;
            Trace.Log(Id, $"  ! 改动 {step.Target} 越出任务范围 {string.Join(",", Scope)}（目标偏离信号）");
        }
        else Trace.Log(Id, $"  改动 {step.Target}");
    }

    /// <summary>
    /// 第 7 讲核心对比：asyncTools=false -> 经典 ReAct 阻塞；asyncTools=true -> start() 立即回执、后台完成回投、期间可处理新输入。
    /// </summary>
    private async Task ToolRunTestsAsync(Step step)
    {
        if (!AsyncTools)
        {
            var t0 = Clock.Now();
            Trace.Log(Id, $"  同步 run(\"{step.Cmd}\") —— 本会话在工具返回前无法响应新输入");
            Status = "waiting-tool";
            await Clock.Sleep(step.Sim ?? 4);
            var waited = Clock.Now() - t0;
            BlockedSeconds += waited;
            BlockedInputs += Inbox.Count;
            Status = "running";
            Trace.Log(Id, $"  run() 返回（阻塞 {waited.Fixed(1)}s，期间积压 {Inbox.Count} 条输入）");
            return;
        }

        var handle = Runner.Start(step.Tool, step.Cmd ?? "", step.Sim ?? 4, step.Outcome ?? "succeeded", step.Output ?? "所有测试通过",
            onDone: h =>
            {
                // 完成后作为新事件推送进会话
                var ev = Trigger.Make(Source.Internal, Subscriptions.Count > 0 ? Subscriptions[0] : "H00",
                    $"后台命令完成：{h.Cmd} -> {h.Status}（{h.Output}）", "system",
                    payload: new Dictionary<string, object?> { ["kind"] = "tool-done", ["handleId"] = h.Id, ["status"] = h.Status, ["output"] = h.Output },
                    clock: Clock);
                Receive(ev, "internal");
                Trace.Log(Id, $"  后台事件回投 {ev.Id}：{h.Cmd} -> {h.Status}");
            });
        Trace.Log(Id, $"  异步 start(\"{step.Cmd}\") -> 回执 Started({handle.Id})，推理循环不再等待");

        // 等待完成事件，但期间持续处理新输入 = 全双工体验
        Status = "waiting-tool";
        var t0b = Clock.Now();
        var timeout = (step.Sim ?? 4) + 20;
        while (Clock.Now() - t0b < timeout)
        {
            if (Inbox.Count > 0)
            {
                Status = "running";
                DrainInbox();
                Status = "waiting-tool";
                if (step.CancelOn != null && Constraints.Any(c => step.CancelOn.IsMatch(c)))
                {
                    var r = Runner.Stop(handle.Id);
                    Trace.Log(Id, $"  依据新约束调用 stop({handle.Id}) -> {r.Reason}");
                    break;
                }
            }
            if (handle.Status != "running" && handle.Status != "started") break;
            await Clock.Sleep(0.1);
        }
        Status = "running";
        Trace.Log(Id, $"  最终状态 {handle.Status}（Started 不等于成功，需以完成事件为准）");
    }

    private async Task ToolDaemonAsync(Step step)
    {
        if (Daemon == null) { await Clock.Sleep(step.Sim ?? 0.5); return; }
        try
        {
            var outp = await Daemon.ExecAsync(step.Cmd ?? "", step.Sim ?? 0.6);
            Trace.Log(Id, $"  daemon[{step.Machine}] {step.Cmd} -> {outp}");
            AddEvidence($"daemon:{step.Cmd}:{outp}");
        }
        catch (EnvironmentUnavailableException)
        {
            InfraErrors += 1;
            PendingDeviceStep = step;
            throw;
        }
    }

    private async Task ToolReproduceAsync(Step step)
    {
        await Clock.Sleep(step.Sim ?? 1.5);
        FindingsState ??= new Findings();
        FindingsState.Reproduced = step.Reproduced ?? false;
        FindingsState.Verified = step.Verified ?? false;
        FindingsState.Confidence = step.Confidence ?? 0;
        Trace.Log(Id, $"  复现结果={(step.Reproduced == true ? "成功" : "失败")}，验证={(step.Verified == true ? "通过" : "未通过")}，置信度={(step.Confidence ?? 0).Fixed(2)}");
    }

    private async Task ToolCreatePrAsync(Step step)
    {
        await Clock.Sleep(step.Sim ?? 0.8);
        var prId = Ids.Next("P");
        var prThread = step.ThreadId ?? $"H-PR-{prId}";
        Store.Put(Kind.PR, id: prId, title: step.Title ?? Trunc(Task, 20),
            summary: $"{step.Title ?? Trunc(Task, 18)}（评审进行中）",
            body: step.Body ?? $"修复内容：{Task}\n验证：{step.Verification ?? "相关测试通过"}",
            links: new[] { Id });
        Store.Link(prId, Id);
        Store.Put(Kind.Comment, id: $"{prId}-C1", title: "评审意见", summary: "建议补充超时文案",
            body: string.Concat(Enumerable.Repeat("超时提示文案需要产品确认，其余实现看起来没问题。", 6)),
            links: new[] { prId });
        Store.Link(prId, $"{prId}-C1");

        PrId = prId;
        PrThread = prThread;
        // 订阅 PR Thread：后续 CI 与评审事件将直通到本 Worker（第 4 讲）
        Bus?.Subscribe(Id, prThread, $"PR {prId}", Source.GhPr);
        AddSub(prThread);
        Store.Link(Id, prThread);
        Trace.Log(Id, $"  创建 {prId} 并订阅其 Thread {prThread}（后续 CI/评审事件将直通）");
    }

    // -- 收尾与汇报 ---------------------------------------------------------

    private async Task ConcludeAsync()
    {
        var findings = FindingsState ?? new Findings();
        Result = new WorkerResult(Id, Task, PrId, findings.Reproduced, findings.Verified, findings.Confidence, ToolLog.Count, ContextTokens);

        if (Speculative)
        {
            // 第 6 讲：后台尝试结束后，交付与尝试是两个独立判断
            var gate = SpeculationGate.ShouldDeliver(
                reproduced: findings.Reproduced ?? false,
                verified: findings.Verified ?? false,
                confidence: findings.Confidence ?? 0,
                userAsked: Constraints.Any(c => c.Contains("进度") || c.Contains("到哪") || c.Contains("怎么样")));
            Trace.Log(Id, $"交付闸门：{(gate.Go ? "交付" : "安静丢弃")} —— {gate.Reason}");
            if (!gate.Go)
            {
                Status = "closed";
                Trace.Log(Id, "推测结果丢弃，不通知用户（避免用注意力负担抵消省下的等待）");
                return;
            }
        }

        Status = "closed";
        var change = string.Join("；", Plan.Where(p => p.Tool == "edit").Select(p => p.Note ?? p.Target));
        Router?.Report(Id, new ReportArgs
        {
            Purpose = PrId != null ? "acceptance" : "decision",
            Fact = new ChannelFact(
                Topic: Title,
                Change: change.Length > 0 ? change : "按计划完成改动",
                Verification: findings.Verified == false ? "未通过验证" : "相关测试通过",
                Background: Task,
                Pending: Constraints.Count > 0 ? $"需确认约束：{Trunc(Constraints[0], 16)}" : "无",
                Ref: PrId ?? ""),
        });
        Trace.Log(Id, $"任务结束，共 {ToolLog.Count} 次工具调用，最终上下文 {ContextTokens} tok");
        await System.Threading.Tasks.Task.CompletedTask; // Task 类型名被本类的 string Task 字段遮蔽，需全限定
    }

    /// <summary>Router 全局状态里的一行摘要（Inline，不含完整历史）。</summary>
    public string Brief()
    {
        var recent = LastReport != "" ? LastReport : (ToolLog.Count > 0 ? ToolLog[^1].Fingerprint : "-");
        return $"{Id} 任务:{Trunc(Task, 16)} | 订阅:{(Subscriptions.Count > 0 ? string.Join(",", Subscriptions) : "-")} | 状态:{Status} | 最近:{recent}";
    }

    /// <summary>第 8 讲：计算无状态，状态可持久化后由新容器恢复。</summary>
    public WorkerSnapshot ToSnapshot() => new()
    {
        Id = Id,
        Task = Task,
        Title = Title,
        Status = Status,
        StepIndex = StepIndex,
        Plan = Plan,
        Scope = Scope,
        Subscriptions = new List<string>(Subscriptions),
        Constraints = new List<string>(Constraints),
        Evidence = new List<string>(EvidenceKeys),
        Findings = FindingsState,
        ContextTokens = ContextTokens,
        ToolCalls = ToolLog.Count,
        Speculative = Speculative,
        AsyncTools = AsyncTools,
        PrId = PrId,
    };

    /// <summary>从持久化状态恢复成一个可继续执行的 Worker（新的无状态计算实例）。</summary>
    public static Worker FromSnapshot(WorkerSnapshot data, WorkerOptions deps)
    {
        var w = new Worker(new WorkerOptions
        {
            Clock = deps.Clock,
            Trace = deps.Trace,
            Store = deps.Store,
            Bus = deps.Bus,
            Llm = deps.Llm,
            Router = deps.Router,
            Daemon = deps.Daemon,
            Runner = deps.Runner,
            Id = data.Id,
            Task = data.Task,
            Title = data.Title,
            Plan = data.Plan,
            Scope = data.Scope,
            Speculative = data.Speculative,
            AsyncTools = data.AsyncTools,
        });
        w.StepIndex = data.StepIndex;
        // 如实还原持久化时的状态：若检查点时是 blocked-env，恢复后 Resume() 才能从挂起的那一步续跑。
        w.Status = string.IsNullOrEmpty(data.Status) ? "idle" : data.Status;
        w.Constraints = data.Constraints ?? new List<string>();
        w.EvidenceKeys = data.Evidence ?? new List<string>();
        w.FindingsState = data.Findings ?? new Findings();
        w.ContextTokens = data.ContextTokens;
        w.Subscriptions = data.Subscriptions ?? new List<string>();
        w.PrId = data.PrId;
        foreach (var t in w.Subscriptions) deps.Bus?.Subscribe(w.Id, t, $"恢复的订阅 {t}", Source.Internal);
        return w;
    }
}
