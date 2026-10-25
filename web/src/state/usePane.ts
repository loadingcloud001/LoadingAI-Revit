import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { AppSettings, ApprovalScope, Attachment, BUILTIN_PROVIDER_NAME, isBuiltinProvider, onHostMessage, postToHost, providersOnly, ProviderConfig } from '../bridge';
import { parseInput } from '../cli/inputParser';
import { SLASH_COMMANDS } from '../cli/slashCommands';
import { autoApproveFor, HostCaps, modeFor, resolveLevel } from '../lib/permissions';
import { EMPTY_QUEUE, editItem, enqueue, onTurnEnd, QueueState, removeItem, resume } from '../lib/queue';
import { initialState, reduce } from './reducer';
import type { Entry, PaneState, PermissionLevel, ReviewDecision, Step, Turn } from './types';

const LEVEL_KEY = 'lai.level';

function readStored(key: string): string | null {
  try { return window.localStorage.getItem(key); } catch { return null; }
}
function writeStored(key: string, value: string) {
  try { window.localStorage.setItem(key, value); } catch { /* private mode or blocked storage */ }
}

function lastTurn(entries: Entry[]): Turn | null {
  for (let i = entries.length - 1; i >= 0; i--) {
    const e = entries[i];
    if (e.kind === 'turn') return e.turn;
  }
  return null;
}

export type Pane = ReturnType<typeof usePane>;

/** Wires the reducer to the host bridge and owns the pane-only state (level, queue). */
export function usePane(opts: { openSettings: () => void }) {
  const [state, dispatch] = useReducer(reduce, undefined, initialState);
  const stateRef = useRef<PaneState>(state);
  stateRef.current = state;
  const [queue, setQueueState] = useState<QueueState>(EMPTY_QUEUE);
  const queueRef = useRef<QueueState>(EMPTY_QUEUE);
  // Updaters stay pure (StrictMode runs them twice); sends happen outside them.
  const setQueue = useCallback((next: QueueState | ((q: QueueState) => QueueState)) => {
    queueRef.current = typeof next === 'function' ? next(queueRef.current) : next;
    setQueueState(queueRef.current);
  }, []);
  const queueSeq = useRef(0);
  // The host announces what it can do (host_caps); an older host announces nothing and gets the older controls.
  const caps: HostCaps = useMemo(() => new Set<string>(state.caps), [state.caps]);
  const [localLevel, setLevelState] = useState<PermissionLevel>(() => resolveLevel(readStored(LEVEL_KEY), false, new Set()));
  // With host levels the host stores the level and enforces it; the pane only shows it (ADR-0017).
  const hostLevel = caps.has('levels') ? state.settings?.permissionLevel : undefined;
  const level: PermissionLevel = hostLevel ?? localLevel;

  useEffect(() => {
    const off = onHostMessage(msg => dispatch({ type: 'host', msg, at: Date.now() }));
    const ask = () => {
      postToHost({ type: 'requestSettings' });
      postToHost({ type: 'getCatalog' });
      postToHost({ type: 'listPlugins' });
    };
    ask();
    const t = window.setTimeout(ask, 800);
    return () => { window.clearTimeout(t); off(); };
  }, []);

  // Without host levels the host switch is the source of truth for Bypass.
  const autoApprove = !!state.settings?.autoApprove;
  useEffect(() => {
    if (!state.settings || caps.has('levels')) return;
    setLevelState(cur => {
      if (autoApprove) return 'bypass';
      return cur === 'bypass' ? resolveLevel(readStored(LEVEL_KEY), false, caps) : cur;
    });
  }, [autoApprove, state.settings, caps]);

  const providers = state.settings?.providers ?? [];
  const activeProvider: ProviderConfig | null = useMemo(
    () => providers.find(p => p.id === state.settings?.activeProviderId) ?? providers[0] ?? null,
    [providers, state.settings?.activeProviderId],
  );

  const saveSettings = useCallback((next: AppSettings) => {
    dispatch({ type: 'settingsLocal', settings: next });
    postToHost({ type: 'saveSettings', settings: providersOnly(next) });
  }, []);

  const setLevel = useCallback((next: PermissionLevel) => {
    const cur = stateRef.current.settings;
    if (stateRef.current.caps.includes('levels')) {
      if (cur) saveSettings({ ...cur, permissionLevel: next, autoApprove: autoApproveFor(next) });
      return;
    }
    setLevelState(next);
    if (next !== 'bypass') writeStored(LEVEL_KEY, next);
    if (cur && autoApproveFor(next) !== !!cur.autoApprove) saveSettings({ ...cur, autoApprove: autoApproveFor(next) });
  }, [saveSettings]);

  const note = useCallback((text: string, kind: 'system' | 'error' = 'system') => dispatch({ type: 'note', kind, text }), []);

  /** Sends now. Returns false when the message could not go (and says why in the transcript). */
  const sendNow = useCallback((raw: string, attachments: Attachment[] = []): boolean => {
    const s = stateRef.current;
    const parsed = parseInput(raw);
    const slash = parsed.slash?.name;
    if (slash === 'clear') { postToHost({ type: 'clear' }); dispatch({ type: 'clear' }); setQueue(EMPTY_QUEUE); return true; }
    if (slash === 'help') {
      note(`Commands:\n${SLASH_COMMANDS.map(c => `${c.label} ${c.argsHint ?? ''} — ${c.description}`).join('\n')}\n\n@mentions: @selection @activeView @visible @warnings @levels @sheets`);
      return true;
    }
    if (slash === 'checkpoints') { postToHost({ type: 'listCheckpoints' }); return true; }
    const text = parsed.cleanText.trim();
    if (!text && attachments.length === 0 && slash !== 'resume') return false;
    const cur = s.settings;
    const active = cur?.providers.find(p => p.id === cur.activeProviderId) ?? cur?.providers[0];
    if (!cur || !active) { note('No model is set up. Open Settings → Models to pick one.', 'error'); opts.openSettings(); return false; }
    if (!isBuiltinProvider(active.id) && !active.apiKey && !active.baseUrl.includes('localhost')) {
      note(`${active.name} has no API key. Add it in Settings → Models.`, 'error'); opts.openSettings(); return false;
    }
    const q = s.quota;
    if (isBuiltinProvider(active.id) && q && q.ok !== false && q.remaining <= 0) {
      note(`${BUILTIN_PROVIDER_NAME} free requests are used up for today${q.resetUtc ? ` (resets ${new Date(q.resetUtc).toLocaleTimeString()})` : ''}. Add your own API key in Settings → Models.`, 'error');
      return false;
    }
    const mode = parsed.effectiveMode ?? modeFor(level);
    dispatch({ type: 'send', text: raw.trim() || '(image)', mode, attachments: attachments.length ? attachments : undefined, at: Date.now() });
    postToHost({ type: 'send', text: text || raw.trim(), attachments: attachments.length ? attachments : undefined, mode, slash, mentions: parsed.mentions });
    return true;
  }, [level, note, opts]);

  /** Sends, or queues when a turn is running. */
  const submit = useCallback((raw: string, attachments: Attachment[] = []): boolean => {
    if (stateRef.current.running) {
      if (!raw.trim()) return false;
      setQueue(q => enqueue(q, `q${++queueSeq.current}`, raw));
      return true;
    }
    return sendNow(raw, attachments);
  }, [sendNow]);

  // When a turn finishes, hand the next queued message to sendNow (or pause the queue).
  const last = lastTurn(state.entries);
  const lastId = last?.id;
  const lastOutcome = last?.outcome;
  useEffect(() => {
    if (!last || lastOutcome === 'running') return;
    const r = onTurnEnd(queueRef.current, last);
    setQueue(r.queue);
    if (r.send) window.setTimeout(() => sendNow(r.send!.text), 250);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lastId, lastOutcome]);

  const approve = useCallback((toolCallId: string, approved: boolean, opts: { scope?: ApprovalScope; reason?: string } = {}) => {
    const reason = opts.reason?.trim();
    postToHost({
      type: 'approveTool', toolCallId, approved,
      scope: approved && opts.scope && opts.scope !== 'once' ? opts.scope : undefined,
      reason: !approved && reason ? reason : undefined,
    });
    dispatch({ type: 'decide', toolCallId, decision: approved ? 'allowed' : 'denied' });
  }, []);

  /** Sends edited inputs back. The host re-checks them and posts the card again; the review stays open. */
  const editArgs = useCallback((toolCallId: string, args: Record<string, unknown>) => {
    postToHost({ type: 'approveTool', toolCallId, approved: false, args });
  }, []);

  /** Turns a queued message into guidance for the running turn. */
  const steerNow = useCallback((id: string) => {
    const item = queueRef.current.items.find(i => i.id === id);
    if (!item || !stateRef.current.running) return;
    setQueue(q => removeItem(q, id));
    postToHost({ type: 'steer', text: item.text });
  }, []);

  // A steer the host could not deliver (the turn ended first) goes out as a normal message.
  useEffect(() => onHostMessage(msg => {
    if (msg.type !== 'steer_ack' || msg.deliveredAt != null) return;
    const text = msg.text;
    window.setTimeout(() => {
      if (stateRef.current.running) setQueue(q => enqueue(q, `q${++queueSeq.current}`, text));
      else sendNow(text);
    }, 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), []);

  const elementAction = useCallback((kind: 'selectElements' | 'showElements' | 'isolateElements', ids: number[]) => {
    if (ids.length) postToHost({ type: kind, ids });
  }, []);

  const retryStep = useCallback((step: Step) => {
    const inputs = step.argsText.trim();
    submit(`Retry ${step.name} with the same inputs${inputs ? `:\n${inputs}` : '.'}`);
  }, [submit]);

  const submitPreset = useCallback((approved: boolean, override?: Record<string, unknown>) => {
    const pending = stateRef.current.pendingReview;
    if (!pending) return;
    const values = { ...pending.values, ...(override ?? {}) };
    postToHost({ type: 'submitPreset', toolCallId: pending.toolCallId, approved, values: approved ? values : undefined });
    dispatch({ type: 'decide', toolCallId: pending.toolCallId, decision: (approved ? 'allowed' : 'cancelled') as ReviewDecision });
  }, []);

  const pick = useCallback((fieldId: string) => {
    const pending = stateRef.current.pendingReview;
    if (!pending || pending.pickBusy) return;
    dispatch({ type: 'presetPickStart', fieldId });
    postToHost({ type: 'presetPick', toolCallId: pending.toolCallId, fieldId });
  }, []);

  const stop = useCallback(() => {
    postToHost({ type: 'cancel' });
    dispatch({ type: 'stop' });
  }, []);

  const clear = useCallback(() => {
    postToHost({ type: 'clear' });
    dispatch({ type: 'clear' });
    setQueue(EMPTY_QUEUE);
  }, []);

  return {
    state,
    dispatch,
    caps,
    level,
    setLevel,
    activeProvider,
    saveSettings,
    submit,
    approve,
    editArgs,
    steerNow,
    retryStep,
    selectIds: (ids: number[]) => elementAction('selectElements', ids),
    showIds: (ids: number[]) => elementAction('showElements', ids),
    isolateIds: (ids: number[]) => elementAction('isolateElements', ids),
    submitPreset,
    setPresetValue: (fieldId: string, value: unknown) => dispatch({ type: 'presetValue', fieldId, value }),
    pick,
    stop,
    clear,
    queue,
    queueActions: {
      remove: (id: string) => setQueue(q => removeItem(q, id)),
      edit: (id: string, text: string) => setQueue(q => editItem(q, id, text)),
      resume: () => {
        const r = resume(queueRef.current);
        if (stateRef.current.running || !r.items.length) { setQueue(r); return; }
        const [first, ...rest] = r.items;
        setQueue({ ...r, items: rest });
        window.setTimeout(() => sendNow(first.text), 0);
      },
      clear: () => setQueue(EMPTY_QUEUE),
    },
  };
}
