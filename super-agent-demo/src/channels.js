/**
 * channels.js —— 多信道交互：按渠道组织表达（原文 4.2）
 *
 * 对应讲义第 7 讲第六、七节：
 *   IM 等即时通信        -> 轻量、低打扰；需要决策或验收时才通知；直接回答、给简短结论
 *   GitHub / 邮件 / 文档 -> 结构化、高密度；交代背景、改动与验证结果
 *
 * 原文给出的"单次回复不超过 100 词"是其实践中有效的 IM 硬性规范；
 * 讲义提醒不能机械换算为所有中文消息的统一字数上限，
 * 因此这里把中日韩字符按"1 字 = 1 词"计入同一预算，仅作教学示范。
 */

/** 通知目的：决定在低打扰渠道是否值得发出消息。 */
export const Purpose = Object.freeze({
  PROGRESS: 'progress', // 中间进展
  DECISION: 'decision', // 需要人做决定
  ACCEPTANCE: 'acceptance', // 需要验收
  ANSWER: 'answer', // 用户直接提问的回答
  ALERT: 'alert', // 异常告警
});

export const CHANNEL_NORMS = Object.freeze({
  im: {
    label: 'IM 即时通信',
    density: '轻量低打扰',
    maxWords: 100,
    notifyOn: [Purpose.DECISION, Purpose.ACCEPTANCE, Purpose.ANSWER, Purpose.ALERT],
  },
  github_pr: {
    label: 'GitHub PR 描述',
    density: '结构化高密度',
    maxWords: Infinity,
    sections: ['问题背景', '改动', '验证', '待确认'],
    notifyOn: [Purpose.PROGRESS, Purpose.DECISION, Purpose.ACCEPTANCE, Purpose.ANSWER, Purpose.ALERT],
  },
  email: {
    label: '邮件',
    density: '结构化高密度',
    maxWords: Infinity,
    sections: ['背景', '结论', '依据', '待确认'],
    notifyOn: [Purpose.PROGRESS, Purpose.DECISION, Purpose.ACCEPTANCE, Purpose.ANSWER, Purpose.ALERT],
  },
  terminal: {
    label: '终端日志',
    density: '完整过程',
    maxWords: Infinity,
    notifyOn: [Purpose.PROGRESS, Purpose.DECISION, Purpose.ACCEPTANCE, Purpose.ANSWER, Purpose.ALERT],
  },
});

/** 词数统计：中日韩字符按字计，其余按空白分词。 */
export function countWords(text) {
  let wide = 0;
  const rest = [];
  for (const ch of String(text ?? '')) {
    if (/[\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF]/.test(ch)) wide += 1;
    else rest.push(ch);
  }
  const latin = rest.join('').split(/\s+/).filter(Boolean).length;
  return wide + latin;
}

/**
 * 低打扰判断：这条消息现在值得发给该渠道吗？
 * 讲义第 6 讲第五节：把"是否值得尝试"与"是否值得打断用户"当作不同问题。
 */
export function shouldNotify(channel, purpose) {
  const norm = CHANNEL_NORMS[channel] ?? CHANNEL_NORMS.im;
  return norm.notifyOn.includes(purpose);
}

/**
 * 同一个经过验证的事实，按渠道渲染成不同密度的表达。
 * @param {string} channel
 * @param {{topic:string, background?:string, change:string, verification:string, pending?:string, ref?:string}} fact
 */
export function renderForChannel(channel, fact) {
  switch (channel) {
    case 'im':
      return `${fact.topic}已处理，${fact.verification}。${fact.ref ? `${fact.ref} ` : ''}请确认是否采用。`;
    case 'github_pr':
      return [
        `问题背景：${fact.background ?? fact.topic}`,
        `改动：${fact.change}`,
        `验证：${fact.verification}`,
        `待确认：${fact.pending ?? '无'}`,
      ].join('\n');
    case 'email':
      return [`背景：${fact.background ?? fact.topic}`, `结论：${fact.change}`, `依据：${fact.verification}`, `待确认：${fact.pending ?? '无'}`].join('\n');
    default:
      return `[${fact.topic}] 改动=${fact.change} 验证=${fact.verification} 待确认=${fact.pending ?? '-'}`;
  }
}

/** 规范审计：是否超出该渠道的表达约束。 */
export function auditChannel(channel, text) {
  const norm = CHANNEL_NORMS[channel] ?? CHANNEL_NORMS.im;
  const words = countWords(text);
  if (words > norm.maxWords) {
    return { ok: false, words, reason: `超出 ${norm.label} 规范：${words} 词 > ${norm.maxWords} 词上限` };
  }
  if (norm.sections) {
    const missing = norm.sections.filter((s) => !text.includes(s));
    if (missing.length) return { ok: false, words, reason: `${norm.label} 缺少结构化小节：${missing.join('、')}` };
  }
  return { ok: true, words, reason: `符合 ${norm.label}「${norm.density}」规范` };
}
