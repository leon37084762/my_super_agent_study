/**
 * 演示 02 —— 双层架构：连续性与吞吐量的权衡
 *
 * 对应讲义第 2 讲（原文第 2、2.1、2.2 节）。
 *
 * 三种会话组织方式跑同一个场景：
 *   方案 A  所有事件共用一个 Session   -> 上下文连续，但串行、多任务过程混杂
 *   方案 B  每个事件新建一个 Session   -> 有利于并发，但缺少全局上下文、交互割裂
 *   方案 C  Router / Worker 双层架构   -> 全局连续性与并发执行分开组织（真实运行）
 *
 * 场景（取自讲义第 2 讲补充案例）：
 *   t=0.0s  用户提出「修复登录超时」
 *   t=1.5s  用户补充「先不要改接口」        <- 必须与正在执行的任务建立联系
 *   t=4.0s  用户提出「优化导出功能」        <- 新任务不必等待旧任务完成
 *
 * 方法说明：先真实运行方案 C，量出"单个任务从分派到回复"的执行成本，
 * 再用同一成本参数计算 A / B 的数值模型，避免三套方案使用不一致的假设。
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';

const REWORK = 8; // 叫停没有及时关联到任务时的返工成本（模拟秒，教学设定）
const T1 = 0; // 「修复登录超时」到达时刻
const T_STOP = 1.5; // 「先不要改接口」到达时刻
const T2 = 4; // 「优化导出功能」到达时刻

/** 方案 C：Router / Worker 双层架构，真实运行。 */
async function runDualLayer() {
  const sys = buildSystem({ title: '方案 C：Router / Worker 双层架构（真实运行）', msPerSimSecond: 25, globalStateTokens: 9000 });
  const { clock, bus, router, trace } = sys;

  trace.note('Router 只做交互与分派；Worker 在隔离上下文中并发执行，不直接回复用户');
  router.start(); // 常驻循环，不阻塞

  const publishAt = async (at, input) => {
    const wait = at - clock.now();
    if (wait > 0) await clock.sleep(wait);
    return bus.publish(input);
  };

  await publishAt(T1, {
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  // 关键：叫停发生在主对话 H00（未被任何 Worker 订阅），必须由 Router 关联到在跑任务
  await publishAt(T_STOP, { source: Source.IM_WORK, threadId: 'H00', text: '先不要改接口，等评审结论', author: 'user-1', payload: { threadTitle: '与用户的主对话' } });
  await publishAt(T2, {
    source: Source.IM_WORK,
    threadId: 'H02',
    text: '导出大文件时失败，日志显示内存溢出，需要优化导出功能',
    author: 'user-2',
    payload: { threadTitle: '工作群：导出问题' },
  });

  const settled = await router.quiesce({ timeoutSimSec: 90 });
  router.stop();
  await router.done;

  const byText = (prefix) => [...router.workers.values()].find((w) => w.task.startsWith(prefix));
  const w1 = byText('线上登录');
  const w2 = byText('导出大文件');
  const doneAt = (w) => router.replies.find((r) => r.workerId === w?.id)?.at ?? null;

  trace.section('方案 C 结果');
  trace.note(`Router 轮数 ${router.rounds.length}，决策 ${router.rounds.reduce((s, r) => s + r.batchSize, 0)} 条，Worker ${router.workers.size} 个`);
  trace.note(`${w1?.id} 于 t=${w1?.dispatchedAt.toFixed(1)}s 分派，t=${doneAt(w1).toFixed(1)}s 回复；中途接收全局约束 ${w1?.constraints.length} 条，接口改动步骤被移除 -> 无返工`);
  trace.note(`${w2?.id} 于 t=${w2?.dispatchedAt.toFixed(1)}s 分派，t=${doneAt(w2).toFixed(1)}s 回复；与 ${w1?.id} 并发推进，不必等前者结束`);
  trace.note(`对外回复统一由 Router 发出 ${router.replies.length} 条（Worker 的推理与工具过程不外泄）`);
  if (!settled) trace.note('注意：quiesce 超时，仍有 Worker 未结束');

  return {
    方案: 'C 双层架构',
    任务1完成: doneAt(w1),
    任务2完成: doneAt(w2),
    并发: `是（${router.workers.size} 个 Worker）`,
    叫停关联: w1?.constraints.length ? '是（Router 广播到在跑任务）' : '否',
    上下文: '按任务隔离',
    对外回复: `统一 ${router.replies.length} 条`,
    exec1: doneAt(w1) - w1.dispatchedAt,
    exec2: doneAt(w2) - w2.dispatchedAt,
  };
}

/** 方案 A：所有事件进入同一个 Session，串行执行。执行成本用方案 C 的实测值。 */
function simulateSharedSession(trace, exec1, exec2) {
  trace.section('方案 A：所有事件共用一个 Session（数值模型，执行成本取自方案 C 实测）');
  const stopSeen = T1 + exec1; // 串行会话要等当前任务跑完才能看新事件
  const work1Done = stopSeen + REWORK;
  const work2Done = work1Done + exec2;
  trace.note(`t=${T1}s 任务1 开始，独占唯一的 ReAct Loop（实测执行成本 ${exec1.toFixed(1)}s）`);
  trace.note(`t=${T_STOP}s「先不要改接口」到达，但会话正忙 -> 排队到 t=${stopSeen.toFixed(1)}s 才被看到，接口已改完 -> 返工 ${REWORK}s`);
  trace.note(`t=${work1Done.toFixed(1)}s 任务1 完成；t=${T2}s 到达的任务2 一直排队，t=${work2Done.toFixed(1)}s 才完成`);
  trace.note('两个任务的日志、测试结果不断进入同一上下文，互相稀释注意力');
  return { 方案: 'A 共用一个 Session', 任务1完成: work1Done, 任务2完成: work2Done, 并发: '否（串行）', 叫停关联: '否（迟到，返工）', 上下文: '混杂', 对外回复: '统一 1 个会话' };
}

/** 方案 B：每个事件都新建独立 Session。 */
function simulateSessionPerEvent(trace, exec1, exec2) {
  trace.section('方案 B：每个事件新建一个 Session（数值模型，执行成本取自方案 C 实测）');
  const work1Done = T1 + exec1 + REWORK;
  const work2Done = T2 + exec2;
  trace.note(`t=${T1}s S1 启动执行任务1`);
  trace.note(`t=${T_STOP}s S2 启动处理「先不要改接口」—— S2 是全新会话，不知道 S1 在做什么，只能回一句「已记录」`);
  trace.note(`t=${(T1 + exec1).toFixed(1)}s S1 按旧要求改完接口，事后发现冲突 -> 返工 ${REWORK}s，t=${work1Done.toFixed(1)}s 收尾`);
  trace.note(`t=${T2}s S3 启动执行任务2，与 S1 并发，t=${work2Done.toFixed(1)}s 完成（没有分派开销，单任务反而更快）`);
  trace.note('S1 / S2 / S3 各自向用户回复，用户面对三个互不知情的会话');
  return { 方案: 'B 每事件新建 Session', 任务1完成: work1Done, 任务2完成: work2Done, 并发: '是', 叫停关联: '否（无全局上下文）', 上下文: '隔离但割裂', 对外回复: '各自回复 3 条' };
}

async function main() {
  const trace = new Trace(null, { title: '演示 02 · 双层架构：连续性与吞吐量的权衡' });

  const c = await runDualLayer();
  const a = simulateSharedSession(trace, c.exec1, c.exec2);
  const b = simulateSessionPerEvent(trace, c.exec1, c.exec2);

  trace.section('三种方案对比（时间为从 t=0 起的模拟秒）');
  const rows = [a, b, c].map((r) => [
    r.方案,
    `${r.任务1完成.toFixed(1)}s`,
    `${r.任务2完成.toFixed(1)}s`,
    `${((r.任务1完成 + r.任务2完成) / 2).toFixed(1)}s`,
    r.并发,
    r.叫停关联,
    r.对外回复,
  ]);
  trace.block(renderTable(['方案', '任务1完成', '任务2完成', '平均', '并发', '叫停关联', '对外回复'], rows));
  trace.note(`方案 C 为真实运行；A / B 使用 C 实测的单任务执行成本（${c.exec1.toFixed(1)}s / ${c.exec2.toFixed(1)}s）与 ${REWORK}s 返工假设。`);
  trace.note('方案 B 的任务2 看起来最快，是因为它跳过了全局协调；代价体现在任务1 的返工与三条各自发出的回复上。');

  takeaways(trace, [
    '共用一个 Session 保住连续性，代价是串行执行与过程混杂；每事件新建 Session 换来并发，代价是全局上下文与交互的割裂。',
    '双层架构把两类职责分开：Router 常驻负责全局关联、交互与分派；Worker 在隔离上下文中并发执行具体任务。',
    '「先不要改接口」之所以生效，不是因为它进了同一个会话，而是因为 Router 维护任务关联，把这条不含任务关键词的全局约束广播给了在跑 Worker。',
    'Router 不直接执行任务，所以在 Worker 跑测试的十几秒里仍能接收并分派新任务；这就是它保持轻量的原因。',
    '两层是逻辑职责而非 Session 数量：Worker 内部还能开 Subagent 或 Agent Team，对 Router 透明。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
