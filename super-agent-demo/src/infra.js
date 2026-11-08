/**
 * infra.js —— 计算 / 状态 / 环境分离，以及外部 Watchdog
 *
 * 对应讲义：
 *   第 8 讲（原文 5.1）  计算与状态分离：Agent Loop 放进无状态弹性容器，Session 状态存数据库
 *   第 8 讲（原文 5.2）  计算与执行环境分离：重状态环境留在原地，端侧 daemon 执行命令并返回结果
 *   第 8 讲（BYOM）      可接入个人开发机、托管机器或按需环境；支持间歇在线设备
 *   第 9 讲（原文 6）    独立的 Watchdog Session，由定时消息驱动，构成外部 Control Loop
 */

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname } from 'node:path';
import { Source, makeTrigger } from './core.js';
import { judgeProgress } from './llm.js';

// ---------------------------------------------------------------------------
// 1. SessionStore：持久化会话状态（数据库的替身）
// ---------------------------------------------------------------------------

export class SessionStore {
  /**
   * @param {object} o
   * @param {string|null} o.file  传入路径则真正落盘，演示"状态在计算实例之外"
   */
  constructor({ clock, trace, file = null }) {
    this.clock = clock;
    this.trace = trace;
    this.file = file;
    this.snapshots = [];
    this.failures = 0; // 供 Watchdog 检查"状态恢复失败"
    this.unavailable = false;
  }

  /** 把 Router 与 Worker 的会话状态序列化保存。 */
  save({ router, workers, store, label = 'checkpoint' }) {
    if (this.unavailable) {
      this.failures += 1;
      throw new Error('数据库不可用，检查点写入失败');
    }
    const snapshot = {
      version: 1,
      savedAt: this.clock.now(),
      label,
      router: {
        replies: router.replies.length,
        rounds: router.rounds.length,
        decisions: router.rounds.reduce((s, r) => s + r.batchSize, 0),
        cacheReadTokens: router.rounds.reduce((s, r) => s + r.cacheReadTokens, 0),
        globalStateTokens: router.globalStateTokens,
      },
      workers: workers.map((w) => w.toJSON()),
      objects: {
        count: store.objects.size,
        threads: [...store.threads.values()].map((t) => ({
          id: t.id,
          source: t.source,
          title: t.title,
          events: t.eventIds.length,
          subscribers: [...(t.subscriberIds ?? [])],
        })),
      },
    };
    this.snapshots.push(snapshot);
    if (this.file) {
      mkdirSync(dirname(this.file), { recursive: true });
      writeFileSync(this.file, JSON.stringify(snapshot, null, 2), 'utf8');
    }
    this.trace.log('STORE', `检查点「${label}」已持久化：${workers.length} 个 Worker，${store.objects.size} 个对象${this.file ? `，落盘 ${this.file}` : ''}`);
    return snapshot;
  }

  load() {
    if (this.file && existsSync(this.file)) {
      return JSON.parse(readFileSync(this.file, 'utf8'));
    }
    return this.snapshots[this.snapshots.length - 1] ?? null;
  }
}

// ---------------------------------------------------------------------------
// 2. Daemon：留在原地的执行环境（BYOM）
// ---------------------------------------------------------------------------

export class EnvironmentUnavailable extends Error {}

export class Daemon {
  /**
   * @param {object} o
   * @param {string} o.machine   设备标识，例如 'laptop-01'（个人开发机）/ 'cloud-runner'
   * @param {boolean} [o.online] 间歇在线设备可初始为离线
   */
  constructor({ id, machine, clock, trace, online = true, bus = null, threadId = 'H-DEV' }) {
    this.id = id ?? `D-${machine}`;
    this.machine = machine;
    this.clock = clock;
    this.trace = trace;
    this.online = online;
    this.bus = bus;
    this.threadId = threadId;
    this.execLog = [];
    this.pending = []; // 离线期间挂起、等设备上线后自动触发的命令
    this.errors = 0;
  }

  /** daemon 不保存会话状态，只执行命令并返回结果（第 8 讲 5.1）。 */
  async exec({ cmd, simSeconds = 0.6, allowQueue = true }) {
    if (!this.online) {
      this.errors += 1;
      if (allowQueue) this.pending.push(cmd);
      throw new EnvironmentUnavailable(`设备 ${this.machine} 离线，无法执行「${cmd}」`);
    }
    await this.clock.sleep(simSeconds);
    const out = `ok(${cmd.split(' ')[0]})`;
    this.execLog.push({ cmd, at: this.clock.now(), out });
    this.trace.log('DAEMON', `[${this.machine}] 执行「${cmd}」-> ${out}`);
    return out;
  }

  goOffline(reason = '设备休眠') {
    this.online = false;
    this.trace.log('DAEMON', `[${this.machine}] 离线（${reason}）；云端 Loop 与状态不受影响，但该环境上的命令无法执行`);
  }

  /** 第 8 讲 5.3：设备重新上线后，由云端 Agent 自动触发需要在该设备上完成的任务。 */
  goOnline() {
    this.online = true;
    this.trace.log('DAEMON', `[${this.machine}] 重新上线，挂起命令 ${this.pending.length} 条`);
    if (this.bus && this.pending.length) {
      const ev = makeTrigger({
        clock: this.clock,
        source: Source.INTERNAL,
        threadId: this.threadId,
        text: `设备 ${this.machine} 已上线，可继续执行 ${this.pending.length} 条挂起命令`,
        author: 'system',
        payload: { kind: 'device-online', machine: this.machine, pending: [...this.pending] },
      });
      this.pending = [];
      this.bus.publish(ev);
      return ev;
    }
    return null;
  }
}

// ---------------------------------------------------------------------------
// 3. Watchdog：主系统之外的独立 Control Loop
// ---------------------------------------------------------------------------

export class Watchdog {
  /**
   * @param {object} o
   * @param {number} [o.intervalSimSec] 定时消息周期（原文未给出固定检查周期，这里是教学设定）
   * @param {string[]} [o.watchWorkers] 只检查这些 Worker，默认全部
   */
  constructor({ clock, trace, llm, router, bus, store, daemons = [], intervalSimSec = 6, maxRounds = 40 }) {
    this.clock = clock;
    this.trace = trace;
    this.llm = llm; // 独立的 Watchdog Session，自带工具能力
    this.router = router;
    this.bus = bus;
    this.store = store;
    this.daemons = daemons;
    this.intervalSimSec = intervalSimSec;
    this.maxRounds = maxRounds;
    this.running = false;
    this.checks = [];
    this.interventions = [];
    this.openIssues = new Map(); // workerId -> 上一次结论，用于"持续复查直到恢复正常"
    this.done = null;
  }

  /** 启动定时驱动的外部 Control Loop（不阻塞）。需要等待退出时用 await watchdog.done。 */
  start() {
    if (this.done) return this;
    this.running = true;
    this.trace.log('WATCHDOG', `独立 Session 启动，每 ${this.intervalSimSec}s 由定时消息驱动（外部 Control Loop）`);
    this.done = (async () => {
      let round = 0;
      while (this.running && round < this.maxRounds) {
        await this.clock.sleep(this.intervalSimSec);
        round += 1;
        await this.#tick(round);
        if (this.router.workers.size > 0 && [...this.router.workers.values()].every((w) => w.status === 'closed') && this.openIssues.size === 0) {
          this.running = false;
        }
      }
    })();
    return this;
  }

  stop() {
    this.running = false;
  }

  async #tick(round) {
    const workers = [...this.router.workers.values()];
    // 基础设施异常 = 离线设备数 + 状态存储写入失败数（store 应为 SessionStore；防御性兜底避免 NaN）
    const infraErrors = this.daemons.filter((d) => !d.online).length + (this.store?.failures ?? 0);

    const evidenceList = workers.map((w) => ({ id: w.id, ev: w.progressEvidence() }));
    const promptTokens = 1800 + evidenceList.length * 260;

    const { result } = await this.llm.call({
      promptTokens,
      outputTokens: 40 + evidenceList.length * 30,
      produce: () =>
        evidenceList.map(({ id, ev }) => ({ id, ev, verdict: judgeProgress(ev), worker: workers.find((w) => w.id === id) })),
    });

    this.checks.push({ round, at: this.clock.now(), infraErrors, results: result.map((r) => ({ id: r.id, verdict: r.verdict.verdict, reason: r.verdict.reason })) });
    this.trace.log('WATCHDOG', `第 ${round} 轮检查：基础设施异常 ${infraErrors}，Worker ${workers.length} 个`);

    for (const r of result) {
      const prev = this.openIssues.get(r.id);
      if (r.verdict.verdict === 'healthy' || r.verdict.verdict === 'watch') {
        if (prev) {
          this.trace.log('WATCHDOG', `  ${r.id} 已恢复正常（此前：${prev.verdict}）—— 复查通过，解除关注`);
          this.openIssues.delete(r.id);
        } else {
          this.trace.log('WATCHDOG', `  ${r.id} ${r.verdict.verdict === 'watch' ? '观察中' : '正常'}：${r.verdict.reason}`);
        }
        continue;
      }
      this.openIssues.set(r.id, r.verdict);
      this.trace.log('WATCHDOG', `  ! ${r.id} 异常[${r.verdict.verdict}]：${r.verdict.reason}`);
      this.#intervene(r);
    }

    if (infraErrors > 0 && workers.length === 0) {
      this.trace.log('WATCHDOG', `  ! 基础设施异常 ${infraErrors}（设备离线/状态写入失败），持续复查直到恢复`);
    }
  }

  /**
   * 介入修复。第 9 讲：Watchdog 本身也是有完整工具能力的 Agent Session，
   * 可以诊断并推动恢复，而不只是发出告警。
   */
  #intervene({ id, verdict, worker }) {
    if (!worker) return;
    const action =
      verdict.verdict === 'infra'
        ? 'retry-later'
        : verdict.verdict === 'drift'
          ? 'inject-constraint'
          : 'inject-hint';

    const text =
      action === 'inject-constraint'
        ? '请立即回到任务范围，停止无关重构（Watchdog 介入）'
        : action === 'inject-hint'
          ? '检测到重复调用且无新证据，请更换假设或上报阻塞（Watchdog 介入）'
          : '基础设施暂不可用，先挂起等待环境恢复（Watchdog 介入）';

    const ev = makeTrigger({
      clock: this.clock,
      source: Source.INTERNAL,
      threadId: [...worker.subscriptions][0] ?? 'H-WD',
      text,
      author: 'watchdog',
      payload: { kind: 'watchdog', action },
    });
    this.interventions.push({ workerId: id, verdict: verdict.verdict, action, at: this.clock.now() });
    this.trace.log('WATCHDOG', `    介入 ${id}：${action} -> 注入事件 ${ev.id}`);
    // 注入到 Worker 收件箱：这是"在线修正主系统行为"的一种最小形式
    worker.receive(ev, 'watchdog');
    if (action === 'inject-constraint') worker.constraints.push(text);
  }

  /** 维护者用自然语言询问故障原因（第 9 讲第六节）。 */
  async ask(question) {
    const snapshot = {
      checks: this.checks.length,
      openIssues: [...this.openIssues.entries()].map(([k, v]) => `${k}:${v.verdict}`),
      interventions: this.interventions.map((i) => `${i.workerId}/${i.action}`),
      offlineDaemons: this.daemons.filter((d) => !d.online).map((d) => d.machine),
    };
    const { result } = await this.llm.call({
      promptTokens: 900 + JSON.stringify(snapshot).length,
      outputTokens: 80,
      produce: () => ({ question, snapshot, answer: this.#answer(question, snapshot) }),
    });
    this.trace.log('WATCHDOG', `维护者提问：${question}`);
    this.trace.log('WATCHDOG', `诊断回答：${result.answer}`);
    return result;
  }

  #answer(question, snapshot) {
    if (snapshot.offlineDaemons.length && /为什么|原因|故障/.test(question)) {
      return `当前 ${snapshot.offlineDaemons.join('、')} 离线，依赖该环境的命令挂起；云端 Loop 与状态仍可用，设备上线后会自动触发。`;
    }
    if (snapshot.openIssues.length) return `仍有 ${snapshot.openIssues.length} 个未恢复问题：${snapshot.openIssues.join('；')}。已介入 ${snapshot.interventions.length} 次。`;
    if (/进展|停滞/.test(question)) return `已执行 ${snapshot.checks} 轮检查，未发现停滞或目标偏离。`;
    return `已完成 ${snapshot.checks} 轮检查，介入 ${snapshot.interventions.length} 次，当前无未恢复问题。`;
  }

  summary() {
    const byVerdict = {};
    for (const c of this.checks) for (const r of c.results) byVerdict[r.verdict] = (byVerdict[r.verdict] ?? 0) + 1;
    return { rounds: this.checks.length, byVerdict, interventions: this.interventions.length, openIssues: this.openIssues.size };
  }
}
