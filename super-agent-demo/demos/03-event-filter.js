/**
 * 演示 03 —— 事件驱动与两级事件过滤
 *
 * 对应讲义第 3 讲（原文第 3 节开头、第 3.1 节）。
 *
 * 演示四个必须分清的阶段：
 *   接收到事件 -> 允许进入候选集 -> 值得后续处理 -> 分派给执行者
 * 本演示停在第三个阶段（filterOnly），路由分派见演示 04。
 *
 * 并演示三件事：
 *   1. 程序层 Fast Path 用确定性规则拦掉大量消息，完全不调用模型
 *   2. 语义层 Slow Path 才判断"值不值得行动"，且策略可用自然语言调整
 *   3. 规则过严会误过滤有价值事件；换一套语义策略，同一条事件结论会不同
 */

import { DEFAULT_SOURCE_POLICY, Policy, Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';

/** 十条事件，覆盖讲义第 3 讲列举的全部来源与判断分支。 */
const EVENTS = [
  { key: '发布通知', source: Source.IM_NOTIFY, threadId: 'H-N1', text: '版本 v2.3.1 已发布到生产环境', author: 'release-bot' },
  { key: '告警(有价值)', source: Source.IM_NOTIFY, threadId: 'H-N2', text: '告警：导出服务连续失败 12 次，日志已附', author: 'alert-bot' },
  { key: '闲聊群', source: Source.IM_SOCIAL, threadId: 'H-S1', text: '午饭吃什么？', author: 'user-9' },
  { key: '工作群闲聊', source: Source.IM_WORK, threadId: 'H-W1', text: '楼下新开了家面馆，周末有人去打球吗', author: 'user-2' },
  { key: '故障反馈', source: Source.IM_WORK, threadId: 'H-W1', text: '当前产品登录报错，附复现步骤和日志', author: 'user-1' },
  { key: '改进建议', source: Source.IM_WORK, threadId: 'H-W2', text: '以后界面也许可以更简洁一些', author: 'user-3' },
  { key: '已有人处理', source: Source.IM_WORK, threadId: 'H-W3', text: '这个问题已经有人在处理，先不用接手', author: 'user-4' },
  { key: '会议邀请', source: Source.EMAIL, threadId: 'H-E1', text: '周五下午 3 点架构评审会议邀请', author: 'calendar' },
  { key: '点名邮件', source: Source.EMAIL, threadId: 'H-E2', text: 'super-agent 帮我核对这次评审有没有时间冲突', author: 'user-1', mentions: ['super-agent'] },
  { key: 'CI 失败', source: Source.GH_CI, threadId: 'H-C1', text: 'CI 运行结果：构建失败，单元测试 2 项不通过', author: 'ci-bot' },
];

/** 跑一遍过滤流程，返回每条事件的两级结果。 */
async function runPass({ label, semanticPolicy = 'default', sourcePolicy, note = '' }) {
  const sys = buildSystem({
    title: label,
    msPerSimSecond: 10,
    filterOnly: true, // 只做过滤，不做分派（第 3 讲到"值得处理"为止）
    semanticPolicy,
    sourcePolicy,
    globalStateTokens: 4000,
    maxBatch: 8,
  });
  if (note) sys.trace.note(note);

  sys.router.start();
  const programResults = [];
  for (const e of EVENTS) {
    const r = await sys.bus.publish(e);
    programResults.push({ key: e.key, triggerId: r.trigger.id, stage: r.stage, reason: r.reason });
  }
  await sys.router.quiesce({ timeoutSimSec: 40 });
  sys.router.stop();
  await sys.router.done;

  const rows = programResults.map((p) => {
    const ev = EVENTS.find((e) => e.key === p.key);
    const sem = sys.router.filterResults.find((f) => f.id === p.triggerId);
    return {
      事件: p.key,
      来源: ev.source,
      程序层: p.stage === 'program-filtered' ? `过滤（${p.reason}）` : '进入候选集',
      语义层: sem ? (sem.worth ? `值得处理（${sem.reason}）` : `无须行动（${sem.reason}）`) : '未调用模型',
      模型调用: sem ? '是' : '否',
    };
  });
  return { sys, rows };
}

async function main() {
  const trace = new Trace(null, { title: '演示 03 · 事件驱动与两级事件过滤' });

  // ---- 第一遍：默认策略 -------------------------------------------------
  const pass1 = await runPass({
    label: '第一遍：默认配置（通知群/闲聊群直接忽略，邮件仅响应明确请求，工作群允许观察）',
    note: '语义策略：关注当前产品的问题反馈；日常闲聊不采取行动；要求暂缓时结合现有任务状态判断。',
  });
  trace.section('第一遍结果：默认策略');
  trace.block(
    renderTable(
      ['事件', '来源', '程序层 Fast Path', '语义层 Slow Path', '调用模型'],
      pass1.rows.map((r) => [r.事件, r.来源, r.程序层, r.语义层, r.模型调用]),
    ),
  );
  const f1 = pass1.sys.bus.funnel();
  trace.note(`漏斗：${JSON.stringify(f1)}`);
  trace.note(
    `教学成本模型：总事件 N=${f1.接收事件}，程序层保留比例 p=${(f1.进入候选集 / f1.接收事件).toFixed(2)}，` +
      `需要模型判断的事件从 ${f1.接收事件} 条降到 ${f1.进入候选集} 条`,
  );
  trace.note(`Router 批处理：${pass1.sys.router.rounds.length} 轮处理了 ${pass1.sys.router.rounds.reduce((s, r) => s + r.batchSize, 0)} 条候选事件，读缓存 Token ${pass1.sys.stats.cacheReadTokens}`);
  trace.note('「已有人处理，先不用接手」被判为无须行动，是因为 filterOnly 模式下没有在跑任务；');
  trace.note('  在真实运行中它会作为约束投递给相关 Worker（见演示 02 的全局约束广播）。');

  // ---- 第二遍：换一套语义策略 -------------------------------------------
  const pass2 = await runPass({
    label: '第二遍：只把语义策略换成「需求收集」，程序层规则不变',
    semanticPolicy: 'collect-ideas',
    note: '语义策略：除问题反馈外，也收集产品改进线索（"以后…""如果能…""建议…"）。',
  });
  trace.section('第二遍结果：语义策略改为需求收集');
  const changed = pass2.rows.filter((r, i) => r.语义层 !== pass1.rows[i].语义层);
  trace.block(renderTable(['事件', '默认策略', '需求收集策略'], changed.map((r, i) => [r.事件, pass1.rows.find((x) => x.事件 === r.事件).语义层, r.语义层])));
  trace.note('同一条事件、同一套程序层规则，只改语义策略就得到不同结论：');
  trace.note('  这正是讲义强调的"事件是否值得处理，取决于系统目标与策略"。');

  // ---- 第三遍：放宽一条被误过滤的规则 -----------------------------------
  const relaxed = { ...DEFAULT_SOURCE_POLICY, [Source.IM_NOTIFY]: Policy.OBSERVE };
  const pass3 = await runPass({
    label: '第三遍：把通知群从「直接忽略」放宽为「作为候选事件」',
    sourcePolicy: relaxed,
    note: '动机：告警类消息虽然来自通知群，却可能携带值得处理的故障信息。',
  });
  trace.section('第三遍结果：放宽通知群规则');
  const alarm1 = pass1.rows.find((r) => r.事件 === '告警(有价值)');
  const alarm3 = pass3.rows.find((r) => r.事件 === '告警(有价值)');
  trace.block(renderTable(['事件', '放宽前', '放宽后'], [['告警(有价值)', `${alarm1.程序层} / ${alarm1.语义层}`, `${alarm3.程序层} / ${alarm3.语义层}`]]));
  const f3 = pass3.sys.bus.funnel();
  trace.note(`代价：进入候选集的事件从 ${f1.进入候选集} 条增加到 ${f3.进入候选集} 条，语义层调用与 Token 随之上升。`);

  trace.section('三遍对比');
  trace.block(
    renderTable(
      ['配置', '接收', '程序层过滤', '候选事件', '判定值得处理', 'Router 轮数', '读缓存 Token'],
      [
        ['默认策略', f1.接收事件, f1.程序层过滤掉, f1.进入候选集, pass1.sys.router.filterResults.filter((r) => r.worth).length, pass1.sys.router.rounds.length, pass1.sys.stats.cacheReadTokens],
        ['需求收集策略', f1.接收事件, pass2.sys.bus.funnel().程序层过滤掉, pass2.sys.bus.funnel().进入候选集, pass2.sys.router.filterResults.filter((r) => r.worth).length, pass2.sys.router.rounds.length, pass2.sys.stats.cacheReadTokens],
        ['放宽通知群', f3.接收事件, f3.程序层过滤掉, f3.进入候选集, pass3.sys.router.filterResults.filter((r) => r.worth).length, pass3.sys.router.rounds.length, pass3.sys.stats.cacheReadTokens],
      ],
      ['l', 'r', 'r', 'r', 'r', 'r', 'r'],
    ),
  );

  takeaways(trace, [
    'Trigger 只是"进入系统视野的环境事件"，不等于已确认的任务，更不等于获准执行操作。',
    '程序层用确定性规则（来源、群组范围、白名单）快速缩小候选范围，这一级完全不调用模型。',
    '语义层才判断"值不值得行动"，它是面向人类的自然语言扩展点，改策略就能改行为，但不保证每次判断都准确。',
    '过滤过严会让有价值事件在第一级就被丢弃（本例的告警消息）；过松则把大量无关事件推给模型，抬高成本。',
    '"通过过滤"只回答值不值得继续处理，"交给谁"是下一讲的路由问题，两者都借用 Fast / Slow Path 思想但职责不同。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
