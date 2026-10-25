/**
 * Parsers for the host's text tool messages (`tool_call` / `tool_result`). The host posts these
 * strings today; the structured `tool_start` / `tool_end` messages planned in P3 make them a
 * fallback. Pure: no React, no window, no bridge.
 */

/** Write-confirm outcome. Matched only when the text itself is an outcome line. */
export type Outcome = 'confirmed' | 'cancelled' | 'timeout';

export type ResultClass =
  | { kind: 'outcome'; name: string; outcome: Outcome }
  | { kind: 'ran'; name: string }
  | { kind: 'other'; name: string };

// Host result lines are "{name} result: …" or "{name} <outcome line>". The name may
// contain spaces, so the split is anchored on the exact line endings the host posts
// and takes the shortest name that leaves one of those endings.
const RESULT_LINE = /^(.*?\S)\s+(result:.*|cancelled by user\.?|cancelled: preset timeout\.?|denied by user\.?|timed out\b.*|timeout\.?|confirmed\.?)$/is;

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

const AWAITING_MARKER = /\(awaiting (approval|preset) ([^)]*)\)/;

export function callInfo(text: string): { name: string; awaiting: 'approval' | 'preset' | null; awaitingId: string | null; display: string; args: string } {
  const m = AWAITING_MARKER.exec(text);
  const awaiting = m ? (m[1] === 'preset' ? 'preset' as const : 'approval' as const) : null;
  const display = (m ? text.replace(m[0], '') : text).trim();
  const colon = display.indexOf(':');
  const name = (colon >= 0 ? display.slice(0, colon) : display).trim() || 'Tool';
  const args = colon >= 0 ? display.slice(colon + 1).trim() : '';
  return { name, awaiting, awaitingId: m ? m[2].trim() : null, display, args };
}

/** The text after "result:" — what the tool returned. */
export function resultBody(text: string): string {
  const { rest } = splitToolText(text);
  return rest.toLowerCase().startsWith('result:') ? rest.slice('result:'.length).trim() : rest;
}
