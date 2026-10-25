import type { AppSettings, FromHost, PluginLoadIssue, ProviderConfig, ProviderPreset, TodoPayload } from '../bridge';
import { callInfo, classifyResult, resultBody } from './legacyText';
import type { Entry, Note, NoteKind, PaneAction, PaneState, Step, StepState, Turn } from './types';

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
  }));
  return {
    activeProviderId: raw.activeProviderId ?? raw.ActiveProviderId ?? '',
    providers,
    plugins: raw.plugins ?? raw.Plugins ?? undefined,
    autoApprove: raw.autoApprove === true || raw.AutoApprove === true,
  };
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
  return finishTurn(s1, 'done', at);
}

function newTurn(id: string, userText: string, mode: Turn['mode'], at?: number): Turn {
  return { id, mode, userText, steps: [], notes: [], todos: [], answer: '', answerStreaming: false, outcome: 'running', startedAt: now(at) };
}

function reduceHost(s: PaneState, msg: FromHost, at?: number): PaneState {
  switch (msg.type) {
    case 'status': return { ...s, status: String(msg.text ?? '') };
    case 'trace': return addNote(s, 'trace', String(msg.text ?? ''));
    case 'tool_call': return onToolCall(s, String(msg.text ?? ''), at);
    case 'tool_result': return onToolResult(s, String(msg.text ?? ''), at);
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
      if (activeIndex(s) < 0) return addNote(s, 'error', text);
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
