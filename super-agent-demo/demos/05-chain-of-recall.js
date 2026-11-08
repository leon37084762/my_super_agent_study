/**
 * 演示 05 —— 上下文组织与链式召回
 *
 * 对应讲义第 5 讲（原文第 3.3 节）。
 *
 * Router 不常驻全部历史，只保留摘要与关联 ID，需要时靠"链式召回"沿引用图逐跳找回。
 * 本演示分四部分：
 *   1. 先跑一个真实任务，让系统自然留下一张引用图（Trigger/Thread/Worker/PR/评论），
 *      再补入若干条带长日志的历史消息，模拟"累积了很久的工作群"
 *   2. Inline 与 Spill：窗口内放摘要 + 关联 ID + 读取入口，完整内容留在窗口外
 *   3. 关键区分：把长内容"截断"并不等于 Spill —— 必须保留读回完整内容的入口
 *   4. 链式召回 vs 两种对照方案（一次性拼历史 / 只放 ID 全靠读回）的 Token 与往返对比
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';
import { Kind, RecallSession, flattenWholeHistory, idOnlyView } from '../src/context.js';
import { estimateTokens } from '../src/llm.js';

const KIND_LABEL = { trigger: '事件', thread: '话题流', session: 'Worker', pr: 'PR', comment: '评论', log: '日志' };

// 一条"很长"的历史日志正文：模拟工作群里粘贴的完整排查记录，用于让 Spill 发挥省 Token 的作用。
// 正文远长于 Inline 上限（50 字）时，链式召回只注入前缀，而"一次性拼历史"要注入全文，差距才明显。
const LONG_LOG =
  '调用链采样：网关->鉴权->会话服务，鉴权段 P99 从 120ms 抬升到 900ms，会话服务连接池等待队列积压，峰值时段出现 504；已导出对应时段全部请求的时间戳、耗时分布与重试次数，逐段核对超时与重试配置，完整日志体积较大，此处仅摘录开头部分，其余见附件。';

/** 向 Thread 补入 n 条历史消息（含较长正文），模拟长期累积的讨论。 */
function seedHistory(store, threadId, n = 8) {
  for (let i = 0; i < n; i += 1) {
    const body = `${LONG_LOG}${LONG_LOG.slice(0, 60)}（第 ${i + 1} 条历史消息，完整日志见附件）`;
    const id = `E${String(i + 1).padStart(2, '0')}`;
    store.put({ id, kind: Kind.TRIGGER, title: `历史消息 ${i + 1}`, summary: `排查记录 ${i + 1}：登录网关高峰期 504`, body });
    store.appendEvent(threadId, id);
  }
  return n;
}

// ---------------------------------------------------------------------------
// 第一部分：跑一个真实任务 + 补入历史，得到一张引用图
// ---------------------------------------------------------------------------

async function buildReferenceGraph(trace) {
  const sys = buildSystem({ title: '第一部分：一个真实任务 + 历史消息留下的引用图', msPerSimSecond: 30, globalStateTokens: 6000 });
  const { bus, router, store } = sys;
  router.start();
  await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '线上登录偶发超时，用户反馈附了复现步骤，麻烦排查修复',
    author: 'user-1',
    payload: { threadTitle: '工作群：登录故障' },
  });
  await router.quiesce({ timeoutSimSec: 60 });
  router.stop();
  await router.done;

  const seeded = seedHistory(store, 'H01', 8);
  trace.note(`任务跑完后，又向 H01 补入 ${seeded} 条历史消息（含较长日志正文），模拟一条"聊了很久"的工作群。`);
  trace.note('对象库里现在是一张引用图（每个对象都有唯一 ID，并携带紧密关联对象的 ID）：');
  const rows = [...store.objects.values()].map((o) => [
    o.id,
    KIND_LABEL[o.kind] ?? o.kind,
    (o.summary ?? o.title ?? '').slice(0, 20),
    String(o.body ?? '').length,
    [...(o.links ?? [])].filter((x) => x !== o.id).slice(0, 5).join(' ') || '-',
  ]);
  trace.block(renderTable(['ID', '类型', '摘要', '正文字数', '关联 ID（引用图的边）'], rows));
  trace.note(`对象总数 ${store.objects.size}；这张图就是链式召回可以逐跳行走的"地图"。`);
  return sys;
}

// ---------------------------------------------------------------------------
// 第二、三部分：Inline / Spill，以及"截断 ≠ Spill"
// ---------------------------------------------------------------------------

function partInlineSpill(sys, trace) {
  const { store } = sys;
  const commentId = [...store.objects.values()].find((o) => o.kind === Kind.COMMENT)?.id ?? 'P01-C1';
  const obj = store.get(commentId);

  trace.section('第二部分：Inline（窗口内视图）与 Spill（完整内容留窗口外）');
  trace.note(`对象 ${commentId} 的完整正文有 ${String(obj.body).length} 字，超过 Inline 上限 ${store.inlineCharLimit} 字。`);
  trace.note('注入上下文时看到的是 brief()——摘要 + 被 Spill 的前缀 + 读回入口 + 关联 ID：');
  trace.block(store.brief(commentId));

  trace.section('第三部分：关键区分 —— "截断"不等于 Spill');
  const naive = String(obj.body).slice(0, store.inlineCharLimit) + '…';
  trace.note('做法甲（错误）：只把长内容截断塞进窗口，模型永远拿不到后半段：');
  trace.block(`正文: ${naive}   <-- 到此为止，没有入口，被截掉的内容无法补回`);
  trace.note('做法乙（正确 = Spill）：同样只放前缀，但保留 read("...") 入口，需要时能取回完整内容：');
  const full = store.readFull(commentId);
  trace.block(`read("${commentId}") -> 取回完整 ${full.text.length} 字；下一跳关联：${full.nextHop.map((n) => `${n.id}(${KIND_LABEL[n.kind] ?? '?'})`).join(' ')}`);
  trace.note('差别不在"放多少字"，而在"有没有留一条把完整内容读回来的路"。这是第 5 讲自测题的核心。');
  return { commentId, bodyLen: String(obj.body).length };
}

// ---------------------------------------------------------------------------
// 第四部分：链式召回 vs 两种对照方案
// ---------------------------------------------------------------------------

async function partChainRecall(sys, trace) {
  const { store } = sys;

  // 用户在原 Thread 追问评审进度：把它挂进 H01，使它与引用图相连
  const askId = 'T99';
  store.put({ id: askId, kind: Kind.TRIGGER, title: '追问进度', summary: '用户询问上次登录修复的评审进度', body: '上次那个登录超时的修复，评审到哪一步了？' });
  store.appendEvent('H01', askId);
  trace.section('第四部分：从追问事件出发，沿引用图链式召回');
  trace.note(`起点 ${askId}「上次那个登录超时的修复，评审到哪一步了？」，目标：找到相关的 PR / 评审对象。`);

  const recall = new RecallSession(store, { tokenBudget: 4000 });
  const res = await recall.walk(askId, {
    matches: (o) => o && (o.kind === Kind.PR || (o.kind === Kind.THREAD && o.source === Source.GH_PR)),
    maxHops: 5,
    onHop: (h) => trace.note(`  第 ${h.hop} 跳 ${h.id}(${KIND_LABEL[h.kind] ?? '?'}) ${h.action === 'hit' ? '命中，读回全文' : '展开，仅注入摘要+关联ID'} 累计 ${h.injectedTokens} tok`),
  });
  trace.note(`召回路径：${res.path}${res.aborted ? '（超出预算中止）' : ''}`);
  const hit = [...recall.hops].reverse().find((h) => h.action === 'hit');
  trace.note(`命中 ${hit?.id}：${store.get(hit?.id)?.summary ?? ''}`);
  trace.note('注意：沿途的历史消息只注入了摘要与被 Spill 的前缀，没有把它们的完整日志正文读进窗口。');

  // 对照方案 A：把整条 Thread 的全部历史（含完整正文）一次性拼进上下文
  const flatten = flattenWholeHistory(store, 'H01');
  // 对照方案 B：上下文只放一个 ID，任何信息都要靠工具逐跳读回
  const idOnly = idOnlyView(store, 'H01');
  const idOnlyReads = res.path.split(' -> ').length; // 走完整条链大约要这么多次读回

  const ratio = (flatten.injectedTokens / Math.max(1, res.injectedTokens)).toFixed(1);
  trace.section('第四部分结果：三种上下文组织方式对比');
  trace.block(
    renderTable(
      ['方案', '注入 Token', '工具读回次数', '特点'],
      [
        ['链式召回（本讲）', res.injectedTokens, res.readCalls, '逐跳只注入摘要，命中才读全文'],
        ['A 一次性拼全部历史', flatten.injectedTokens, flatten.readCalls, '把所有历史消息的完整正文都塞进窗口'],
        ['B 只放一个 ID', idOnly.injectedTokens, idOnlyReads, '注入最小，但每跳都要读回、步数膨胀'],
      ],
      ['l', 'r', 'r', 'l'],
    ),
  );
  trace.note(`链式召回注入 ${res.injectedTokens} tok，只读回命中的 ${res.readCalls} 个对象；`);
  trace.note(`一次性拼历史注入 ${flatten.injectedTokens} tok，约为链式召回的 ${ratio} 倍——因为它连每条历史消息的完整日志正文都注入了，其中多数与本次追问无关；`);
  trace.note(`只放 ID 注入 ${idOnly.injectedTokens} tok 最省，但要沿链读回约 ${idOnlyReads} 次，把成本换成了执行步数与延迟（第 5 讲自测题 1）。`);
  trace.note('说明：Token 用 estimateTokens 教学近似（中文按字、其余按 4 字符折算），仅用于比较相对量级。');
  return { chain: res.injectedTokens, flatten: flatten.injectedTokens, idOnly: idOnly.injectedTokens, reads: res.readCalls, ratio };
}

async function main() {
  const trace = new Trace(null, { title: '演示 05 · 上下文组织与链式召回' });

  const sys = await buildReferenceGraph(trace);
  const spill = partInlineSpill(sys, trace);
  const recall = await partChainRecall(sys, trace);

  trace.section('全演示小结');
  trace.block(
    renderTable(
      ['机制', '演示结果'],
      [
        ['唯一 ID + 引用图', `${sys.store.objects.size} 个对象互相以 ID 关联`],
        ['Spill 保留读回入口', `${spill.commentId} 正文 ${spill.bodyLen} 字，窗口内只放前缀 + read() 入口`],
        ['链式召回注入', `${recall.chain} tok，读回 ${recall.reads} 次`],
        ['对比：一次性拼历史', `${recall.flatten} tok（约 ${recall.ratio} 倍，含大量无关正文）`],
        ['对比：只放 ID', `${recall.idOnly} tok，但往返读回次数最多`],
      ],
    ),
  );
  trace.note(`口径核对：estimateTokens('登录超时 login timeout') = ${estimateTokens('登录超时 login timeout')}（中英混排示例）。`);

  takeaways(trace, [
    '为 Trigger / Thread / Worker Session 赋予唯一 ID，注入时带上紧密关联对象的 ID，就构成一张可按需行走的引用图。',
    'Inline 把近期很可能用的信息直接嵌入窗口，减少工具往返；Spill 把完整内容留在窗口外，只在窗口内保留摘要与读取入口。',
    '把长内容截断并不等于 Spill：截断丢掉了后半段且无法补回，Spill 的关键是保留"读回完整内容"的入口。',
    '链式召回从最少信息开始，逐跳只注入摘要，命中目标才读回全文；Thread 历史越长、单条正文越大，相对"一次性拼历史"省得越多。',
    '两个极端都不划算：一次性拼全部历史注入过大且混入无关内容；只放一个 ID 注入最省，却把成本转嫁成反复读回的执行步数。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
