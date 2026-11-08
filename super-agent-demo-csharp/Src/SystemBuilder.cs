// SystemBuilder.cs —— 把各模块组装成一个可运行的 Super-agent 教学系统
//
// 这里提供三类东西：
//   1. BuildSystem()：一次性接好 Clock / Trace / ObjectStore / EventBus / Router / Worker
//   2. Tasks 与计划模板：教学替身。第 10 讲原文说明“具体编码工作用一个只消耗时间的工具代替”，
//      因此大部分任务并不真的改代码，只是按设计走完事件与工具的流转。
//   3. 合成负载与统计工具：消息流生成、百分位数、单 Session 消融模型

using System.Text.RegularExpressions;

namespace SuperAgentDemo;

// ---------------------------------------------------------------------------
// 1. 任务模板
// ---------------------------------------------------------------------------

public sealed class TaskTemplate
{
    public string Label = "";
    public string Keyword = "";
    public List<string> Scope = new();
    public List<Step> Plan = new();
}

public static class SystemBuilder
{
    /// <summary>与 JS Math.round 一致（正数四舍五入、远离零），避免 .NET 默认的银行家舍入。</summary>
    private static double JsRound(double v) => Math.Round(v, MidpointRounding.AwayFromZero);
    private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

    public static readonly IReadOnlyDictionary<string, TaskTemplate> Tasks = new Dictionary<string, TaskTemplate>
    {
        ["loginTimeout"] = new TaskTemplate
        {
            Label = "修复登录超时",
            Keyword = "登录",
            Scope = new List<string> { "src/auth" },
            Plan = new List<Step>
            {
                new() { Tool = "read", Target = "src/auth/login.js", Sim = 0.8, Note = "定位登录超时处理", Evidence = "ev:login-locate" },
                new() { Tool = "edit", Target = "src/auth/login.js", Sim = 1.2, Note = "补充超时处理与界面状态复位", Evidence = "ev:login-edit", Tag = "api" },
                new() { Tool = "run_tests", Cmd = "npm test -- auth", Sim = 5, Outcome = "succeeded", Output = "auth 12 项测试通过", CancelOn = new Regex("只检查登录模块") },
                new() { Tool = "create_pr", Title = "fix(auth): 登录超时后复位界面状态", Verification = "auth 相关测试通过" },
            },
        },
        ["exportPerf"] = new TaskTemplate
        {
            Label = "优化导出功能",
            Keyword = "导出",
            Scope = new List<string> { "src/export" },
            Plan = new List<Step>
            {
                new() { Tool = "read", Target = "src/export/csv.js", Sim = 0.9, Note = "分析导出逻辑", Evidence = "ev:export-locate" },
                new() { Tool = "edit", Target = "src/export/csv.js", Sim = 1.4, Note = "改为分批写出，避免一次性载入", Evidence = "ev:export-edit" },
                new() { Tool = "run_tests", Cmd = "npm test -- export", Sim = 4.5, Outcome = "succeeded", Output = "export 8 项测试通过" },
                new() { Tool = "create_pr", Title = "perf(export): 分批写出大文件", Verification = "export 相关测试通过" },
            },
        },
        ["markdownTilde"] = new TaskTemplate
        {
            Label = "Markdown 波浪号渲染",
            Keyword = "波浪号",
            Scope = new List<string> { "src/markdown" },
            Plan = new List<Step>
            {
                new() { Tool = "reproduce", Sim = 1.6, Reproduced = true, Verified = false, Confidence = 0.5, Note = "静默复现波浪号渲染问题" },
                new() { Tool = "edit", Target = "src/markdown/inline.js", Sim = 1.5, Note = "修正删除线语法解析", Evidence = "ev:tilde-edit" },
                new() { Tool = "reproduce", Sim = 1.4, Reproduced = true, Verified = true, Confidence = 0.86, Note = "回归验证" },
                new() { Tool = "create_pr", Title = "fix(markdown): 波浪号删除线解析", Verification = "复现用例回归通过" },
            },
        },
        ["lagUnreproducible"] = new TaskTemplate
        {
            Label = "大文件打开卡顿",
            Keyword = "卡顿",
            Scope = new List<string> { "src/editor" },
            Plan = new List<Step>
            {
                new() { Tool = "read", Target = "src/editor/loader.js", Sim = 1.0, Note = "检查加载路径", Evidence = "ev:lag-read" },
                new() { Tool = "reproduce", Sim = 2.2, Reproduced = false, Verified = false, Confidence = 0.2, Note = "尝试复现，未成功" },
                new() { Tool = "edit", Target = "src/editor/loader.js", Sim = 1.2, Note = "推测性调整缓存策略", Evidence = "ev:lag-guess" },
            },
        },
        ["deviceBound"] = new TaskTemplate
        {
            Label = "处理笔记本上未提交的改动",
            Keyword = "未提交",
            Scope = new List<string> { "src/local" },
            Plan = new List<Step>
            {
                new() { Tool = "daemon_exec", Machine = "laptop-01", Cmd = "git status --porcelain", Sim = 0.8, Evidence = "ev:git-status" },
                new() { Tool = "daemon_exec", Machine = "laptop-01", Cmd = "npm run lint -- src/local", Sim = 1.4, Evidence = "ev:lint" },
                new() { Tool = "edit", Target = "src/local/session.js", Sim = 1.0, Note = "按 lint 结果修正", Evidence = "ev:local-edit" },
                new() { Tool = "create_pr", Title = "chore(local): 修正会话初始化", Verification = "lint 通过" },
            },
        },
        ["drifting"] = new TaskTemplate
        {
            Label = "修一个小的配置读取问题",
            Keyword = "配置",
            Scope = new List<string> { "src/config" },
            Plan = new List<Step>
            {
                new() { Tool = "read", Target = "src/config/load.js", Sim = 0.7, Note = "定位配置读取", Evidence = "ev:cfg-read" },
                new() { Tool = "edit", Target = "src/config/load.js", Sim = 0.9, Note = "修正默认值合并", Evidence = "ev:cfg-edit" },
                // 目标偏离：为一个“小修复”不断扩大到无关模块（第 9 讲）
                new() { Tool = "edit", Target = "src/ui/theme.js", Sim = 0.9, Note = "顺手重构主题模块", Evidence = "ev:cfg-edit" },
                new() { Tool = "edit", Target = "src/api/client.js", Sim = 0.9, Note = "顺手重构接口层", Evidence = "ev:cfg-edit" },
                new() { Tool = "edit", Target = "src/api/router.js", Sim = 0.9, Note = "继续重构", Evidence = "ev:cfg-edit" },
            },
        },
        ["stalled"] = new TaskTemplate
        {
            Label = "修复偶发的测试失败",
            Keyword = "偶发",
            Scope = new List<string> { "src/core" },
            Plan = new List<Step>
            {
                new() { Tool = "read", Target = "src/core/scheduler.js", Sim = 0.7, Note = "查看调度实现", Evidence = "ev:st-read" },
                // 停滞：连续相同调用且没有新证据（第 9 讲补充案例）
                new() { Tool = "run_tests", Cmd = "npm test -- scheduler", Sim = 1.2, Outcome = "failed", Output = "同一处断言失败", AsyncTools = false },
                new() { Tool = "run_tests", Cmd = "npm test -- scheduler", Sim = 1.2, Outcome = "failed", Output = "同一处断言失败", AsyncTools = false },
                new() { Tool = "run_tests", Cmd = "npm test -- scheduler", Sim = 1.2, Outcome = "failed", Output = "同一处断言失败", AsyncTools = false },
                new() { Tool = "run_tests", Cmd = "npm test -- scheduler", Sim = 1.2, Outcome = "failed", Output = "同一处断言失败", AsyncTools = false },
            },
        },
    };

    // 顺序即优先级：markdownTilde 先于 loginTimeout（“渲染”优先于“超时”）
    private static readonly (Regex Re, string Key)[] TaskRules =
    {
        (new Regex("波浪号|渲染"), "markdownTilde"),
        (new Regex("登录|超时"), "loginTimeout"),
        (new Regex("导出"), "exportPerf"),
        (new Regex("卡顿|大文件"), "lagUnreproducible"),
        (new Regex("未提交|笔记本|本地"), "deviceBound"),
        (new Regex("配置"), "drifting"),
        (new Regex("偶发"), "stalled"),
    };

    public static string? TaskKeyFor(string taskText)
    {
        foreach (var (re, key) in TaskRules) if (re.IsMatch(taskText)) return key;
        return null;
    }

    private static int _burnSeq;

    /// <summary>第 10 讲的实验设定：用一个只消耗时间的工具代替具体编码工作。</summary>
    public static List<Step> MockCodingPlan(double simSeconds = 6)
    {
        _burnSeq += 1;
        return new List<Step>
        {
            new()
            {
                Tool = "burn",
                Cmd = $"mock-coding-work({simSeconds.ToString("0.##", Fmt.Inv)}s)",
                Sim = simSeconds,
                Note = "只消耗时间的替身工具，不代表真实代码质量",
                Evidence = $"ev:burn-{_burnSeq}",
            },
        };
    }

    private static Step CloneStep(Step s) => new()
    {
        Tool = s.Tool,
        Target = s.Target,
        Cmd = s.Cmd,
        Sim = s.Sim,
        Note = s.Note,
        Tag = s.Tag,
        Evidence = s.Evidence,
        Machine = s.Machine,
        Spilled = s.Spilled,
        Reproduced = s.Reproduced,
        Verified = s.Verified,
        Confidence = s.Confidence,
        ThreadId = s.ThreadId,
        Title = s.Title,
        Body = s.Body,
        Verification = s.Verification,
        Outcome = s.Outcome,
        Output = s.Output,
        AsyncTools = s.AsyncTools,
        CancelOn = s.CancelOn,
    };

    private static List<Step> ClonePlan(List<Step> plan) => plan.Select(CloneStep).ToList();

    // -----------------------------------------------------------------------
    // 2. 组装系统
    // -----------------------------------------------------------------------

    public static BuiltSystem BuildSystem(SystemOptions opts)
    {
        Ids.Reset();
        _burnSeq = 0;

        var clock = new Clock(opts.MsPerSimSecond);
        var trace = new Trace(clock, opts.Title);
        var store = new ObjectStore(opts.InlineCharLimit);
        var stats = new LlmStats();
        var runner = new AsyncToolRunner(clock, trace);
        var bus = new EventBus(clock, trace, store, opts.DirectDelivery, opts.SourcePolicy, opts.Whitelist, opts.BotName);

        Func<string, List<Step>> resolvePlan = taskText =>
        {
            if (opts.PlanResolver != null) return opts.PlanResolver(taskText);
            var key = TaskKeyFor(taskText);
            if (key != null) return ClonePlan(Tasks[key].Plan);
            return MockCodingPlan(6);
        };

        // workerFactory 引用 router，而 router 又引用 workerFactory：用局部变量闭包打破环依赖。
        // 工厂只在运行期（Router 分派时）被调用，那时 router 已赋值——与 JS 的 TDZ 闭包写法等价。
        Router? router = null;
        WorkerFactory workerFactory = spec =>
        {
            var id = Ids.Next("W");
            var plan = resolvePlan(spec.Task);
            var key = TaskKeyFor(spec.Task);
            var scope = key != null ? new List<string>(Tasks[key].Scope) : new List<string>();
            var needsDaemon = plan.FirstOrDefault(s => s.Tool == "daemon_exec");
            var daemon = needsDaemon != null
                ? opts.Daemons.FirstOrDefault(d => d.Machine == needsDaemon.Machine) ?? opts.Daemons.FirstOrDefault()
                : null;
            var w = new Worker(new WorkerOptions
            {
                Id = id,
                Task = spec.Task,
                Title = key != null ? Tasks[key].Label : Trunc(spec.Task, 12),
                Clock = clock,
                Trace = trace,
                Store = store,
                Bus = bus,
                Llm = new MockLlm(clock, id, opts.WorkerModel, stats),
                Router = router,
                Daemon = daemon,
                Runner = runner,
                Plan = plan,
                Scope = scope,
                Speculative = opts.Speculative,
                AsyncTools = opts.AsyncTools && !plan.Any(s => s.AsyncTools == false),
                OriginThread = spec.OriginThread,
            });
            w.OriginTriggerId = spec.OriginTrigger?.Id;
            return w;
        };

        router = new Router(
            clock, trace, store, bus,
            new MockLlm(clock, "ROUTER", opts.RouterModel, stats),
            workerFactory,
            opts.FilterOnly, opts.MaxBatch, opts.GlobalStateTokens,
            userChannel: opts.UserChannel, semanticPolicy: opts.SemanticPolicy);
        bus.AttachRouter(router);

        return new BuiltSystem
        {
            Clock = clock,
            Trace = trace,
            Store = store,
            Stats = stats,
            Bus = bus,
            Router = router,
            Runner = runner,
            Daemons = opts.Daemons,
            WorkerFactory = workerFactory,
        };
    }

    // -----------------------------------------------------------------------
    // 3. 合成负载：消息流生成（第 10 讲 7.1 的实验输入）
    // -----------------------------------------------------------------------

    private static readonly string[] WorkRequests =
    {
        "线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复",
        "导出大文件时失败，日志显示内存溢出，需要优化导出功能",
        "Markdown 里的波浪号删除线渲染不对，讨论一下怎么修",
        "打开大文件时编辑器卡顿明显，帮忙看看性能",
        "配置读取的默认值合并有问题，小修一下",
        "偶发的调度测试失败，帮忙定位",
    };
    private static readonly string[] Followups = { "补充一下日志，报错栈在这里", "这个问题今天必须处理", "顺便看看有没有相似的历史 issue", "刚又出现一次，频率变高了" };
    private static readonly string[] Stops = { "先不要改接口，等评审结论", "暂缓一下，产品要重新确认范围" };
    private static readonly string[] Chitchat = { "午饭吃什么", "今天下班早点走", "周末有人打球吗", "哈哈哈这个表情太好笑了", "恭喜发版，辛苦了" };
    private static readonly string[] CiMsgs = { "CI 运行结果：构建通过，测试失败 2 项", "CI 运行结果：全部通过", "CI 运行结果：构建失败，依赖安装超时" };

    /// <summary>
    /// 生成一条合成消息流。原文设定：约 300 条消息、其中约 20 条工作请求，其余为闲聊、跟进、叫停与 CI 消息。
    /// 这里默认按比例缩小，便于几秒内跑完；比例保持一致。rng 调用顺序严格对齐 JS，保证同一 seed 得到同一负载。
    /// </summary>
    public static List<StreamMsg> MakeStream(int total = 90, int workCount = 6, double durationSec = 600, int seed = 20260711, double workSeconds = 6)
    {
        var rng = new Mulberry32(seed);
        var msgs = new List<StreamMsg>();
        var workThreads = new List<string>();

        double At() => JsRound(rng.Next() * durationSec * 10) / 10;
        int Pick(int n) => (int)Math.Floor(rng.Next() * n);

        for (var i = 0; i < workCount; i += 1)
        {
            var threadId = $"H{(i + 1).ToString("00")}";
            workThreads.Add(threadId);
            msgs.Add(new StreamMsg
            {
                At = JsRound(durationSec * (0.05 + 0.85 * rng.Next()) * 10) / 10,
                Kind = "work",
                Input = new EventInput
                {
                    Source = Source.ImWork,
                    ThreadId = threadId,
                    Text = WorkRequests[i % WorkRequests.Length],
                    Author = $"user-{1 + (i % 3)}",
                    Payload = new Dictionary<string, object?> { ["threadTitle"] = $"工作群讨论 {threadId}", ["workSeconds"] = workSeconds },
                },
            });
        }

        var others = total - workCount;
        for (var i = 0; i < others; i += 1)
        {
            var r = rng.Next();
            if (r < 0.42)
            {
                var at = At();
                var source = rng.Next() < 0.5 ? Source.ImSocial : Source.ImNotify;
                var tid = $"H-SOC{1 + Pick(3)}";
                var text = Chitchat[Pick(Chitchat.Length)];
                var author = $"user-{1 + Pick(5)}";
                msgs.Add(new StreamMsg { At = at, Kind = "chitchat", Input = new EventInput { Source = source, ThreadId = tid, Text = text, Author = author } });
            }
            else if (r < 0.62 && workThreads.Count > 0)
            {
                var tid = workThreads[Pick(workThreads.Count)];
                var at = At();
                var text = Followups[Pick(Followups.Length)];
                msgs.Add(new StreamMsg { At = at, Kind = "followup", Input = new EventInput { Source = Source.ImWork, ThreadId = tid, Text = text, Author = "user-2" } });
            }
            else if (r < 0.66 && workThreads.Count > 0)
            {
                var tid = workThreads[Pick(workThreads.Count)];
                var at = At();
                var text = Stops[Pick(Stops.Length)];
                msgs.Add(new StreamMsg { At = at, Kind = "stop", Input = new EventInput { Source = Source.ImWork, ThreadId = tid, Text = text, Author = "user-1" } });
            }
            else if (r < 0.88 && workThreads.Count > 0)
            {
                var tid = workThreads[Pick(workThreads.Count)];
                var text = CiMsgs[Pick(CiMsgs.Length)];
                var at = At();
                msgs.Add(new StreamMsg
                {
                    At = at,
                    Kind = "ci",
                    Input = new EventInput
                    {
                        Source = Source.GhCi,
                        ThreadId = tid,
                        Text = text,
                        Author = "ci-bot",
                        Payload = new Dictionary<string, object?> { ["status"] = text.Contains("失败") ? "failure" : "success", ["detail"] = text },
                    },
                });
            }
            else
            {
                var at = At();
                var text = Chitchat[Pick(Chitchat.Length)];
                msgs.Add(new StreamMsg { At = at, Kind = "chitchat", Input = new EventInput { Source = Source.ImWork, ThreadId = "H-SOC9", Text = text, Author = "user-4" } });
            }
        }

        return msgs.OrderBy(m => m.At).ToList(); // 稳定排序，等价 JS Array.prototype.sort
    }

    /// <summary>按时间轴把消息流灌进总线。</summary>
    public static async Task<List<PublishedMsg>> ReplayStream(EventBus bus, List<StreamMsg> msgs, Clock clock)
    {
        double prev = 0;
        var published = new List<PublishedMsg>();
        foreach (var m in msgs)
        {
            var gap = Math.Max(0, m.At - prev);
            prev = m.At;
            if (gap > 0) await clock.Sleep(gap);
            var t = await bus.PublishAsync(m.Input);
            published.Add(new PublishedMsg { At = m.At, Kind = m.Kind, Input = m.Input, Result = t });
        }
        return published;
    }

    // -----------------------------------------------------------------------
    // 4. 统计工具
    // -----------------------------------------------------------------------

    public static double Mean(List<double> arr) => arr.Count > 0 ? arr.Sum() / arr.Count : 0;
    public static double Median(List<double> arr) => Percentile(arr, 50);

    public static double Percentile(List<double> arr, double p)
    {
        if (arr.Count == 0) return 0;
        var s = arr.OrderBy(x => x).ToList();
        var idx = Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * s.Count) - 1));
        return s[idx];
    }

    public static string FmtMin(double sec) => $"{(sec / 60).Fixed(1)} 分";

    public static string FmtTok(double n)
    {
        if (n >= 1e8) return $"{(n / 1e8).Fixed(2)} 亿";
        if (n >= 1e4) return $"{(n / 1e4).Fixed(1)} 万";
        return ((long)JsRound(n)).ToString(Fmt.Inv);
    }

    // -----------------------------------------------------------------------
    // 5. 单 Session + Subagent 工具的消融模型（第 10 讲 7.1 的方案三、四）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 教学模型（不是真实运行）：一个常驻主会话串行处理所有消息。
    /// 原文报告的现象被抽象为两条规则：
    ///   - 中性提示：模型几乎不主动调用 Subagent，工作都由主会话承担，且不断被新消息打断；
    ///   - 优先委派提示：多数工作被委派出去，但偶发一次“任务很小，自己做”，形成长尾。
    /// </summary>
    public static SingleSessionResult SimulateSingleSession(
        List<StreamMsg> messages, string delegation = "neutral", double workSec = 90, double llmSec = 2.5,
        int contextTokens = 12000, int seed = 7, double preferRate = 0.8)
    {
        var rng = new Mulberry32(seed);
        double freeAt = 0;
        int cacheReadTokens = 0, decisions = 0, delegated = 0;
        var completion = new List<(double ArrivedAt, double DoneAt)>();
        var interruptions = new List<double>();

        foreach (var m in messages)
        {
            var start = Math.Max(m.At, freeAt);
            var queued = start - m.At;
            if (queued > 0.5) interruptions.Add(queued);
            cacheReadTokens += contextTokens; // 每条消息都要重读主会话上下文
            decisions += 1;
            var busy = llmSec;

            if (m.Kind == "work")
            {
                // 中性提示下几乎不委派；优先委派时按概率委派，剩下的自己扛
                var rate = delegation == "prefer" ? preferRate : 0;
                var willDelegate = rng.Next() < rate;
                if (willDelegate)
                {
                    delegated += 1;
                    busy += llmSec; // 委派决策本身也要一轮
                    completion.Add((m.At, start + busy + workSec));
                }
                else
                {
                    busy += workSec; // 主会话亲自执行，期间无法处理其他消息
                    completion.Add((m.At, start + busy));
                }
            }
            freeAt = start + busy;
        }

        var times = completion.Select(c => c.DoneAt - c.ArrivedAt).ToList();
        return new SingleSessionResult
        {
            Variant = delegation == "prefer" ? "单 Session + 优先委派提示" : "单 Session + 中性提示",
            CompletionTimes = times,
            P50 = Percentile(times, 50),
            P95 = Percentile(times, 95),
            CacheReadTokens = cacheReadTokens,
            Decisions = decisions,
            Delegated = delegated,
            Tasks = completion.Count,
            Interruptions = interruptions.Count,
            MeanInterruptionWait = Mean(interruptions),
        };
    }
}

// ---------------------------------------------------------------------------
// 数据结构
// ---------------------------------------------------------------------------

public sealed class SystemOptions
{
    public string Title = "";
    public double MsPerSimSecond = 80;
    public bool DirectDelivery = true;   // false = 消融「去掉直通」
    public bool FilterOnly;              // true = 只演示第 3 讲的两级过滤，不分派
    public int MaxBatch = 8;             // Router 单轮最多合并的事件数（1 = 关闭批处理）
    public int GlobalStateTokens = 9000; // Router 常驻全局状态规模（长输入的来源）
    public string RouterModel = "lite-router";
    public string WorkerModel = "strong-worker";
    public int InlineCharLimit = 50;
    public IReadOnlyDictionary<string, string>? SourcePolicy;
    public HashSet<string>? Whitelist;
    public string BotName = "super-agent";
    public bool AsyncTools = true;       // false = 经典同步 run()
    public bool Speculative;             // Worker 以推测执行模式运行
    public List<Daemon> Daemons = new();
    public Func<string, List<Step>>? PlanResolver;
    public string UserChannel = "im";
    public string SemanticPolicy = "default";
}

public sealed class BuiltSystem
{
    public Clock Clock = null!;
    public Trace Trace = null!;
    public ObjectStore Store = null!;
    public LlmStats Stats = null!;
    public EventBus Bus = null!;
    public Router Router = null!;
    public AsyncToolRunner Runner = null!;
    public List<Daemon> Daemons = new();
    public WorkerFactory WorkerFactory = null!;
}

public sealed class StreamMsg
{
    public double At;
    public string Kind = "";
    public EventInput Input = new();
}

public sealed class PublishedMsg
{
    public double At;
    public string Kind = "";
    public EventInput Input = new();
    public PublishResult? Result;
}

public sealed class SingleSessionResult
{
    public string Variant = "";
    public List<double> CompletionTimes = new();
    public double P50;
    public double P95;
    public int CacheReadTokens;
    public int Decisions;
    public int Delegated;
    public int Tasks;
    public int Interruptions;
    public double MeanInterruptionWait;
}

/// <summary>可复现的伪随机数（mulberry32）：保证多次运行的对比是同一负载。严格对齐 JS 的 32 位整数语义。</summary>
public sealed class Mulberry32
{
    private int _a;

    public Mulberry32(int seed) => _a = seed;

    public double Next()
    {
        unchecked
        {
            _a += 0x6d2b79f5;                                  // a = (a + 0x6d2b79f5) | 0
            var t = (_a ^ (int)((uint)_a >> 15)) * (1 | _a);     // Math.imul(a ^ (a>>>15), 1 | a)
            t = (t + ((t ^ (int)((uint)t >> 7)) * (61 | t))) ^ t;
            return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
        }
    }
}
