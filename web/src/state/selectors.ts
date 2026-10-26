import type { PaneState, PendingReview, Turn } from './types';

export function activeTurn(s: PaneState): Turn | null {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind === 'turn') return e.turn.outcome === 'running' ? e.turn : null;
  }
  return null;
}

export function pendingReview(s: PaneState): PendingReview | null {
  return s.pendingReview;
}

export function canSend(s: PaneState): boolean {
  return !s.running;
}

/** The most recent turn, running or not. */
export function lastTurnOf(s: PaneState): Turn | null {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind === 'turn') return e.turn;
  }
  return null;
}

/** What a screen reader hears when a turn ends. Empty while the turn runs. */
export function turnAnnouncement(t: Turn | null): string {
  if (!t || t.outcome === 'running') return '';
  if (t.rolledBack) return 'Rolled back. The changes were undone.';
  const base = t.outcome === 'error' ? 'The turn failed.' : t.outcome === 'cancelled' ? 'Stopped.' : 'Finished.';
  const c = t.changes;
  const total = c ? c.added + c.modified + c.deleted : 0;
  return total ? `${base} ${total} element${total === 1 ? '' : 's'} changed.` : base;
}

/** The one live status line shown while a turn runs. */
export function liveStatus(s: PaneState): string {
  if (s.pendingReview) return 'Waiting for your review';
  if (s.progress?.label) return s.progress.label;
  const t = activeTurn(s);
  const step = t?.steps.find(st => st.state === 'running');
  if (step) return `Running ${step.name}`;
  return 'Thinking…';
}

export function turnStats(t: Turn): { steps: number; ms: number } {
  const end = t.endedAt ?? t.steps.reduce((m, st) => Math.max(m, st.endedAt ?? st.startedAt), t.startedAt);
  return { steps: t.steps.length, ms: Math.max(0, end - t.startedAt) };
}
