/**
 * core.js —— 通用基础设施：事件模型、模拟时钟、可读日志
 *
 * 对应讲义：
 *   第 3 讲   Trigger（环境事件）与三个阶段的区分
 *   第 3.2 节 Thread（把相关事件组织成一条流）
 *   第 5 讲   为 Trigger / Thread / Worker Session 赋予唯一 ID，构成引用图
 *   第 10 讲  合成负载：消息间隔与工作时长可按比例缩放
 *
 * 教学说明：这里的一切都是"教学替身"。MockLLM 不是真实模型，
 * 事件源不是真实 IM / GitHub 接入，目的是把设计机制变得可观察、可测量。
 */

// ---------------------------------------------------------------------------
// 1. 事件来源与程序层策略
// ---------------------------------------------------------------------------

/** 原文第 3 节列举的事件来源（Trigger），加上系统内部事件。 */
export const Source = Object.freeze({
  IM_WORK: 'im:work', // 工作群 A：允许观察问题反馈
  IM_SOCIAL: 'im:social', // 闲聊群：不在处理范围
  IM_NOTIFY: 'im:notify', // 通知群 B：所有事件直接忽略
  GH_PR: 'github:pr', // PR 上的提交与评审意见
  GH_CI: 'github:ci', // CI 运行结果
  EMAIL: 'email', // 邮件（会议邀请等）
  SCHEDULE: 'schedule', // 日程提醒
  INTERNAL: 'internal', // 系统内部：工具完成事件、Watchdog 定时消息
});

/**
 * 原文第 3.1 节：程序层策略可按来源分别设置为
 * "仅响应明确请求""作为候选事件"或"直接忽略"。
 */
export const Policy = Object.freeze({
  IGNORE: 'ignore',
  EXPLICIT_ONLY: 'explicit_only',
  OBSERVE: 'observe',
});

export const POLICY_LABEL = Object.freeze({
  [Policy.IGNORE]: '直接忽略',
  [Policy.EXPLICIT_ONLY]: '仅响应明确请求',
  [Policy.OBSERVE]: '作为候选事件',
});

/** 一份示例配置：教学设定，不是原文给出的规则表。 */
export const DEFAULT_SOURCE_POLICY = Object.freeze({
  [Source.IM_WORK]: Policy.OBSERVE,
  [Source.IM_SOCIAL]: Policy.IGNORE,
  [Source.IM_NOTIFY]: Policy.IGNORE,
  [Source.GH_PR]: Policy.OBSERVE,
  [Source.GH_CI]: Policy.OBSERVE,
  [Source.EMAIL]: Policy.EXPLICIT_ONLY,
  [Source.SCHEDULE]: Policy.OBSERVE,
  [Source.INTERNAL]: Policy.OBSERVE,
});

// ---------------------------------------------------------------------------
// 2. 唯一 ID：Trigger(T) / Thread(H) / Worker(W) / PR(P) / 评论(C) / 日志(L)
// ---------------------------------------------------------------------------

const counters = new Map();

/** 生成讲义风格的教学 ID：T01、H02、W03…… */
export function nextId(prefix) {
  const n = (counters.get(prefix) ?? 0) + 1;
  counters.set(prefix, n);
  return `${prefix}${String(n).padStart(2, '0')}`;
}

export function resetIds() {
  counters.clear();
}

// ---------------------------------------------------------------------------
// 3. 模拟时钟
// ---------------------------------------------------------------------------

export const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * 演示用的时间轴：真实墙上时间按比例换算成"模拟秒"。
 * 第 10 讲原文注明消息间隔与工作时长按 1/4 缩放、模型调用耗时为真实值；
 * 这里为了几秒内跑完，默认 1 模拟秒 = 80 毫秒，并允许每个 demo 单独调整。
 *
 * 时钟读数由墙上时间推导，因此并发的多个任务看到的时刻是一致的。
 */
export class Clock {
  constructor(msPerSimSecond = 80) {
    this.k = msPerSimSecond;
    this.t0 = Date.now();
  }

  /** 当前模拟时间（秒）。 */
  now() {
    return (Date.now() - this.t0) / this.k;
  }

  /** 睡眠若干模拟秒。 */
  sleep(simSeconds) {
    return delay(Math.max(0, simSeconds * this.k));
  }

  /** 日志时间戳。 */
  stamp() {
    return `+${this.now().toFixed(1).padStart(5, ' ')}s`;
  }
}

// ---------------------------------------------------------------------------
// 4. Trigger：一个环境事件
// ---------------------------------------------------------------------------

/**
 * @param {object} o
 * @param {string} o.source     Source 之一
 * @param {string} o.threadId   所属 Thread（第 3.2 节）
 * @param {string} o.text       事件正文（摘要来源）
 * @param {string} [o.author]   发出者
 * @param {string[]} [o.mentions] 明确提及的对象，例如 ['super-agent']
 * @param {object} [o.payload]  结构化字段：CI 状态、PR 号、邀请时间等
 * @param {Clock}  [o.clock]
 */
export function makeTrigger({ source, threadId, text, author = 'someone', mentions = [], payload = {}, clock = null }) {
  return {
    id: nextId('T'),
    source,
    threadId,
    text,
    author,
    mentions,
    payload,
    at: clock ? clock.now() : 0,
  };
}

// ---------------------------------------------------------------------------
// 5. 中日韩字符宽度与表格渲染（让控制台输出对齐）
// ---------------------------------------------------------------------------

const WIDE =
  /[\u1100-\u115F\u2E80-\uA4CF\uA960-\uA97F\uAC00-\uD7A3\uF900-\uFAFF\uFE10-\uFE19\uFE30-\uFE6F\uFF00-\uFF60\uFFE0-\uFFE6]/;

export function displayWidth(s) {
  let w = 0;
  for (const ch of String(s)) w += WIDE.test(ch) ? 2 : 1;
  return w;
}

export function padEnd(s, width) {
  const d = width - displayWidth(s);
  return d > 0 ? String(s) + ' '.repeat(d) : String(s);
}

export function padStart(s, width) {
  const d = width - displayWidth(s);
  return d > 0 ? ' '.repeat(d) + String(s) : String(s);
}

/**
 * 渲染一张 ASCII 表格。
 * @param {string[]} headers
 * @param {Array<Array<string|number>>} rows
 * @param {Array<'l'|'r'>} [aligns] 每列对齐方式，默认左对齐
 */
export function renderTable(headers, rows, aligns = []) {
  const cells = rows.map((r) => headers.map((_, i) => (r[i] === undefined || r[i] === null ? '' : String(r[i]))));
  const widths = headers.map((h, i) =>
    Math.max(displayWidth(h), ...cells.map((row) => displayWidth(row[i])), 1),
  );
  const line = (row) =>
    '| ' +
    row
      .map((c, i) => (aligns[i] === 'r' ? padStart(c, widths[i]) : padEnd(c, widths[i])))
      .join(' | ') +
    ' |';
  const sep = '+' + widths.map((w) => '-'.repeat(w + 2)).join('+') + '+';
  return [sep, line(headers.map(String)), sep, ...cells.map(line), sep].join('\n');
}

// ---------------------------------------------------------------------------
// 6. Trace：把系统内部动作打印成可读时间线
// ---------------------------------------------------------------------------

export class Trace {
  constructor(clock = null, { title = '' } = {}) {
    this.clock = clock;
    this.lines = [];
    if (title) this.section(title);
  }

  /** 分节标题。 */
  section(text) {
    this.#raw('');
    this.#raw(`=== ${text} ===`);
  }

  /** 教学旁白：解释正在演示的机制，不属于系统输出。 */
  note(text) {
    this.#raw(`  # ${text}`);
  }

  /** 系统动作日志。 */
  log(actor, text) {
    const stamp = this.clock ? this.clock.stamp() : '        ';
    this.#raw(`[${stamp}] ${padEnd(actor, 9)}| ${text}`);
  }

  /** 多行块（表格、上下文快照等）。 */
  block(text) {
    for (const l of String(text).split('\n')) this.#raw(`           ${l}`);
  }

  #raw(line) {
    this.lines.push(line);
    console.log(line);
  }

  dump() {
    return this.lines.join('\n');
  }
}

/** demo 结束时打印结论要点。 */
export function takeaways(trace, items) {
  trace.section('本讲要点');
  items.forEach((t, i) => trace.note(`${i + 1}. ${t}`));
}
