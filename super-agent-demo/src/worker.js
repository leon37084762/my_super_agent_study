/**
 * worker.js —— Worker 层：隔离上下文执行、异步工具、推测执行
 *
 * 对应讲义：
 *   第 2 讲（原文 2.2）  Worker 上下文彼此隔离、挂载完整执行工具、向 Router 汇报状态，
 *                        不直接对外回复用户；同层 Worker 通过订阅 Thread 发现彼此
 *   第 4 讲（原文 3.2）  已订阅事件直通到达；超出任务范围可取消订阅或交回 Router
 *   第 6 讲（原文 3.4）  推测执行：先在后台尝试，有价值才交付，无价值则安静丢弃
 *   第 7 讲（原文 4.1）  异步 ReAct Loop：start() 立即回执、完成后作为新事件回投、可 stop()；
 *                        新输入在"当前推理步结束后"处理，不能随时打断一次正在进行的推理
 */

import { Source, makeTrigger, nextId } from './core.js';
import { estimateTokens } from './llm.js';
import { Kind } from './context.js';

// ---------------------------------------------------------------------------
// 1. 异步工具运行器：start() / stop()
// ---------------------------------------------------------------------------

export class AsyncToolRunner {
  constructor({ clock, trace }) {
    this.clock = clock;
    this.trace = trace;
    this.handles = new Map();
  }

  /**
   * 启动一个耗时命令，立即返回 Started 回执。
   * 注意（第 7 讲自测题 1）：Started 只说明启动成功，不代表任务完成或成功。
   */
  start({ name, cmd, simSeconds, outcome = 'succeeded', output = '', onDone = null }) {
    const handle = {
      id: nextId('J'),
      name,
      cmd,
      status: 'started', // started -> running -> succeeded | failed | stopped
      startedAt: this.clock.now(),
      simSeconds,
      outcome,
      output,
      finishedAt: null,
    };
    this.handles.set(handle.id, handle);
    handle.status = 'running';

    // 后台执行：不占用调用方的推理循环
    (async () => {
      await this.clock.sleep(simSeconds);
      if (handle.status !== 'running') return; // 已被 stop()
      handle.status = handle.outcome;
      handle.finishedAt = this.clock.now();
      if (onDone) onDone(handle);
    })();

    return handle;
  }

  /** 停止一个仍在运行的后台命令。是否真的停下、产物是否保留，取决于实现。 */
  stop(id) {
    const h = this.handles.get(id);
    if (!h) return { ok: false, reason: '句柄不存在' };
    if (h.status !== 'running') return { ok: false, reason: `已结束（${h.status}），无法停止` };
    h.status = 'stopped';
    h.finishedAt = this.clock.now();
    return { ok: true, reason: `已停止 ${h.cmd}` };
  }

  status(id) {
    const h = this.handles.get(id);
    return h ? { status: h.status, elapsed: this.clock.now() - h.startedAt } : null;
  }
}

// ---------------------------------------------------------------------------
// 2. 推测执行的三道闸门（第 6 讲第五节）
// ---------------------------------------------------------------------------

/**
 * 讲义强调这是三个不同的问题，不能混为一谈：
 *   是否后台尝试 / 是否继续投入 / 是否交付（打断用户）
 */
export class SpeculationGate {
  /** 线索是否值得先在后台试一把？关注副作用范围与预算。 */
  static shouldAttempt({ clueStrength = 0, sideEffect = 'read-only', budgetLeft = 1 }) {
    if (budgetLeft <= 0) return { go: false, reason: '推测预算已用尽' };
    if (clueStrength <= 0) return { go: false, reason: '没有可用线索' };
    if (sideEffect === 'destructive') return { go: false, reason: '涉及不可撤销的外部影响，须先取得授权' };
    return { go: true, reason: `线索强度 ${clueStrength}，副作用范围 ${sideEffect}，可在后台尝试` };
  }

  /** 是否继续投入？看是否有新证据。 */
  static shouldContinue({ newEvidence = 0, attempts = 0, maxAttempts = 3 }) {
    if (newEvidence > 0) return { go: true, reason: '出现新证据，值得继续' };
    if (attempts >= maxAttempts) return { go: false, reason: `已尝试 ${attempts} 次且无新证据，停止投入` };
    return { go: true, reason: '尚未达到尝试上限' };
  }

  /**
   * 是否交付给用户？只有经过验证的结果才呈现，否则保持安静。
   * 第 6 讲：不能把"后台进程退出"写成"所有功能正确"。
   */
  static shouldDeliver({ reproduced = false, verified = false, confidence = 0, userAsked = false }) {
    if (userAsked) return { go: true, reason: '用户主动询问，即使结果不确定也应说明当前状态' };
    if (reproduced && verified && confidence >= 0.7) return { go: true, reason: '已复现且验证通过，可交付' };
    if (!reproduced) return { go: false, reason: '未能复现，不把猜测写成结论 —— 保持安静' };
    return { go: false, reason: '结果未经验证 —— 保持安静，避免制造注意力负担' };
  }
}

// ---------------------------------------------------------------------------
// 3. Worker
// ---------------------------------------------------------------------------

export class Worker {
  /**
   * @param {object} o
   * @param {string} o.task            任务描述
   * @param {Array}  o.plan            步骤计划（教学替身：真实 Worker 由模型自行规划）
   * @param {string[]} [o.scope]       允许改动的范围，用于第 9 讲的目标偏离检查
   * @param {boolean} [o.speculative]  推测执行模式：安静尝试，交付前过闸门
   * @param {boolean} [o.asyncTools]   true = start()/stop()；false = 阻塞式 run()
   * @param {import('./infra.js').Daemon|null} [o.daemon] 端侧执行环境
   */
  constructor({
    id = nextId('W'),
    task,
    title = null,
    clock,
    trace,
    store,
    bus,
    llm,
    router,
    daemon = null,
    runner = null,
    plan = [],
    scope = [],
    speculative = false,
    asyncTools = true,
    originThread = null,
  }) {
    this.id = id;
    this.task = task;
    /** 面向用户表达时用的短标题（避免把整句任务描述当成标题截断） */
    this.title = title ?? task.slice(0, 12);
    this.clock = clock;
    this.trace = trace;
    this.store = store;
    this.bus = bus;
    this.llm = llm;
    this.router = router;
    this.daemon = daemon;
    this.runner = runner ?? new AsyncToolRunner({ clock, trace });
    this.plan = plan;
    this.scope = scope;
    this.speculative = speculative;
    this.asyncTools = asyncTools;

    this.status = 'idle'; // idle | running | waiting-tool | closed | blocked-env
    this.stepIndex = 0;
    this.inbox = [];
    this.subscriptions = new Set(originThread ? [originThread] : []);
    this.constraints = []; // 用户中途追加的约束
    this.toolLog = []; // 供 Watchdog 判断进展
    this.evidence = new Set(); // 已获得的新证据指纹
    this.scopeViolations = 0;
    this.infraErrors = 0;
    this.blockedInputs = 0; // 同步工具期间被阻塞的用户输入数
    this.blockedSeconds = 0;
    this.observedCount = 0;
    this.lastReport = '';
    this.result = null;
    this.contextTokens = 400; // 隔离上下文：只含本任务信息
    this.done = null;
  }

  // -- 事件入口 -----------------------------------------------------------

  /** 收到事件（直通 / Router 分派 / 内部工具完成）。 */
  receive(trigger, how = 'direct') {
    // 分派事件是任务的起点，不算"推理步中途插入"
    const midStep = how === 'dispatch' ? false : this.status === 'running';
    this.inbox.push({ trigger, how, at: this.clock.now(), midStep });
    if (how === 'direct' && this.speculative) {
      this.trace.log(this.id, `直通收到 ${trigger.id}（推测模式：先不打扰用户）`);
    }
  }

  /** Router 在直通时观察到事件后同步给 Worker 的全局提示。 */
  noteObserved(trigger) {
    this.observedCount += 1;
  }

  // -- 主循环 -------------------------------------------------------------

  /** 启动 Worker 自己的 ReAct 循环（不阻塞）。需要等待结束时用 await worker.done。 */
  start() {
    if (this.done) return this;
    this.status = 'running';
    this.trace.log(this.id, `启动，任务「${this.task.slice(0, 28)}」，订阅 ${[...this.subscriptions].join(' ') || '无'}，上下文隔离（${this.contextTokens} tok 起）`);
    this.done = this.#loop();
    return this;
  }

  async #loop() {
    while (this.status === 'running') {
      // 全双工边界：新输入在"当前推理步结束后"处理，而不是随时打断推理。
      // 必须先排空收件箱再取步骤：约束可能删除或追加步骤。
      this.#drainInbox();
      if (this.stepIndex >= this.plan.length) {
        await this.#conclude();
        break;
      }
      const step = this.plan[this.stepIndex];
      try {
        await this.#reasonAndAct(step);
        this.stepIndex += 1;
      } catch (err) {
        // 执行环境不可用时任务挂起，而不是丢失进度（第 8 讲）
        this.status = 'blocked-env';
        this.blockedReason = err.message;
        this.trace.log(this.id, `挂起于第 ${this.stepIndex} 步：${err.message}`);
        return;
      }
    }
  }

  /** 设备重新上线后由云端触发续跑：状态仍在，计算实例可以是全新的。 */
  resume(reason = '设备重新上线') {
    if (this.status !== 'blocked-env') return this;
    this.status = 'running';
    this.trace.log(this.id, `从第 ${this.stepIndex} 步续跑（${reason}）`);
    this.done = this.#loop();
    return this;
  }

  /** 排空收件箱：这是订阅直通的信息进入隔离上下文的唯一入口。 */
  #drainInbox() {
    while (this.inbox.length) {
      const { trigger, how, midStep } = this.inbox.shift();
      if (midStep) {
        this.trace.log(this.id, `输入 ${trigger.id} 到达时正在推理步中 -> 等本步结束后处理（不能随时打断推理）`);
      }
      this.contextTokens += estimateTokens(trigger.text) + 30;

      if (how === 'steer' || /先不要|暂缓|别改|不要动/.test(trigger.text)) {
        this.constraints.push(trigger.text);
        this.evidence.add(`constraint:${trigger.id}`);
        this.trace.log(this.id, `接收约束：${trigger.text}`);
        this.#applyConstraints(trigger);
        continue;
      }
      if (trigger.source === Source.GH_CI) {
        // 第 4 讲：CI 失败事件直通到达，Worker 自行继续修改，无须 Router 逐条转发
        const failed = trigger.payload?.status === 'failure';
        this.evidence.add(`ci:${trigger.id}:${trigger.payload?.status}`);
        this.trace.log(this.id, `CI 事件 ${trigger.id} 状态=${trigger.payload?.status ?? '?'}`);
        if (failed && !this.speculative) this.#appendFixCycle(trigger);
        continue;
      }
      if (trigger.source === Source.INTERNAL && trigger.payload?.kind === 'tool-done') {
        this.evidence.add(`tool:${trigger.id}`);
        continue;
      }
      // 超出任务范围 -> 交回 Router 重新分派（第 4 讲）
      if (how !== 'dispatch' && !this.#inScope(trigger)) {
        this.trace.log(this.id, `${trigger.id} 超出本任务范围 -> 交回 Router 重新分派`);
        this.bus?.unsubscribe(this.id, trigger.threadId);
        this.subscriptions.delete(trigger.threadId);
        this.router?.enqueue(trigger);
        continue;
      }
      this.evidence.add(`event:${trigger.id}`);
      this.trace.log(this.id, `纳入上下文 ${trigger.id}「${trigger.text.slice(0, 20)}」`);
    }
  }

  #inScope(trigger) {
    const keywords = ['登录', '超时', '导出', '渲染', '波浪号', '性能', '卡顿'];
    const mine = keywords.filter((k) => this.task.includes(k));
    return mine.length === 0 || mine.some((k) => trigger.text.includes(k));
  }

  /** 用户中途叫停某类改动：从计划里剔除相关步骤。 */
  #applyConstraints(trigger) {
    const banned = /接口/.test(trigger.text) ? 'api' : /测试/.test(trigger.text) ? 'test' : null;
    if (!banned) return;
    const before = this.plan.length;
    // 已经执行完的步骤被移除时，要把游标往前拉，否则会跳过下一步
    const removedBefore = this.plan.slice(0, this.stepIndex).filter((s) => s.tag === banned).length;
    this.plan = this.plan.filter((s) => s.tag !== banned);
    this.stepIndex = Math.max(0, this.stepIndex - removedBefore);
    if (this.plan.length !== before) {
      this.trace.log(this.id, `按约束调整计划：移除 ${before - this.plan.length} 个 ${banned} 相关步骤，下一步为 ${this.plan[this.stepIndex]?.tool ?? '收尾'}`);
    }
  }

  /** CI 失败后追加一轮"修改-重测"（体现直通带来的自我修正闭环）。 */
  #appendFixCycle(trigger) {
    const target = this.scope[0] ?? 'src/unknown.js';
    this.plan.splice(this.stepIndex + 1, 0, {
      tool: 'edit',
      target,
      sim: 1.2,
      note: `根据 ${trigger.id} 的失败信息修正`,
      tag: 'fix',
      evidence: `failure:${trigger.payload?.detail ?? 'ci'}`,
    });
    this.trace.log(this.id, `追加一轮修复-重测（由 ${trigger.id} 直通触发）`);
  }

  // -- 单步推理与工具执行 -------------------------------------------------

  async #reasonAndAct(step) {
    const promptTokens = this.contextTokens + estimateTokens(JSON.stringify(step));
    const outputTokens = 60;

    const { latency } = await this.llm.call({
      promptTokens,
      outputTokens,
      produce: () => step,
    });
    this.contextTokens = Math.round(promptTokens * 0.82); // 上下文随任务推进增长
    this.trace.log(this.id, `推理 ${latency.toFixed(2)}s -> ${step.tool}(${step.target ?? step.cmd ?? ''})${step.note ? ` // ${step.note}` : ''}`);

    this.#recordTool(step);
    switch (step.tool) {
      case 'read':
        await this.#toolRead(step);
        break;
      case 'edit':
        await this.#toolEdit(step);
        break;
      case 'run_tests':
        await this.#toolRunTests(step);
        break;
      case 'daemon_exec':
        await this.#toolDaemon(step);
        break;
      case 'create_pr':
        await this.#toolCreatePr(step);
        break;
      case 'reproduce':
        await this.#toolReproduce(step);
        break;
      default:
        await this.clock.sleep(step.sim ?? 0.5);
    }
  }

  /** Watchdog 用的执行记录：相同调用重复且无新证据 = 停滞信号（第 9 讲）。 */
  #recordTool(step) {
    const fingerprint = `${step.tool}:${step.target ?? step.cmd ?? ''}`;
    const last = this.toolLog[this.toolLog.length - 1];
    const evKey = step.evidence ?? null;
    // 关键区分："活动量"不等于"信息增量"。只有第一次出现的证据才算新证据。
    const isNewEvidence = evKey !== null && !this.evidence.has(evKey);
    this.toolLog.push({
      at: this.clock.now(),
      fingerprint,
      evidence: evKey,
      repeated: last?.fingerprint === fingerprint && last?.evidence === evKey,
      isNewEvidence,
    });
    if (evKey) this.evidence.add(evKey);
  }

  /** 供 Watchdog 读取的进展证据。 */
  progressEvidence() {
    const recent = this.toolLog.slice(-6);
    let repeatedSameCall = 0;
    for (let i = recent.length - 1; i > 0; i -= 1) {
      if (recent[i].repeated) repeatedSameCall += 1;
      else break;
    }
    const newEvidenceCount = recent.filter((r) => r.isNewEvidence).length;
    return {
      repeatedSameCall,
      newEvidenceCount,
      scopeViolations: this.scopeViolations,
      infraErrors: this.infraErrors,
      stage: this.status === 'waiting-tool' ? 'waiting-tool' : this.status === 'closed' ? 'closed' : 'iterating',
      toolCalls: this.toolLog.length,
    };
  }

  async #toolRead(step) {
    await this.clock.sleep(step.sim ?? 0.6);
    const obj = this.store.get(step.target);
    const text = obj ? this.store.readFull(step.target).text : `读取 ${step.target}：${step.evidence ?? ''}`;
    this.contextTokens += estimateTokens(String(text).slice(0, 400));
    if (step.spilled) {
      this.trace.log(this.id, `  内容超预算 -> Spill 到窗口外，保留入口 read("${step.target}")`);
    }
  }

  async #toolEdit(step) {
    await this.clock.sleep(step.sim ?? 0.8);
    if (this.scope.length && !this.scope.some((s) => String(step.target).includes(s) || s.includes(String(step.target)))) {
      this.scopeViolations += 1;
      this.trace.log(this.id, `  ! 改动 ${step.target} 越出任务范围 ${this.scope.join(',')}（目标偏离信号）`);
    } else {
      this.trace.log(this.id, `  改动 ${step.target}`);
    }
  }

  /**
   * 第 7 讲的核心对比：
   *   asyncTools=false -> 经典 ReAct：调用 run() 后循环阻塞，期间无法响应新输入
   *   asyncTools=true  -> start() 立即回执，后台完成后作为新事件回投会话，期间可处理新输入
   */
  async #toolRunTests(step) {
    if (!this.asyncTools) {
      const t0 = this.clock.now();
      this.trace.log(this.id, `  同步 run("${step.cmd}") —— 本会话在工具返回前无法响应新输入`);
      this.status = 'waiting-tool';
      await this.clock.sleep(step.sim ?? 4);
      const waited = this.clock.now() - t0;
      this.blockedSeconds += waited;
      this.blockedInputs += this.inbox.length;
      this.status = 'running';
      this.trace.log(this.id, `  run() 返回（阻塞 ${waited.toFixed(1)}s，期间积压 ${this.inbox.length} 条输入）`);
      return;
    }

    const handle = this.runner.start({
      name: step.tool,
      cmd: step.cmd,
      simSeconds: step.sim ?? 4,
      outcome: step.outcome ?? 'succeeded',
      output: step.output ?? '所有测试通过',
      onDone: (h) => {
        // 完成后作为新事件推送进会话
        const ev = makeTrigger({
          clock: this.clock,
          source: Source.INTERNAL,
          threadId: [...this.subscriptions][0] ?? 'H00',
          text: `后台命令完成：${h.cmd} -> ${h.status}（${h.output}）`,
          author: 'system',
          payload: { kind: 'tool-done', handleId: h.id, status: h.status, output: h.output },
        });
        this.receive(ev, 'internal');
        this.trace.log(this.id, `  后台事件回投 ${ev.id}：${h.cmd} -> ${h.status}`);
      },
    });
    this.trace.log(this.id, `  异步 start("${step.cmd}") -> 回执 Started(${handle.id})，推理循环不再等待`);

    // 等待完成事件，但期间持续处理新输入 = 全双工体验
    this.status = 'waiting-tool';
    const t0 = this.clock.now();
    const timeout = (step.sim ?? 4) + 20;
    while (this.clock.now() - t0 < timeout) {
      if (this.inbox.length) {
        this.status = 'running';
        this.#drainInbox();
        this.status = 'waiting-tool';
        if (step.cancelOn && this.constraints.some((c) => step.cancelOn.test(c))) {
          const r = this.runner.stop(handle.id);
          this.trace.log(this.id, `  依据新约束调用 stop(${handle.id}) -> ${r.reason}`);
          break;
        }
      }
      if (handle.status !== 'running' && handle.status !== 'started') break;
      await this.clock.sleep(0.1);
    }
    this.status = 'running';
    this.trace.log(this.id, `  最终状态 ${handle.status}（Started 不等于成功，需以完成事件为准）`);
  }

  async #toolDaemon(step) {
    if (!this.daemon) {
      await this.clock.sleep(step.sim ?? 0.5);
      return;
    }
    try {
      const out = await this.daemon.exec({ machine: step.machine, cmd: step.cmd, simSeconds: step.sim ?? 0.6 });
      this.trace.log(this.id, `  daemon[${step.machine}] ${step.cmd} -> ${out}`);
      this.evidence.add(`daemon:${step.cmd}:${out}`);
    } catch (err) {
      this.infraErrors += 1;
      this.pendingDeviceStep = step;
      throw err;
    }
  }

  async #toolReproduce(step) {
    await this.clock.sleep(step.sim ?? 1.5);
    this.findings ??= {};
    this.findings.reproduced = !!step.reproduced;
    this.findings.verified = !!step.verified;
    this.findings.confidence = step.confidence ?? 0;
    this.trace.log(
      this.id,
      `  复现结果=${step.reproduced ? '成功' : '失败'}，验证=${step.verified ? '通过' : '未通过'}，置信度=${(step.confidence ?? 0).toFixed(2)}`,
    );
  }

  async #toolCreatePr(step) {
    await this.clock.sleep(step.sim ?? 0.8);
    const prId = nextId('P');
    const prThread = step.threadId ?? `H-PR-${prId}`;
    this.store.put({
      id: prId,
      kind: Kind.PR,
      title: step.title ?? this.task.slice(0, 20),
      summary: `${step.title ?? this.task.slice(0, 18)}（评审进行中）`,
      body: step.body ?? `修复内容：${this.task}\n验证：${step.verification ?? '相关测试通过'}`,
      links: [this.id],
    });
    this.store.link(prId, this.id);
    this.store.put({
      id: `${prId}-C1`,
      kind: Kind.COMMENT,
      title: '评审意见',
      summary: '建议补充超时文案',
      body: '超时提示文案需要产品确认，其余实现看起来没问题。'.repeat(6),
      links: [prId],
    });
    this.store.link(prId, `${prId}-C1`);

    this.prId = prId;
    this.prThread = prThread;
    // 订阅 PR Thread：后续 CI 与评审事件将直通到本 Worker（第 4 讲）
    this.bus?.subscribe(this.id, prThread, `PR ${prId}`, Source.GH_PR);
    this.subscriptions.add(prThread);
    this.store.link(this.id, prThread);
    this.trace.log(this.id, `  创建 ${prId} 并订阅其 Thread ${prThread}（后续 CI/评审事件将直通）`);
  }

  // -- 收尾与汇报 ---------------------------------------------------------

  async #conclude() {
    const findings = this.findings ?? {};
    this.result = {
      workerId: this.id,
      task: this.task,
      prId: this.prId ?? null,
      reproduced: findings.reproduced ?? null,
      verified: findings.verified ?? null,
      confidence: findings.confidence ?? null,
      toolCalls: this.toolLog.length,
      contextTokens: this.contextTokens,
    };

    if (this.speculative) {
      // 第 6 讲：后台尝试结束后，交付与尝试是两个独立判断
      const gate = SpeculationGate.shouldDeliver({
        reproduced: !!findings.reproduced,
        verified: !!findings.verified,
        confidence: findings.confidence ?? 0,
        userAsked: this.constraints.some((c) => /进度|到哪|怎么样/.test(c)),
      });
      this.trace.log(this.id, `交付闸门：${gate.go ? '交付' : '安静丢弃'} —— ${gate.reason}`);
      if (!gate.go) {
        this.status = 'closed';
        this.trace.log(this.id, `推测结果丢弃，不通知用户（避免用注意力负担抵消省下的等待）`);
        return;
      }
    }

    this.status = 'closed';
    this.router?.report(this.id, {
      purpose: this.prId ? 'acceptance' : 'decision',
      fact: {
        topic: this.title,
        background: this.task,
        change: this.plan.filter((p) => p.tool === 'edit').map((p) => p.note ?? p.target).join('；') || '按计划完成改动',
        verification: findings.verified === false ? '未通过验证' : '相关测试通过',
        pending: this.constraints.length ? `需确认约束：${this.constraints[0].slice(0, 16)}` : '无',
        ref: this.prId ?? '',
      },
    });
    this.trace.log(this.id, `任务结束，共 ${this.toolLog.length} 次工具调用，最终上下文 ${this.contextTokens} tok`);
  }

  /** Router 全局状态里的一行摘要（Inline，不含完整历史）。 */
  brief() {
    return `${this.id} 任务:${this.task.slice(0, 16)} | 订阅:${[...this.subscriptions].join(',') || '-'} | 状态:${this.status} | 最近:${this.lastReport || this.toolLog.at(-1)?.fingerprint || '-'}`;
  }

  /** 第 8 讲：计算无状态，状态可持久化后由新容器恢复。 */
  toJSON() {
    return {
      id: this.id,
      task: this.task,
      title: this.title,
      status: this.status,
      stepIndex: this.stepIndex,
      plan: this.plan,
      scope: this.scope,
      subscriptions: [...this.subscriptions],
      constraints: this.constraints,
      evidence: [...this.evidence],
      findings: this.findings ?? null,
      contextTokens: this.contextTokens,
      toolCalls: this.toolLog.length,
      speculative: this.speculative,
      asyncTools: this.asyncTools,
      prId: this.prId ?? null,
    };
  }

  /** 从持久化状态恢复成一个可继续执行的 Worker（新的无状态计算实例）。 */
  static fromJSON(data, deps) {
    const w = new Worker({ ...deps, id: data.id, task: data.task, title: data.title, plan: data.plan, scope: data.scope, speculative: data.speculative, asyncTools: data.asyncTools });
    w.stepIndex = data.stepIndex;
    // 如实还原持久化时的状态：若检查点时是 blocked-env，恢复后 resume() 才能从挂起的那一步续跑。
    w.status = data.status ?? 'idle';
    w.constraints = data.constraints ?? [];
    w.evidence = new Set(data.evidence ?? []);
    w.findings = data.findings ?? {};
    w.contextTokens = data.contextTokens ?? 400;
    w.subscriptions = new Set(data.subscriptions ?? []);
    w.prId = data.prId ?? null;
    for (const t of w.subscriptions) deps.bus?.subscribe(w.id, t, `恢复的订阅 ${t}`, Source.INTERNAL);
    return w;
  }
}
