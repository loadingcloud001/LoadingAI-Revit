import type { QuotaPayload } from '../bridge';
import type { StepState } from '../state/types';

/** "0.4s", "3.2s", "1m 05s". */
export function formatMs(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '';
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60000);
  const s = Math.round((ms % 60000) / 1000);
  return `${m}m ${String(s).padStart(2, '0')}s`;
}

/** First non-empty line of a tool result, trimmed for the step row. */
export function stepSummary(text: string | undefined, max = 90): string {
  const line = (text ?? '').split('\n').map(l => l.trim()).find(l => l.length > 0) ?? '';
  return line.length > max ? `${line.slice(0, max - 1)}…` : line;
}

export function stepStateLabel(state: StepState): string {
  switch (state) {
    case 'running': return 'running';
    case 'review': return 'needs review';
    case 'done': return 'done';
    case 'failed': return 'failed';
    case 'denied': return 'denied';
    case 'timeout': return 'timed out';
    case 'cancelled': return 'cancelled';
  }
}

/**
 * Turns a raw provider or host error into "what failed · what to try". Never shows a bare
 * "Error:" prefix. Unknown text is kept, trimmed, as the reason.
 */
export function errorCopy(raw: string): { title: string; detail: string; action?: 'settings' | 'retry' } {
  const text = raw.replace(/^error:\s*/i, '').trim();
  const lower = text.toLowerCase();
  if (/\b401\b|unauthori[sz]ed|invalid api key|incorrect api key/.test(lower))
    return { title: 'The model provider refused the API key', detail: 'Check the key in Settings → Models, or switch to the built-in model.', action: 'settings' };
  if (/\b429\b|rate limit|too many requests/.test(lower))
    return { title: 'The model provider is rate-limiting requests', detail: 'Wait a moment and retry, or switch model.', action: 'retry' };
  if (/daily limit|quota|requests\/day|no requests left/.test(lower))
    return { title: 'Free requests used up for today', detail: 'Add your own API key in Settings → Models, or try again after the reset.', action: 'settings' };
  if (/timed? ?out|timeout/.test(lower))
    return { title: 'The request timed out', detail: 'Retry. If it keeps happening, try a smaller request.', action: 'retry' };
  if (/network|fetch failed|connection|econn|dns|unreachable/.test(lower))
    return { title: 'Could not reach the model provider', detail: 'Check the network or the provider URL in Settings → Models.', action: 'retry' };
  if (/no provider|no api key|has no api key/.test(lower))
    return { title: 'No model is set up', detail: 'Pick the built-in model or add a key in Settings → Models.', action: 'settings' };
  return { title: 'Something went wrong', detail: text || 'No detail from the host.', action: 'retry' };
}

/** Calm quota line; null when there is nothing worth saying. */
export function quotaLine(q: QuotaPayload | null): { text: string; tone: 'muted' | 'change' | 'danger' } | null {
  if (!q) return null;
  if (q.ok === false) return { text: q.message ?? 'Free model unavailable', tone: 'danger' };
  if (q.remaining <= 0) return { text: 'Free requests used up today', tone: 'danger' };
  if (q.remaining <= 10) return { text: `Running low · ${q.remaining} left today`, tone: 'change' };
  return { text: `${q.remaining}/${q.limit} free today`, tone: 'muted' };
}

/** The Allow button names what will happen: first line of the host's summary, else the tool. */
export function effectLabel(summary: string | undefined, toolName: string, max = 28): string {
  const first = (summary ?? '').split('\n').map(l => l.trim()).find(Boolean);
  const base = first || `Run ${toolName}`;
  return base.length > max ? `${base.slice(0, max - 1)}…` : base;
}

/** "Colour the walls by type and…" for a folded past turn. */
export function turnTitle(text: string, max = 60): string {
  const t = text.replace(/\s+/g, ' ').trim();
  if (!t) return 'Agent';
  return t.length > max ? `${t.slice(0, max - 1)}…` : t;
}
