import { describe, expect, it } from 'vitest';
import type { FromHost } from '../bridge';
import { SCENARIOS } from '../mock/scenarios';
import { initialState, reduce } from './reducer';
import { lastTurnOf, turnAnnouncement } from './selectors';
import type { PaneAction, PaneState, Turn } from './types';

function run(state: PaneState, actions: PaneAction[]): PaneState {
  return actions.reduce(reduce, state);
}
function host(msgs: FromHost[]): PaneAction[] {
  return msgs.map(msg => ({ type: 'host' as const, msg }));
}

/** Sends the scenario prompt, plays its steps, then the chosen gate branch. */
function play(name: string, branch?: 'allow' | 'deny' | 'edit'): PaneState {
  const sc = SCENARIOS[name];
  let s = run(initialState(), [{ type: 'send', text: sc.prompt, mode: 'build', at: 0 }]);
  if (sc.caps) s = run(s, host([{ type: 'host_caps', caps: sc.caps }]));
  s = run(s, host(sc.steps.map(x => x.msg)));
  if (branch && sc.gate?.[branch]) s = run(s, host(sc.gate[branch]!.map(x => x.msg)));
  return s;
}

function lastTurn(s: PaneState): Turn {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind === 'turn') return e.turn;
  }
  throw new Error('no turn');
}

describe('host capabilities and theme', () => {
  it('host_caps replaces the list and theme records Revit dark or light', () => {
    let s = run(initialState(), host([{ type: 'host_caps', caps: ['singleGate', 'levels'] }]));
    expect(s.caps).toEqual(['singleGate', 'levels']);
    expect(s.hostDark).toBeNull();
    s = run(s, host([{ type: 'theme', dark: false }]));
    expect(s.hostDark).toBe(false);
    s = run(s, host([{ type: 'theme', dark: true }]));
    expect(s.hostDark).toBe(true);
  });

  it('an older host announces nothing', () => {
    expect(initialState().caps).toEqual([]);
  });
});

describe('structured turns: the single in-chat review', () => {
  it('turn_start marks the pane turn structured and records level and host id', () => {
    const s = play('reviewL3');
    const t = lastTurn(s);
    expect(t).toMatchObject({ hostId: 'T1', level: 'confirm', structured: true, outcome: 'running' });
  });

  it('a tool_review opens one card on the step the host started', () => {
    const s = play('reviewL3');
    const t = lastTurn(s);
    expect(t.steps.map(x => x.name)).toEqual(['get_beams', 'split_beams_at_columns']);
    const step = t.steps[1];
    expect(step).toMatchObject({ callId: 'c2', state: 'review', tier: 'L3' });
    expect(step.review?.kind).toBe('card');
    expect(step.review?.card?.canSession).toBe(true);
    expect(s.pendingReview).toMatchObject({ toolCallId: 'c2', kind: 'card', stepId: step.id });
    expect(s.running).toBe(true);
  });

  it('allow: the step finishes with the host time, the receipt attaches, and only turn_end ends the turn', () => {
    let s = play('reviewL3');
    const allow = SCENARIOS.reviewL3.gate!.allow.map(x => x.msg);
    // tool_end, turn_changes, assistant: the answer is in but the turn is still open.
    s = run(s, host(allow.slice(0, 3)));
    let t = lastTurn(s);
    expect(t.steps[1]).toMatchObject({ state: 'done', outcome: 'ran', ms: 600 });
    expect(t.steps[1].review?.decided).toBe('allowed');
    expect(s.pendingReview).toBeNull();
    expect(t.answer).toContain('36 pieces');
    expect(t.changes).toMatchObject({ added: 28, modified: 8, deleted: 0 });
    expect(t.outcome).toBe('running');
    expect(s.running).toBe(true);
    s = run(s, host(allow.slice(3)));
    t = lastTurn(s);
    expect(t.outcome).toBe('done');
    expect(s.running).toBe(false);
  });

  it('deny: the step is denied and the turn still ends normally when nothing was written', () => {
    const s = play('reviewL3', 'deny');
    const t = lastTurn(s);
    expect(t.steps[1]).toMatchObject({ state: 'denied', outcome: 'denied' });
    expect(t.steps[1].resultText).toContain('only the Level 3 beams');
    expect(t.outcome).toBe('done');
    expect(t.rolledBack).toBeUndefined();
    expect(s.pendingReview).toBeNull();
  });

  it('edit inputs: the re-posted review replaces the card on the same step and the review stays open', () => {
    const s = play('reviewL3', 'edit');
    const t = lastTurn(s);
    expect(t.steps).toHaveLength(2);
    const card = t.steps[1].review!.card!;
    expect(card.edited).toBe(true);
    expect(card.summary).toContain('Split 5 beams');
    expect(t.steps[1].state).toBe('review');
    expect(s.pendingReview?.toolCallId).toBe('c2');
    expect(t.steps[1].argsText).toContain('Level 3');
  });

  it('a destructive denial rolls the turn back and ends it in error', () => {
    const s = play('reviewDestructive', 'deny');
    const t = lastTurn(s);
    expect(t.steps[0]).toMatchObject({ destructive: true, state: 'denied' });
    expect(t.outcome).toBe('error');
    expect(t.rolledBack).toBe(true);
  });

  it('a later failure rolls an earlier write back', () => {
    const s = play('rolledBack');
    const t = lastTurn(s);
    expect(t.steps.map(x => x.state)).toEqual(['done', 'failed']);
    expect(t.rolledBack).toBe(true);
    expect(t.outcome).toBe('error');
  });

  it('blocked by the permission level is a failed step, not a hung review', () => {
    const s = play('blocked');
    const t = lastTurn(s);
    expect(t.steps[0]).toMatchObject({ state: 'failed', outcome: 'blocked' });
    expect(s.pendingReview).toBeNull();
    expect(t.outcome).toBe('done');
  });

  it('a Run form review seeds its values, and a re-post keeps what the person typed', () => {
    let s = play('reviewForm');
    expect(s.pendingReview?.values).toMatchObject({ parameter: 'Type', palette: 'heat' });
    s = run(s, [{ type: 'presetValue', fieldId: 'parameter', value: 'Mark' }]);
    expect(s.pendingReview?.values.parameter).toBe('Mark');
    s = run(s, host([{ type: 'tool_review', review: SCENARIOS.reviewForm.steps[2].msg.type === 'tool_review' ? (SCENARIOS.reviewForm.steps[2].msg as Extract<FromHost, { type: 'tool_review' }>).review : (undefined as never) }]));
    expect(s.pendingReview?.values.parameter).toBe('Mark');
    expect(lastTurn(s).steps).toHaveLength(1);
  });

  it('the receipt records counts and per-category ids', () => {
    const s = play('receipt');
    const t = lastTurn(s);
    expect(t.changes).toMatchObject({ added: 3, modified: 39, deleted: 2 });
    expect(t.changes?.byCategory.map(c => c.category)).toEqual(['Walls', 'Doors']);
    expect(t.changes?.byCategory[0].ids).toHaveLength(42);
    expect(t.level).toBe('bypass');
  });

  it('the older text messages are ignored once the host speaks in structured turns', () => {
    let s = play('reviewL3');
    const before = lastTurn(s).steps.length;
    s = run(s, host([
      { type: 'tool_call', text: 'split_beams_at_columns: {} (awaiting approval c2)' },
      { type: 'tool_result', text: 'split_beams_at_columns result: duplicate' },
    ]));
    expect(lastTurn(s).steps).toHaveLength(before);
  });

  it('messages for an unknown call or turn change nothing', () => {
    const s = play('reviewL3');
    const same = run(s, host([
      { type: 'tool_end', toolCallId: 'nope', ok: true, outcome: 'ran', summary: '', text: '', ms: 1 },
      { type: 'turn_changes', turnId: 'nope', added: 1, modified: 0, deleted: 0, byCategory: [] },
    ]));
    expect(lastTurn(same).changes).toBeUndefined();
    expect(lastTurn(same).steps).toHaveLength(2);
  });

  it('a host turn the pane did not start still shows up', () => {
    const s = run(initialState(), host([
      { type: 'turn_start', turnId: 'H1', mode: 'build', level: 'confirm' },
      { type: 'tool_start', turnId: 'H1', toolCallId: 'k1', name: 'get_model_summary', tier: 'L1', destructive: false, args: '{}', startedAt: 1 },
    ]));
    expect(s.running).toBe(true);
    expect(lastTurn(s).steps[0]).toMatchObject({ callId: 'k1', state: 'running' });
  });

  it('Stop still ends the turn and clears the review at once', () => {
    let s = play('reviewL3');
    s = run(s, [{ type: 'stop' }]);
    expect(s.pendingReview).toBeNull();
    expect(lastTurn(s).outcome).toBe('cancelled');
    expect(lastTurn(s).steps[1].state).toBe('cancelled');
    // The host's own bare "Cancelled." afterwards adds nothing.
    const after = run(s, host([{ type: 'error', text: 'Cancelled.' }]));
    expect(after.entries).toHaveLength(s.entries.length);
  });
});

describe('errors and steering', () => {
  it('a bare Cancelled with no running turn is not an error note, other errors still are', () => {
    const idle = initialState();
    expect(run(idle, host([{ type: 'error', text: 'Cancelled.' }])).entries).toHaveLength(0);
    expect(run(idle, host([{ type: 'error', text: 'cancelled' }])).entries).toHaveLength(0);
    expect(run(idle, host([{ type: 'error', text: '401 Unauthorized' }])).entries).toHaveLength(1);
  });

  it('a delivered steer shows as a note in the turn; an undelivered one is left to the pane', () => {
    let s = play('steer');
    s = run(s, host([{ type: 'steer_ack', text: 'only walls please' }]));
    expect(lastTurn(s).notes).toHaveLength(0);
    s = run(s, host([{ type: 'steer_ack', text: 'only walls please', deliveredAt: 5 }]));
    expect(lastTurn(s).notes).toEqual([expect.objectContaining({ kind: 'steer', text: 'only walls please' })]);
  });
});

describe('settings from the host', () => {
  const base = { activeProviderId: 'a', providers: [] };
  const load = (settings: unknown) => run(initialState(), host([{ type: 'settings', settings } as unknown as FromHost])).settings!;

  it('carries the level, always-allow list, select-changed switch and instructions', () => {
    const s = load({ ...base, permissionLevel: 'autoEdit', alwaysAllowTools: ['set_parameter', 3, 'rename_view'], selectChangedAfterTurn: false, instructions: 'Use mm.' });
    expect(s).toMatchObject({ permissionLevel: 'autoEdit', alwaysAllowTools: ['set_parameter', 'rename_view'], selectChangedAfterTurn: false, instructions: 'Use mm.' });
  });

  it('an unknown level is dropped, and defaults are safe', () => {
    const s = load({ ...base, permissionLevel: 'god' });
    expect(s.permissionLevel).toBeUndefined();
    expect(s.alwaysAllowTools).toEqual([]);
    expect(s.selectChangedAfterTurn).toBe(true);
    expect(s.instructions).toBe('');
  });
});

describe('screen reader announcement', () => {
  it('says nothing while the turn runs, then what happened', () => {
    expect(turnAnnouncement(lastTurnOf(play('reviewL3')))).toBe('');
    expect(turnAnnouncement(lastTurnOf(play('reviewL3', 'allow')))).toBe('Finished. 36 elements changed.');
    expect(turnAnnouncement(lastTurnOf(play('blocked')))).toBe('Finished.');
    expect(turnAnnouncement(lastTurnOf(play('rolledBack')))).toBe('Rolled back. The changes were undone.');
    expect(turnAnnouncement(null)).toBe('');
  });

  it('a stopped or failed turn says so', () => {
    const stopped = reduce(play('reviewL3'), { type: 'stop' });
    expect(turnAnnouncement(lastTurnOf(stopped))).toBe('Stopped.');
    const failed = reduce(play('reviewL3'), { type: 'host', msg: { type: 'error', text: '401 Unauthorized' } });
    expect(turnAnnouncement(lastTurnOf(failed))).toBe('The turn failed.');
  });
});
