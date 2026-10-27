import type { AppSettings, ChatMode, FromHost, PluginLoadIssue, ProviderConfig, ProviderPreset, TodoPayload, ToolOutcome } from '../bridge';
import { callInfo, classifyResult, resultBody } from './legacyText';
import type { Entry, Note, NoteKind, PaneAction, PaneState, PermissionLevel, ReviewDecision, Step, StepState, Turn } from './types';

export function initialState(): PaneState {
  return {
    entries: [],
    running: false,
    pendingReview: null,
    progress: null,
    status: 'Ready',
    context: null,
    quota: null,
    settings: null,
    catalog: [],
    plugins: [],
    loadIssues: [],
    checkpoints: [],
    suggestions: [],
    providerTest: null,
    models: {},
    modelsLoading: null,
    caps: [],
    notes: undefined,
    playbooks: { items: [], issues: [], folder: '' },
    hostDark: null,
    seq: 0,
  };
}

const OPEN: StepState[] = ['running', 'review'];

function nextId(s: PaneState, prefix: string): [string, PaneState] {
  const seq = s.seq + 1;
  return [`${prefix}${seq}`, { ...s, seq }];
}

function now(at?: number): number {
  return typeof at === 'number' ? at : Date.now();
}

/** Index of the running turn in entries, or -1. */
function activeIndex(s: PaneState): number {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind === 'turn') return e.turn.outcome === 'running' ? i : -1;
  }
  return -1;
}

function withTurn(s: PaneState, idx: number, fn: (t: Turn) => Turn): PaneState {
  const e = s.entries[idx];
  if (!e || e.kind !== 'turn') return s;
  const entries = s.entries.slice();
  entries[idx] = { kind: 'turn', turn: fn(e.turn) };
  return { ...s, entries };
}

function withSteps(t: Turn, fn: (steps: Step[]) => Step[]): Turn {
  return { ...t, steps: fn(t.steps) };
}

/** Adds a note to the running turn, or as a standalone entry when no turn is open. */
function addNote(s: PaneState, kind: NoteKind, text: string): PaneState {
  const [id, s1] = nextId(s, 'n');
  const note: Note = { id, kind, text };
  const idx = activeIndex(s1);
  if (idx >= 0) return withTurn(s1, idx, t => ({ ...t, notes: [...t.notes, note] }));
  return { ...s1, entries: [...s1.entries, { kind: 'note', note }] };
}

function lastOpenStep(t: Turn, name: string): number {
  for (let i = t.steps.length - 1; i >= 0; i--) {
    if (t.steps[i].name === name && OPEN.includes(t.steps[i].state)) return i;
  }
  return -1;
}

function finishTurn(s: PaneState, outcome: Turn['outcome'], at?: number): PaneState {
  const idx = activeIndex(s);
  const ended = now(at);
  const s1 = idx < 0 ? s : withTurn(s, idx, t => ({
    ...t,
    outcome,
    endedAt: ended,
    answerStreaming: false,
    reasoning: t.reasoning ? { ...t.reasoning, streaming: false } : undefined,
    steps: t.steps.map(st => st.state === 'running'
      ? { ...st, state: outcome === 'done' ? 'done' as const : 'cancelled' as const, endedAt: st.endedAt ?? ended }
      : st.state === 'review' ? { ...st, state: 'cancelled' as const, endedAt: st.endedAt ?? ended } : st),
  }));
  return { ...s1, running: false, pendingReview: null, progress: null };
}

function plainTodos(plan: { steps?: { title: string; tool?: string; status?: string }[] }): TodoPayload[] {
  return (plan.steps ?? []).map((st, i) => ({ id: `plan-${i}`, title: st.title, tool: st.tool, status: (st.status as TodoPayload['status']) ?? 'pending' }));
}

function normalizeSettings(raw: any): AppSettings | null {
  if (!raw || typeof raw !== 'object') return null;
  const provs = raw.providers ?? raw.Providers ?? [];
  if (!Array.isArray(provs)) return null;
  const providers: ProviderConfig[] = provs.map((p: any) => ({
    id: p.id ?? p.Id ?? '',
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKey: p.apiKey ?? p.ApiKey ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
    supportsImages: supportsImagesOf(p.supportsImages !== undefined ? p.supportsImages : p.SupportsImages),
  }));
  const level = raw.permissionLevel ?? raw.PermissionLevel;
  const always = raw.alwaysAllowTools ?? raw.AlwaysAllowTools;
  const select = raw.selectChangedAfterTurn ?? raw.SelectChangedAfterTurn;
  return {
    activeProviderId: raw.activeProviderId ?? raw.ActiveProviderId ?? '',
    providers,
    plugins: raw.plugins ?? raw.Plugins ?? undefined,
    autoApprove: raw.autoApprove === true || raw.AutoApprove === true,
    permissionLevel: LEVEL_IDS.includes(level) ? (level as PermissionLevel) : undefined,
    alwaysAllowTools: Array.isArray(always) ? always.filter((n: unknown): n is string => typeof n === 'string') : [],
    selectChangedAfterTurn: select !== false,
    instructions: String(raw.instructions ?? raw.Instructions ?? ''),
  };
}

const LEVEL_IDS = ['readonly', 'plan', 'confirm', 'autoEdit', 'bypass'];

/** The "can see images" switch is a tri-state: true, false, or null/undefined for Auto. */
function supportsImagesOf(v: unknown): boolean | null | undefined {
  if (v === true) return true;
  if (v === false) return false;
  if (v === null) return null;
  return undefined;
}

function normalizePresets(raw: any): ProviderPreset[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((p: any) => ({
    id: p.id ?? p.Id ?? '',
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
    description: p.description ?? p.Description ?? '',
  }));
}

function onToolCall(s: PaneState, text: string, at?: number): PaneState {
  const info = callInfo(text);
  let idx = activeIndex(s);
  if (idx < 0) return addNote(s, 'trace', info.display);
  const t = (s.entries[idx] as Extract<Entry, { kind: 'turn' }>).turn;
  if (info.awaitingId && t.steps.some(st => st.review?.toolCallId === info.awaitingId)) return s;
  const open = lastOpenStep(t, info.name);
  if (open >= 0 && (t.steps[open].state === 'running' || t.steps[open].review?.decided === 'allowed')) {
    return withTurn(s, idx, tt => withSteps(tt, steps => steps.map((st, i) => i === open ? { ...st, state: 'running', argsText: info.args || st.argsText } : st)));
  }
  const [id, s1] = nextId(s, 's');
  const step: Step = { id, name: info.name, argsText: info.args, state: info.awaiting ? 'review' : 'running', startedAt: now(at) };
  idx = activeIndex(s1);
  return withTurn(s1, idx, tt => withSteps(tt, steps => [...steps, step]));
}

function onToolResult(s: PaneState, text: string, at?: number): PaneState {
  const c = classifyResult(text);
  if (!c.name) return addNote(s, /^tool argument error/i.test(text.trim()) ? 'error' : 'system', text.trim());
  let s1 = s;
  let idx = activeIndex(s1);
  if (idx < 0) return addNote(s, 'system', text.trim());
  let t = (s1.entries[idx] as Extract<Entry, { kind: 'turn' }>).turn;
  let pos = lastOpenStep(t, c.name);
  if (pos < 0) {
    const [id, s2] = nextId(s1, 's');
    s1 = withTurn(s2, idx, tt => withSteps(tt, steps => [...steps, { id, name: c.name, argsText: '', state: 'running', startedAt: now(at) }]));
    idx = activeIndex(s1);
    t = (s1.entries[idx] as Extract<Entry, { kind: 'turn' }>).turn;
    pos = t.steps.length - 1;
  }
  const target = t.steps[pos];
  let next: Step;
  if (c.kind === 'ran') next = { ...target, state: 'done', resultText: resultBody(text), endedAt: now(at) };
  else if (c.kind === 'outcome' && c.outcome === 'confirmed') next = { ...target, state: 'running', review: target.review ? { ...target.review, decided: 'allowed' } : undefined };
  else if (c.kind === 'outcome' && c.outcome === 'timeout') next = { ...target, state: 'timeout', resultText: text.trim(), endedAt: now(at), review: target.review ? { ...target.review, decided: 'timeout' } : undefined };
  else if (c.kind === 'outcome') {
    const denied = /denied/i.test(text);
    next = { ...target, state: denied ? 'denied' : 'cancelled', resultText: text.trim(), endedAt: now(at), review: target.review ? { ...target.review, decided: target.review.decided ?? (denied ? 'denied' : 'cancelled') } : undefined };
  } else next = { ...target, state: 'done', resultText: resultBody(text), endedAt: now(at) };
  s1 = withTurn(s1, idx, tt => withSteps(tt, steps => steps.map((st, i) => i === pos ? next : st)));
  if (s1.pendingReview && s1.pendingReview.stepId === target.id && next.state !== 'review') s1 = { ...s1, pendingReview: null };
  return s1;
}

function onAssistant(s: PaneState, incoming: string, at?: number): PaneState {
  let s1 = s;
  if (activeIndex(s1) < 0) {
    if (!incoming) return s;
    const [id, s2] = nextId(s1, 't');
    s1 = { ...s2, entries: [...s2.entries, { kind: 'turn', turn: newTurn(id, '', 'build', at) }] };
  }
  const idx = activeIndex(s1);
  s1 = withTurn(s1, idx, t => {
    let answer = t.answer;
    if (t.answerStreaming) {
      if (incoming && !(answer === incoming || answer.startsWith(incoming))) answer = incoming.startsWith(answer) || incoming.length >= answer.length ? incoming : answer;
    } else if (incoming) {
      answer = answer ? `${answer}\n\n${incoming}` : incoming;
    }
    return { ...t, answer };
  });
  // A host turn ends with turn_end, after any steer it could not deliver (those become the next message).
  const cur = s1.entries[idx];
  if (cur.kind === 'turn' && cur.turn.structured) return s1;
  return finishTurn(s1, 'done', at);
}

function newTurn(id: string, userText: string, mode: Turn['mode'], at?: number): Turn {
  return { id, mode, userText, steps: [], notes: [], todos: [], answer: '', answerStreaming: false, outcome: 'running', startedAt: now(at) };
}

/** Index of the turn the host calls `hostId`, else the running turn, else -1. */
function turnFor(s: PaneState, hostId?: string): number {
  if (hostId) {
    for (let i = s.entries.length - 1; i >= 0; i--) {
      const e = s.entries[i];
      if (e.kind === 'turn' && e.turn.hostId === hostId) return i;
    }
    // A turn the host has not named yet is the running one; a turn it named differently is someone else's.
    const idx = activeIndex(s);
    if (idx < 0) return -1;
    const e = s.entries[idx];
    return e.kind === 'turn' && e.turn.hostId && e.turn.hostId !== hostId ? -1 : idx;
  }
  return activeIndex(s);
}

/** Adds a running, host-structured turn when the host talks about one the pane never started. */
function openHostTurn(s: PaneState, hostId: string | undefined, at?: number): PaneState {
  const [tid, s1] = nextId(s, 't');
  const turn = { ...newTurn(tid, '', 'build', at), hostId, structured: true };
  return { ...s1, running: true, entries: [...s1.entries, { kind: 'turn', turn }] };
}

function findStep(s: PaneState, callId: string): { idx: number; pos: number } | null {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind !== 'turn') continue;
    const pos = e.turn.steps.findIndex(st => st.callId === callId || st.review?.toolCallId === callId);
    if (pos >= 0) return { idx: i, pos };
  }
  return null;
}

function endState(outcome: ToolOutcome, ok: boolean): StepState {
  switch (outcome) {
    case 'ran': return ok ? 'done' : 'failed';
    case 'denied': return 'denied';
    case 'timeout': return 'timeout';
    case 'cancelled': return 'cancelled';
    default: return 'failed';
  }
}

function decidedFor(outcome: ToolOutcome): ReviewDecision | undefined {
  switch (outcome) {
    case 'ran': return 'allowed';
    case 'denied': return 'denied';
    case 'timeout': return 'timeout';
    case 'cancelled': return 'cancelled';
    default: return undefined;
  }
}

/** Once the host speaks in structured turns, the older text messages for that turn are duplicates. */
function inStructuredTurn(s: PaneState): boolean {
  const idx = activeIndex(s);
  if (idx < 0) return false;
  const e = s.entries[idx];
  return e.kind === 'turn' && !!e.turn.structured;
}

function reduceHost(s: PaneState, msg: FromHost, at?: number): PaneState {
  switch (msg.type) {
    case 'status': return { ...s, status: String(msg.text ?? '') };
    case 'trace': return addNote(s, 'trace', String(msg.text ?? ''));
    case 'tool_call': return inStructuredTurn(s) ? s : onToolCall(s, String(msg.text ?? ''), at);
    case 'tool_result': return inStructuredTurn(s) ? s : onToolResult(s, String(msg.text ?? ''), at);
    case 'host_caps': return { ...s, caps: Array.isArray(msg.caps) ? msg.caps.filter(c => typeof c === 'string') : [] };
    case 'theme': return { ...s, hostDark: !!msg.dark };
    case 'turn_start': {
      let s1 = s;
      let idx = activeIndex(s1);
      if (idx < 0) {
        s1 = openHostTurn(s1, msg.turnId, at);
        idx = activeIndex(s1);
      }
      return withTurn(s1, idx, t => ({ ...t, hostId: msg.turnId, level: msg.level, structured: true, mode: (msg.mode as ChatMode) || t.mode }));
    }
    case 'tool_start': {
      let s1 = s;
      let idx = turnFor(s1, msg.turnId);
      if (idx < 0) {
        s1 = openHostTurn(s1, msg.turnId, at);
        idx = activeIndex(s1);
      }
      const t = (s1.entries[idx] as Extract<Entry, { kind: 'turn' }>).turn;
      if (t.steps.some(st => st.callId === msg.toolCallId)) return s1;
      const [id, s2] = nextId(s1, 's');
      const step: Step = {
        id, callId: msg.toolCallId, name: msg.name, tier: msg.tier, destructive: msg.destructive,
        argsText: msg.args ?? '', state: 'running', startedAt: now(at),
      };
      return withTurn(s2, idx, tt => withSteps(tt, steps => [...steps, step]));
    }
    case 'tool_review': {
      const r = msg.review;
      if (!r) return s;
      let s1 = s;
      let found = findStep(s1, r.toolCallId);
      if (!found) {
        let idx = activeIndex(s1);
        if (idx < 0) {
          s1 = openHostTurn(s1, undefined, at);
          idx = activeIndex(s1);
        }
        const [id, s2] = nextId(s1, 's');
        const step: Step = { id, callId: r.toolCallId, name: r.toolName, tier: r.tier, destructive: r.destructive, argsText: r.args ?? '', state: 'running', startedAt: now(at) };
        s1 = withTurn(s2, idx, tt => withSteps(tt, steps => [...steps, step]));
        found = findStep(s1, r.toolCallId)!;
      }
      const target = (s1.entries[found.idx] as Extract<Entry, { kind: 'turn' }>).turn.steps[found.pos];
      const hasForm = !!r.fields?.length;
      const next: Step = {
        ...target, name: r.toolName, tier: r.tier, destructive: r.destructive, argsText: r.args ?? target.argsText, state: 'review',
        review: { kind: 'card', toolCallId: r.toolCallId, card: r },
      };
      const s2 = withTurn(s1, found.idx, tt => withSteps(tt, steps => steps.map((st, i) => i === found!.pos ? next : st)));
      const prev = s2.pendingReview;
      // The host re-posts a plain card after the user edits its inputs. A form the person is filling keeps its live values.
      const keep = prev && prev.toolCallId === r.toolCallId ? prev : null;
      return {
        ...s2,
        pendingReview: {
          stepId: target.id,
          toolCallId: r.toolCallId,
          kind: 'card',
          values: keep ? keep.values : hasForm ? { ...(r.values ?? {}) } : {},
          pickBusy: keep ? keep.pickBusy : null,
        },
      };
    }
    case 'tool_end': {
      const found = findStep(s, msg.toolCallId);
      if (!found) return s;
      const target = (s.entries[found.idx] as Extract<Entry, { kind: 'turn' }>).turn.steps[found.pos];
      const decided = target.review ? (target.review.decided ?? decidedFor(msg.outcome)) : undefined;
      const next: Step = {
        ...target,
        state: endState(msg.outcome, msg.ok),
        outcome: msg.outcome,
        resultText: msg.text,
        ms: msg.ms,
        partial: msg.partial,
        image: msg.image,
        endedAt: now(at),
        review: target.review ? { ...target.review, decided } : undefined,
      };
      const s1 = withTurn(s, found.idx, tt => withSteps(tt, steps => steps.map((st, i) => i === found.pos ? next : st)));
      return s1.pendingReview && s1.pendingReview.stepId === target.id ? { ...s1, pendingReview: null } : s1;
    }
    case 'turn_end': {
      const idx = turnFor(s, msg.turnId);
      if (idx < 0) return s;
      const turn = (s.entries[idx] as Extract<Entry, { kind: 'turn' }>).turn;
      const s1 = withTurn(s, idx, t => ({ ...t, rolledBack: msg.rolledBack === true ? true : t.rolledBack }));
      if (turn.outcome !== 'running') return s1;
      const outcome = msg.outcome === 'cancelled' ? 'cancelled' : msg.outcome === 'error' ? 'error' : 'done';
      return finishTurn(s1, outcome, at);
    }
    case 'turn_changes': {
      const idx = turnFor(s, msg.turnId);
      if (idx < 0) return s;
      return withTurn(s, idx, t => ({
        ...t,
        changes: { added: msg.added, modified: msg.modified, deleted: msg.deleted, idsTruncated: msg.idsTruncated, byCategory: Array.isArray(msg.byCategory) ? msg.byCategory : [] },
      }));
    }
    case 'steer_ack':
      // Delivered: show it in the turn. Undelivered is handled by the hook, which sends it as a normal message.
      return msg.deliveredAt != null ? addNote(s, 'steer', msg.text) : s;
    case 'tool_approval':
    case 'tool_preset': {
      const isApproval = msg.type === 'tool_approval';
      const payload = isApproval ? msg.approval : msg.preset;
      if (!payload) return s;
      let s1 = s;
      if (activeIndex(s1) < 0) {
        const [tid, s2] = nextId(s1, 't');
        s1 = { ...s2, running: true, entries: [...s2.entries, { kind: 'turn', turn: newTurn(tid, '', 'build', at) }] };
      }
      const [id, s3] = nextId(s1, 's');
      const step: Step = {
        id,
        name: payload.toolName,
        tier: payload.tier,
        argsText: isApproval ? msg.approval.args : JSON.stringify(msg.preset.values ?? {}),
        state: 'review',
        review: isApproval
          ? { kind: 'approval', toolCallId: payload.toolCallId, approval: msg.approval }
          : { kind: 'preset', toolCallId: payload.toolCallId, preset: msg.preset },
        startedAt: now(at),
      };
      const idx = activeIndex(s3);
      const s4 = withTurn(s3, idx, t => withSteps(t, steps => [...steps, step]));
      return {
        ...s4,
        pendingReview: {
          stepId: id,
          toolCallId: payload.toolCallId,
          kind: isApproval ? 'approval' : 'preset',
          values: isApproval ? {} : { ...(msg.preset.values ?? {}) },
          pickBusy: null,
        },
      };
    }
    case 'presetPickResult': {
      const pr = s.pendingReview;
      const s1 = pr ? { ...s, pendingReview: { ...pr, pickBusy: null } } : s;
      const result = msg.result;
      if (msg.ok === false || result?.ok === false) return addNote(s1, 'error', `Pick failed: ${msg.error ?? result?.error ?? 'unknown'}`);
      if (result?.cancelled) return addNote(s1, 'system', 'Pick cancelled.');
      if (!pr || pr.toolCallId !== msg.toolCallId) return s1;
      const values = { ...pr.values };
      if (Array.isArray(result?.ids)) values[msg.fieldId] = result!.ids;
      else if (result?.box) values[msg.fieldId] = result.box;
      return { ...s1, pendingReview: { ...pr, pickBusy: null, values } };
    }
    case 'plan': {
      const idx = activeIndex(s);
      if (idx < 0) return s;
      return withTurn(s, idx, t => ({ ...t, plan: msg.plan, todos: msg.plan.steps?.length ? plainTodos(msg.plan) : t.todos }));
    }
    case 'todo': {
      const idx = activeIndex(s);
      const todos = Array.isArray(msg.todos) ? msg.todos : [];
      return idx < 0 ? s : withTurn(s, idx, t => ({ ...t, todos }));
    }
    case 'progress': {
      const p = msg.progress;
      if (!p || p.phase === 'idle') return { ...s, progress: null };
      return { ...s, progress: p };
    }
    case 'context': return { ...s, context: msg.context };
    case 'quota': return msg.quota ? { ...s, quota: msg.quota } : s;
    case 'settings': {
      const settings = normalizeSettings(msg.settings);
      return settings ? { ...s, settings } : { ...s, status: 'Settings parse error' };
    }
    case 'catalog': return { ...s, catalog: normalizePresets(msg.presets) };
    case 'plugins': {
      const issues = (msg as { loadIssues?: PluginLoadIssue[] }).loadIssues;
      return { ...s, plugins: Array.isArray(msg.plugins) ? msg.plugins : [], loadIssues: Array.isArray(issues) ? issues : [] };
    }
    case 'checkpoints': {
      const list = Array.isArray(msg.checkpoints) ? msg.checkpoints : [];
      const s1 = { ...s, checkpoints: list };
      return addNote(s1, 'system', list.length ? list.map(c => `${c.id}  ${c.preview ?? ''}`).join('\n') : 'No checkpoints.');
    }
    case 'suggestions': return { ...s, suggestions: Array.isArray(msg.suggestions) ? msg.suggestions : [] };
    case 'notes': return { ...s, notes: msg.notes };
    case 'playbooks': return {
      ...s,
      playbooks: {
        items: Array.isArray(msg.items) ? msg.items : [],
        issues: Array.isArray(msg.issues) ? msg.issues : [],
        folder: String(msg.folder ?? ''),
      },
    };
    case 'testResult': {
      const models = msg.models && Array.isArray(msg.models) && msg.providerId ? { ...s.models, [msg.providerId]: msg.models } : s.models;
      return { ...s, models, providerTest: { id: msg.providerId ?? '', ok: !!msg.ok, message: String(msg.message ?? ''), loading: false } };
    }
    case 'models': {
      if (msg.error) return { ...s, modelsLoading: null, providerTest: { id: msg.providerId, ok: false, message: msg.error, loading: false } };
      const list = msg.models ?? [];
      return { ...s, modelsLoading: null, models: { ...s.models, [msg.providerId]: list }, providerTest: { id: msg.providerId, ok: true, message: `${list.length} models`, loading: false } };
    }
    case 'reasoning': {
      const idx = activeIndex(s);
      if (idx < 0) return s;
      return withTurn(s, idx, t => ({ ...t, reasoning: { text: String(msg.text ?? ''), streaming: !!msg.isStreaming } }));
    }
    case 'delta': {
      const chunk = String(msg.text ?? '');
      if (!chunk) return s;
      let s1 = s;
      if (activeIndex(s1) < 0) {
        const [id, s2] = nextId(s1, 't');
        s1 = { ...s2, entries: [...s2.entries, { kind: 'turn', turn: newTurn(id, '', 'build', at) }] };
      }
      const idx = activeIndex(s1);
      return { ...withTurn(s1, idx, t => ({ ...t, answer: t.answerStreaming ? t.answer + chunk : (t.answer ? `${t.answer}\n\n${chunk}` : chunk), answerStreaming: true })), running: true };
    }
    case 'assistant': return onAssistant(s, String(msg.text ?? ''), at);
    case 'error': {
      const text = String(msg.text ?? '');
      // Stop already ended the turn in the pane; the host's own "Cancelled." is not news.
      if (activeIndex(s) < 0) return /^cancelled\.?$/i.test(text.trim()) ? s : addNote(s, 'error', text);
      return finishTurn(addNote(s, 'error', text), 'error', at);
    }
    case 'history': {
      const hist = Array.isArray(msg.messages) ? msg.messages : [];
      let s1: PaneState = { ...s, entries: [], running: false, pendingReview: null, progress: null };
      s1 = addNote(s1, 'system', `Restored ${hist.length} messages`);
      for (const h of hist) {
        if (h.role === 'user') {
          const [id, s2] = nextId(s1, 't');
          s1 = { ...s2, entries: [...s2.entries, { kind: 'turn', turn: { ...newTurn(id, h.text, 'build', at), outcome: 'done', attachments: h.attachments } }] };
        } else if (h.role === 'assistant') {
          const last = s1.entries[s1.entries.length - 1];
          if (last?.kind === 'turn') {
            const entries = s1.entries.slice();
            entries[entries.length - 1] = { kind: 'turn', turn: { ...last.turn, answer: last.turn.answer ? `${last.turn.answer}\n\n${h.text}` : h.text } };
            s1 = { ...s1, entries };
          } else {
            const [id, s2] = nextId(s1, 't');
            s1 = { ...s2, entries: [...s2.entries, { kind: 'turn', turn: { ...newTurn(id, '', 'build', at), answer: h.text, outcome: 'done' } }] };
          }
        } else s1 = addNote(s1, 'system', h.text);
      }
      return s1;
    }
    default: return s;
  }
}

export function reduce(s: PaneState, a: PaneAction): PaneState {
  switch (a.type) {
    case 'host': return reduceHost(s, a.msg, a.at);
    case 'send': {
      const [id, s1] = nextId(s, 't');
      const turn: Turn = { ...newTurn(id, a.text, a.mode, a.at), attachments: a.attachments };
      return {
        ...s1,
        entries: [...s1.entries, { kind: 'turn', turn }],
        running: true,
        progress: { phase: 'thinking', label: a.mode === 'plan' ? 'Planning…' : 'Thinking…', indeterminate: true },
      };
    }
    case 'note': return addNote(s, a.kind, a.text);
    case 'decide': {
      const pr = s.pendingReview;
      const idx = activeIndex(s);
      const s1 = pr && pr.toolCallId === a.toolCallId ? { ...s, pendingReview: null } : s;
      if (idx < 0) return s1;
      return withTurn(s1, idx, t => withSteps(t, steps => steps.map(st => st.review?.toolCallId === a.toolCallId
        ? { ...st, state: a.decision === 'allowed' ? 'running' : st.state, review: { ...st.review, decided: a.decision } }
        : st)));
    }
    case 'presetValue': {
      const pr = s.pendingReview;
      return pr ? { ...s, pendingReview: { ...pr, values: { ...pr.values, [a.fieldId]: a.value } } } : s;
    }
    case 'presetPickStart': {
      const pr = s.pendingReview;
      return pr && !pr.pickBusy ? { ...s, pendingReview: { ...pr, pickBusy: a.fieldId } } : s;
    }
    case 'stop': return finishTurn(s, 'cancelled');
    case 'clear': {
      const [id, s1] = nextId(s, 'n');
      return { ...s1, entries: [{ kind: 'note', note: { id, kind: 'system', text: 'Cleared.' } }], running: false, pendingReview: null, progress: null };
    }
    case 'settingsLocal': return { ...s, settings: a.settings };
    case 'providerTest': return { ...s, providerTest: a.test };
    case 'modelsLoading': return { ...s, modelsLoading: a.providerId };
    default: return s;
  }
}
