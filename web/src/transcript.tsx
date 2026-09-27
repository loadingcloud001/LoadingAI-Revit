import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import type { Attachment, PlanPayload } from './bridge';

export type TranscriptMessage = {
  id: string;
  role: 'you' | 'assistant' | 'trace' | 'system' | 'tool_call' | 'tool_result' | 'plan' | 'approval' | 'preset' | 'reasoning';
  text: string;
  attachments?: Attachment[];
  plan?: PlanPayload;
  isStreaming?: boolean;
};

const PREVIEW_CHARS = 280;

const kLabel = { fontSize: 10, fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase' as const, color: '#6a6a6a', marginBottom: 3 };
const rowStyle = { padding: '8px 10px', borderBottom: '1px solid #ececec', fontSize: 12.5, background: '#f7f7f7' };
const linkBtn = 'px-1.5 py-0.5 border bg-white text-[10px]';

export type TranscriptItem =
  | { kind: 'turn'; id: string; role: 'you' | 'assistant'; messages: TranscriptMessage[] }
  | { kind: 'tool'; id: string; call?: TranscriptMessage; result?: TranscriptMessage }
  | { kind: 'note'; id: string; message: TranscriptMessage };

/** Write-confirm outcome. Matched only when the text itself is an outcome line. */
export type Outcome = 'confirmed' | 'cancelled' | 'timeout';

export type ResultClass =
  | { kind: 'outcome'; name: string; outcome: Outcome }
  | { kind: 'ran'; name: string }
  | { kind: 'other'; name: string };

/** Lifecycle of one tool: live before its result lands, terminal after. */
export type ToolState = Outcome | 'done' | 'awaiting-approval' | 'awaiting-preset' | 'running';

const LIVE_STATES: ToolState[] = ['awaiting-approval', 'awaiting-preset', 'running'];

/** Groups a tool call with the next result, and keeps you/agent turns together. Cards stay outside. */
export function groupTranscript(messages: TranscriptMessage[]): TranscriptItem[] {
  const items: TranscriptItem[] = [];
  for (let i = 0; i < messages.length; i++) {
    const m = messages[i];
    if (m.role === 'approval' || m.role === 'preset' || m.role === 'plan') continue;
    if (m.role === 'tool_call') {
      const next = messages[i + 1];
      if (next?.role === 'tool_result') {
        items.push({ kind: 'tool', id: m.id, call: m, result: next });
        i++;
      } else {
        items.push({ kind: 'tool', id: m.id, call: m });
      }
      continue;
    }
    if (m.role === 'tool_result') {
      items.push({ kind: 'tool', id: m.id, result: m });
      continue;
    }
    if (m.role === 'you' || m.role === 'assistant') {
      const last = items[items.length - 1];
      if (last?.kind === 'turn' && last.role === m.role) last.messages.push(m);
      else items.push({ kind: 'turn', id: m.id, role: m.role, messages: [m] });
      continue;
    }
    items.push({ kind: 'note', id: m.id, message: m });
  }
  return items;
}

// Host result lines are "{name} result: …" or "{name} <outcome line>". The name may
// contain spaces, so the split is anchored on the exact line endings the host posts
// and takes the shortest name that leaves one of those endings.
const RESULT_LINE = /^(.*?\S)\s+(result:.*|cancelled by user\.?|cancelled: preset timeout\.?|denied by user\.?|timed out\b.*|timeout\.?|confirmed\.?)$/i;

export function splitToolText(text: string): { name: string; rest: string } {
  const t = text.trim();
  const m = RESULT_LINE.exec(t);
  return m ? { name: m[1].trim(), rest: m[2].trim() } : { name: '', rest: t };
}

export function classifyResult(text: string): ResultClass {
  const { name, rest } = splitToolText(text);
  if (!name) return { kind: 'other', name: '' };
  const s = rest.trim().toLowerCase();
  if (s.startsWith('result:')) return { kind: 'ran', name };
  if (s.startsWith('confirmed')) return { kind: 'outcome', name, outcome: 'confirmed' };
  if (s.startsWith('timed out') || s.startsWith('timeout') || s.startsWith('cancelled: preset timeout')) return { kind: 'outcome', name, outcome: 'timeout' };
  if (s.startsWith('cancelled') || s.startsWith('canceled') || s.startsWith('denied')) return { kind: 'outcome', name, outcome: 'cancelled' };
  return { kind: 'other', name };
}

export function outcomeLabel(outcome: Outcome): string {
  return outcome === 'confirmed' ? 'Confirmed' : outcome === 'timeout' ? 'Timeout' : 'Cancelled';
}

function stateLabel(state: ToolState): string {
  if (state === 'awaiting-approval') return 'Awaiting approval';
  if (state === 'awaiting-preset') return 'Awaiting preset';
  if (state === 'running') return 'Running';
  if (state === 'done') return 'Done';
  return outcomeLabel(state);
}

const AWAITING_MARKER = /\(awaiting (approval|preset) [^)]*\)/;

export function callInfo(text: string): { name: string; awaiting: 'approval' | 'preset' | null; display: string } {
  const m = AWAITING_MARKER.exec(text);
  const awaiting = m ? (m[1] === 'preset' ? 'preset' as const : 'approval' as const) : null;
  const display = (m ? text.replace(m[0], '') : text).trim();
  const name = (display.split(':')[0] ?? '').trim() || 'Tool';
  return { name, awaiting, display };
}

/** Resolves each tool_call to its state once its tool_result lands anywhere in the conversation. */
export function toolStates(all: TranscriptMessage[]): Map<string, ToolState> {
  const states = new Map<string, ToolState>();
  const open = new Map<string, string[]>();
  for (const m of all) {
    if (m.role === 'tool_call') {
      const info = callInfo(m.text);
      states.set(m.id, info.awaiting ? `awaiting-${info.awaiting}` : 'running');
      const list = open.get(info.name) ?? [];
      list.push(m.id);
      open.set(info.name, list);
    } else if (m.role === 'tool_result') {
      const c = classifyResult(m.text);
      const state: ToolState = c.kind === 'outcome' ? c.outcome : 'done';
      states.set(m.id, state);
      const list = open.get(c.name);
      if (list && list.length) {
        for (const id of list) states.set(id, state);
        open.delete(c.name);
      }
    }
  }
  return states;
}

async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch { /* WebView2 may block clipboard without a user-gesture grant */ }
  try {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.left = '-9999px';
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(area);
    return ok;
  } catch {
    return false;
  }
}

function StateChip({ state }: { state: ToolState }) {
  const color = state === 'confirmed' ? '#1f6b3a' : state === 'cancelled' ? '#8a1f11' : state === 'timeout' ? '#8a5a11' : '#5c5c5c';
  return (
    <span className="text-[10px] px-1.5 py-0.5 border shrink-0" style={{ color, borderColor: '#d0d0d0' }}>
      {stateLabel(state)}
    </span>
  );
}

function ToolCard({ call, result, state }: { call?: TranscriptMessage; result?: TranscriptMessage; state: ToolState | null }) {
  const full = result?.text ?? '';
  const long = full.length > PREVIEW_CHARS;
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const callText = call ? callInfo(call.text) : null;
  const name = callText?.name || (result ? splitToolText(result.text).name || 'Tool' : 'Tool');
  const shown = !full ? '' : open || !long ? full : `${full.slice(0, PREVIEW_CHARS)}…`;
  const statusLine = state === 'awaiting-approval' ? 'Waiting for your decision.'
    : state === 'awaiting-preset' ? 'Set the options to continue.'
      : state === 'running' ? 'Running…' : null;

  const onCopy = async () => {
    const ok = await copyText(full);
    setCopied(ok);
    window.setTimeout(() => setCopied(false), 1200);
  };

  return (
    <div style={{ ...rowStyle, background: '#fff' }}>
      <div className="flex items-center gap-1.5">
        <div style={{ ...kLabel, marginBottom: 0 }} className="flex-1 truncate">Tool · {name}</div>
        {state && <StateChip state={state} />}
      </div>
      {callText && <div className="font-mono text-[11px] break-all mt-1" style={{ color: '#5c5c5c' }}>{callText.display}</div>}
      {full ? (
        <>
          <div className="font-mono text-[11px] whitespace-pre-wrap break-all mt-1" style={{ color: '#3f3f3f' }}>{shown}</div>
          <div className="flex gap-1 mt-1.5">
            {long && (
              <button type="button" className={linkBtn} aria-expanded={open} onClick={() => setOpen(v => !v)}>
                {open ? 'Collapse' : 'Expand'}
              </button>
            )}
            <button type="button" className={linkBtn} onClick={onCopy}>{copied ? 'Copied' : 'Copy'}</button>
          </div>
        </>
      ) : statusLine ? (
        <div className="text-[11px] mt-1" style={{ color: '#6a6a6a' }}>{statusLine}</div>
      ) : null}
    </div>
  );
}

function TurnBlock({ role, messages }: { role: 'you' | 'assistant'; messages: TranscriptMessage[] }) {
  return (
    <div style={{ ...rowStyle, background: role === 'you' ? '#f7f7f7' : '#fff' }}>
      <div style={kLabel}>{role === 'you' ? 'You' : 'Agent'}</div>
      {messages.map(m => (
        <div key={m.id} className={messages.length > 1 ? 'mt-1' : undefined}>
          {role === 'you' ? (
            <div>
              {m.text && <div className="whitespace-pre-wrap break-words">{m.text}</div>}
              {m.attachments?.length ? (
                <div className="flex flex-wrap gap-1 mt-1">
                  {m.attachments.map(a => <img key={a.id} src={a.dataUrl} alt={a.name} style={{ maxWidth: 160, maxHeight: 100, objectFit: 'cover', border: '1px solid #d0d0d0' }} />)}
                </div>
              ) : null}
            </div>
          ) : (
            <div className="text-xs leading-relaxed"><ReactMarkdown>{m.text}</ReactMarkdown></div>
          )}
        </div>
      ))}
    </div>
  );
}

export function TranscriptSlice({ messages, allMessages }: { messages: TranscriptMessage[]; allMessages?: TranscriptMessage[] }) {
  if (messages.length === 0) return null;
  const states = toolStates(allMessages ?? messages);
  return (
    <>
      {groupTranscript(messages).map(item => {
        if (item.kind === 'tool') {
          // One chip per tool: a finished card carries its outcome, a call still
          // waiting on a result carries only its live state.
          const live = item.call ? states.get(item.call.id) ?? 'running' : 'running';
          const state = item.result ? states.get(item.result.id) ?? 'done' : LIVE_STATES.includes(live) ? live : null;
          return <ToolCard key={item.id} call={item.call} result={item.result} state={state} />;
        }
        if (item.kind === 'turn') return <TurnBlock key={item.id} role={item.role} messages={item.messages} />;
        const m = item.message;
        return (
          <div key={m.id} style={rowStyle}>
            <div style={kLabel}>{m.role === 'reasoning' ? 'Think' : m.role === 'trace' ? 'Trace' : 'System'}</div>
            <div className={m.role === 'trace' ? 'font-mono text-[11px] break-all whitespace-pre-wrap' : 'whitespace-pre-wrap text-xs break-words'} style={{ color: '#3f3f3f' }}>{m.text}</div>
          </div>
        );
      })}
    </>
  );
}
