/**
 * 演示 08 —— 计算、状态与执行环境分离
 *
 * 对应讲义第 8 讲（原文第 5.1、5.2、5.3 节 + BYOM）。
 *
 * 三个分离：
 *   5.1 计算与状态分离：Agent Loop 放进无状态弹性容器，Session 状态存"数据库"（这里用 SessionStore + 落盘文件替身）
 *   5.2 计算与执行环境分离：重状态环境留在原地，端侧 daemon 执行命令并回传结果
 *   5.3 间歇在线设备：设备离线 -> 任务挂起但不丢进度；设备上线 -> 云端触发在原地续跑
 *
 * 本演示分三部分：
 *   1. 设备离线导致 Worker 挂起，进度停在具体某一步（不丢失）
 *   2. 状态序列化落盘 -> "销毁"计算实例 -> 设备上线 -> 用状态在全新实例中恢复并续跑到完成
 *   3. BYOM：托管环境一直在线可直接执行；个人开发机间歇在线，离线挂起、上线发布续跑事件
 */

import { Source, Trace, renderTable, takeaways } from '../src/core.js';
import { buildSystem } from '../src/system.js';
import { MockLLM } from '../src/llm.js';
import { Worker } from '../src/worker.js';
import { Daemon, SessionStore } from '../src/infra.js';

async function waitUntil(clock, pred, timeoutSec = 60) {
  const t0 = clock.now();
  while (clock.now() - t0 < timeoutSec) {
    if (pred()) return true;
    await clock.sleep(0.05);
  }
  return false;
}

const CHECKPOINT_FILE = 'tmp/demo08/checkpoint.json';

// ---------------------------------------------------------------------------
// 第一、二部分：设备离线挂起 -> 状态持久化 -> 新实例恢复续跑
// ---------------------------------------------------------------------------

async function partSuspendAndRestore(trace) {
  const sys = buildSystem({
    title: '第一/二部分：设备离线挂起 -> 状态落盘 -> 销毁实例 -> 上线后在新实例续跑',
    msPerSimSecond: 40,
    globalStateTokens: 6000,
    daemons: [],
  });
  const { clock, bus, router, store, stats, runner } = sys;

  // 个人开发机 laptop-01 一开始处于离线（休眠）状态
  const daemon = new Daemon({ machine: 'laptop-01', clock, trace: sys.trace, online: false, bus, threadId: 'H-DEV' });
  sys.daemons.push(daemon);
  const sessionStore = new SessionStore({ clock, trace: sys.trace, file: CHECKPOINT_FILE });

  router.start();
  await bus.publish({
    source: Source.IM_WORK,
    threadId: 'H01',
    text: '帮我处理笔记本上未提交的改动，本地构建报错，先跑 lint 排查再提个 PR',
    author: 'user-1',
    payload: { threadTitle: '工作群' },
  });

  const blocked = await waitUntil(clock, () => router.workers.size >= 1 && [...router.workers.values()][0].status === 'blocked-env', 40);
  const w = [...router.workers.values()][0];
  if (!blocked || !w) {
    router.stop();
    await router.done;
    throw new Error('未能观察到 Worker 因设备离线而挂起（任务可能未被分派）');
  }
  trace.section('第一部分 · 执行环境留在原地：设备离线则任务挂起，但不丢进度');
  trace.note(`任务「${w.task.slice(0, 16)}」需要在 laptop-01 上执行命令，但设备离线`);
  trace.note(`Worker 挂起于第 ${w.stepIndex} 步（状态 ${w.status}）：${w.blockedReason}`);
  trace.note('关键：挂起的是"这台设备上的这一步"，云端 Loop 与已完成进度都还在，不是整个任务失败重来。');

  // --- 5.1 计算与状态分离：把状态存到"数据库"（落盘），计算实例本身无状态 ---
  trace.section('第二部分 · 计算与状态分离：状态落盘，计算实例可被替换');
  const snap = sessionStore.save({ router, workers: [w], store, label: '设备离线检查点' });
  trace.note(`检查点已写入数据库/磁盘：Worker ${snap.workers.length} 个、对象 ${snap.objects.count} 个，文件 ${CHECKPOINT_FILE}`);
  const serialized = w.toJSON();
  const jsonText = JSON.stringify(serialized);
  trace.note(`Worker 状态被序列化为纯数据（${jsonText.length} 字节）：status=${serialized.status}, stepIndex=${serialized.stepIndex}, plan=${serialized.plan.length} 步, 订阅=${serialized.subscriptions.join('/')}`);
  trace.note('此刻可以认为原计算实例被弹性回收——状态不在实例里，而在数据库/这段 JSON 里。');

  // 从磁盘读回（证明状态确实脱离了计算实例）
  const reloaded = sessionStore.load();
  trace.note(`从磁盘读回检查点：label=${reloaded?.label}，savedAt=${reloaded?.savedAt?.toFixed(1)}s，Worker[0].status=${reloaded?.workers?.[0]?.status}`);

  // --- 5.3 设备重新上线：云端触发在原地续跑 ---
  const pendingBefore = daemon.pending.length;
  const onlineEv = daemon.goOnline();
  trace.note(`laptop-01 重新上线（此前挂起命令 ${pendingBefore} 条）；发布上线事件 ${onlineEv?.id ?? '-'}，云端据此触发续跑`);

  // 在一个全新的无状态计算实例中，用持久化状态恢复 Worker
  const deps = {
    clock,
    trace: sys.trace,
    store,
    bus,
    llm: new MockLLM({ clock, actor: `${w.id}*`, model: 'strong-worker', stats }),
    router,
    daemon,
    runner,
  };
  const restored = Worker.fromJSON(JSON.parse(jsonText), deps);
  trace.note(`新的计算实例恢复出 ${restored.id}（同一个逻辑会话）：status=${restored.status}, stepIndex=${restored.stepIndex} —— 计算是全新的，状态是接着上次的`);
  restored.resume('设备重新上线，云端从检查点触发续跑');
  await restored.done;
  trace.note(`续跑完成：最终状态 ${restored.status}，走完 ${restored.stepIndex}/${restored.plan.length} 步，产出 PR=${restored.prId ?? '-'}`);

  router.stop();
  await router.done;

  trace.section('第一/二部分结果');
  trace.block(
    renderTable(
      ['阶段', '计算实例', '状态存放位置', '进度'],
      [
        ['设备离线', '原实例（挂起）', '内存中的 Worker', `停在第 ${w.stepIndex} 步`],
        ['检查点落盘', '原实例将被回收', `数据库/磁盘 ${CHECKPOINT_FILE}`, 'stepIndex 已持久化'],
        ['设备上线续跑', '全新实例（fromJSON）', '从磁盘读回', `续跑到第 ${restored.stepIndex} 步完成`],
      ],
    ),
  );
  return { sys, blockedStep: w.stepIndex, restoredStatus: restored.status, prId: restored.prId, file: CHECKPOINT_FILE };
}

// ---------------------------------------------------------------------------
// 第三部分：BYOM —— 多种执行环境、间歇在线
// ---------------------------------------------------------------------------

async function partByom(sys, trace) {
  const { clock, bus } = sys;
  trace.section('第三部分 · BYOM：托管环境常驻在线，个人设备间歇在线');

  const cloud = new Daemon({ machine: 'cloud-runner', clock, trace: sys.trace, online: true, bus, threadId: 'H-CLOUD' });
  sys.daemons.push(cloud);
  const out = await cloud.exec({ cmd: 'npm run build', simSeconds: 0.4 });
  trace.note(`托管环境 cloud-runner（按需拉起、常驻在线）执行 build -> ${out}；daemon 不保存会话状态，只执行命令回传结果`);

  const laptop = new Daemon({ machine: 'laptop-02', clock, trace: sys.trace, online: true, bus, threadId: 'H-LAP2' });
  sys.daemons.push(laptop);
  await laptop.exec({ cmd: 'git status', simSeconds: 0.3 });
  laptop.goOffline('合上盖子休眠');
  try {
    await laptop.exec({ cmd: 'npm test' });
  } catch (e) {
    trace.note(`laptop-02 离线后执行命令 -> 抛出「${e.message}」，命令进入挂起队列（${laptop.pending.length} 条），等设备上线`);
  }
  const ev = laptop.goOnline();
  trace.note(`laptop-02 重新上线 -> 发布事件 ${ev?.id}，携带挂起命令 [${ev?.payload?.pending?.join(', ') ?? '-'}]，云端可据此自动续跑`);
  trace.note('BYOM：个人开发机、托管机器、按需环境都能接入；重状态环境留在原地，云端只发命令、收结果。');
  return { cloudOk: !!out, laptopQueued: ev?.payload?.pending?.length ?? 0 };
}

async function main() {
  const trace = new Trace(null, { title: '演示 08 · 计算、状态与执行环境分离' });

  const r = await partSuspendAndRestore(trace);
  const byom = await partByom(r.sys, trace);

  trace.section('全演示小结');
  trace.block(
    renderTable(
      ['机制', '演示结果'],
      [
        ['计算与执行环境分离', '设备离线时 Worker 挂起于第 ' + r.blockedStep + ' 步，进度不丢'],
        ['计算与状态分离', '状态序列化落盘 ' + r.file + '，实例可回收'],
        ['新实例恢复续跑', 'fromJSON + resume -> 状态 ' + r.restoredStatus + '，产出 ' + (r.prId ?? '-')],
        ['BYOM 多环境', 'cloud-runner 常驻在线执行成功；laptop-02 离线挂起 ' + byom.laptopQueued + ' 条、上线后续跑'],
      ],
    ),
  );

  takeaways(trace, [
    '把 Agent Loop 放进无状态弹性容器、把 Session 状态存进数据库，计算实例就能被随时回收或替换而不丢进度。',
    '把计算与执行环境分离：重状态的环境（开发机、依赖、数据）留在原地，端侧 daemon 只负责执行命令并回传结果。',
    '设备离线不是任务失败，而是"挂起在这台设备的这一步"；进度停在具体 stepIndex，云端 Loop 与状态都还在。',
    '设备重新上线后，云端从检查点恢复出同一个逻辑会话（全新计算实例 + 持久化状态），从挂起的那一步续跑。',
    'BYOM 让个人开发机、托管机器、按需环境都能作为执行环境接入，间歇在线设备靠"挂起队列 + 上线事件"自动补齐。',
  ]);
}

main().catch((err) => {
  console.error(err);
  process.exit(1); // 强制退出：避免出错时 Router 轮询定时器让 Node 永不退出
});
