/**
 * context.js —— 上下文组织：Inline / Spill / 局部性 / 链式召回
 *
 * 对应讲义第 5 讲（原文第 3.3 节）：
 *   Inline：把近期很可能使用的信息直接嵌入当前上下文，减少工具往返
 *   Spill ：完整内容放在窗口外，窗口内保留索引或摘要 + 可访问入口
 *   局部性：从最少信息开始，逐步 Inline 关联对象，超限则 Spill
 *   链式召回：为 Trigger / Thread / Worker Session 赋予唯一 ID，
 *             注入时携带紧密关联对象的 ID，构成引用图，按需逐跳展开
 *
 * 关键约束（讲义自测题 2）：把长内容截断并不等于 Spill，
 * 必须同时保留"读回完整内容"的入口，否则模型无法补回被截断的部分。
 */

import { nextId } from './core.js';
import { estimateTokens } from './llm.js';

export const Kind = Object.freeze({
  TRIGGER: 'trigger',
  THREAD: 'thread',
  SESSION: 'session',
  PR: 'pr',
  COMMENT: 'comment',
  LOG: 'log',
});

const KIND_LABEL = {
  trigger: '事件',
  thread: '话题流',
  session: 'Worker',
  pr: 'PR',
  comment: '评论',
  log: '日志',
};

// ---------------------------------------------------------------------------
// 1. 对象库
// ---------------------------------------------------------------------------

export class ObjectStore {
  /**
   * @param {object} o
   * @param {number} [o.inlineCharLimit] 单条正文 Inline 的字符上限，超出部分 Spill（原文举例"前 50 字"）
   */
  constructor({ inlineCharLimit = 50 } = {}) {
    this.inlineCharLimit = inlineCharLimit;
    this.objects = new Map();
    /** threadId -> { id, kind, source, title, eventIds:[], subscriberIds:Set } */
    this.threads = new Map();
    this.readCount = 0; // 统计"读回完整内容"的工具往返次数
  }

  // -- 写入 ---------------------------------------------------------------

  put({ id = null, kind, title, summary, body = '', links = [], meta = {} }) {
    const oid = id ?? nextId(kind === Kind.TRIGGER ? 'T' : kind === Kind.THREAD ? 'H' : 'O');
    const obj = { id: oid, kind, title, summary, body, links: new Set(links), meta, at: Date.now() };
    this.objects.set(oid, obj);
    return obj;
  }

  /** 取得或创建一条 Thread（第 3.2 节：同一 PR / 话题 / 邮件往来下的连续事件）。 */
  ensureThread({ id, source, title }) {
    let t = this.threads.get(id);
    if (!t) {
      t = { id, kind: Kind.THREAD, source, title, summary: title, eventIds: [], subscriberIds: new Set(), body: '' };
      this.threads.set(id, t);
      this.objects.set(id, t);
    }
    return t;
  }

  /** 把一个事件挂到 Thread 上，并建立双向引用（引用图的一条边）。 */
  appendEvent(threadId, triggerId) {
    const t = this.ensureThread({ id: threadId, source: 'unknown', title: threadId });
    t.eventIds.push(triggerId);
    const ev = this.objects.get(triggerId);
    if (ev) {
      ev.links.add(threadId);
      t.links = t.links ?? new Set();
      t.links.add(triggerId);
      // 局部性：被回复消息 / 相邻消息作为紧密关联对象
      if (ev.meta?.replyTo) this.link(triggerId, ev.meta.replyTo);
      const prev = t.eventIds[t.eventIds.length - 2];
      if (prev) this.link(triggerId, prev);
    }
    return t;
  }

  /** 建立双向关联。 */
  link(aId, bId) {
    const a = this.objects.get(aId);
    const b = this.objects.get(bId);
    if (a) (a.links ??= new Set()).add(bId);
    if (b) (b.links ??= new Set()).add(aId);
  }

  get(id) {
    return this.objects.get(id) ?? null;
  }

  // -- Inline：注入上下文时看到的那一小块 ---------------------------------

  /**
   * 生成对象的"窗口内视图"：摘要 + 紧密关联对象 ID + 完整内容入口。
   * 正文超过 inlineCharLimit 时执行 Spill，只留前缀并标注读取入口。
   *
   * 返回形如：
   *   T42 事件 | 用户询问上次登录修复的评审进度
   *        正文: 前 50 字…… [已 Spill，read("T42") 取回完整内容]
   *        关联: H08(话题流) W03(Worker)
   */
  brief(id) {
    const o = this.objects.get(id);
    if (!o) return `${id} <缺失对象>`;
    const label = KIND_LABEL[o.kind] ?? o.kind;
    const lines = [`${o.id} ${label} | ${o.summary ?? o.title ?? ''}`];

    const body = String(o.body ?? '');
    if (body.length > this.inlineCharLimit) {
      lines.push(`     正文: ${body.slice(0, this.inlineCharLimit)}… [已 Spill，read("${o.id}") 取回完整 ${body.length} 字]`);
    } else if (body) {
      lines.push(`     正文: ${body}`);
    }

    const links = [...(o.links ?? [])].filter((x) => x !== o.id);
    if (links.length) {
      const parts = links.map((lid) => {
        const lo = this.objects.get(lid);
        return lo ? `${lid}(${KIND_LABEL[lo.kind] ?? lo.kind})` : lid;
      });
      lines.push(`     关联: ${parts.join(' ')}`);
    }
    if (o.kind === Kind.THREAD && o.eventIds?.length) {
      lines.push(`     含 ${o.eventIds.length} 条事件: ${o.eventIds.slice(-6).join(' ')}${o.eventIds.length > 6 ? ' …' : ''}`);
      if (o.subscriberIds?.size) lines.push(`     订阅者: ${[...o.subscriberIds].join(' ')}`);
    }
    return lines.join('\n');
  }

  /**
   * Spill 的可访问入口：工具调用读回完整内容。
   * 读回的结果同样应用 Inline / Spill，并带回下一跳关联对象的摘要与 ID。
   */
  readFull(id) {
    this.readCount += 1;
    const o = this.objects.get(id);
    if (!o) return { ok: false, text: `${id} 不存在`, nextHop: [] };
    const links = [...(o.links ?? [])].filter((x) => x !== id);
    const nextHop = links.map((lid) => {
      const lo = this.objects.get(lid);
      return lo ? { id: lid, kind: lo.kind, summary: lo.summary ?? lo.title ?? '' } : { id: lid };
    });
    let body = String(o.body ?? '');
    if (o.kind === Kind.THREAD && o.eventIds?.length) {
      body = o.eventIds
        .map((eid) => {
          const ev = this.objects.get(eid);
          return ev ? `- ${ev.id} [${ev.meta?.source ?? ''}] ${ev.summary ?? ''}` : `- ${eid} <缺失>`;
        })
        .join('\n');
    }
    return { ok: true, text: body || '<无正文>', nextHop };
  }
}

// ---------------------------------------------------------------------------
// 2. 链式召回：模型按需沿引用图逐跳展开
// ---------------------------------------------------------------------------

/**
 * 一次召回过程的可观察记录。
 * 讲义第 5 讲的对比：链式召回 vs 把所有历史一次性拼进提示词。
 */
export class RecallSession {
  constructor(store, { tokenBudget = 4000 } = {}) {
    this.store = store;
    this.tokenBudget = tokenBudget;
    this.hops = [];
    this.injectedTokens = 0;
    this.readCalls = 0;
    this.aborted = false;
    this.hitText = '';
  }

  /**
   * 从起点对象开始沿引用图逐跳展开，直到命中或超出预算。
   *
   * 关键设计：每一跳只注入 brief（摘要 + 关联 ID），
   * 只有命中的对象才 readFull 取回完整内容。
   * 这就是"从最少信息开始、逐步 Inline、超限则 Spill"的召回方式。
   *
   * @param {string} startId
   * @param {object} [o]
   * @param {(obj:object)=>boolean} [o.matches] 命中判定
   * @param {number} [o.maxHops]
   * @param {Function} [o.onHop]
   */
  async walk(startId, { matches, maxHops = 6, onHop = null } = {}) {
    let frontier = [startId];
    const seen = new Set();
    for (let hop = 1; hop <= maxHops && frontier.length; hop += 1) {
      const next = [];
      for (const id of frontier) {
        if (seen.has(id)) continue;
        seen.add(id);
        const obj = this.store.get(id);

        // 从最少信息开始：先只看摘要与关联 ID
        const brief = this.store.brief(id);
        this.injectedTokens += estimateTokens(brief);
        if (this.injectedTokens > this.tokenBudget) {
          this.aborted = true;
          this.hops.push({ hop, id, kind: obj?.kind, action: 'abort', injectedTokens: this.injectedTokens, reason: `超出上下文预算 ${this.tokenBudget} token` });
          if (onHop) onHop(this.hops[this.hops.length - 1]);
          return this.result();
        }

        const hit = matches ? matches(obj) : false;
        let preview = '';
        if (hit) {
          // 命中之后才把完整内容读回（Spill 的可访问入口）
          const full = this.store.readFull(id);
          this.readCalls += 1;
          preview = full.text.slice(0, 46).replace(/\n/g, ' ');
        }
        this.hops.push({ hop, id, kind: obj?.kind, action: hit ? 'hit' : 'expand', injectedTokens: this.injectedTokens, preview });
        if (onHop) onHop(this.hops[this.hops.length - 1]);
        if (hit) {
          this.hitText = preview;
          return this.result();
        }

        for (const lid of obj?.links ?? []) if (!seen.has(lid)) next.push(lid);
      }
      frontier = next;
    }
    return this.result();
  }

  result() {
    return {
      hops: this.hops.length,
      readCalls: this.readCalls,
      injectedTokens: this.injectedTokens,
      aborted: this.aborted,
      hit: this.hops.some((h) => h.action === 'hit'),
      hitText: this.hitText,
      path: this.hops.map((h) => h.id).join(' -> '),
    };
  }
}

/**
 * 对照方案 A：把某条 Thread 的全部历史一次性 Inline 进上下文。
 * 用于和链式召回比较"注入 Token"与"读取次数"。
 */
export function flattenWholeHistory(store, threadId) {
  const t = store.get(threadId);
  if (!t) return { injectedTokens: 0, readCalls: 0 };
  let text = '';
  for (const eid of t.eventIds) {
    const ev = store.get(eid);
    if (ev) text += `${ev.summary ?? ''}\n${ev.body ?? ''}\n`;
  }
  return { injectedTokens: estimateTokens(text), readCalls: 0 };
}

/**
 * 对照方案 B：上下文里只放一个 ID，任何信息都要靠工具读回。
 * 讲义自测题 1：这会迫使模型为常用信息反复读取，增加执行步数。
 */
export function idOnlyView(store, id) {
  return { injectedTokens: estimateTokens(id), readCalls: 1, text: store.readFull(id).text };
}
