/**
 * 演示 07 —— 面向人的交互：异步全双工 + 多信道表达
 *
 * 对应讲义第 7 讲（原文第 4.1、4.2 节）。
 *
 * 4.1 异步 ReAct Loop：
 *   - 工具用 start() 而非阻塞式 run()：立即拿到 Started 回执，后台完成后再作为新事件回投
 *   - Started 只说明"启动成功"，不代表完成，更不代表成功
 *   - 新输入在"当前推理步结束后"处理，不能随时打断一次正在进行的推理
 *   - 因此耗时工具运行期间仍能接收并处理用户输入 = 全双工体验
 * 4.2 多信道表达：
 *   - Worker 不直接回复用户，由 Router 统一按渠道组织表达
 *   - IM 轻量低打扰：需要决策/验收/回答/告警才通知，抑制中间进展；单次回复有词数上限
 *   - GitHub / 邮件 结构化高密度：交代背景、改动、验证、待确认
 *
 * 三部分：全双工对比 / start·Started·stop 语义 / 多信道表达与低打扰抑制
 */

import { Clock, Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';
import { AsyncToolRunner } from '../src/worker.js';
import { CHANNEL_NORMS, Purpose, auditChannel, countWords, renderForChannel, shouldNotify } from '../src/channels.js';

async function waitUntil(clock, pred, timeoutSec = 60) {
  const t0 = clock.now();
  while (clock.now() - t0 < timeoutSec) {
    if (pred()) return true;
    await clock.sleep(0.05);
  }
  return false;
}

// ---------------------------------------------------------------------------
// 第一部分：同步 run() vs 异步 start()（全双工）
// ---------------------------------------------------------------------------

async function runDuplex(asyncTools) {
  const sys = buildSystem({ title: '', msPerSimSecond: 40, globalStateTokens: 6000, asyncTools });
  const { bus, router, clock } = sys;
  router.start();
  await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  await waitUntil(clock, () => router.workers.size >= 1);
  const w = [...router.workers.values()][0];

  // 等 Worker 进入那段耗时测试（状态 waiting-tool），此时用户又发来一条补充
  await waitUntil(clock, () => w.status === 'waiting-tool', 40);
  const tSent = clock.now();
  await bus.publish({ source: Source.IM_WORK, threadId: 'H01', text: '登录超时又出现一次，频率变高了，附新日志', author: 'user-1' });

  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;

  return {
    mode: asyncTools ? '异步 start()' : '同步 run()',
    blockedInputs: w.blockedInputs,
    blockedSeconds: w.blockedSeconds,
    // 同步模式下补充消息要等测试跑完才被处理；异步模式下几乎即时被吸收
    userWait: asyncTools ? clock.now() - tSent - w.blockedSeconds : w.blockedSeconds,
    processedDuringTool: asyncTools ? '是（测试运行期间即纳入上下文）' : '否（阻塞到 run() 返回）',
  };
}

async function partDuplex(trace) {
  trace.section('第一部分 · 同步 run() vs 异步 start()：耗时工具运行期间能否响应用户');
  trace.note('场景：Worker 正在跑一段 5 秒的测试，期间用户又补充了一条与登录相关的信息。');
  const sync = await runDuplex(false);
  const asyn = await runDuplex(true);
  trace.block(
    renderTable(
      ['工具调用方式', '期间被阻塞的输入数', '阻塞时长', '补充消息何时被处理'],
      [
        [sync.mode, sync.blockedInputs, `${sync.blockedSeconds.toFixed(1)}s`, sync.processedDuringTool],
        [asyn.mode, asyn.blockedInputs, `${asyn.blockedSeconds.toFixed(1)}s`, asyn.processedDuringTool],
      ],
      ['l', 'r', 'r', 'l'],
    ),
  );
  trace.note('同步 run()：调用后整个会话被占住，测试返回前无法吸收任何新输入，用户补充只能排队。');
  trace.note('异步 start()：立即拿到回执，推理循环在等待完成事件的同时持续排空收件箱 = 全双工。');
  trace.note('注意：即便异步，新输入也是在"当前推理步结束后"处理，不会打断一次正在进行的模型推理。');
  return { sync, asyn };
}

// ---------------------------------------------------------------------------
// 第二部分：start() / Started 回执 / stop() 的语义
// ---------------------------------------------------------------------------

async function partRunnerSemantics(trace) {
  trace.section('第二部分 · start() 立即回执、完成事件回投、stop() 可中止');
  const clock = new Clock(60);
  const runner = new AsyncToolRunner({ clock, trace });

  let completion = null;
  const h = runner.start({
    name: 'run_tests',
    cmd: 'npm test -- flaky',
    simSeconds: 3,
    outcome: 'failed',
    output: '2 项断言失败',
    onDone: (x) => {
      completion = x;
    },
  });
  trace.note(`start() 立即返回回执：id=${h.id}，status=${h.status}（这只是"启动成功"，不代表完成，更不代表通过）`);
  await clock.sleep(0.5);
  const mid = runner.status(h.id);
  trace.note(`0.5s 后查询：status=${mid.status}，已运行 ${mid.elapsed.toFixed(1)}s —— 仍在后台跑，主循环没有被占住`);
  await clock.sleep(3);
  trace.note(`完成事件回投：status=${completion?.status}，output=${completion?.output} —— 真实结果以完成事件为准，这里是 failed`);

  const h2 = runner.start({ name: 'build', cmd: 'npm run build', simSeconds: 5, outcome: 'succeeded' });
  await clock.sleep(0.3);
  const stopRunning = runner.stop(h2.id);
  trace.note(`对仍在运行的 ${h2.id} 调用 stop() -> ok=${stopRunning.ok}（${stopRunning.reason}），status=${h2.status}`);
  const stopFinished = runner.stop(h.id);
  trace.note(`对已结束的 ${h.id} 再调用 stop() -> ok=${stopFinished.ok}（${stopFinished.reason}）`);
  trace.note('第 7 讲自测题：把 Started 当成成功，就会在测试其实失败时误报"已完成"。');
  return { startedStatus: h.status, completionStatus: completion?.status, stoppedOk: stopRunning.ok };
}

// ---------------------------------------------------------------------------
// 第三部分：多信道表达 + 低打扰抑制
// ---------------------------------------------------------------------------

const FACT = {
  topic: '登录超时修复',
  background: '线上登录偶发超时，用户反馈附了复现步骤',
  change: '补充超时处理并复位界面加载状态',
  verification: 'auth 相关 12 项测试通过',
  pending: '超时提示文案待产品确认',
  ref: 'P01',
};

async function partChannels(trace) {
  trace.section('第三部分 · 同一个经过验证的事实，按渠道渲染成不同密度');
  for (const ch of ['im', 'github_pr', 'email']) {
    const text = renderForChannel(ch, FACT);
    const audit = auditChannel(ch, text);
    const norm = CHANNEL_NORMS[ch];
    trace.note(`渠道：${norm.label}（${norm.density}，词数上限 ${norm.maxWords === Infinity ? '无' : norm.maxWords}）`);
    trace.block(text);
    trace.note(`字数 ${countWords(text)}，规范审计：${audit.ok ? '通过' : '不通过'} —— ${audit.reason}`);
  }

  trace.section('第三部分 · 低打扰：不同"通知目的"是否值得在该渠道发出');
  const purposeLabel = { progress: '中间进展', decision: '需要决策', acceptance: '需要验收', answer: '回答问题', alert: '异常告警' };
  trace.block(
    renderTable(
      ['通知目的', 'IM 即时通信', 'GitHub PR', '邮件'],
      Object.values(Purpose).map((p) => [purposeLabel[p] ?? p, shouldNotify('im', p) ? '通知' : '抑制', shouldNotify('github_pr', p) ? '通知' : '抑制', shouldNotify('email', p) ? '通知' : '抑制']),
    ),
  );
  const longIm = '登录超时问题已经修复，相关测试全部通过，请确认是否采用本次改动。'.repeat(6);
  const longAudit = auditChannel('im', longIm);
  trace.note(`把一段超长说明发到 IM（${countWords(longIm)} 词）：审计 ok=${longAudit.ok} —— ${longAudit.reason}`);
  trace.note('讲义提醒：原文"单次不超过 100 词"是其 IM 实践，不能机械换算成所有中文消息的统一字数上限，这里仅作教学示范。');

  // 真实运行一次，展示 Router 统一对外回复，并抑制一条中间进展
  const sys = buildSystem({ title: '', msPerSimSecond: 30, globalStateTokens: 6000, userChannel: 'im' });
  const { bus, router } = sys;
  router.start();
  await bus.publish({ source: Source.IM_WORK, threadId: 'H01', text: '线上登录偶发超时，麻烦排查修复', author: 'user-1', payload: { threadTitle: '工作群' } });
  await router.quiesce({ timeoutSimSec: 60 });
  const w = [...router.workers.values()][0];
  const repliesBefore = router.replies.length;
  // 模拟 Worker 汇报一条"中间进展"：在 IM 低打扰渠道应被抑制
  router.report(w.id, { purpose: Purpose.PROGRESS, fact: { topic: '登录超时', change: '正在跑测试', verification: '-' } });
  trace.section('第三部分 · Worker 不直接回复用户，中间进展在 IM 被抑制');
  trace.note(`任务收尾时 Router 已统一发出验收回复 ${repliesBefore} 条（Worker 的推理与工具过程不外泄）`);
  trace.note(`再模拟汇报一条"中间进展"：replies ${repliesBefore} -> ${router.replies.length}，suppressedProgress=${router.suppressedProgress}（IM 低打扰，未打断用户）`);
  router.stop();
  await router.done;
  return { repliesBefore, suppressed: router.suppressedProgress };
}

async function main() {
  const trace = new Trace(null, { title: '演示 07 · 面向人的交互：异步全双工 + 多信道表达' });

  const duplex = await partDuplex(trace);
  await partRunnerSemantics(trace);
  const ch = await partChannels(trace);

  trace.section('全演示小结');
  trace.block(
    renderTable(
      ['机制', '演示结果'],
      [
        ['同步 run() 阻塞输入', `${duplex.sync.blockedInputs} 条，阻塞 ${duplex.sync.blockedSeconds.toFixed(1)}s`],
        ['异步 start() 全双工', `${duplex.asyn.blockedInputs} 条被阻塞（测试期间即处理补充消息）`],
        ['Started 语义', '回执=启动成功，真实结果以完成事件为准（本例 failed）'],
        ['多信道表达', '同一事实渲染成 IM / GitHub / 邮件 三种密度'],
        ['低打扰抑制', `中间进展被抑制 ${ch.suppressed} 条，验收回复统一由 Router 发出 ${ch.repliesBefore} 条`],
      ],
    ),
  );

  takeaways(trace, [
    '把阻塞式 run() 换成 start()，工具在后台跑、完成后作为新事件回投，耗时操作期间会话仍能吸收新输入，这就是全双工。',
    'Started 只表示"启动成功"，既不代表完成也不代表成功；真实结果必须等完成事件，否则会误报。',
    '全双工不等于随时打断：新输入在当前推理步结束后才被处理，一次正在进行的模型推理不可被抢占。',
    'Worker 不直接回复用户，由 Router 统一按渠道组织表达，避免把内部推理与工具过程外泄给人。',
    '渠道规范决定表达密度与是否打扰：IM 轻量低打扰、抑制中间进展且有词数上限；GitHub/邮件结构化高密度、交代背景与验证。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
