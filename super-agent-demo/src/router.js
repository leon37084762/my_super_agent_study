/**
 * router.js —— Router 层：常驻全局协调 + 批处理 + 统一对外回复
 *
 * 对应讲义：
 *   第 2 讲（原文 2.1）  Router 是全局序点、协调点和记忆；不执行具体任务；可用较轻量模型
 *   第 2 讲第五节        批处理：上一轮推理期间到达的事件积压，下一轮合并处理
 *   第 4 讲（原文 3.2）  未订阅事件由 Router 判断分派；已订阅事件直通，Router 仍可观察
 *   第 5 讲（原文 3.3）  Router 不携带全部历史，只保留摘要与关联 ID，靠链式召回补回
 *   第 7 讲（原文 4.2）  Worker 不直接回复用户，由 Router 统一按渠道组织表达
 */

import { Source } from './core.js';
import { estimateTokens, judgeRouting } from './llm.js';
import { Kind, RecallSession } from './context.js';
import { Purpose, auditChannel, renderForChannel, shouldNotify } from './channels.js';

const SYSTEM_PROMPT = `你是常驻的全局路由层（Router）。
职责：承接人类交互、维护跨任务关联、分派独立任务、统一对外回复。
禁止：亲自执行代码修改、测试与调试；不要介入 Worker 内部的步骤拆解。
判断顺序：这条事件是否值得处理 -> 是否已有相关任务 -> 交给谁 -> 是否需要回复用户。`;

export class Router {
  /**
   * @param {object} o
   * @param {import('./core.js').Clock} o.clock
   * @param {import('./core.js').Trace} o.trace
   * @param {import('./context.js').ObjectStore} o.store
   * @param {import('./bus.js').EventBus} o.bus
   * @param {import('./llm.js').MockLLM} o.llm       Router 专用（通常是轻量档）
   * @param {(spec:object)=>{start:Function}} o.workerFactory  创建 Worker 的工厂，避免模块环依赖
   * @param {boolean} [o.filterOnly]  true = 只演示第 3 讲的语义过滤，不做分派
   * @param {number}  [o.maxBatch]    单轮最多合并多少条事件（第 2 讲：模型单轮处理能力的上限）
   * @param {number}  [o.globalStateTokens] 常驻全局状态的规模（长输入、短输出的来源）
   */
  constructor({
    clock,
    trace,
    store,
    bus,
    llm,
    workerFactory,
    filterOnly = false,
    maxBatch = 8,
    globalStateTokens = 9000,
    pollSimSec = 0.05,
    userChannel = 'im',
    semanticPolicy = 'default',
  }) {
    this.clock = clock;
    this.trace = trace;
    this.store = store;
    this.bus = bus;
    this.llm = llm;
    this.workerFactory = workerFactory;
    this.filterOnly = filterOnly;
    this.maxBatch = maxBatch;
    this.globalStateTokens = globalStateTokens;
    this.pollSimSec = pollSimSec;
    this.userChannel = userChannel;
    /** 语义层策略（第 3.1 节）：可以用自然语言调整的扩展点，这里用模式名代替提示词 */
    this.semanticPolicy = semanticPolicy;
    /** filterOnly 模式下记录每条事件的语义判断结果 */
    this.filterResults = [];

    this.queue = []; // 待决策事件（Slow Path）
    this.workers = new Map(); // workerId -> Worker
    this.running = false;
    // 本轮是否正在推理：batch 已从队列取出、但决策尚未落地的空窗期为 true。
    // quiesce 必须避开这个窗口，否则会误判"队列空且无 Worker"而提前返回。
    this.busy = false;
    this.rounds = []; // 每轮：{ batchSize, latency, promptTokens, cacheReadTokens, at }
    this.replies = []; // 面向用户的输出
    this.decisionLatencies = []; // 每条事件：到达 -> 决策完成
    this.observedWhileDirect = 0; // 直通时 Router 仍观察到的事件数
    this.suppressedProgress = 0; // 低打扰规范下被抑制的中间进展
    this.done = null;
  }

  // -- 事件入口 -----------------------------------------------------------

  enqueue(trigger) {
    this.queue.push({ trigger, arrivedAt: trigger.at ?? this.clock.now() });
  }

  /** 直通投递时由 Bus 调用：Worker 直接收事件，Router 不花模型调用。 */
  deliver(workerId, trigger, how = 'router') {
    const w = this.workers.get(workerId);
    if (!w) return false;
    w.receive(trigger, how);
    return true;
  }

  /**
   * 第 4 讲自测题 1：直通不等于 Router 失去全局信息。
   * 这里只做程序化的状态更新（摘要级别），不调用模型。
   */
  observe(trigger) {
    this.observedWhileDirect += 1;
    for (const w of this.workers.values()) {
      if (w.subscriptions.has(trigger.threadId)) w.noteObserved(trigger);
    }
  }

  // -- 主循环 -------------------------------------------------------------

  /** 启动常驻循环（不阻塞）。需要等待退出时用 await router.done。 */
  start() {
    if (this.done) return this;
    this.running = true;
    this.done = (async () => {
      while (this.running) {
        if (this.queue.length === 0) {
          await this.clock.sleep(this.pollSimSec);
          continue;
        }
        // 批处理：一次取走当前积压，推理期间新到的事件自然积到下一轮
        const batch = this.queue.splice(0, this.maxBatch);
        const overflow = this.queue.length;
        this.busy = true; // 取出即置忙：覆盖"队列已空但 Worker 尚未创建"的空窗
        try {
          await this.#round(batch, overflow);
        } finally {
          this.busy = false;
        }
      }
    })();
    return this;
  }

  stop() {
    this.running = false;
  }

  async #round(batch, overflowAtStart) {
    const { text, cachedTokens, promptTokens } = this.#buildContext(batch);
    const outputTokens = 30 + batch.length * 42; // Router 输出短：每条事件一小段决策
    const startedAt = this.clock.now();

    const { result, latency } = await this.llm.call({
      promptTokens,
      cacheReadTokens: cachedTokens,
      outputTokens,
      produce: () => {
        const view = this.globalView(); // 本轮开始时的全局快照（批内各条事件看到同一视图）
        return batch.map((item) => ({
          ...item,
          decision: judgeRouting(item.trigger, view),
        }));
      },
    });

    this.rounds.push({
      at: startedAt,
      batchSize: batch.length,
      latency,
      promptTokens,
      cacheReadTokens: cachedTokens,
      overflowAtStart,
      contextChars: text.length,
    });
    this.trace.log('ROUTER', `批处理 ${batch.length} 条事件，单轮耗时 ${latency.toFixed(2)}s（输入 ${promptTokens} tok，其中读缓存 ${cachedTokens} tok）`);

    for (const item of result) await this.#apply(item);
  }

  /** 组装本轮注入的上下文：系统提示 + 全局状态摘要 + 每条事件的 brief。 */
  #buildContext(batch) {
    const global = this.workers.size
      ? [...this.workers.values()].map((w) => w.brief()).join('\n')
      : '（当前没有在跑任务）';
    // 常驻全局状态被抽象成固定规模：真实系统里它随任务数增长，但只放摘要
    const padded = global + ' '.repeat(Math.max(0, this.globalStateTokens * 3 - global.length));
    const cachedPart = `${SYSTEM_PROMPT}\n[全局状态]\n${padded}\n`;
    const batchPart = `[本批事件 ${batch.length} 条]\n${batch.map((b) => this.store.brief(b.trigger.id)).join('\n')}\n`;
    const cachedTokens = Math.max(estimateTokens(cachedPart), this.globalStateTokens);
    return {
      text: cachedPart + batchPart,
      cachedTokens,
      promptTokens: cachedTokens + estimateTokens(batchPart),
    };
  }

  /** Router 眼中的全局视图：只有摘要级信息（第 5 讲：不是全部历史）。 */
  globalView() {
    return {
      policy: this.semanticPolicy,
      workers: [...this.workers.values()].map((w) => ({
        id: w.id,
        task: w.task,
        status: w.status,
        threadIds: [...w.subscriptions],
      })),
    };
  }

  // -- 决策落地 -----------------------------------------------------------

  async #apply({ trigger, decision, arrivedAt }) {
    this.decisionLatencies.push({ id: trigger.id, waited: this.clock.now() - arrivedAt, action: decision.action });
    this.bus?.markRouted(trigger);

    if (this.filterOnly) {
      // 第 3 讲到"值得处理"为止，不进入路由分派
      const worth = decision.action !== 'ignore';
      this.filterResults.push({ id: trigger.id, source: trigger.source, text: trigger.text, worth, reason: decision.reason });
      this.trace.log('ROUTER', `语义层判断 ${trigger.id}: ${worth ? '值得处理' : '无须行动'} —— ${decision.reason}`);
      return;
    }

    switch (decision.action) {
      case 'ignore':
        this.trace.log('ROUTER', `语义层判断 ${trigger.id}: 无须行动 —— ${decision.reason}`);
        return;

      case 'dispatch': {
        const w = this.#spawnWorker(trigger);
        this.trace.log('ROUTER', `分派新任务 ${w.id}「${w.task}」并订阅 ${trigger.threadId}（${decision.reason}）`);
        w.receive(trigger, 'dispatch');
        return;
      }

      case 'attach': {
        this.trace.log('ROUTER', `事件 ${trigger.id} 并入已有任务 ${decision.workerId}（${decision.reason}）`);
        this.deliver(decision.workerId, trigger, 'router');
        return;
      }

      case 'steer': {
        this.trace.log('ROUTER', `约束投递 ${trigger.id} -> ${decision.workerId}：${trigger.text}`);
        this.deliver(decision.workerId, trigger, 'steer');
        return;
      }

      case 'steer-all': {
        // 第 2 讲：跨任务的高层关联由 Router 维护。
        // 一条不含任务关键词的全局约束，仍能关联到所有在跑任务。
        const targets = [...this.workers.values()].filter((w) => w.status !== 'closed');
        this.trace.log('ROUTER', `全局约束广播 ${trigger.id} -> ${targets.map((w) => w.id).join(' ') || '无'}：${trigger.text}`);
        for (const w of targets) this.deliver(w.id, trigger, 'steer');
        return;
      }

      case 'recall-and-reply': {
        await this.#recallAndReply(trigger);
        return;
      }

      default:
        this.trace.log('ROUTER', `未知决策 ${decision.action}`);
    }
  }

  #spawnWorker(trigger) {
    const w = this.workerFactory({
      task: trigger.text,
      originTrigger: trigger,
      originThread: trigger.threadId,
      router: this,
    });
    this.workers.set(w.id, w);
    w.dispatchedAt = this.clock.now();
    this.store.put({
      id: w.id,
      kind: Kind.SESSION,
      title: w.task,
      summary: `任务：${w.task.slice(0, 24)}`,
      body: '',
      meta: { worker: true },
    });
    this.bus.subscribe(w.id, trigger.threadId, `${trigger.source} 话题`, trigger.source);
    w.start(); // 异步启动，不阻塞 Router（第 2 讲：Router 不陷入具体执行）
    return w;
  }

  /**
   * 链式召回后回复用户（第 5 讲完整案例）。
   * Router 不常驻任务历史，靠 ID 引用图按需逐跳找回。
   */
  async #recallAndReply(trigger) {
    const recall = new RecallSession(this.store, { tokenBudget: 1200 });
    const res = await recall.walk(trigger.id, {
      matches: (obj) => obj && (obj.kind === Kind.PR || (obj.kind === Kind.THREAD && obj.source === Source.GH_PR)),
      maxHops: 5,
      onHop: (h) =>
        this.trace.log(
          'ROUTER',
          `  召回第 ${h.hop} 跳 ${h.id}(${h.kind ?? '?'}) ${h.action === 'hit' ? '命中' : '展开'} 累计注入 ${h.injectedTokens} tok`,
        ),
    });

    const lastHit = [...recall.hops].reverse().find((h) => h.action === 'hit');
    const target = lastHit ? this.store.get(lastHit.id) : null;
    const answer = target
      ? `${trigger.text.slice(0, 12)}… 已找到：${target.summary ?? target.title}（沿 ${res.path} 召回，${res.readCalls} 次读取，注入 ${res.injectedTokens} tok）`
      : `未能沿引用图定位到相关对象（召回 ${res.hops} 跳，注入 ${res.injectedTokens} tok）`;

    this.#say('im', Purpose.ANSWER, answer, { trigger });
  }

  // -- 对外表达 -----------------------------------------------------------

  /**
   * Worker 汇报 -> Router 统一回复用户。
   * 低打扰渠道会抑制中间进展（第 6 讲：不必每次尝试都通知用户）。
   */
  report(workerId, { purpose, fact, text = null, channel = this.userChannel }) {
    const w = this.workers.get(workerId);
    if (!w) return;
    w.lastReport = fact?.topic ?? text ?? '';

    if (!shouldNotify(channel, purpose)) {
      this.suppressedProgress += 1;
      this.trace.log('ROUTER', `抑制 ${workerId} 的中间进展（${channel} 低打扰规范，不打断用户）`);
      return;
    }
    const body = text ?? renderForChannel(channel, fact);
    this.#say(channel, purpose, body, { workerId });
  }

  #say(channel, purpose, text, meta = {}) {
    const audit = auditChannel(channel, text);
    this.replies.push({ channel, purpose, text, audit, at: this.clock.now(), ...meta });
    this.trace.log('TO-USER', `[${channel}] ${text.replace(/\n/g, ' / ')}`);
    if (!audit.ok) this.trace.log('TO-USER', `  ! 规范审计不通过：${audit.reason}`);
  }

  // -- 统计 ---------------------------------------------------------------

  summary() {
    const batches = this.rounds.map((r) => r.batchSize);
    const lat = this.rounds.map((r) => r.latency);
    const mean = (a) => (a.length ? a.reduce((x, y) => x + y, 0) / a.length : 0);
    const totalDecisions = batches.reduce((x, y) => x + y, 0);
    const servicePerDecision = mean(lat.map((l, i) => l / batches[i]));
    return {
      routerRounds: this.rounds.length,
      decisions: totalDecisions,
      meanBatch: mean(batches),
      meanRoundLatency: mean(lat),
      servicePerDecision,
      cacheReadTokens: this.rounds.reduce((s, r) => s + r.cacheReadTokens, 0),
      promptTokens: this.rounds.reduce((s, r) => s + r.promptTokens, 0),
      observedWhileDirect: this.observedWhileDirect,
      suppressedProgress: this.suppressedProgress,
      replies: this.replies.length,
    };
  }

  /** 等待队列排空且所有 Worker 结束（closed）。超时而未结束则返回 false。 */
  async quiesce({ timeoutSimSec = 120 } = {}) {
    const isDone = (w) => w.status === 'closed' || w.status === 'idle';
    const isSettled = () => !this.busy && this.queue.length === 0 && [...this.workers.values()].every(isDone);
    const t0 = this.clock.now();
    while (this.clock.now() - t0 < timeoutSimSec) {
      if (isSettled()) {
        // 再等一小段，确认没有尾巴事件（例如后台工具的完成事件）
        await this.clock.sleep(0.3);
        if (isSettled()) return true;
      }
      await this.clock.sleep(0.1);
    }
    return false;
  }
}
