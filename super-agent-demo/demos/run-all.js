/**
 * run-all.js —— 依次串跑全部演示（对应讲义第 2~10 讲），最后给出通过/失败汇总。
 *
 * 用法：
 *   node demos/run-all.js          # 跑全部
 *   npm run all                    # 同上
 *   node demos/run-all.js 04 07    # 只跑指定编号
 *
 * 说明：每个演示作为独立子进程运行（互不干扰），输出直接透传到当前终端。
 *       为防个别演示意外死挂拖住整体，给每个子进程设了超时上限。
 */

import { spawnSync } from 'node:child_process';
import { writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

// [编号, 文件名, 对应讲义标题]
const DEMOS = [
  ['02', '02-dual-layer.js', '双层架构：Router 协调 + Worker 执行'],
  ['03', '03-event-filter.js', '事件驱动与事件过滤（Fast Path / Slow Path）'],
  ['04', '04-subscription-direct.js', '事件路由与订阅直通'],
  ['05', '05-chain-of-recall.js', '上下文组织与链式召回'],
  ['06', '06-speculative.js', '提前行动与推测执行（三道闸门）'],
  ['07', '07-async-full-duplex.js', '面向人的交互：异步全双工 + 多信道表达'],
  ['08', '08-state-separation.js', '计算、状态与执行环境分离'],
  ['09', '09-watchdog.js', 'Watchdog 与异常恢复'],
  ['10', '10-batching-ablation.js', '初步实验与证据解读：批处理压测 + 消融'],
];

const PER_DEMO_TIMEOUT_MS = 120000; // 单个演示最长 2 分钟

const only = process.argv.slice(2).filter((a) => /^\d{2}$/.test(a));
const selected = only.length ? DEMOS.filter(([n]) => only.includes(n)) : DEMOS;

if (!selected.length) {
  console.error(`没有匹配的演示编号：${only.join(', ')}（可用：${DEMOS.map((d) => d[0]).join(' ')}）`);
  process.exit(2);
}

const bar = '='.repeat(76);
const results = [];

console.log(bar);
console.log('Super-agent 教学演示 · 串跑');
console.log(`计划运行 ${selected.length} 个演示：${selected.map((d) => d[0]).join(' ')}`);
console.log(bar);

for (const [num, file, desc] of selected) {
  console.log(`\n${bar}\n>> 演示 ${num} · ${desc}\n   demos/${file}\n${bar}`);
  const t0 = Date.now();
  const r = spawnSync(process.execPath, [join(here, file)], { stdio: 'inherit', timeout: PER_DEMO_TIMEOUT_MS });
  const ms = Date.now() - t0;

  let ok = r.status === 0;
  let note = '';
  if (r.error) {
    ok = false;
    note = `启动失败：${r.error.message}`;
  } else if (r.signal) {
    ok = false;
    note = `被信号终止（${r.signal}），可能超时 ${PER_DEMO_TIMEOUT_MS / 1000}s`;
  } else if (!ok) {
    note = `退出码 ${r.status}`;
  }
  if (!ok) console.error(`\n[失败] 演示 ${num}：${note}`);
  results.push({ num, desc, ok, ms, note });
}

console.log(`\n${bar}\n运行汇总\n${bar}`);
const summaryLines = [];
for (const r of results) {
  const flag = r.ok ? '[通过]' : '[失败]';
  const line = `${flag} 演示 ${r.num}  ${(r.ms / 1000).toFixed(1).padStart(5)}s  ${r.desc}${r.ok ? '' : `  <- ${r.note}`}`;
  console.log(line);
  summaryLines.push(line);
}
const failed = results.filter((r) => !r.ok);
const tail = `\n共 ${results.length} 个演示：通过 ${results.length - failed.length}，失败 ${failed.length}。`;
console.log(tail);
console.log('提示：单独重跑某个演示可用  npm run d09  或  node demos/09-watchdog.js');

// 由 node 直接写 UTF-8 汇总文件（绕开 shell 重定向在中文 Windows 上的编码损坏），便于留档与核对
try {
  const tmpDir = join(here, '..', 'tmp');
  mkdirSync(tmpDir, { recursive: true });
  writeFileSync(join(tmpDir, 'run-all-summary.txt'), `${summaryLines.join('\n')}\n${tail}\n`, 'utf8');
} catch {
  /* 汇总文件只是附加产物，写失败不影响运行结果 */
}

if (failed.length) {
  console.error(`失败编号：${failed.map((f) => f.num).join(', ')}`);
  process.exit(1);
}
