/**
 * system.js —— 把各模块组装成一个可运行的 Super-agent 教学系统
 *
 * 这里提供三类东西：
 *   1. buildSystem()：一次性接好 Clock / Trace / ObjectStore / EventBus / Router / Worker
 *   2. TASKS 与计划模板：教学替身。第 10 讲原文说明"具体编码工作用一个只消耗时间的工具代替"，
 *      因此大部分任务并不真的改代码，只是按设计走完事件与工具的流转。
 *   3. 合成负载与统计工具：消息流生成、百分位数、单 Session 消融模型
 */

import { Clock, Trace, Source, nextId, resetIds } from './core.js';
import { LLMStats, MockLLM } from './llm.js';
import { ObjectStore } from './context.js';
import { EventBus } from './bus.js';
import { Router } from './router.js';
import { AsyncToolRunner, Worker } from './worker.js';

// ---------------------------------------------------------------------------
// 1. 任务模板
// ---------------------------------------------------------------------------

export const TASKS = Object.freeze({
  loginTimeout: {
    label: '修复登录超时',
    keyword: '登录',
    scope: ['src/auth'],
    plan: [
      { tool: 'read', target: 'src/auth/login.js', sim: 0.8, note: '定位登录超时处理', evidence: 'ev:login-locate' },
      { tool: 'edit', target: 'src/auth/login.js', sim: 1.2, note: '补充超时处理与界面状态复位', evidence: 'ev:login-edit', tag: 'api' },
      { tool: 'run_tests', cmd: 'npm test -- auth', sim: 5, outcome: 'succeeded', output: 'auth 12 项测试通过', cancelOn: /只检查登录模块/ },
      { tool: 'create_pr', title: 'fix(auth): 登录超时后复位界面状态', verification: 'auth 相关测试通过' },
    ],
  },
  exportPerf: {
    label: '优化导出功能',
    keyword: '导出',
    scope: ['src/export'],
    plan: [
      { tool: 'read', target: 'src/export/csv.js', sim: 0.9, note: '分析导出逻辑', evidence: 'ev:export-locate' },
      { tool: 'edit', target: 'src/export/csv.js', sim: 1.4, note: '改为分批写出，避免一次性载入', evidence: 'ev:export-edit' },
      { tool: 'run_tests', cmd: 'npm test -- export', sim: 4.5, outcome: 'succeeded', output: 'export 8 项测试通过' },
      { tool: 'create_pr', title: 'perf(export): 分批写出大文件', verification: 'export 相关测试通过' },
    ],
  },
  markdownTilde: {
    label: 'Markdown 波浪号渲染',
    keyword: '波浪号',
    scope: ['src/markdown'],
    plan: [
      { tool: 'reproduce', sim: 1.6, reproduced: true, verified: false, confidence: 0.5, note: '静默复现波浪号渲染问题' },
      { tool: 'edit', target: 'src/markdown/inline.js', sim: 1.5, note: '修正删除线语法解析', evidence: 'ev:tilde-edit' },
      { tool: 'reproduce', sim: 1.4, reproduced: true, verified: true, confidence: 0.86, note: '回归验证' },
      { tool: 'create_pr', title: 'fix(markdown): 波浪号删除线解析', verification: '复现用例回归通过' },
    ],
  },
  lagUnreproducible: {
    label: '大文件打开卡顿',
    keyword: '卡顿',
    scope: ['src/editor'],
    plan: [
      { tool: 'read', target: 'src/editor/loader.js', sim: 1.0, note: '检查加载路径', evidence: 'ev:lag-read' },
      { tool: 'reproduce', sim: 2.2, reproduced: false, verified: false, confidence: 0.2, note: '尝试复现，未成功' },
      { tool: 'edit', target: 'src/editor/loader.js', sim: 1.2, note: '推测性调整缓存策略', evidence: 'ev:lag-guess' },
    ],
  },
  deviceBound: {
    label: '处理笔记本上未提交的改动',
    keyword: '未提交',
    scope: ['src/local'],
    plan: [
      { tool: 'daemon_exec', machine: 'laptop-01', cmd: 'git status --porcelain', sim: 0.8, evidence: 'ev:git-status' },
      { tool: 'daemon_exec', machine: 'laptop-01', cmd: 'npm run lint -- src/local', sim: 1.4, evidence: 'ev:lint' },
      { tool: 'edit', target: 'src/local/session.js', sim: 1.0, note: '按 lint 结果修正', evidence: 'ev:local-edit' },
      { tool: 'create_pr', title: 'chore(local): 修正会话初始化', verification: 'lint 通过' },
    ],
  },
  drifting: {
    label: '修一个小的配置读取问题',
    keyword: '配置',
    scope: ['src/config'],
    plan: [
      { tool: 'read', target: 'src/config/load.js', sim: 0.7, note: '定位配置读取', evidence: 'ev:cfg-read' },
      { tool: 'edit', target: 'src/config/load.js', sim: 0.9, note: '修正默认值合并', evidence: 'ev:cfg-edit' },
      // 目标偏离：为一个"小修复"不断扩大到无关模块（第 9 讲）
      { tool: 'edit', target: 'src/ui/theme.js', sim: 0.9, note: '顺手重构主题模块', evidence: 'ev:cfg-edit' },
      { tool: 'edit', target: 'src/api/client.js', sim: 0.9, note: '顺手重构接口层', evidence: 'ev:cfg-edit' },
      { tool: 'edit', target: 'src/api/router.js', sim: 0.9, note: '继续重构', evidence: 'ev:cfg-edit' },
    ],
  },
  stalled: {
    label: '修复偶发的测试失败',
    keyword: '偶发',
    scope: ['src/core'],
    plan: [
      { tool: 'read', target: 'src/core/scheduler.js', sim: 0.7, note: '查看调度实现', evidence: 'ev:st-read' },
      // 停滞：连续相同调用且没有新证据（第 9 讲补充案例）
      { tool: 'run_tests', cmd: 'npm test -- scheduler', sim: 1.2, outcome: 'failed', output: '同一处断言失败', asyncTools: false },
      { tool: 'run_tests', cmd: 'npm test -- scheduler', sim: 1.2, outcome: 'failed', output: '同一处断言失败', asyncTools: false },
      { tool: 'run_tests', cmd: 'npm test -- scheduler', sim: 1.2, outcome: 'failed', output: '同一处断言失败', asyncTools: false },
      { tool: 'run_tests', cmd: 'npm test -- scheduler', sim: 1.2, outcome: 'failed', output: '同一处断言失败', asyncTools: false },
    ],
  },
});

const TASK_RULES = [
  [/波浪号|渲染/, 'markdownTilde'],
  [/登录|超时/, 'loginTimeout'],
  [/导出/, 'exportPerf'],
  [/卡顿|大文件/, 'lagUnreproducible'],
  [/未提交|笔记本|本地/, 'deviceBound'],
  [/配置/, 'drifting'],
  [/偶发/, 'stalled'],
];

export function taskKeyFor(taskText) {
  for (const [re, key] of TASK_RULES) if (re.test(taskText)) return key;
  return null;
}

let burnSeq = 0;

/**
 * 第 10 讲的实验设定：用一个只消耗时间的工具代替具体编码工作。
 * @param {number} simSeconds 该"编码工作"耗时（模拟秒）
 */
export function mockCodingPlan(simSeconds = 6) {
  burnSeq += 1;
  return [
    {
      tool: 'burn',
      cmd: `mock-coding-work(${simSeconds}s)`,
      sim: simSeconds,
      note: '只消耗时间的替身工具，不代表真实代码质量',
      evidence: `ev:burn-${burnSeq}`,
    },
  ];
}

// ---------------------------------------------------------------------------
// 2. 组装系统
// ---------------------------------------------------------------------------

/**
 * @param {object} [opts]
 * @param {string}  [opts.title]            Trace 标题
 * @param {number}  [opts.msPerSimSecond]    1 模拟秒对应多少真实毫秒（越大演示越慢）
 * @param {boolean} [opts.directDelivery]    false = 消融「去掉直通」
 * @param {boolean} [opts.filterOnly]        true  = 只演示第 3 讲的两级过滤，不分派
 * @param {number}  [opts.maxBatch]          Router 单轮最多合并的事件数（1 = 关闭批处理）
 * @param {number}  [opts.globalStateTokens] Router 常驻全局状态规模（长输入的来源）
 * @param {boolean} [opts.speculative]       Worker 以推测执行模式运行
 * @param {boolean} [opts.asyncTools]        false = 经典同步 run()
 * @param {Function}[opts.planResolver]      (taskText, spec) => plan
 */
export function buildSystem(opts = {}) {
  const {
    title = '',
    msPerSimSecond = 80,
    directDelivery = true,
    filterOnly = false,
    maxBatch = 8,
    globalStateTokens = 9000,
    routerModel = 'lite-router',
    workerModel = 'strong-worker',
    inlineCharLimit = 50,
    sourcePolicy = undefined,
    whitelist = null,
    botName = 'super-agent',
    asyncTools = true,
    speculative = false,
    daemons = [],
    planResolver = null,
    userChannel = 'im',
    semanticPolicy = 'default',
  } = opts;

  resetIds();
  burnSeq = 0;

  const clock = new Clock(msPerSimSecond);
  const trace = new Trace(clock, { title });
  const store = new ObjectStore({ inlineCharLimit });
  const stats = new LLMStats();
  const runner = new AsyncToolRunner({ clock, trace });
  const bus = new EventBus({ clock, trace, store, directDelivery, sourcePolicy, whitelist, botName });

  const resolvePlan = (taskText) => {
    if (planResolver) return planResolver(taskText);
    const key = taskKeyFor(taskText);
    if (key) return structuredClone(TASKS[key].plan);
    return mockCodingPlan(6);
  };

  const workerFactory = (spec) => {
    const id = nextId('W');
    const plan = resolvePlan(spec.task);
    const key = taskKeyFor(spec.task);
    const scope = key ? [...TASKS[key].scope] : [];
    const needsDaemon = plan.find((s) => s.tool === 'daemon_exec');
    const daemon = needsDaemon ? daemons.find((d) => d.machine === needsDaemon.machine) ?? daemons[0] ?? null : null;
    const w = new Worker({
      id,
      task: spec.task,
      title: key ? TASKS[key].label : spec.task.slice(0, 12),
      clock,
      trace,
      store,
      bus,
      llm: new MockLLM({ clock, actor: id, model: workerModel, stats }),
      router,
      daemon,
      runner,
      plan,
      scope,
      speculative,
      asyncTools: asyncTools && !plan.some((s) => s.asyncTools === false),
      originThread: spec.originThread,
    });
    w.originTriggerId = spec.originTrigger?.id ?? null;
    return w;
  };

  const router = new Router({
    clock,
    trace,
    store,
    bus,
    llm: new MockLLM({ clock, actor: 'ROUTER', model: routerModel, stats }),
    workerFactory,
    filterOnly,
    maxBatch,
    globalStateTokens,
    userChannel,
    semanticPolicy,
  });
  bus.attachRouter(router);

  return { clock, trace, store, stats, bus, router, runner, daemons, workerFactory };
}

// ---------------------------------------------------------------------------
// 3. 合成负载：消息流生成（第 10 讲 7.1 的实验输入）
// ---------------------------------------------------------------------------

/** 可复现的伪随机数：保证多次运行的对比是同一负载。 */
export function mulberry32(seed) {
  let a = seed >>> 0;
  return function rng() {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const WORK_REQUESTS = [
  '线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复',
  '导出大文件时失败，日志显示内存溢出，需要优化导出功能',
  'Markdown 里的波浪号删除线渲染不对，讨论一下怎么修',
  '打开大文件时编辑器卡顿明显，帮忙看看性能',
  '配置读取的默认值合并有问题，小修一下',
  '偶发的调度测试失败，帮忙定位',
];

const FOLLOWUPS = ['补充一下日志，报错栈在这里', '这个问题今天必须处理', '顺便看看有没有相似的历史 issue', '刚又出现一次，频率变高了'];
const STOPS = ['先不要改接口，等评审结论', '暂缓一下，产品要重新确认范围'];
const CHITCHAT = ['午饭吃什么', '今天下班早点走', '周末有人打球吗', '哈哈哈这个表情太好笑了', '恭喜发版，辛苦了'];
const CI_MSGS = ['CI 运行结果：构建通过，测试失败 2 项', 'CI 运行结果：全部通过', 'CI 运行结果：构建失败，依赖安装超时'];

/**
 * 生成一条合成消息流。
 * 原文设定：约 300 条消息、其中约 20 条工作请求，其余为闲聊、跟进、叫停与 CI 消息。
 * 这里默认按比例缩小，便于几秒内跑完；比例保持一致。
 *
 * @returns {Array<{at:number, input:object, kind:string}>} 按时间排序
 */
export function makeStream({ total = 90, workCount = 6, durationSec = 600, seed = 20260711, workSeconds = 6 } = {}) {
  const rng = mulberry32(seed);
  const msgs = [];
  const workThreads = [];

  const at = () => Math.round(rng() * durationSec * 10) / 10;

  for (let i = 0; i < workCount; i += 1) {
    const threadId = `H${String(i + 1).padStart(2, '0')}`;
    workThreads.push(threadId);
    msgs.push({
      at: Math.round((durationSec * (0.05 + 0.85 * rng())) * 10) / 10,
      kind: 'work',
      input: {
        source: Source.IM_WORK,
        threadId,
        text: WORK_REQUESTS[i % WORK_REQUESTS.length],
        author: `user-${1 + (i % 3)}`,
        payload: { threadTitle: `工作群讨论 ${threadId}`, workSeconds },
      },
    });
  }

  const others = total - workCount;
  for (let i = 0; i < others; i += 1) {
    const r = rng();
    if (r < 0.42) {
      msgs.push({
        at: at(),
        kind: 'chitchat',
        input: { source: rng() < 0.5 ? Source.IM_SOCIAL : Source.IM_NOTIFY, threadId: `H-SOC${1 + Math.floor(rng() * 3)}`, text: CHITCHAT[Math.floor(rng() * CHITCHAT.length)], author: `user-${1 + Math.floor(rng() * 5)}` },
      });
    } else if (r < 0.62 && workThreads.length) {
      const tid = workThreads[Math.floor(rng() * workThreads.length)];
      msgs.push({ at: at(), kind: 'followup', input: { source: Source.IM_WORK, threadId: tid, text: FOLLOWUPS[Math.floor(rng() * FOLLOWUPS.length)], author: 'user-2' } });
    } else if (r < 0.66 && workThreads.length) {
      const tid = workThreads[Math.floor(rng() * workThreads.length)];
      msgs.push({ at: at(), kind: 'stop', input: { source: Source.IM_WORK, threadId: tid, text: STOPS[Math.floor(rng() * STOPS.length)], author: 'user-1' } });
    } else if (r < 0.88 && workThreads.length) {
      const tid = workThreads[Math.floor(rng() * workThreads.length)];
      const text = CI_MSGS[Math.floor(rng() * CI_MSGS.length)];
      msgs.push({
        at: at(),
        kind: 'ci',
        input: { source: Source.GH_CI, threadId: tid, text, author: 'ci-bot', payload: { status: /失败/.test(text) ? 'failure' : 'success', detail: text } },
      });
    } else {
      msgs.push({ at: at(), kind: 'chitchat', input: { source: Source.IM_WORK, threadId: `H-SOC9`, text: CHITCHAT[Math.floor(rng() * CHITCHAT.length)], author: 'user-4' } });
    }
  }

  msgs.sort((a, b) => a.at - b.at);
  return msgs;
}

/** 按时间轴把消息流灌进总线。 */
export async function replayStream(bus, msgs, clock) {
  let prev = 0;
  const published = [];
  for (const m of msgs) {
    const gap = Math.max(0, m.at - prev);
    prev = m.at;
    if (gap > 0) await clock.sleep(gap);
    const t = await bus.publish(m.input);
    published.push({ ...m, result: t });
  }
  return published;
}

// ---------------------------------------------------------------------------
// 4. 统计工具
// ---------------------------------------------------------------------------

export function mean(arr) {
  return arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0;
}

export function median(arr) {
  return percentile(arr, 50);
}

export function percentile(arr, p) {
  if (!arr.length) return 0;
  const s = [...arr].sort((a, b) => a - b);
  const idx = Math.min(s.length - 1, Math.max(0, Math.ceil((p / 100) * s.length) - 1));
  return s[idx];
}

export function fmtMin(sec) {
  return `${(sec / 60).toFixed(1)} 分`;
}

export function fmtTok(n) {
  if (n >= 1e8) return `${(n / 1e8).toFixed(2)} 亿`;
  if (n >= 1e4) return `${(n / 1e4).toFixed(1)} 万`;
  return String(Math.round(n));
}

// ---------------------------------------------------------------------------
// 5. 单 Session + Subagent 工具的消融模型（第 10 讲 7.1 的方案三、四）
// ---------------------------------------------------------------------------

/**
 * 教学模型（不是真实运行）：一个常驻主会话串行处理所有消息。
 *
 * 原文报告的现象被抽象为两条规则：
 *   - 中性提示：模型几乎不主动调用 Subagent，工作都由主会话承担，且不断被新消息打断；
 *   - 优先委派提示：多数工作被委派出去，但偶发一次"任务很小，自己做"，形成长尾。
 *
 * @param {object} o
 * @param {Array}  o.messages        makeStream() 的输出
 * @param {'neutral'|'prefer'} o.delegation
 * @param {number} o.workSec         一个工作请求占用的执行时间
 * @param {number} o.llmSec          一次模型调用耗时
 * @param {number} o.contextTokens   主会话每轮重读的上下文（读缓存 Token 的来源）
 */
export function simulateSingleSession({ messages, delegation = 'neutral', workSec = 90, llmSec = 2.5, contextTokens = 12000, seed = 7, preferRate = 0.8 }) {
  const rng = mulberry32(seed);
  let freeAt = 0;
  let cacheReadTokens = 0;
  let decisions = 0;
  let delegated = 0;
  const completion = [];
  const interruptions = [];

  for (const m of messages) {
    const start = Math.max(m.at, freeAt);
    const queued = start - m.at;
    if (queued > 0.5) interruptions.push(queued);
    cacheReadTokens += contextTokens; // 每条消息都要重读主会话上下文
    decisions += 1;
    let busy = llmSec;

    if (m.kind === 'work') {
      // 中性提示下几乎不委派；优先委派时按概率委派，剩下的自己扛
      const rate = delegation === 'prefer' ? preferRate : 0;
      const willDelegate = rng() < rate;
      if (willDelegate) {
        delegated += 1;
        busy += llmSec; // 委派决策本身也要一轮
        completion.push({ arrivedAt: m.at, doneAt: start + busy + workSec, delegated: true });
      } else {
        busy += workSec; // 主会话亲自执行，期间无法处理其他消息
        completion.push({ arrivedAt: m.at, doneAt: start + busy, delegated: false });
      }
    }
    freeAt = start + busy;
  }

  const times = completion.map((c) => c.doneAt - c.arrivedAt);
  return {
    variant: delegation === 'prefer' ? '单 Session + 优先委派提示' : '单 Session + 中性提示',
    completionTimes: times,
    p50: percentile(times, 50),
    p95: percentile(times, 95),
    cacheReadTokens,
    decisions,
    delegated,
    tasks: completion.length,
    interruptions: interruptions.length,
    meanInterruptionWait: mean(interruptions),
  };
}
