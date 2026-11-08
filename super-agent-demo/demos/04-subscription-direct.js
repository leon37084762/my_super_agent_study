/**
 * 演示 04 —— 事件路由与订阅直通
 *
 * 对应讲义第 4 讲（原文第 3.2 节）。
 *
 * 第 3 讲回答"值不值得处理"，本讲回答"交给谁、怎么送达"。核心是两条投递路径：
 *   未订阅的 Thread -> 交 Router 判断归属（Slow Path，要调用模型）
 *   已订阅的 Thread -> 程序直通到 Worker（Fast Path，不调用模型）
 *
 * 演示四件事：
 *   1. 同一条 Thread，首个事件走 Router 分派，后续事件沿订阅直通，Router 不再花模型调用
 *   2. 直通不等于 Router 失明：Router 仍 observe 事件、维护全局状态
 *   3. CI 失败沿订阅直通到 Worker，Worker 自行追加"修改-重测"，无须 Router 逐条转发
 *   4. 超出任务范围的事件，Worker 取消订阅并交回 Router 重新分派
 *   5. 订阅是多对多：一个 Worker 可同时订阅工作 Thread 与 PR Thread
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';

/** 轮询等待某个条件成立（模拟秒超时）。 */
async function waitUntil(clock, pred, timeoutSec = 60) {
  const t0 = clock.now();
  while (clock.now() - t0 < timeoutSec) {
    if (pred()) return true;
    await clock.sleep(0.1);
  }
  return false;
}

// ---------------------------------------------------------------------------
// 第一部分：两级投递路径对比
// ---------------------------------------------------------------------------

async function partDeliveryPaths(trace) {
  const sys = buildSystem({
    title: '第一部分：未订阅交 Router（Slow Path） vs 已订阅直通（Fast Path）',
    msPerSimSecond: 40,
    globalStateTokens: 6000,
  });
  const { clock, bus, router } = sys;
  router.start();

  // 1) H01 尚无任何订阅者：事件只能交 Router 判断归属
  const r1 = await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  trace.note(`首个事件 -> stage=${r1.stage}（${r1.reason}）`);

  await waitUntil(clock, () => router.workers.size >= 1);
  const w1 = [...router.workers.values()][0];
  const roundsAfterDispatch = router.rounds.length;
  trace.note(`Router 花 ${roundsAfterDispatch} 轮模型调用判定归属 -> 分派 ${w1.id} 并订阅 H01`);

  // 2) H01 现在有了订阅者 W01：同 Thread 的后续事件走 Fast Path 直通
  const r2 = await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '登录超时又出现一次，频率变高了，附上新日志',
    author: 'user-1',
  });
  trace.note(`后续事件 -> stage=${r2.stage}（${r2.reason}）`);
  await clock.sleep(0.6);
  const roundsAfterDirect = router.rounds.length;

  trace.section('第一部分结果：投递路径与模型调用');
  trace.block(
    renderTable(
      ['事件', '目标 Thread', '投递路径', '是否触发 Router 模型调用'],
      [
        ['首条工作请求', 'H01（未订阅）', r1.stage, `是（rounds ${roundsAfterDispatch}）`],
        ['同 Thread 补充', 'H01（已订阅 W01）', r2.stage, `否（rounds 仍 ${roundsAfterDirect}）`],
      ],
    ),
  );
  const funnel = bus.funnel();
  trace.note(`总线漏斗：${JSON.stringify(funnel)}`);
  trace.note(`直通时 Router 仍观察事件、维护全局状态：observedWhileDirect=${router.observedWhileDirect}（第 4 讲自测题：直通≠Router 失明）`);

  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;

  // 多对多：W01 收尾前创建了 PR，于是同时订阅工作 Thread 与 PR Thread
  trace.section('第一部分补充：订阅是多对多');
  trace.note(`${w1.id} 当前订阅的 Thread：${[...w1.subscriptions].join('、') || '无'}`);
  trace.note('一个 Worker 可订阅多个 Thread（工作群 + PR）；反过来一个 Thread 也可被多个 Worker 订阅。');
  return { funnel, roundsAfterDispatch, roundsAfterDirect, w1 };
}

// ---------------------------------------------------------------------------
// 第二部分：CI 失败沿订阅直通，Worker 自我修正
// ---------------------------------------------------------------------------

async function partCiDirect(trace) {
  const sys = buildSystem({
    title: '第二部分：CI 失败事件直通到 Worker，自行追加"修改-重测"',
    msPerSimSecond: 40,
    globalStateTokens: 6000,
  });
  const { clock, bus, router } = sys;
  router.start();

  await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  await waitUntil(clock, () => router.workers.size >= 1);
  const w1 = [...router.workers.values()][0];
  const planBefore = w1.plan.length;
  trace.note(`${w1.id} 已启动，初始计划 ${planBefore} 步；此刻它刚进入执行，尚未跑完`);

  // CI 失败发布在 W01 订阅的 H01 上 -> 直通，不经过 Router 决策
  const ci = await bus.publish({
    source: Source.GH_CI,
    threadId: 'H01',
    text: 'CI 运行结果：构建通过，登录相关测试失败 2 项',
    author: 'ci-bot',
    payload: { status: 'failure', detail: 'auth 超时断言失败' },
  });
  trace.note(`CI 失败事件 -> stage=${ci.stage}（沿订阅直通，Router 未调用模型）`);

  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;

  const planAfter = w1.plan.length;
  const fixSteps = w1.plan.filter((s) => s.tag === 'fix');
  trace.section('第二部分结果：直通触发的自我修正闭环');
  trace.note(`计划从 ${planBefore} 步增长到 ${planAfter} 步，其中由 CI 直通追加的修复步骤 ${fixSteps.length} 个`);
  fixSteps.forEach((s) => trace.note(`  追加：${s.tool}(${s.target}) // ${s.note}`));
  trace.note('关键点：CI 事件不需要 Router 逐条转发，Worker 沿订阅直接收到并决定如何回应。');
  return { planBefore, planAfter, fixSteps: fixSteps.length };
}

// ---------------------------------------------------------------------------
// 第三部分：超出任务范围 -> 取消订阅并交回 Router
// ---------------------------------------------------------------------------

async function partOutOfScope(trace) {
  const sys = buildSystem({
    title: '第三部分：超出任务范围的事件，Worker 取消订阅并交回 Router 重新分派',
    msPerSimSecond: 40,
    globalStateTokens: 6000,
  });
  const { clock, bus, router } = sys;
  router.start();

  await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  await waitUntil(clock, () => router.workers.size >= 1);
  const w1 = [...router.workers.values()][0];
  trace.note(`${w1.id}（登录任务）已订阅 H01`);

  // 一条与登录无关的"导出"请求发到同一个 Thread：会先直通到 W01
  const r = await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '导出大文件时失败，日志显示内存溢出，需要优化导出功能',
    author: 'user-2',
  });
  trace.note(`导出请求 -> stage=${r.stage}（先沿 H01 订阅直通到 ${w1.id}）`);

  await waitUntil(clock, () => router.workers.size >= 2, 40);
  const w2 = [...router.workers.values()].find((w) => w.id !== w1.id);

  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;

  trace.section('第三部分结果：范围检查与重新分派');
  trace.note(`${w1.id} 判定"导出"超出本任务范围 -> 取消订阅 H01，把事件交回 Router`);
  trace.note(`${w1.id} 现存订阅：${[...w1.subscriptions].join('、') || '（已退出 H01）'}`);
  if (w2) trace.note(`Router 重新分派 ${w2.id}「${w2.task.slice(0, 16)}」处理导出请求`);
  else trace.note('注意：未观察到第二个 Worker，导出请求可能仍在队列中');
  trace.note('这就是"直通"与"绑死"的区别：Worker 收到不属于自己的事件时，能把它退回全局协调层。');
  return { w1Subs: [...w1.subscriptions], spawnedW2: !!w2 };
}

async function main() {
  const trace = new Trace(null, { title: '演示 04 · 事件路由与订阅直通' });

  const p1 = await partDeliveryPaths(trace);
  const p2 = await partCiDirect(trace);
  const p3 = await partOutOfScope(trace);

  trace.section('三部分小结');
  trace.block(
    renderTable(
      ['机制', '观察到的现象', '数值'],
      [
        ['两级投递路径', '未订阅交 Router，已订阅直通', `交Router ${p1.funnel.交Router决策} / 直通 ${p1.funnel.直通投递}`],
        ['直通省下的模型调用', 'Router 轮数不因直通事件增加', `${p1.roundsAfterDispatch} -> ${p1.roundsAfterDirect}`],
        ['CI 自我修正', '直通触发追加修复步骤', `计划 ${p2.planBefore} -> ${p2.planAfter} 步`],
        ['范围检查', '超范围事件交回 Router 重新分派', `派生第二个 Worker：${p3.spawnedW2 ? '是' : '否'}`],
      ],
    ),
  );

  takeaways(trace, [
    '路由要分两条路径：已订阅 Thread 走程序直通（Fast Path，不调用模型），未订阅 Thread 才交 Router 判断归属（Slow Path）。',
    '直通省下的是"每条后续消息都要重新判断归属"的模型调用；Thread 越长、消息越多，节省越明显。',
    '直通不代表 Router 失去全局信息：Router 仍然 observe 直通事件、更新摘要级全局状态，需要时才能做跨任务关联。',
    'CI 失败、评审意见这类后续事件沿订阅直达 Worker，Worker 自行决定如何回应（追加修复-重测），无须 Router 逐条转发。',
    '订阅是多对多且有边界：Worker 发现事件超出任务范围时取消订阅并交回 Router，避免把无关工作硬塞进当前上下文。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
