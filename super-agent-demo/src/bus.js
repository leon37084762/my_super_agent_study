/**
 * bus.js —— 事件总线：两级过滤 + Thread 订阅直通
 *
 * 对应讲义：
 *   第 3 讲（原文 3.1）  程序层策略 Fast Path：来源、群组范围、白名单
 *   第 4 讲（原文 3.2）  Thread、订阅、已订阅直通 / 未订阅交 Router
 *
 * 四个阶段必须分清（第 3 讲学习检查标准）：
 *   接收到事件 -> 允许进入候选集 -> 值得后续处理 -> 分派给执行者
 * 本文件负责前两个阶段与"投递路径选择"，第三、四阶段由 Router 完成。
 */

import { DEFAULT_SOURCE_POLICY, POLICY_LABEL, Policy, Source, makeTrigger } from './core.js';
import { Kind } from './context.js';

/**
 * 程序层准入检查（Fast Path）。纯代码，不调用模型。
 *
 * @returns {{pass:boolean, stage:string, reason:string, policy?:string}}
 */
export function programFilter(trigger, { sourcePolicy = DEFAULT_SOURCE_POLICY, whitelist = null, botName = 'super-agent' } = {}) {
  const policy = sourcePolicy[trigger.source] ?? Policy.IGNORE;

  if (policy === Policy.IGNORE) {
    return { pass: false, stage: '来源策略', reason: `${trigger.source} 配置为「直接忽略」` };
  }
  if (whitelist && whitelist.size > 0 && trigger.source !== Source.INTERNAL && !whitelist.has(trigger.author)) {
    return { pass: false, stage: '白名单', reason: `发出者 ${trigger.author} 不在白名单` };
  }
  if (policy === Policy.EXPLICIT_ONLY && !(trigger.mentions ?? []).includes(botName)) {
    return { pass: false, stage: '明确请求', reason: `来源为「${POLICY_LABEL[Policy.EXPLICIT_ONLY]}」，但未提及 ${botName}` };
  }
  return { pass: true, stage: '准入通过', reason: `来源策略：${POLICY_LABEL[policy]}`, policy };
}

export class EventBus {
  /**
   * @param {object} o
   * @param {import('./core.js').Clock} o.clock
   * @param {import('./core.js').Trace} o.trace
   * @param {import('./context.js').ObjectStore} o.store
   * @param {boolean} [o.directDelivery] false = 消融「去掉直通」（第 10 讲方案二）
   */
  constructor({ clock, trace, store, directDelivery = true, sourcePolicy = DEFAULT_SOURCE_POLICY, whitelist = null, botName = 'super-agent' }) {
    this.clock = clock;
    this.trace = trace;
    this.store = store;
    this.directDelivery = directDelivery;
    this.cfg = { sourcePolicy, whitelist, botName };
    this.router = null;
    this.counters = {
      received: 0,
      programFiltered: 0,
      candidate: 0,
      directDelivered: 0,
      toRouter: 0,
    };
    /** 事件到达时间 -> 处理完成时间，用于统计端到端等待 */
    this.latencies = [];
  }

  attachRouter(router) {
    this.router = router;
  }

  /** 订阅关系：Worker <-> Thread 是多对多（第 4 讲第五节）。 */
  subscribe(workerId, threadId, title = threadId, source = 'unknown') {
    const t = this.store.ensureThread({ id: threadId, source, title });
    t.subscriberIds.add(workerId);
    this.store.link(threadId, workerId);
    return t;
  }

  unsubscribe(workerId, threadId) {
    const t = this.store.get(threadId);
    if (t?.subscriberIds) {
      t.subscriberIds.delete(workerId);
      return true;
    }
    return false;
  }

  /**
   * 发布一个事件，走完"程序过滤 -> 直通 / 交 Router"的完整路径。
   * @returns {Promise<{stage:string, reason:string, trigger?:object}>}
   */
  async publish(input) {
    const trigger = input.id ? input : makeTrigger({ clock: this.clock, ...input });
    this.counters.received += 1;

    // -- 阶段 1/2：程序层 Fast Path -------------------------------------
    const admission = programFilter(trigger, this.cfg);
    if (!admission.pass) {
      this.counters.programFiltered += 1;
      this.trace.log('BUS', `程序层过滤 ${trigger.id} <- ${trigger.source}: ${admission.reason}（未调用模型）`);
      return { stage: 'program-filtered', reason: admission.reason, trigger };
    }

    // 进入候选集：登记到对象库，构成引用图（第 5 讲的 ID 与关联）
    this.counters.candidate += 1;
    const thread = this.store.ensureThread({
      id: trigger.threadId,
      source: trigger.source,
      title: trigger.payload?.threadTitle ?? `${trigger.source} 话题 ${trigger.threadId}`,
    });
    this.store.put({
      id: trigger.id,
      kind: Kind.TRIGGER,
      title: trigger.text.slice(0, 20),
      summary: summarize(trigger.text),
      body: trigger.text,
      meta: { source: trigger.source, author: trigger.author, replyTo: trigger.payload?.replyTo },
    });
    this.store.appendEvent(trigger.threadId, trigger.id);
    this.trace.log('BUS', `候选事件 ${trigger.id} @${thread.id}「${summarize(trigger.text)}」(${admission.reason})`);

    // -- 阶段 3：已订阅 Thread -> 程序直通，不再经 Router 分派 ------------
    if (this.directDelivery && thread.subscriberIds.size > 0) {
      this.counters.directDelivered += 1;
      for (const wid of thread.subscriberIds) {
        this.router?.deliver(wid, trigger, 'direct');
      }
      this.trace.log('BUS', `Fast Path 直通 ${trigger.id} -> ${[...thread.subscriberIds].join(' ')}（Router 不调用模型）`);
      this.router?.observe(trigger); // Router 仍可观察事件、维护全局状态
      this.#markHandled(trigger);
      return { stage: 'direct-delivered', reason: `沿订阅投递给 ${[...thread.subscriberIds].join(' ')}`, trigger };
    }

    // -- 阶段 4：未订阅 -> Slow Path，交 Router 判断归属 ------------------
    this.counters.toRouter += 1;
    if (!this.router) return { stage: 'dropped', reason: '未挂载 Router', trigger };
    this.router.enqueue(trigger);
    return { stage: 'to-router', reason: '未订阅，交 Router 判断与分派', trigger };
  }

  #markHandled(trigger) {
    const arrived = trigger.at ?? 0;
    this.latencies.push(this.clock.now() - arrived);
  }

  markRouted(trigger) {
    this.#markHandled(trigger);
  }

  funnel() {
    const c = this.counters;
    return {
      接收事件: c.received,
      程序层过滤掉: c.programFiltered,
      进入候选集: c.candidate,
      直通投递: c.directDelivered,
      交Router决策: c.toRouter,
    };
  }
}

/** 教学用的摘要：取前 24 字，模拟"注入摘要而非全文"。 */
export function summarize(text, n = 24) {
  const t = String(text ?? '').replace(/\s+/g, ' ').trim();
  return t.length > n ? `${t.slice(0, n)}…` : t;
}
