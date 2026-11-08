# Super-agent 教学演示（super-agent-demo）

配套《Super-agent 设计探索：迈向更自主的 Agent 系统》学习讲义（第 2~10 讲）的**可运行示例代码**。
零依赖 Node.js（ESM）实现，用最小可读的"教学替身"把讲义里的每个机制**真的跑起来**，边看输出边对照讲义理解。

> **定位声明（务必先读）**
> 本目录是**帮助理解讲义的教学示例**，不是 Super-agent 的官方实现，也不是原论文的代码复现或性能基准。
> 所有模型都是规则化的 Mock（`MockLLM`），所有"编码工作"都用只消耗时间的替身工具模拟，
> 所有数值只用于说明**机制方向**（哪个更省、哪个更快、关掉某机制指标会怎样变差），**不代表任何真实产品或真实模型的绝对表现**。
> 这一点与仓库根 `README.md` 的"也不是 Super-agent 的代码实现……未运行原型或复现实验"表述一致。

---

## 快速开始

**环境要求**：Node.js ≥ 18（开发验证于 v24）。**无需 `npm install`**——整个项目零第三方依赖。

```bash
# 进入本目录
cd super-agent-demo

# 方式一：串跑全部 9 个演示（第 2~10 讲），末尾给出通过/失败汇总
npm run all
#   等价于  node demos/run-all.js
#   只跑指定编号：node demos/run-all.js 04 07

# 方式二：单独跑某一讲
npm run d02      # 双层架构
npm run d09      # Watchdog 与异常恢复
node demos/10-batching-ablation.js   # 直接调文件亦可
```

每个演示都是**独立进程、自包含**，运行几秒即结束，控制台会打印带时间戳的中文旁白、对比表格与"本讲要点"。

> Windows PowerShell 小贴士：若把输出重定向到文件，中文可能出现乱码（控制台编码所致）。
> 直接在终端查看即可；`npm run all` 会另用 Node 以 UTF-8 写出 `tmp/run-all-summary.txt` 留档。

---

## 演示与讲义映射

| 演示 | 文件 | 对应讲义 | 原文 | 核心机制（看点） |
|---|---|---|---|---|
| 02 | `02-dual-layer.js` | 第 2 讲 | 2、2.1、2.2 | 双层架构：单 Session / 每事件一 Session / Router-Worker 三方案对比，连续性与吞吐量如何两全 |
| 03 | `03-event-filter.js` | 第 3 讲 | 3 开头、3.1 | 两级事件过滤：程序层 Fast Path（确定性规则，不调模型）+ 语义层 Slow Path（模型判断"值不值得行动"，策略可用自然语言调） |
| 04 | `04-subscription-direct.js` | 第 4 讲 | 3.2 | 事件路由与订阅直通：未订阅交 Router 判断归属，已订阅直通 Worker（不调模型）；直通不等于 Router 失明；超范围事件退订交回 |
| 05 | `05-chain-of-recall.js` | 第 5 讲 | 3.3 | 上下文组织与链式召回：Inline / Spill / 唯一 ID 引用图逐跳展开；"截断 ≠ Spill"（必须留读回入口）；与两种对照方案的 Token/往返对比 |
| 06 | `06-speculative.js` | 第 6 讲 | 3.4 | 提前行动与推测执行：三道闸门 `shouldAttempt` / `shouldContinue` / `shouldDeliver`；验证通过才交付 vs 未复现安静丢弃 |
| 07 | `07-async-full-duplex.js` | 第 7 讲 | 4.1、4.2 | 面向人的交互：异步 ReAct Loop（`start()` 立即回执、完成后回投、`stop()`）、新输入在"当前推理步结束后"处理；多信道表达（IM 低打扰 / GitHub·邮件 高密度） |
| 08 | `08-state-separation.js` | 第 8 讲 | 5.1、5.2、5.3 + BYOM | 计算/状态/环境分离：状态落盘 → "销毁"实例 → 设备上线 → 全新实例恢复续跑；端侧 daemon 执行；间歇在线设备离线挂起不丢进度 |
| 09 | `09-watchdog.js` | 第 9 讲 | 6 | Watchdog 与异常恢复：独立 Session + 定时消息驱动的外部 Control Loop；区分"活动量"与"实质进展"；五档判定 healthy/watch/stalled/drift/infra；在线介入（注入约束/提示、延后重试）；维护者自然语言询问 |
| 10 | `10-batching-ablation.js` | 第 10 讲 | 7、7.1、7.2 | 初步实验与证据解读：批处理压测三指标（batch 大小 / 单轮耗时 / 每决策分摊）+ 消融（关闭批处理、去掉直通）+ 与"单 Session + Subagent"基线对比 |

> 第 1 讲（目标与整体架构）与第 11 讲（开放问题与串讲）偏综述与讨论，无独立演示；其概念贯穿在上述各讲之中。

---

## 目录结构与模块职责

```
super-agent-demo/
├── package.json          # 零依赖；scripts: all + d02~d10
├── README.md             # 本文件
├── demos/                # 9 个演示 + 串跑脚本（每个都可独立运行）
│   ├── 02-dual-layer.js  ... 10-batching-ablation.js
│   └── run-all.js        # 依次串跑，超时保护，写 UTF-8 汇总
├── src/                  # 教学替身的"系统实现"（9 个模块）
│   ├── core.js           # 事件模型(Trigger/Thread)、模拟时钟 Clock、可读日志 Trace、renderTable/takeaways、Source/Policy
│   ├── llm.js            # MockLLM(延迟+Token 计费)、judgeWorthActing/judgeRouting/judgeProgress、WORK_SIGNALS、LLMStats
│   ├── context.js        # Inline/Spill/局部性/链式召回；RecallSession 与唯一 ID 引用图
│   ├── bus.js            # 事件总线：两级过滤(Fast Path) + Thread 订阅直通；funnel 漏斗统计
│   ├── router.js         # Router 层：全局协调 + 批处理 + 统一对外回复 + 链式召回注入 + quiesce
│   ├── worker.js         # Worker 层：隔离上下文执行 + 异步工具(start/stop) + 推测执行三道闸门 + 序列化(toJSON/fromJSON/resume)
│   ├── channels.js       # 多信道表达：IM 低打扰 / GitHub·邮件 高密度；单次回复词数预算
│   ├── infra.js          # SessionStore(状态持久化) + Daemon(端侧执行环境/BYOM/间歇在线) + Watchdog(外部 Control Loop 五档判定)
│   └── system.js         # buildSystem() 组装 + TASKS 任务模板 + makeStream/replayStream 合成负载 + simulateSingleSession 基线模型 + 统计工具
└── tmp/                  # 运行产物（汇总文件等），已在根 .gitignore 忽略
```

模块依赖是自底向上的：`core` ← `llm`/`context`/`channels` ← `bus`/`worker` ← `router` ← `infra` ← `system`，
演示脚本只从 `system`（组装）、`core`（日志/表格）与个别模块（如 `llm` 的 `judgeProgress`、`context` 的 `RecallSession`、`infra` 的 `Watchdog`）按需导入。

---

## 教学替身的关键约定

为了"能跑、可读、公平对比"，示例对真实系统做了这些简化，阅读时请带着这些前提：

- **MockLLM 不是真模型**：延迟 = 固定开销 + prefill + decode（按 Token 计），"语义判断"是规则匹配。
  三档模型画像对应讲义：`lite-router`（Router 输入长输出短，用轻量模型）、`strong-worker`（Worker 决定结果，用最强模型）、`watchdog`。
- **模拟时钟**：`Clock(msPerSimSecond)` 把"模拟秒"映射到真实毫秒（默认 1 模拟秒 = 80ms），日志时间戳是**模拟时间**。
- **编码工作是替身**：第 10 讲原文说"具体编码工作用一个只消耗时间的工具代替"，因此任务不真的改代码，只按设计走完事件与工具流转（`mock-coding-work`）。
- **任务文案需含"工作信号"**：只有包含 `WORK_SIGNALS`（如"报错/失败/修复/优化/排查/超时/bug"等）的消息才会被判定为值得分派，用于演示语义过滤。
- **合成负载可复现**：消息流用固定种子（`mulberry32`）生成，但真实异步调度会引入轻微时序抖动，**同一演示多次运行的具体数字可能略有出入**。

---

## 代表性运行结果（演示 10）

下面是**一次代表性运行**的关键数字，用于说明"机制方向"，非性能基准；你自己跑出来的数可能小幅波动。

**① 批处理压测（突发 30 条消息涌入）**

| 指标 | 数值 | 含义 |
|---|---|---|
| 接收事件 | 30 | 突发流量 |
| 平均 batch 大小 | 5.00 | 上一轮推理期间到达的事件积压，下一轮合并处理 |
| 单轮耗时 | ~1.93s | 一次 Router 推理 |
| 每决策分摊时间 | ~0.48s | 把一轮成本摊到本轮多条事件上（≪ 单轮耗时） |
| Router 轮数 | 6 | 常驻层被唤醒并推理的次数 |
| 读缓存 Token(Router) | ~5.4 万 | 每轮都要重读常驻全局状态摘要 |

**② 消融对比（关掉机制，看指标变差）**

| 消融 | 开启 | 关闭 | 说明 |
|---|---|---|---|
| 批处理（同一突发负载） | 6 轮 / 5.4 万 Token | 30 轮 / 27.0 万 Token | 一轮只处理一条 → 全局状态被重读 30 次 |
| 订阅直通（真实合成流） | 交 Router 31 / 29.8 万 Token / 11 Worker | 交 Router 62 / 54.9 万 Token / 25 Worker | 已订阅事件也要经 Router 判断归属 |

**③ 与"单 Session + Subagent"基线对比**

| 方案 | 委派/Worker | 读缓存 Token 合计 | 完成 p50 | 完成 p95 | 决策通道是否被任务占住 |
|---|---|---|---|---|---|
| 单 Session + 中性提示 | 0 | 144.0 万 | 7.8 分 | 14.4 分 | 是·串行执行·排队 119 次 |
| 单 Session + 优先委派 | 7 | 144.0 万 | 4.9 分 | 5.7 分 | 部分·偶发自己做·排队 119 次 |
| 双层架构（本系统实测） | 11 Worker | 29.8 万 | 0.4 分 | 0.6 分 | 否·Router 只决策不执行 |

最硬的一条对比：读缓存 Token **144.0 万 → 29.8 万（约 4.8×）**——单会话每条消息都重读整段上下文，
双层架构里 Router 只读摘要、Worker 上下文彼此隔离；且 Router 从不执行任务，决策通道永不被 90s 的任务占住（排队 119 次 → 0 次）。

---

## 建议的学习路径

1. 先读讲义对应的一讲，再跑对应演示，把控制台旁白/表格与讲义概念逐条对上。
2. 想改就改：每个演示顶部都有场景参数（任务文案、消息流、策略、`maxBatch`、`asyncTools` 等），
   改完重跑，观察数值与叙事如何变化——这正是"消融"的思路。
3. 想深挖机制，直接读 `src/` 对应模块；文件头注释标了它对应讲义的哪一节。
4. 全部跑一遍确认环境正常：`npm run all`。

## 边界与声明

- 本示例为 AI 辅助编写的**学习资料**，非原作者官方教材，**不是 Super-agent 的代码实现，也不是论文实验的复现**。
- 所有数值服务于"理解机制方向"，**不构成性能基准**，不应被引用为原作者的实测记录。
- 原文 PDF 及其内容的版权归原作者及相关权利人所有。
