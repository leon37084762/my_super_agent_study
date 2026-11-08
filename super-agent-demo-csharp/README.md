# Super-agent 教学演示 · C# / .NET 9 版（super-agent-demo-csharp）

配套《Super-agent 设计探索：迈向更自主的 Agent 系统》学习讲义（第 2~10 讲）的**可运行示例代码**。
这是同目录 `super-agent-demo/`（零依赖 Node.js 版）的**忠实 C# 移植**：用最小可读的“教学替身”把讲义里的每个机制**真的跑起来**，边看输出边对照讲义理解。

> **定位声明（务必先读）**
> 本目录是**帮助理解讲义的教学示例**，不是 Super-agent 的官方实现，也不是原论文的代码复现或性能基准。
> 所有模型都是规则化的 Mock（`MockLlm`），所有“编码工作”都用只消耗时间的替身工具模拟，
> 所有数值只用于说明**机制方向**（哪个更省、哪个更快、关掉某机制指标会怎样变差），**不代表任何真实产品或真实模型的绝对表现**。

---

## 快速开始

**环境要求**：.NET SDK 9.0（`global.json` 已锁定主版本）。**无第三方依赖**——只用 BCL。

```bash
# 进入本目录
cd super-agent-demo-csharp

# 方式一：串跑全部 9 个演示（第 2~10 讲），末尾给出通过/失败汇总
dotnet run
#   等价于  dotnet run -- all
#   只跑指定编号：dotnet run -- 04 07

# 方式二：单独跑某一讲（编号直接作为参数）
dotnet run -- 02      # 双层架构
dotnet run -- 09      # Watchdog 与异常恢复
dotnet run -- 10      # 批处理压测 + 消融对比
```

每个演示都**自包含**，运行**瞬间结束**（见下节“虚拟时钟”），控制台会打印带时间戳的中文旁白、对比表格与“本讲要点”。

> Windows PowerShell 小贴士：若把输出重定向到文件，中文可能出现乱码（控制台编码所致）。
> 直接查看即可，或先执行 `[Console]::OutputEncoding=[System.Text.Encoding]::UTF8`。程序入口也已尝试把 `Console.OutputEncoding` 设为 UTF-8。

---

## 与 Node 版的关系：虚拟时钟与确定性（重要）

C# 版在**机制、旁白、表格、判定逻辑**上与 Node 版逐条对齐，但把“时间”的推进方式换成了**离散事件仿真的虚拟时钟**（`Src/Async.cs` 的 `SingleThreadSynchronizationContext` + `Src/Core.cs` 的 `Clock`）：

- **单线程协作式调度**：复刻 Node 的单线程事件循环——所有 `async` 续体在同一个泵里排队执行；`Clock.Sleep(模拟秒)` 只是**登记一个到期定时器**，泵在“没有就绪续体”时把虚拟时刻**精确推进**到最近的定时器。**不使用真实 `Task.Delay` / `Task.Run` / 线程池**。
- **完全确定、可复现、瞬间跑完**：由于不再依赖墙上时钟，`msPerSimSecond` 在虚拟模式下**不影响任何数值**；同一输入每次运行结果**完全一致**，整套 9 个演示在一秒内跑完。
- **与 Node 的数值关系**：
  - **所有“与时间无关”的量与 Node 精确一致**——漏斗计数、决策数、`maxBatch=1` 的确定性基线（关闭批处理 = 30 轮 / 27.0 万 Token）、解析模型（单 Session 基线：委派 0/7、排队 119、144.0 万、p50/p95）、合成负载构成（`chitchat×75，followup×19，stop×1，ci×17，work×8`）、演示 08 的序列化字节数（726）、演示 09 的五档判定与介入次数等。
  - **依赖墙上时刻的量按设计不同**——时间戳、`savedAt`、完成时间 p50/p95，以及**突发流量下的批处理轮数**。Node 用真实 `setTimeout`（亚毫秒被钳制、有调度抖动），**多次运行本身就会波动**（演示 10 的突发批处理在 Node 上实测为 7/7/8 轮不等）；C# 虚拟时钟则**稳定给出确定值**（5 轮）。这不是缺陷，而是本项目“保证确定性”目标的直接结果，C# 的结果更贴近模型的确定行为。

一句话：**C# 版把 Node 版里“因真实定时器抖动而每次略有出入”的部分，变成了确定、可复现的结果；机制与所有非时间数值保持一致。**

---

## 演示与讲义映射

| 演示 | 文件 | 对应讲义 | 原文 | 核心机制（看点） |
|---|---|---|---|---|
| 02 | `Demos/Demo02.cs` | 第 2 讲 | 2、2.1、2.2 | 双层架构：单 Session / 每事件一 Session / Router-Worker 三方案对比，连续性与吞吐量如何两全 |
| 03 | `Demos/Demo03.cs` | 第 3 讲 | 3 开头、3.1 | 两级事件过滤：程序层 Fast Path（确定性规则，不调模型）+ 语义层 Slow Path（模型判断“值不值得行动”，策略可用自然语言调） |
| 04 | `Demos/Demo04.cs` | 第 4 讲 | 3.2 | 事件路由与订阅直通：未订阅交 Router 判断归属，已订阅直通 Worker（不调模型）；直通不等于 Router 失明；超范围事件退订交回 |
| 05 | `Demos/Demo05.cs` | 第 5 讲 | 3.3 | 上下文组织与链式召回：Inline / Spill / 唯一 ID 引用图逐跳展开；“截断 ≠ Spill”（必须留读回入口）；与两种对照方案的 Token/往返对比 |
| 06 | `Demos/Demo06.cs` | 第 6 讲 | 3.4 | 提前行动与推测执行：三道闸门 `ShouldAttempt` / `ShouldContinue` / `ShouldDeliver`；验证通过才交付 vs 未复现安静丢弃 |
| 07 | `Demos/Demo07.cs` | 第 7 讲 | 4.1、4.2 | 面向人的交互：异步 ReAct Loop（`Start()` 立即回执、完成后回投、`Stop()`）、新输入在“当前推理步结束后”处理；多信道表达（IM 低打扰 / GitHub·邮件 高密度） |
| 08 | `Demos/Demo08.cs` | 第 8 讲 | 5.1、5.2、5.3 + BYOM | 计算/状态/环境分离：状态落盘 → “销毁”实例 → 设备上线 → 全新实例恢复续跑；端侧 daemon 执行；间歇在线设备离线挂起不丢进度 |
| 09 | `Demos/Demo09.cs` | 第 9 讲 | 6 | Watchdog 与异常恢复：独立 Session + 定时消息驱动的外部 Control Loop；区分“活动量”与“实质进展”；五档判定 healthy/watch/stalled/drift/infra；在线介入（注入约束/提示、延后重试）；维护者自然语言询问 |
| 10 | `Demos/Demo10.cs` | 第 10 讲 | 7、7.1、7.2 | 初步实验与证据解读：批处理压测三指标（batch 大小 / 单轮耗时 / 每决策分摊）+ 消融（关闭批处理、去掉直通）+ 与“单 Session + Subagent”基线对比 |

> 第 1 讲（目标与整体架构）与第 11 讲（开放问题与串讲）偏综述与讨论，无独立演示；其概念贯穿在上述各讲之中。

---

## 目录结构与模块职责

```
super-agent-demo-csharp/
├── global.json            # 锁定 .NET SDK 主版本 9
├── SuperAgentDemo.csproj  # net9.0；启用 ImplicitUsings / Nullable
├── Program.cs             # 入口与分发器（单编号直跑 / 多编号或 all 串跑 + 汇总）
├── README.md              # 本文件
├── Demos/                 # 9 个演示 + 公共辅助（每个都可单独运行）
│   ├── DemoCommon.cs      # Demo.Run(单线程泵驱动)/WaitUntil/Payload/Trunc/Table
│   └── Demo02.cs ... Demo10.cs
├── Src/                   # 教学替身的“系统实现”
│   ├── Async.cs           # 【C# 特有】SingleThreadSynchronizationContext：单线程虚拟调度泵（复刻 Node 事件循环 + 虚拟时钟）
│   ├── Core.cs            # Clock(虚拟/墙上双模)、Trace、Trigger/Thread、Source/Policy、Table.RenderTable、Ids、Fmt
│   ├── Llm.cs             # MockLlm(延迟+Token 计费)、JudgeWorthActing/JudgeRouting/JudgeProgress、Evidence、ModelProfiles、Tokenizer、LlmStats
│   ├── ContextLib.cs      # Inline/Spill/局部性/链式召回；RecallSession 与唯一 ID 引用图；ObjectStore/Kind
│   ├── Bus.cs             # 事件总线：两级过滤(Fast Path) + Thread 订阅直通；Funnel 漏斗统计
│   ├── Router.cs          # Router 层：全局协调 + 批处理 + 统一对外回复 + 链式召回注入 + Quiesce
│   ├── Worker.cs          # Worker 层：隔离上下文执行 + 异步工具(Start/Stop) + 推测执行三道闸门 + 序列化(ToSnapshot/FromSnapshot/Resume)
│   ├── Channels.cs        # 多信道表达：IM 低打扰 / GitHub·邮件 高密度；单次回复词数预算
│   ├── Infra.cs           # SessionStore(状态持久化) + Daemon(端侧执行环境/BYOM/间歇在线) + Watchdog(外部 Control Loop 五档判定)
│   └── SystemBuilder.cs   # BuildSystem() 组装 + Tasks 任务模板 + MakeStream/ReplayStream 合成负载 + SimulateSingleSession 基线模型 + 统计工具
└── tmp/                   # 运行产物（如演示 08 的 checkpoint.json）
```

模块依赖自底向上：`Async`/`Core` ← `Llm`/`ContextLib`/`Channels` ← `Bus`/`Worker` ← `Router` ← `Infra` ← `SystemBuilder`，
演示只从 `SystemBuilder`（组装）、`Core`（日志/表格）与个别模块（如 `Llm` 的 `Progress.JudgeProgress`、`ContextLib` 的 `RecallSession`、`Infra` 的 `Watchdog`）按需引用。

---

## 教学替身的关键约定

为了“能跑、可读、公平对比”，示例对真实系统做了这些简化，阅读时请带着这些前提：

- **MockLlm 不是真模型**：延迟 = 固定开销 + prefill + decode（按 Token 计），“语义判断”是规则匹配。
  三档模型画像对应讲义：`lite-router`（Router 输入长输出短，用轻量模型）、`strong-worker`（Worker 决定结果，用最强模型）、`watchdog`。
- **虚拟时钟**：`Clock(msPerSimSecond)` 在单线程泵内以“模拟秒”精确推进（无就绪续体时跳到最近定时器），日志时间戳是**模拟时间**且**完全确定**；`msPerSimSecond` 只在与真实墙上时钟回退时才有意义，虚拟模式下不影响数值。
- **编码工作是替身**：第 10 讲原文说“具体编码工作用一个只消耗时间的工具代替”，因此任务不真的改代码，只按设计走完事件与工具流转（`mock-coding-work`）。
- **任务文案需含“工作信号”**：只有包含工作信号词（如“报错/失败/修复/优化/排查/超时/bug”等）的消息才会被判定为值得分派，用于演示语义过滤。
- **合成负载可复现**：消息流用固定种子（`Mulberry32`）生成；配合虚拟时钟，**同一演示多次运行的数字完全一致**（这一点比 Node 版更强——Node 版因真实定时器抖动会有轻微时序波动）。

---

## 代表性运行结果（演示 10）

下面是 C# 版**确定性运行**的关键数字（每次运行一致），用于说明“机制方向”，非性能基准。

**① 批处理压测（突发 30 条消息涌入）**

| 指标 | 数值 | 含义 |
|---|---|---|
| 接收事件 | 30 | 突发流量 |
| 平均 batch 大小 | 6.00 | 上一轮推理期间到达的事件积压，下一轮合并处理 |
| 单轮耗时 | 2.18s | 一次 Router 推理 |
| 每决策分摊时间 | 0.403s | 把一轮成本摊到本轮多条事件上（≪ 单轮耗时） |
| Router 轮数 | 5 | 常驻层被唤醒并推理的次数 |
| 读缓存 Token(Router) | 4.5 万 | 每轮都要重读常驻全局状态摘要 |

**② 消融对比（关掉机制，看指标变差）**

| 消融 | 开启 | 关闭 | 说明 |
|---|---|---|---|
| 批处理（同一突发负载） | 5 轮 / 4.5 万 Token | 30 轮 / 27.0 万 Token | 一轮只处理一条 → 全局状态被重读 30 次 |
| 订阅直通（真实合成流） | 交 Router 31 / 29.6 万 Token / 11 Worker | 交 Router 62 / 52.7 万 Token / 25 Worker | 已订阅事件也要经 Router 判断归属 |

**③ 与“单 Session + Subagent”基线对比**

| 方案 | 委派/Worker | 读缓存 Token 合计 | 完成 p50 | 完成 p95 | 决策通道是否被任务占住 |
|---|---|---|---|---|---|
| 单 Session + 中性提示 | 0 | 144.0 万 | 7.8 分 | 14.4 分 | 是·串行执行·排队 119 次 |
| 单 Session + 优先委派 | 7 | 144.0 万 | 4.9 分 | 5.7 分 | 部分·偶发自己做·排队 119 次 |
| 双层架构（本系统实测） | 11 Worker | 29.6 万 | 0.2 分 | 0.3 分 | 否·Router 只决策不执行 |

最硬的一条对比：读缓存 Token **144.0 万 → 29.6 万（约 4.9×）**——单会话每条消息都重读整段上下文，
双层架构里 Router 只读摘要、Worker 上下文彼此隔离；且 Router 从不执行任务，决策通道永不被 90s 的任务占住（排队 119 次 → 0 次）。

> 完成时间 p50/p95 与批处理轮数依赖时钟：C# 虚拟时钟给出确定值（上表），Node 版因真实定时器抖动会小幅波动（如批处理 7~8 轮）。
> 其余所有非时间数值两版一致。

---

## 建议的学习路径

1. 先读讲义对应的一讲，再跑对应演示，把控制台旁白/表格与讲义概念逐条对上。
2. 想改就改：每个演示顶部都有场景参数（任务文案、消息流、策略、`MaxBatch`、`AsyncTools` 等），
   改完重跑，观察数值与叙事如何变化——这正是“消融”的思路。得益于虚拟时钟，改动后的结果依然确定、可复现。
3. 想深挖机制，直接读 `Src/` 对应模块；文件头注释标了它对应讲义的哪一节。
4. 全部跑一遍确认环境正常：`dotnet run`。

## 边界与声明

- 本示例为 AI 辅助编写的**学习资料**，非原作者官方教材，**不是 Super-agent 的代码实现，也不是论文实验的复现**。
- 所有数值服务于“理解机制方向”，**不构成性能基准**，不应被引用为原作者的实测记录。
- 原文 PDF 及其内容的版权归原作者及相关权利人所有。
