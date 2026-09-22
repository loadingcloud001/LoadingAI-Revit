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

export function confirmOutcome(text: string): 'confirmed' | 'cancelled' | 'timeout' | null {
  const t = text.toLowerCase();
  if (t.includes('timeout') || t.includes('timed out')) return 'timeout';
  if (/\bcancel/.test(t) || t.includes('denied')) return 'cancelled';
  if (t.includes('result:')) return 'confirmed';
  return null;
}

function outcomeLabel(kind: NonNullable<ReturnType<typeof confirmOutcome>>): string {
  if (kind === 'confirmed') return 'Confirmed';
  if (kind === 'timeout') return 'Timeout';
  return 'Cancelled';
}

function toolName(text: string): string {
  const head = text.split(':')[0]?.trim() || text.trim();
  return head.replace(/\s+result$/i, '') || 'Tool';
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

function ToolCard({ call, result }: { call?: TranscriptMessage; result?: TranscriptMessage }) {
  const full = result?.text ?? '';
  const long = full.length > PREVIEW_CHARS;
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const name = toolName(call?.text || full);
  const outcome = full ? confirmOutcome(full) : null;
  const shown = !full ? '' : open || !long ? full : `${full.slice(0, PREVIEW_CHARS)}…`;

  const onCopy = async () => {
    const ok = await copyText(full);
    setCopied(ok);
    window.setTimeout(() => setCopied(false), 1200);
  };

  return (
    <div style={{ ...rowStyle, background: '#fff' }}>
      <div className="flex items-center gap-1.5">
        <div style={{ ...kLabel, marginBottom: 0 }} className="flex-1 truncate">Tool · {name}</div>
        {outcome && (
          <span className="text-[10px] px-1.5 py-0.5 border shrink-0" style={{ color: outcome === 'confirmed' ? '#1f6b3a' : '#8a1f11', borderColor: '#d0d0d0' }}>
            {outcomeLabel(outcome)}
          </span>
        )}
      </div>
      {call && <div className="font-mono text-[11px] break-all mt-1" style={{ color: '#5c5c5c' }}>{call.text}</div>}
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
      ) : (
        <div className="text-[11px] mt-1" style={{ color: '#6a6a6a' }}>Waiting for result…</div>
      )}
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

export function TranscriptSlice({ messages }: { messages: TranscriptMessage[] }) {
  if (messages.length === 0) return null;
  return (
    <>
      {groupTranscript(messages).map(item => {
        if (item.kind === 'tool') return <ToolCard key={item.id} call={item.call} result={item.result} />;
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
