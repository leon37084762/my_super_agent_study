/**
 * 演示 09 —— Watchdog 与异常恢复
 *
 * 对应讲义第 9 讲（原文第 6 节）。
 *
 * 核心思想：
 *   - 主系统之外再放一个独立的 Watchdog Session，由"定时消息"驱动，构成外部 Control Loop；
 *   - 它区分"活动量"（有没有在调用工具）与"实质进展"（有没有新证据、有没有越界、基础设施是否可用）；
 *   - 判定分五档：healthy / watch / stalled / drift / infra；
 *   - Watchdog 本身也是有完整工具能力的 Agent，能诊断并"在线介入"（注入约束/提示、延后重试），不只是告警；
 *   - 维护者可以用自然语言向它询问故障原因；异常解除后持续复查直到恢复正常。
 *
 * 四部分：
 *   1. 判定逻辑表（确定性）：同样在"调用工具"，进展却可能完全不同
 *   2. 目标偏离 drift：为"小修复"不断扩大到无关模块 -> 注入约束
 *   3. 停滞 stalled：连续相同调用且无新证据 -> 注入提示；换新证据后复查恢复正常
 *   4. 基础设施异常 infra：设备离线导致挂起 -> 延后重试 + 维护者自然语言诊断
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';
import { MockLLM, judgeProgress } from '../src/llm.js';
import { Watchdog, Daemon, SessionStore } from '../src/infra.js';

async function waitUntil(clock, pred, timeoutSec = 60) {
  const t0 = clock.now();
  while (clock.now() - t0 < timeoutSec) {
    if (pred()) return true;
    await clock.sleep(0.05);
  }
  return false;
}

/** 把 Watchdog 每轮检查渲染成时间线，体现"定时消息驱动的外部 Control Loop"。 */
function timeline(wd) {
  return (
    wd.checks
      .map(
        (c) =>
          `第 ${String(c.round).padStart(2, ' ')} 轮 @${c.at.toFixed(1).padStart(5, ' ')}s | infra ${c.infraErrors} | ` +
          (c.results.map((r) => `${r.id}=${r.verdict}`).join('  ') || '（暂无 Worker）'),
      )
      .join('\n') || '（尚未产生检查记录）'
  );
}

// ---------------------------------------------------------------------------
// 第一部分：判定逻辑（纯函数，确定性）
// ---------------------------------------------------------------------------

function partVerdictLogic(trace) {
  trace.section('第一部分 · 判定逻辑：区分"活动量"与"实质进展"');
  trace.note('同样都在"调用工具"，进展却可能完全不同——Watchdog 不看热闹，看证据。');
  const cases = [
    ['正常推进（不断有新证据）', { toolCalls: 3, repeatedSameCall: 0, newEvidenceCount: 2, scopeViolations: 0, infraErrors: 0, stage: 'iterating' }],
    ['等待耗时工具（自测题 3）', { toolCalls: 1, repeatedSameCall: 0, newEvidenceCount: 0, scopeViolations: 0, infraErrors: 0, stage: 'waiting-tool' }],
    ['原地打转（重复调用无新证据）', { toolCalls: 6, repeatedSameCall: 4, newEvidenceCount: 0, scopeViolations: 0, infraErrors: 0, stage: 'iterating' }],
    ['目标偏离（越出任务范围）', { toolCalls: 5, repeatedSameCall: 0, newEvidenceCount: 1, scopeViolations: 2, infraErrors: 0, stage: 'iterating' }],
    ['基础设施异常（设备/依赖报错）', { toolCalls: 2, repeatedSameCall: 0, newEvidenceCount: 0, scopeViolations: 0, infraErrors: 3, stage: 'iterating' }],
    ['调用在变化（虽少但在推进）', { toolCalls: 4, repeatedSameCall: 1, newEvidenceCount: 1, scopeViolations: 0, infraErrors: 0, stage: 'iterating' }],
  ];
  const rows = cases.map(([label, ev]) => {
    const v = judgeProgress(ev);
    return [
      label,
      `${ev.toolCalls} / ${ev.repeatedSameCall}`,
      `新证据${ev.newEvidenceCount}·越界${ev.scopeViolations}·infra${ev.infraErrors}·${ev.stage}`,
      v.verdict,
      v.reason,
    ];
  });
  trace.block(renderTable(['场景', '活动量(调用/重复)', '实质进展信号', '判定', '理由'], rows, ['l', 'r', 'l', 'l', 'l']));
  trace.note('关键区分：watch（等待耗时工具）不等于 stalled（反复相同调用且无新证据）——长跑测试不该被误判为卡死。');
  trace.note('优先级：infra > drift > watch > stalled > healthy；越界即使"有新证据"也算偏离。');
  return rows;
}

// ---------------------------------------------------------------------------
// 通用：启动一个被 Watchdog 盯住的系统
// ---------------------------------------------------------------------------

async function runWatched({ taskText, planResolver = null, asyncTools = true, intervalSimSec = 1.5, maxRounds = 24, msPerSimSecond = 40, until }) {
  const sys = buildSystem({ title: '', msPerSimSecond, globalStateTokens: 6000, asyncTools, planResolver });
  const { clock, bus, router, stats } = sys;
  // Watchdog 监视的是“状态存储”（SessionStore），用它的 failures 计数判断状态恢复失败
  const sessionStore = new SessionStore({ clock, trace: sys.trace });
  const wd = new Watchdog({
    clock,
    trace: sys.trace,
    llm: new MockLLM({ clock, actor: 'WATCHDOG', model: 'watchdog', stats }),
    router,
    bus,
    store: sessionStore,
    daemons: sys.daemons,
    intervalSimSec,
    maxRounds,
  });
  router.start();
  wd.start();
  await bus.publish({ source: Source.IM_WORK, threadId: 'H01', text: taskText, author: 'user-1', payload: { threadTitle: '工作群' } });
  const ctx = { sys, wd, router, clock };
  const ok = await waitUntil(clock, () => until(ctx), 60);
  await router.quiesce({ timeoutSimSec: 30 });
  wd.stop();
  router.stop();
  await wd.done;
  await router.done;
  return { ...ctx, ok, worker: [...router.workers.values()][0] ?? null };
}

// ---------------------------------------------------------------------------
// 第二部分：目标偏离 drift
// ---------------------------------------------------------------------------

async function partDrift(trace) {
  trace.section('第二部分 · 目标偏离(drift)：一个"小修复"不断扩散到无关模块');
  trace.note('任务范围 src/config，但计划里顺手重构了 theme / api-client / api-router（越界）。');
  const { wd, worker, ok } = await runWatched({
    taskText: '配置读取的默认值合并有 bug，帮忙排查修复一下',
    intervalSimSec: 2,
    until: (c) => c.wd.interventions.some((i) => i.verdict === 'drift') && [...c.router.workers.values()].every((w) => w.status === 'closed'),
  });
  trace.note(`Watchdog 是否捕获 drift：${ok ? '是' : '否（超时）'}；越界次数 scopeViolations=${worker?.scopeViolations}`);
  trace.block(timeline(wd));
  const s = wd.summary();
  trace.note(`介入 ${s.interventions} 次（drift -> inject-constraint：注入"立即回到任务范围"的约束事件到 Worker 收件箱）。`);
  trace.note('第 9 讲：Watchdog 是有完整工具能力的独立 Session，能在线修正主系统行为，而不只是发一条告警。');
  return { detected: ok, violations: worker?.scopeViolations ?? 0, interventions: s.interventions, rounds: s.rounds };
}

// ---------------------------------------------------------------------------
// 第三部分：停滞 stalled 与恢复
// ---------------------------------------------------------------------------

async function partStalled(trace) {
  trace.section('第三部分 · 停滞(stalled)：连续相同调用且无新证据 -> 注入提示 -> 恢复');
  trace.note('计划：反复跑同一条失败测试（无新证据），最后更换假设做一次带新证据的修改。');
  // 自定义计划：6 次完全相同、无 evidence 的失败测试，随后一次带新证据的修改
  const stalledPlan = () => {
    const steps = [];
    for (let i = 0; i < 6; i += 1) steps.push({ tool: 'run_tests', cmd: 'npm test -- scheduler', sim: 0.15, outcome: 'failed', output: '同一处断言失败' });
    steps.push({ tool: 'edit', target: 'src/core/scheduler.js', sim: 0.3, note: '更换假设：修正竞态', evidence: 'ev:st-fix' });
    return steps;
  };
  const { wd, worker, ok } = await runWatched({
    taskText: '偶发的调度测试反复失败，帮忙排查定位',
    planResolver: stalledPlan,
    asyncTools: false, // 同步 run()：两次测试之间状态回到 running，才可能被判为 stalled 而非 watch
    intervalSimSec: 1,
    until: (c) => c.wd.interventions.some((i) => i.verdict === 'stalled') && c.wd.openIssues.size === 0,
  });
  trace.note(`Watchdog 是否捕获 stalled 并在恢复后解除关注：${ok ? '是' : '否（超时）'}`);
  trace.block(timeline(wd));
  const s = wd.summary();
  trace.note(`介入 ${s.interventions} 次（stalled -> inject-hint：提示"更换假设或上报阻塞"）；最终未恢复问题数 openIssues=${s.openIssues}。`);
  trace.note('恢复判定：Worker 换假设后产生新证据 -> 下一轮复查变 healthy -> Watchdog 解除关注（持续复查直到恢复正常）。');
  return { detected: ok, interventions: s.interventions, openIssues: s.openIssues, byVerdict: s.byVerdict, toolCalls: worker?.toolLog?.length ?? 0 };
}

// ---------------------------------------------------------------------------
// 第四部分：基础设施异常 infra + 维护者自然语言诊断
// ---------------------------------------------------------------------------

async function partInfra(trace) {
  trace.section('第四部分 · 基础设施异常(infra)：设备离线 -> 延后重试 + 自然语言诊断');
  const sys = buildSystem({ title: '', msPerSimSecond: 40, globalStateTokens: 6000, daemons: [] });
  const { clock, bus, router, stats } = sys;
  const daemon = new Daemon({ machine: 'laptop-01', clock, trace: sys.trace, online: false, bus, threadId: 'H-DEV' });
  sys.daemons.push(daemon);
  const sessionStore = new SessionStore({ clock, trace: sys.trace });
  const wd = new Watchdog({
    clock,
    trace: sys.trace,
    llm: new MockLLM({ clock, actor: 'WATCHDOG', model: 'watchdog', stats }),
    router,
    bus,
    store: sessionStore,
    daemons: sys.daemons,
    intervalSimSec: 1.5,
    maxRounds: 20,
  });
  router.start();
  wd.start();
  await bus.publish({ source: Source.IM_WORK, threadId: 'H01', text: '帮我处理笔记本上未提交的改动，本地构建报错，先排查再提 PR', author: 'user-1', payload: { threadTitle: '工作群' } });

  await waitUntil(clock, () => router.workers.size >= 1 && [...router.workers.values()][0].status === 'blocked-env', 40);
  const worker = [...router.workers.values()][0];
  const infraSeen = await waitUntil(clock, () => wd.checks.some((c) => c.results.some((r) => r.verdict === 'infra')), 30);
  trace.note(`Worker 因 laptop-01 离线挂起（infraErrors=${worker?.infraErrors ?? 0}，状态 ${worker?.status ?? '-'}）；Watchdog 判定 infra=${infraSeen ? '是' : '否'}`);
  trace.block(timeline(wd));
  trace.note(`介入动作：infra -> retry-later（先挂起等待环境恢复，不无谓重试）。介入共 ${wd.summary().interventions} 次。`);

  // 维护者用自然语言询问故障原因（第 9 讲第六节）
  const ans = await wd.ask('为什么这个任务一直卡住不动？');
  trace.note(`维护者提问「${ans.question}」-> Watchdog 诊断：${ans.answer}`);
  const interventions = wd.summary().interventions;

  // Watchdog 已完成“检测 + 诊断”职责，先停止它：worker.infraErrors 是累计计数，
  // 若继续跑会在恢复过程中反复复述同一个历史 infra 信号，反而模糊“已恢复”的事实。
  wd.stop();
  await wd.done;
  trace.note('Watchdog 完成本轮职责后停止；下面演示环境恢复（设备上线）+ 云端从挂起处续跑。');

  // 环境恢复：设备上线 + 从挂起处续跑（Watchdog 的检测对应一个真实可恢复的场景）
  daemon.goOnline();
  worker?.resume('设备重新上线，云端触发续跑');
  await worker?.done;
  trace.note(`设备上线后续跑：Worker 最终状态 ${worker?.status ?? '-'}，产出 PR=${worker?.prId ?? '-'}（infra 属可恢复异常，环境就绪即续跑）。`);

  router.stop();
  await router.done;
  return { infraSeen, interventions, answer: ans.answer, resumed: worker?.status ?? '-', prId: worker?.prId ?? null };
}

// ---------------------------------------------------------------------------

async function main() {
  const trace = new Trace(null, { title: '演示 09 · Watchdog 与异常恢复' });

  const logic = partVerdictLogic(trace);
  const drift = await partDrift(trace);
  const stalled = await partStalled(trace);
  const infra = await partInfra(trace);

  trace.section('全演示小结');
  trace.block(
    renderTable(
      ['机制', '演示结果'],
      [
        ['五档判定逻辑', `确定性覆盖 ${logic.map((r) => r[3]).join(' / ')}`],
        ['目标偏离 drift', `越界 ${drift.violations} 次被捕获=${drift.detected ? '是' : '否'}，注入约束 ${drift.interventions} 次（复查 ${drift.rounds} 轮）`],
        ['停滞 stalled', `捕获并恢复=${stalled.detected ? '是' : '否'}，注入提示 ${stalled.interventions} 次，恢复后 openIssues=${stalled.openIssues}`],
        ['基础设施 infra', `离线挂起判定=${infra.infraSeen ? 'infra' : '-'}，retry-later ${infra.interventions} 次，上线续跑->${infra.resumed}/${infra.prId ?? '-'}`],
        ['自然语言诊断', infra.answer.slice(0, 30) + '…'],
      ],
    ),
  );

  takeaways(trace, [
    'Watchdog 是主系统之外的独立 Session，由定时消息驱动，构成一个外部 Control Loop，不依赖主循环"自觉"。',
    '它区分"活动量"与"实质进展"：一直在调用工具不等于在推进；关键看有没有新证据、有没有越界、基础设施是否可用。',
    '判定分五档 healthy/watch/stalled/drift/infra，且"等待耗时工具(watch)"与"反复相同调用无新证据(stalled)"必须分清，避免误杀长跑任务。',
    'Watchdog 本身是有完整工具能力的 Agent，可在线介入（注入约束/提示、延后重试）推动恢复，而不只是发告警。',
    '异常解除后要持续复查直到恢复正常才解除关注；维护者还能用自然语言向它询问故障原因，得到基于快照的诊断。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exit(1); // 强制退出：避免出错时轮询定时器让 Node 永不退出
});
