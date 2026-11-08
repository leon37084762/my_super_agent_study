/**
 * llm.js —— Mock 模型：延迟模型 + Token 计费 + 规则化"语义判断"
 *
 * 对应讲义：
 *   第 2.1 节  Router 常"输入较长、输出较短"，可用较轻量的模型
 *   第 2.2 节  Worker 直接决定任务结果，通常配能力最强的模型
 *   第 3.1 节  语义层策略（Slow Path）：由模型结合提示词与上下文判断是否行动
 *   第 7.2 节  批处理压力测试的三个指标：batch 大小、单次请求耗时、每决策分摊时间
 *   第 10 讲   读缓存 Token（全部 Session 合计）
 *
 * 教学说明：真实模型不可用也不可控，因此这里的"模型"是规则驱动的替身。
 * 它保留两件对理解设计至关重要的性质：
 *   (1) 调用有延迟，且延迟随输入/输出长度变化（Prefill 可并行、Decode 串行）；
 *   (2) 调用有 Token 成本，可以被统计和比较。
 */

import { delay } from './core.js';

// ---------------------------------------------------------------------------
// 1. 模型档位
// ---------------------------------------------------------------------------

/**
 * @property {number} fixedOverheadSec      每次调用的固定开销（排队、建连、调度）
 * @property {number} prefillTokensPerSec   Prefill 吞吐：处理本轮输入上下文的阶段
 * @property {number} decodeTokensPerSec    Decode 吞吐：逐 Token 生成输出
 */
export const MODEL_PROFILES = Object.freeze({
  // Router：轻量模型，Prefill 快、输出短，适合"输入长输出短"的路由决策
  'lite-router': { fixedOverheadSec: 0.3, prefillTokensPerSec: 42000, decodeTokensPerSec: 170 },
  // Worker：最强模型，单位算力更贵、推理更慢，但决定任务质量
  'strong-worker': { fixedOverheadSec: 0.5, prefillTokensPerSec: 14000, decodeTokensPerSec: 62 },
  // Watchdog：独立 Session，中等档位
  'watchdog': { fixedOverheadSec: 0.3, prefillTokensPerSec: 30000, decodeTokensPerSec: 140 },
});

/** 估算一段文本的 Token 数：中文按 1 字/token，其余按 4 字符/token。教学近似。 */
export function estimateTokens(text) {
  let wide = 0;
  let narrow = 0;
  for (const ch of String(text ?? '')) {
    if (/[\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF]/.test(ch)) wide += 1;
    else narrow += 1;
  }
  return Math.ceil(wide + narrow / 4);
}

// ---------------------------------------------------------------------------
// 2. 统计：跨 Session 汇总
// ---------------------------------------------------------------------------

export class LLMStats {
  constructor() {
    this.calls = 0;
    this.promptTokens = 0; // 本轮输入总 Token
    this.cacheReadTokens = 0; // 其中命中缓存读取的部分（第 10 讲的统计口径）
    this.completionTokens = 0;
    this.busySeconds = 0; // 模型被占用的模拟时间
    this.byActor = new Map(); // actor -> 同上的一组数字
  }

  record(actor, { promptTokens = 0, cacheReadTokens = 0, completionTokens = 0, latency = 0 } = {}) {
    this.calls += 1;
    this.promptTokens += promptTokens;
    this.cacheReadTokens += cacheReadTokens;
    this.completionTokens += completionTokens;
    this.busySeconds += latency;
    const a = this.byActor.get(actor) ?? {
      calls: 0,
      promptTokens: 0,
      cacheReadTokens: 0,
      completionTokens: 0,
      busySeconds: 0,
    };
    a.calls += 1;
    a.promptTokens += promptTokens;
    a.cacheReadTokens += cacheReadTokens;
    a.completionTokens += completionTokens;
    a.busySeconds += latency;
    this.byActor.set(actor, a);
  }

  snapshot() {
    return {
      calls: this.calls,
      promptTokens: this.promptTokens,
      cacheReadTokens: this.cacheReadTokens,
      completionTokens: this.completionTokens,
      busySeconds: this.busySeconds,
    };
  }
}

// ---------------------------------------------------------------------------
// 3. MockLLM
// ---------------------------------------------------------------------------

export class MockLLM {
  /**
   * @param {object} o
   * @param {import('./core.js').Clock} o.clock
   * @param {string} o.actor            谁在调用（'ROUTER' / 'W01' / 'WATCHDOG'）
   * @param {keyof MODEL_PROFILES} [o.model]
   * @param {LLMStats} [o.stats]        传入共享实例即可跨 Session 汇总
   */
  constructor({ clock, actor, model = 'lite-router', stats = null }) {
    this.clock = clock;
    this.actor = actor;
    this.model = model;
    this.profile = MODEL_PROFILES[model] ?? MODEL_PROFILES['lite-router'];
    this.stats = stats ?? new LLMStats();
  }

  /** 纯函数：给定输入输出 Token，返回本次调用的模拟耗时（秒）。 */
  latencySeconds(promptTokens, completionTokens) {
    const prefill = promptTokens / this.profile.prefillTokensPerSec;
    const decode = completionTokens / this.profile.decodeTokensPerSec;
    return this.profile.fixedOverheadSec + prefill + decode;
  }

  /**
   * 发起一次"模型调用"。
   * @param {object} o
   * @param {number} o.promptTokens      本轮注入上下文的 Token 数
   * @param {number} [o.cacheReadTokens] 其中读缓存命中的部分
   * @param {number} o.outputTokens      预计输出 Token 数
   * @param {() => any} o.produce        模型"想清楚后"给出的结论（规则替身）
   */
  async call({ promptTokens, cacheReadTokens = promptTokens, outputTokens, produce }) {
    const latency = this.latencySeconds(promptTokens, outputTokens);
    // 先算出结果，再等待等长的时间：模拟"这一轮推理期间系统被占用"。
    const result = produce();
    await delay(Math.max(1, latency * this.clock.k));
    this.stats.record(this.actor, { promptTokens, cacheReadTokens, completionTokens: outputTokens, latency });
    return { result, latency, promptTokens };
  }
}

// ---------------------------------------------------------------------------
// 4. 规则化的"语义判断"：把提示词策略写成可复现的代码
// ---------------------------------------------------------------------------

/** 工作请求线索：出现即认为可能存在需要处理的问题。 */
const WORK_SIGNALS = ['报错', '失败', '修复', '优化', '排查', '复现', '卡顿', '超时', '崩溃', 'bug', '异常', '挂了'];
/** 叫停 / 约束线索：需要改变已有任务的方向。 */
const STEER_SIGNALS = ['先不要', '先不用', '暂缓', '暂停', '别改', '不要动', '叫停', '先停', '等评审'];
/** 询问线索：需要沿引用图召回上下文再回答。 */
const ASK_SIGNALS = ['进度', '到哪', '怎么样了', '上次那个', '评审到', '有没有'];
/** 明确的闲聊：语义层通常判断为无须行动。 */
const CHITCHAT_SIGNALS = ['午饭', '吃什么', '下班', '周末', '哈哈', '恭喜', '辛苦了'];

export function containsAny(text, words) {
  const t = String(text ?? '');
  return words.some((w) => t.includes(w));
}

/** 需求线索：在"需求收集"策略下才值得行动。 */
const IDEA_SIGNALS = ['以后', '如果能', '希望可以', '建议', '也许可以', '要不要考虑'];

/**
 * 语义层策略（第 3.1 节 Slow Path）：这条候选事件是否值得进入后续处理？
 *
 * 下面这段"策略"等价于讲义里的教学提示词：
 *   关注当前产品的问题反馈；日常闲聊不采取行动；
 *   若消息明确要求暂缓，结合现有任务状态判断是否需要调整工作。
 *
 * policy = 'collect-ideas' 时换成需求收集策略，
 * 用于演示第 3 讲的结论：同一条事件是否值得处理，取决于系统目标与策略。
 *
 * @returns {{act: boolean, reason: string, kind: string}}
 */
export function judgeWorthActing(trigger, { activeTaskCount = 0, policy = 'default' } = {}) {
  const { text } = trigger;
  if (containsAny(text, STEER_SIGNALS)) {
    return {
      act: activeTaskCount > 0,
      reason: activeTaskCount > 0 ? '要求暂缓，且有正在进行的任务需要调整' : '要求暂缓，但当前没有相关任务',
      kind: 'steer',
    };
  }
  if (containsAny(text, ASK_SIGNALS)) return { act: true, reason: '用户在询问任务进展，需要召回上下文', kind: 'ask' };
  if (containsAny(text, WORK_SIGNALS)) return { act: true, reason: '包含具体故障/需求信息', kind: 'work' };
  if (containsAny(text, CHITCHAT_SIGNALS)) return { act: false, reason: '日常闲聊，无须 Agent 介入', kind: 'chitchat' };
  if (policy === 'collect-ideas' && containsAny(text, IDEA_SIGNALS)) {
    return { act: true, reason: '需求收集策略：这是一条产品改进线索', kind: 'work' };
  }
  return { act: false, reason: '当前策略下没有明确处理价值', kind: 'other' };
}

/**
 * Router 的路由决策（第 3.2 节 Slow Path）：交给谁处理？
 *
 * @param {object} trigger
 * @param {object} view  Router 注入上下文后能"看见"的全局状态（第 5 讲的 Inline 摘要）
 * @param {Array<{id:string, task:string, threadIds:string[], status:string}>} view.workers
 */
export function judgeRouting(trigger, view) {
  const judgement = judgeWorthActing(trigger, {
    activeTaskCount: view.workers.filter((w) => w.status === 'running').length,
    policy: view.policy,
  });

  if (!judgement.act) return { action: 'ignore', reason: judgement.reason, kind: judgement.kind };

  // 是否已有 Worker 在做同一件事？（依据注入的 Worker 摘要 + 关键词重叠）
  const related = view.workers.find((w) => w.status !== 'closed' && taskOverlap(w.task, trigger.text));

  if (judgement.kind === 'steer') {
    if (related) return { action: 'steer', workerId: related.id, reason: `约束已投递给 ${related.id}` };
    // 全局约束（例如“先不要改接口”）往往不包含具体任务关键词，
    // 但它是用户在主对话中发出的、对所有在跑任务都生效的限制。
    const running = view.workers.filter((w) => w.status !== 'closed');
    return running.length
      ? { action: 'steer-all', reason: `未指向具体任务，作为全局约束广播给 ${running.map((w) => w.id).join(' ')}` }
      : { action: 'ignore', reason: '没有可调整的在跑任务' };
  }
  if (judgement.kind === 'ask') {
    return { action: 'recall-and-reply', workerId: related?.id ?? null, reason: '需要沿引用图召回后回复用户' };
  }
  if (related) return { action: 'attach', workerId: related.id, reason: `已有相关任务 ${related.id}，事件并入其 Thread` };
  return { action: 'dispatch', reason: '新任务，创建 Worker 并订阅该 Thread' };
}

/** 教学用的"任务相关性"判断：共享关键词即视为相关。 */
export function taskOverlap(taskText, eventText) {
  const keywords = ['登录', '导出', '渲染', '超时', '性能', '卡顿', '配置', '文档', '波浪号'];
  const hit = keywords.filter((k) => String(taskText).includes(k));
  return hit.length > 0 && hit.some((k) => String(eventText).includes(k));
}

/**
 * Watchdog 的进展判断（第 6 节）：区分"活动量"与"实质进展"。
 * @param {object} evidence
 * @param {number} evidence.repeatedSameCall   连续相同调用且结果无变化的次数
 * @param {number} evidence.newEvidenceCount   最近窗口内出现的新证据条数
 * @param {number} evidence.scopeViolations    越界修改次数（目标偏离）
 * @param {number} evidence.infraErrors        基础设施报错次数
 * @param {string} evidence.stage              任务阶段：'waiting-tool' | 'iterating' | 'verifying'
 */
export function judgeProgress(evidence) {
  if (evidence.infraErrors > 0) return { verdict: 'infra', reason: `基础设施报错 ${evidence.infraErrors} 次` };
  if (evidence.scopeViolations > 0) return { verdict: 'drift', reason: `越出任务范围 ${evidence.scopeViolations} 次` };
  // 关键：长时间等待工具不等于停滞（第 9 讲自测题 3）
  if (evidence.stage === 'waiting-tool' && evidence.newEvidenceCount === 0) {
    return { verdict: 'watch', reason: '正在等待耗时工具，暂不判定为停滞' };
  }
  if (evidence.repeatedSameCall >= 3 && evidence.newEvidenceCount === 0) {
    return { verdict: 'stalled', reason: `连续 ${evidence.repeatedSameCall} 次相同调用且无新证据` };
  }
  return { verdict: 'healthy', reason: '有新证据或调用在变化' };
}
