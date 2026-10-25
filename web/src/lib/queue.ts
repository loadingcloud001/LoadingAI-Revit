import type { Turn } from '../state/types';

/** Follow-ups typed while a turn runs. Pure helpers; the pane owns the state. */
export type QueueItem = { id: string; text: string };
export type QueueState = { items: QueueItem[]; paused: boolean; reason?: string };

export const EMPTY_QUEUE: QueueState = { items: [], paused: false };

export function enqueue(q: QueueState, id: string, text: string): QueueState {
  const t = text.trim();
  return t ? { ...q, items: [...q.items, { id, text: t }] } : q;
}

export function removeItem(q: QueueState, id: string): QueueState {
  const items = q.items.filter(i => i.id !== id);
  return { ...q, items, paused: items.length ? q.paused : false, reason: items.length ? q.reason : undefined };
}

export function editItem(q: QueueState, id: string, text: string): QueueState {
  return { ...q, items: q.items.map(i => i.id === id ? { ...i, text } : i) };
}

/**
 * Called when a turn ends. Errors, Stop and denials pause the queue so the person can look
 * before the next request runs (Replit and Augment do the same).
 */
export function onTurnEnd(q: QueueState, turn: Turn): { queue: QueueState; send: QueueItem | null } {
  if (!q.items.length) return { queue: q, send: null };
  if (q.paused) return { queue: q, send: null };
  const denied = turn.steps.some(s => s.state === 'denied' || s.state === 'timeout');
  if (turn.outcome === 'error') return { queue: { ...q, paused: true, reason: 'Paused after an error' }, send: null };
  if (turn.outcome === 'cancelled') return { queue: { ...q, paused: true, reason: 'Paused after Stop' }, send: null };
  if (denied) return { queue: { ...q, paused: true, reason: 'Paused after a denied change' }, send: null };
  const [first, ...rest] = q.items;
  return { queue: { ...q, items: rest }, send: first };
}

export function resume(q: QueueState): QueueState {
  return { ...q, paused: false, reason: undefined };
}
