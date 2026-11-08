/**
 * 演示 10 —— 初步实验与证据解读：批处理压力测试 + 消融对比
 *
 * 对应讲义第 10 讲（原文第 7 节）。
 *
 * 原文的实验思路：
 *   7.1 用合成负载（约 300 条消息、其中约 20 条工作请求，其余闲聊/跟进/叫停/CI）压测系统；
 *       并给出若干"基线方案"：单 Session、单 Session + Subagent 工具（中性提示 / 优先委派提示）；
 *   7.2 批处理压力测试关注三个指标：batch 大小、单次请求耗时、每决策分摊时间；
 *       用"读缓存 Token（全部 Session 合计）"衡量上下文重读成本；
 *   具体编码工作用"只消耗时间的工具"代替；数值服务于理解机制方向，不是性能基准。
 *
 * 四部分：
 *   1. 批处理压力测试：突发流量涌入时的漏斗与三个核心指标
 *   2. 消融对比：A 关闭批处理（同一突发负载） / B 去掉直通（真实合成流）
 *   3. 与"单 Session + Subagent"基线对比（解析模型 vs 本系统实测）
 *   4. 证据解读：这些数字能说明什么、不能说明什么
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem, makeStream, replayStream, simulateSingleSession, percentile, fmtTok, fmtMin } from '../src/system.js';

// ---------------------------------------------------------------------------
// 负载：突发流量（用于批处理压测）
// ---------------------------------------------------------------------------

/**
 * 构造一次"突发流量"：n 条消息在极短时间窗口内涌入（模拟早高峰 / 告警风暴）。
 * 全部落在无人订阅的闲聊话题上，因此都会交给 Router 判断——正好用来观察批处理。
 */
function makeBurst(n = 30) {
  const texts = ['午饭吃什么', '今天下班早点走', '周末有人打球吗', '哈哈哈这个表情太好笑了', '恭喜发版，辛苦了', '收到，稍后看', '这个链接谁发的', '+1'];
  const msgs = [];
  for (let i = 0; i < n; i += 1) {
    msgs.push({
      at: Math.round(i * 0.03 * 100) / 100, // 每 0.03 模拟秒来一条，30 条约 0.9s 内涌完
      kind: 'burst',
      input: { source: Source.IM_WORK, threadId: `H-B${1 + (i % 3)}`, text: texts[i % texts.length], author: `user-${1 + (i % 5)}` },
    });
  }
  return msgs;
}

// ---------------------------------------------------------------------------
// 在给定负载上跑一遍双层系统，并采集指标
// ---------------------------------------------------------------------------

async function runDualLayer({ stream, maxBatch = 8, directDelivery = true, msPerSimSecond = 18, globalStateTokens = 9000 }) {
  const sys = buildSystem({ title: '', msPerSimSecond, maxBatch, directDelivery, globalStateTokens });
  const { clock, bus, router, stats } = sys;
  router.start();

  // 并发跟踪每个 Worker 的"到达 -> 完成"时间（用于与单会话基线对比完成时间分布）
  const completions = [];
  const seen = new Set();
  let stopTrack = false;
  const tracker = (async () => {
    while (!stopTrack) {
      for (const w of router.workers.values()) {
        if (w.status === 'closed' && !seen.has(w.id)) {
          seen.add(w.id);
          completions.push({ id: w.id, originTriggerId: w.originTriggerId, dispatchedAt: w.dispatchedAt, doneAt: clock.now() });
        }
      }
      await clock.sleep(0.05);
    }
  })();

  const published = await replayStream(bus, stream, clock);
  const arrivalById = new Map();
  for (const p of published) if (p.result?.trigger) arrivalById.set(p.result.trigger.id, p.at);

  await router.quiesce({ timeoutSimSec: 200 });
  stopTrack = true;
  await tracker;
  router.stop();
  await router.done;

  const times = completions.map((c) => c.doneAt - (arrivalById.get(c.originTriggerId) ?? c.dispatchedAt)).filter((x) => x >= 0);
  return { summary: router.summary(), funnel: bus.funnel(), stats: stats.snapshot(), times, workers: router.workers.size };
}

// ---------------------------------------------------------------------------
// 第一部分：批处理压力测试（突发流量）
// ---------------------------------------------------------------------------

function partStress(trace, r) {
  trace.section('第一部分 · 批处理压力测试：30 条消息在 ~1 秒内涌入');
  trace.block(renderTable(['漏斗阶段', '事件数'], Object.entries(r.funnel).map(([k, v]) => [k, v]), ['l', 'r']));
  const s = r.summary;
  trace.block(
    renderTable(
      ['指标（第 7.2 节）', '数值', '含义'],
      [
        ['① 平均 batch 大小', s.meanBatch.toFixed(2), '单轮合并处理的事件数（第 2 讲批处理）'],
        ['② 单轮平均耗时', `${s.meanRoundLatency.toFixed(2)}s`, '一次 Router 推理：长输入、短输出'],
        ['③ 每决策分摊时间', `${s.servicePerDecision.toFixed(3)}s`, '把一轮耗时摊到本轮多条事件上'],
        ['Router 轮数', s.routerRounds, '常驻层被唤醒并推理的次数'],
        ['决策数', s.decisions, '实际判断过的事件条数'],
        ['读缓存 Token(Router)', fmtTok(s.cacheReadTokens), '每轮都要重读常驻全局状态摘要'],
      ],
      ['l', 'r', 'l'],
    ),
  );
  trace.note(`突发 30 条只用 ${s.routerRounds} 轮就消化完（平均 batch ${s.meanBatch.toFixed(2)}）：上一轮推理期间到达的事件积压，下一轮合并处理。`);
  trace.note(`关键：每决策分摊时间 ${s.servicePerDecision.toFixed(3)}s 远小于单轮耗时 ${s.meanRoundLatency.toFixed(2)}s——批处理把"重读全局状态"的成本摊薄到多条事件上。`);
}

// ---------------------------------------------------------------------------
// 第二部分 A：批处理消融（同一突发负载）
// ---------------------------------------------------------------------------

function partAblationBatch(trace, batched, serial) {
  trace.section('第二部分 · 消融 A：关闭批处理（maxBatch=1，同一突发负载）');
  const row = (name, r) => [name, r.summary.decisions, r.summary.routerRounds, r.summary.meanBatch.toFixed(2), `${r.summary.servicePerDecision.toFixed(3)}s`, fmtTok(r.summary.cacheReadTokens)];
  trace.block(
    renderTable(['配置', '决策数', 'Router轮数', '平均batch', '每决策分摊', '读缓存Token(Router)'], [row('批处理开启（maxBatch=8）', batched), row('关闭批处理（maxBatch=1）', serial)], ['l', 'r', 'r', 'r', 'r', 'r']),
  );
  trace.note(
    `同样的 30 条突发消息：开启批处理只用 ${batched.summary.routerRounds} 轮、每决策分摊 ${batched.summary.servicePerDecision.toFixed(3)}s；` +
      `关闭后变成 ${serial.summary.routerRounds} 轮、每决策分摊 ${serial.summary.servicePerDecision.toFixed(3)}s（一轮只处理一条，全局状态被重读 ${serial.summary.routerRounds} 次）。`,
  );
  trace.note(`读缓存 Token：${fmtTok(batched.summary.cacheReadTokens)} → ${fmtTok(serial.summary.cacheReadTokens)}。这就是批处理压测要看的成本差。`);
}

// ---------------------------------------------------------------------------
// 第二部分 B：直通消融（真实合成流）
// ---------------------------------------------------------------------------

function partAblationDirect(trace, full, noDirect) {
  trace.section('第二部分 · 消融 B：去掉订阅直通（directDelivery=false，真实合成流）');
  const row = (name, r) => [name, r.summary.decisions, r.summary.routerRounds, r.funnel['直通投递'], r.funnel['交Router决策'], fmtTok(r.stats.cacheReadTokens), r.workers];
  trace.block(
    renderTable(['配置', '决策数', 'Router轮数', '直通投递', '交Router', '读缓存Token合计', 'Worker数'], [row('完整（含直通）', full), row('去掉直通', noDirect)], ['l', 'r', 'r', 'r', 'r', 'r', 'r']),
  );
  trace.note(
    `去掉直通后，已订阅 Thread 的事件（CI 结果、跟进补充）也要经 Router 判断归属：交 Router 事件 ${full.funnel['交Router决策']} → ${noDirect.funnel['交Router决策']}，` +
      `Router 轮数 ${full.summary.routerRounds} → ${noDirect.summary.routerRounds}。`,
  );
  trace.note(`全部 Session 读缓存 Token 合计 ${fmtTok(full.stats.cacheReadTokens)} → ${fmtTok(noDirect.stats.cacheReadTokens)}：直通让高频事件绕开 Router，省下大量重复的上下文重读。`);
  trace.note('消融（ablation）的意义：不是"加了机制变好了"，而是"拿掉机制确实变差了"，从而确认该机制在起作用。');
}

// ---------------------------------------------------------------------------
// 第三部分：与"单 Session + Subagent 工具"基线对比
// ---------------------------------------------------------------------------

function partBaseline(trace, stream, dual) {
  trace.section('第三部分 · 与"单 Session + Subagent 工具"基线对比（第 10 讲 7.1 方案三、四）');
  const neutral = simulateSingleSession({ messages: stream, delegation: 'neutral', workSec: 90, llmSec: 2.5, contextTokens: 12000, seed: 7 });
  const prefer = simulateSingleSession({ messages: stream, delegation: 'prefer', workSec: 90, llmSec: 2.5, contextTokens: 12000, seed: 7, preferRate: 0.8 });
  const dP50 = percentile(dual.times, 50);
  const dP95 = percentile(dual.times, 95);
  trace.block(
    renderTable(
      ['方案', '委派/Worker', '读缓存Token合计', '完成p50', '完成p95', '决策通道是否被任务占住'],
      [
        ['单 Session + 中性提示', neutral.delegated, fmtTok(neutral.cacheReadTokens), fmtMin(neutral.p50), fmtMin(neutral.p95), `是·串行执行·排队 ${neutral.interruptions} 次`],
        ['单 Session + 优先委派', prefer.delegated, fmtTok(prefer.cacheReadTokens), fmtMin(prefer.p50), fmtMin(prefer.p95), `部分·偶发自己做·排队 ${prefer.interruptions} 次`],
        ['双层架构（本系统实测）', `${dual.workers} Worker`, fmtTok(dual.stats.cacheReadTokens), fmtMin(dP50), fmtMin(dP95), '否·Router 只决策不执行'],
      ],
      ['l', 'r', 'r', 'r', 'r', 'l'],
    ),
  );
  trace.note(`① 读缓存 Token（尺度一致、最硬的对比）：单会话每条消息都重读整段上下文 -> ${fmtTok(neutral.cacheReadTokens)}；双层架构 Router 只读摘要、Worker 上下文彼此隔离 -> ${fmtTok(dual.stats.cacheReadTokens)}，约 ${(neutral.cacheReadTokens / Math.max(1, dual.stats.cacheReadTokens)).toFixed(1)}× 差距。`);
  trace.note(`② 决策通道：单会话一旦亲自执行 90s 的任务就无法响应新消息（排队 ${neutral.interruptions} 次）；双层架构里 Router 从不执行任务，Worker 并发跑，新消息始终能被决策（排队 0 次）。`);
  trace.note(`③ 委派稳定性：中性提示下模型几乎不主动委派（${neutral.delegated} 次）；优先委派也可能"任务小就自己做"（${prefer.delegated} 次）。双层架构由 Router 强制分派，不依赖模型自觉。`);
  trace.note('口径提示：单 Session 是解析模型（workSec=90 代表一次真实编码），双层是合成负载下的实测且教学计划被压缩；完成时间只看量级与趋势，重点比①②③。');
}

// ---------------------------------------------------------------------------
// 第四部分：证据解读
// ---------------------------------------------------------------------------

function partEvidence(trace) {
  trace.section('第四部分 · 证据解读：这些数字能说明什么、不能说明什么');
  trace.block(
    renderTable(
      ['观察到的现象', '能支持的结论', '不能推出的结论'],
      [
        ['突发流量下批处理减少轮数与重读', '合并决策能摊薄全局状态的重读成本', '不能给出真实模型的绝对延迟'],
        ['去掉直通后交 Router 事件与 Token 上升', '订阅直通确实减少了 Router 的介入', '不能量化真实 IM 环境下的收益'],
        ['单会话读缓存 Token 远高于双层', '串行主会话反复重读整段上下文', '不代表某个具体产品的真实表现'],
        ['MockLLM 延迟随输入/输出变化', '保留了对比所需的定性性质', '不是真实模型的 Token/延迟曲线'],
      ],
      ['l', 'l', 'l'],
    ),
  );
  trace.note('第 10 讲的态度：先做"能跑起来的最小实验"，用消融与对照确认机制的方向，再谈规模与真实性。');
  trace.note('教学替身的数值服务于"理解机制为何有效"，不应被当成性能基准；真实结论需在真实模型与真实负载上复做。');
}

// ---------------------------------------------------------------------------

async function main() {
  const trace = new Trace(null, { title: '演示 10 · 初步实验与证据解读' });

  // 负载一：突发流量（批处理压测）；负载二：真实合成流（直通消融 + 基线对比）
  const burst = makeBurst(30);
  const stream = makeStream({ total: 120, workCount: 8, durationSec: 120, seed: 20260711, workSeconds: 6 });
  const kinds = {};
  for (const m of stream) kinds[m.kind] = (kinds[m.kind] ?? 0) + 1;
  trace.section('实验负载：突发流量 + 合成消息流（按比例缩放的教学替身）');
  trace.note(`突发负载：${burst.length} 条消息在 ~0.9 模拟秒内涌入，全部落在无人订阅的话题上（都需 Router 判断）。`);
  trace.note(`合成流：${stream.length} 条消息跨越约 ${Math.max(...stream.map((m) => m.at)).toFixed(0)} 模拟秒；构成：${Object.entries(kinds).map(([k, v]) => `${k}×${v}`).join('，')}。`);
  trace.note('原文约 300 条消息 / 20 条工作请求，这里按比例缩小以便几秒内跑完；比例与角色保持一致。');

  const burstBatched = await runDualLayer({ stream: burst, maxBatch: 8, msPerSimSecond: 30 });
  const burstSerial = await runDualLayer({ stream: burst, maxBatch: 1, msPerSimSecond: 30 });
  const full = await runDualLayer({ stream, maxBatch: 8, directDelivery: true });
  const noDirect = await runDualLayer({ stream, maxBatch: 8, directDelivery: false });

  partStress(trace, burstBatched);
  partAblationBatch(trace, burstBatched, burstSerial);
  partAblationDirect(trace, full, noDirect);
  partBaseline(trace, stream, full);
  partEvidence(trace);

  trace.section('全演示小结');
  trace.block(
    renderTable(
      ['机制', '演示结果'],
      [
        ['批处理', `突发 30 条用 ${burstBatched.summary.routerRounds} 轮消化（平均 batch ${burstBatched.summary.meanBatch.toFixed(2)}），每决策分摊 ${burstBatched.summary.servicePerDecision.toFixed(3)}s`],
        ['关闭批处理消融', `Router 轮数 ${burstBatched.summary.routerRounds} → ${burstSerial.summary.routerRounds}，读缓存 Token ${fmtTok(burstBatched.summary.cacheReadTokens)} → ${fmtTok(burstSerial.summary.cacheReadTokens)}`],
        ['去掉直通消融', `交 Router 事件 ${full.funnel['交Router决策']} → ${noDirect.funnel['交Router决策']}，读缓存 Token ${fmtTok(full.stats.cacheReadTokens)} → ${fmtTok(noDirect.stats.cacheReadTokens)}`],
        ['对比单会话基线', `读缓存 Token ${fmtTok(full.stats.cacheReadTokens)}（双层） vs ${fmtTok(simulateSingleSession({ messages: stream, delegation: 'neutral', contextTokens: 12000 }).cacheReadTokens)}（单会话中性）`],
      ],
    ),
  );

  takeaways(trace, [
    '用合成负载做"能跑起来的最小实验"：约 300 条消息里只有约 20 条工作请求，其余是闲聊、跟进、叫停与 CI。',
    '批处理压力测试看三个指标——batch 大小、单次请求耗时、每决策分摊时间；突发流量下批处理把一轮推理的成本摊到多条事件上。',
    '消融实验通过"关掉机制看指标变差"来验证机制有效：关闭批处理推高轮数与重读，去掉直通推高交 Router 事件与读缓存 Token。',
    '与"单 Session + Subagent"基线相比，双层架构最硬的收益是读缓存 Token 量级更低、决策通道永不被任务占住、分派不依赖模型自觉。',
    '证据要会解读：教学替身的数值只说明机制方向，不能当性能基准；真实结论需在真实模型与真实负载上复做。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exit(1); // 强制退出：避免出错时轮询定时器让 Node 永不退出
});
