/**
 * 演示 06 —— 提前行动与推测执行
 *
 * 对应讲义第 6 讲（原文第 3.4 节）。
 *
 * 推测执行 = 在用户还没要求时，先在后台悄悄尝试；有价值才交付，无价值就安静丢弃。
 * 讲义强调必须把三个问题分开，不能混为一谈：
 *   闸门一 shouldAttempt  ：这条线索值不值得先在后台试一把？（看副作用范围与预算）
 *   闸门二 shouldContinue ：要不要继续投入？（看有没有新证据）
 *   闸门三 shouldDeliver  ：要不要交付、打断用户？（只有经过验证的结果才呈现）
 *
 * 本演示分两部分：
 *   第一部分：分别驱动三道闸门，说明它们回答的是三个不同的问题
 *   第二部分：真实运行两个推测任务，对比"验证通过才交付" vs "未复现则安静丢弃"，
 *             再与"关闭推测模式"对照，看它如何避免把猜测写成结论、制造注意力负担
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';
import { SpeculationGate } from '../src/worker.js';

const yn = (g) => (g.go ? '是' : '否');

// ---------------------------------------------------------------------------
// 第一部分：三道闸门分别回答三个问题
// ---------------------------------------------------------------------------

function partGates(trace) {
  trace.section('第一部分 · 闸门一 shouldAttempt：值不值得先在后台试一把？');
  const attemptCases = [
    ['有线索 + 只读 + 有预算', { clueStrength: 0.6, sideEffect: 'read-only', budgetLeft: 1 }],
    ['没有任何线索', { clueStrength: 0, sideEffect: 'read-only', budgetLeft: 1 }],
    ['涉及不可撤销的外部影响', { clueStrength: 0.8, sideEffect: 'destructive', budgetLeft: 1 }],
    ['推测预算已用尽', { clueStrength: 0.6, sideEffect: 'read-only', budgetLeft: 0 }],
  ];
  trace.block(
    renderTable(
      ['场景', '后台尝试？', '理由'],
      attemptCases.map(([name, input]) => {
        const g = SpeculationGate.shouldAttempt(input);
        return [name, yn(g), g.reason];
      }),
    ),
  );
  trace.note('关注点：副作用范围与预算。破坏性操作即使线索很强，也必须先取得授权，不能擅自在后台执行。');

  trace.section('第一部分 · 闸门二 shouldContinue：要不要继续投入？');
  const contCases = [
    ['出现新证据', { newEvidence: 1, attempts: 2, maxAttempts: 3 }],
    ['无新证据但未到上限', { newEvidence: 0, attempts: 1, maxAttempts: 3 }],
    ['无新证据且已到上限', { newEvidence: 0, attempts: 3, maxAttempts: 3 }],
  ];
  trace.block(
    renderTable(
      ['场景', '继续投入？', '理由'],
      contCases.map(([name, input]) => {
        const g = SpeculationGate.shouldContinue(input);
        return [name, yn(g), g.reason];
      }),
    ),
  );
  trace.note('关注点：信息增量。"活动量"不等于"进展"——没有新证据的重复尝试应当止损。');

  trace.section('第一部分 · 闸门三 shouldDeliver：要不要交付、打断用户？');
  const deliverCases = [
    ['已复现且验证通过', { reproduced: true, verified: true, confidence: 0.86 }],
    ['已复现但未验证', { reproduced: true, verified: false, confidence: 0.5 }],
    ['未能复现', { reproduced: false, verified: false, confidence: 0.2 }],
    ['未复现，但用户主动问进度', { reproduced: false, verified: false, confidence: 0.2, userAsked: true }],
  ];
  trace.block(
    renderTable(
      ['场景', '交付用户？', '理由'],
      deliverCases.map(([name, input]) => {
        const g = SpeculationGate.shouldDeliver(input);
        return [name, yn(g), g.reason];
      }),
    ),
  );
  trace.note('关注点：只有经过验证的结果才主动呈现；未验证就保持安静。但用户主动询问时，即使不确定也要如实说明当前状态。');
  trace.note('三道闸门是三个独立判断：可以"值得尝试"却"不值得交付"，也可以"停止投入"但"仍交付已有结论"。');
}

// ---------------------------------------------------------------------------
// 第二部分：真实运行推测任务，对比交付与安静丢弃
// ---------------------------------------------------------------------------

async function runSpeculative({ taskText, threadId, speculative }) {
  const sys = buildSystem({
    title: '',
    msPerSimSecond: 25,
    globalStateTokens: 6000,
    speculative,
  });
  const { bus, router } = sys;
  router.start();
  await bus.publish({ source: Source.IM_WORK, threadId, text: taskText, author: 'user-1', payload: { threadTitle: '工作群' } });
  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;
  const w = [...router.workers.values()][0];
  return {
    worker: w,
    replies: router.replies.length,
    replyText: router.replies[0]?.text ?? null,
    findings: w?.findings ?? {},
    delivered: router.replies.length > 0,
  };
}

async function partRuns(trace) {
  trace.section('第二部分 · 真实运行：验证通过才交付');
  const tilde = await runSpeculative({
    taskText: 'Markdown 里的波浪号删除线渲染出错了，帮忙复现并修复',
    threadId: 'H01',
    speculative: true,
  });
  trace.note(`任务「波浪号渲染」后台尝试：复现=${tilde.findings.reproduced}，验证=${tilde.findings.verified}，置信度=${(tilde.findings.confidence ?? 0).toFixed(2)}`);
  trace.note(`交付闸门放行 -> 向用户发出 ${tilde.replies} 条回复：${tilde.replyText ?? '（无）'}`);

  trace.section('第二部分 · 真实运行：未复现则安静丢弃');
  const lag = await runSpeculative({
    taskText: '打开大文件时编辑器卡顿明显，帮忙看看性能',
    threadId: 'H02',
    speculative: true,
  });
  trace.note(`任务「大文件卡顿」后台尝试：复现=${lag.findings.reproduced}，验证=${lag.findings.verified}，置信度=${(lag.findings.confidence ?? 0).toFixed(2)}`);
  trace.note(`交付闸门拦截 -> 向用户发出 ${lag.replies} 条回复（安静丢弃，不制造注意力负担）`);

  trace.section('第二部分 · 对照：关闭推测模式（speculative=false）');
  const lagNoSpec = await runSpeculative({
    taskText: '打开大文件时编辑器卡顿明显，帮忙看看性能',
    threadId: 'H03',
    speculative: false,
  });
  trace.note(`同样未复现的卡顿任务，关闭推测模式后 -> 向用户发出 ${lagNoSpec.replies} 条回复：${lagNoSpec.replyText ?? '（无）'}`);
  trace.note('这正是讲义警告的反面：把"后台跑完了/进程退出了"当成"问题解决了"去打扰用户，等于把猜测写成结论。');

  trace.section('第二部分结果对比');
  trace.block(
    renderTable(
      ['任务', '推测模式', '复现', '验证', '置信度', '是否打扰用户'],
      [
        ['波浪号渲染', '开', String(tilde.findings.reproduced), String(tilde.findings.verified), (tilde.findings.confidence ?? 0).toFixed(2), tilde.delivered ? `是（${tilde.replies} 条）` : '否'],
        ['大文件卡顿', '开', String(lag.findings.reproduced), String(lag.findings.verified), (lag.findings.confidence ?? 0).toFixed(2), lag.delivered ? `是（${lag.replies} 条）` : '否（安静丢弃）'],
        ['大文件卡顿', '关', String(lagNoSpec.findings.reproduced), String(lagNoSpec.findings.verified), (lagNoSpec.findings.confidence ?? 0).toFixed(2), lagNoSpec.delivered ? `是（${lagNoSpec.replies} 条）` : '否'],
      ],
    ),
  );
  return { tilde, lag, lagNoSpec };
}

async function main() {
  const trace = new Trace(null, { title: '演示 06 · 提前行动与推测执行' });

  partGates(trace);
  const runs = await partRuns(trace);

  takeaways(trace, [
    '推测执行把"提前尝试"和"打断用户"解耦：可以先在后台悄悄做，但不等于做出来就要告诉用户。',
    '三道闸门回答三个不同问题——是否后台尝试（副作用/预算）、是否继续投入（有无新证据）、是否交付（是否验证通过）。',
    '只有经过验证的结果才主动交付；未复现或未验证就安静丢弃，避免用注意力负担抵消省下的等待时间。',
    '不能把"后台进程退出"写成"所有功能正确"：关闭推测模式的对照里，未复现的卡顿任务照样汇报了"已处理"。',
    '例外是用户主动询问：即使结果不确定，也应如实说明当前状态，而不是继续沉默。',
  ]);
  return runs;
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
