import { describe, expect, it } from 'vitest';
import type { FromHost } from '../bridge';
import { SCENARIOS } from '../mock/scenarios';
import { initialState, reduce } from './reducer';
import { activeTurn, canSend, liveStatus, pendingReview, turnStats } from './selectors';
import type { PaneAction, PaneState, Turn } from './types';

function run(state: PaneState, actions: PaneAction[]): PaneState {
  return actions.reduce(reduce, state);
}

function host(msgs: FromHost[]): PaneAction[] {
  return msgs.map(msg => ({ type: 'host' as const, msg }));
}

/** Send the scenario prompt, then play its steps (and a gate branch when given). */
function play(name: string, branch?: 'allow' | 'deny', before?: PaneAction[]): PaneState {
  const sc = SCENARIOS[name];
  let s = run(initialState(), [{ type: 'send', text: sc.prompt, mode: 'build', at: 0 }]);
  s = run(s, host(sc.steps.map(x => x.msg)));
  if (before) s = run(s, before);
  if (branch && sc.gate) s = run(s, host(sc.gate[branch].map(x => x.msg)));
  return s;
}

function lastTurn(s: PaneState): Turn {
  for (let i = s.entries.length - 1; i >= 0; i--) {
    const e = s.entries[i];
    if (e.kind === 'turn') return e.turn;
  }
  throw new Error('no turn');
}

describe('reducer: reads', () => {
  it('L1: one finished step and the answer', () => {
    const s = play('l1');
    const t = lastTurn(s);
    expect(t.outcome).toBe('done');
    expect(t.steps).toHaveLength(1);
    expect(t.steps[0]).toMatchObject({ name: 'get_wall_metrics', state: 'done' });
    expect(t.steps[0].resultText).toContain('Total length: 78400 mm');
    expect(t.answer).toContain('14 walls');
    expect(s.running).toBe(false);
    expect(s.progress).toBeNull();
  });

  it('streaming: the answer grows, then settles', () => {
    const sc = SCENARIOS.streaming;
    let s = run(initialState(), [{ type: 'send', text: sc.prompt, mode: 'ask' }]);
    s = run(s, host(sc.steps.slice(0, 2).map(x => x.msg)));
    expect(lastTurn(s).answer).toBe('There are four levels: ');
    expect(lastTurn(s).answerStreaming).toBe(true);
    expect(s.running).toBe(true);
    s = run(s, host(sc.steps.slice(2).map(x => x.msg)));
    expect(lastTurn(s).answer).toBe('There are four levels: Level 1 to Level 4.');
    expect(lastTurn(s).answerStreaming).toBe(false);
    expect(lastTurn(s).outcome).toBe('done');
  });

  it('dry run: no review, one done step', () => {
    const t = lastTurn(play('dryRun'));
    expect(t.steps).toHaveLength(1);
    expect(t.steps[0].review).toBeUndefined();
    expect(t.steps[0].state).toBe('done');
  });
});

describe('reducer: L3 approval', () => {
  it('waits in review as one step, despite the approval and the awaiting tool_call', () => {
    const s = play('l3Approval');
    const t = lastTurn(s);
    expect(t.steps.map(x => x.name)).toEqual(['get_beams', 'split_beams_at_columns']);
    const step = t.steps[1];
    expect(step.state).toBe('review');
    expect(step.review?.kind).toBe('approval');
    expect(pendingReview(s)?.toolCallId).toBe('a1');
    expect(liveStatus(s)).toBe('Waiting for your review');
  });

  it('allow then result: running, then done', () => {
    let s = play('l3Approval', undefined, [{ type: 'decide', toolCallId: 'a1', decision: 'allowed' }]);
    expect(lastTurn(s).steps[1]).toMatchObject({ state: 'running' });
    expect(lastTurn(s).steps[1].review?.decided).toBe('allowed');
    expect(pendingReview(s)).toBeNull();
    s = run(s, host(SCENARIOS.l3Approval.gate!.allow.map(x => x.msg)));
    expect(lastTurn(s).steps[1].state).toBe('done');
    expect(lastTurn(s).outcome).toBe('done');
  });

  it('deny: the step is denied and the agent still answers', () => {
    const s = play('l3Approval', 'deny', [{ type: 'decide', toolCallId: 'a1', decision: 'denied' }]);
    const t = lastTurn(s);
    expect(t.steps[1].state).toBe('denied');
    expect(t.steps[1].review?.decided).toBe('denied');
    expect(t.outcome).toBe('done');
  });

  it('timeout', () => {
    const s = play('l3Timeout');
    expect(lastTurn(s).steps[0].state).toBe('timeout');
    expect(pendingReview(s)).toBeNull();
  });

  it('stop while in review cancels the step and the turn', () => {
    const s = run(play('l3Approval'), [{ type: 'stop' }]);
    const t = lastTurn(s);
    expect(t.steps[1].state).toBe('cancelled');
    expect(t.outcome).toBe('cancelled');
    expect(pendingReview(s)).toBeNull();
    expect(s.running).toBe(false);
  });
});

describe('reducer: Run form', () => {
  it('edits values, takes a pick, then the markerless call re-uses the step', () => {
    let s = play('l2Preset');
    expect(lastTurn(s).steps).toHaveLength(1);
    expect(pendingReview(s)?.values).toEqual({ parameter: 'Type', palette: 'heat' });
    s = run(s, [
      { type: 'presetValue', fieldId: 'palette', value: 'rainbow' },
      { type: 'presetPickStart', fieldId: 'elements' },
    ]);
    expect(pendingReview(s)?.pickBusy).toBe('elements');
    s = run(s, host([{ type: 'presetPickResult', toolCallId: 'p1', fieldId: 'elements', result: { ids: ['10', '11'] } }]));
    expect(pendingReview(s)?.values).toMatchObject({ palette: 'rainbow', elements: ['10', '11'] });
    expect(pendingReview(s)?.pickBusy).toBeNull();
    s = run(s, [{ type: 'decide', toolCallId: 'p1', decision: 'allowed' }]);
    s = run(s, host(SCENARIOS.l2Preset.gate!.allow.map(x => x.msg)));
    const t = lastTurn(s);
    expect(t.steps).toHaveLength(1);
    expect(t.steps[0]).toMatchObject({ state: 'done' });
    expect(t.steps[0].argsText).toContain('heat');
  });

  it('cancel', () => {
    const s = play('l2Preset', 'deny', [{ type: 'decide', toolCallId: 'p1', decision: 'cancelled' }]);
    expect(lastTurn(s).steps[0].state).toBe('cancelled');
  });

  it('a failed pick leaves a note and frees the pick', () => {
    let s = run(play('l2Preset'), [{ type: 'presetPickStart', fieldId: 'elements' }]);
    s = run(s, host([{ type: 'presetPickResult', toolCallId: 'p1', fieldId: 'elements', ok: false, error: 'No view' }]));
    expect(pendingReview(s)?.pickBusy).toBeNull();
    expect(lastTurn(s).notes[lastTurn(s).notes.length - 1]).toMatchObject({ kind: 'error', text: 'Pick failed: No view' });
  });
});

describe('reducer: errors and notes', () => {
  it('a tool argument error becomes an error note, not a nameless step', () => {
    const t = lastTurn(play('toolError'));
    expect(t.steps).toHaveLength(0);
    expect(t.notes[0]).toMatchObject({ kind: 'error' });
    expect(t.outcome).toBe('done');
  });

  it('a provider error ends the turn as an error', () => {
    const s = play('providerError');
    const t = lastTurn(s);
    expect(t.outcome).toBe('error');
    expect(t.notes[0]).toMatchObject({ kind: 'error', text: '401 Unauthorized: invalid API key' });
    expect(canSend(s)).toBe(true);
  });

  it('a trace outside a turn is a standalone note', () => {
    const s = run(initialState(), host([{ type: 'trace', text: 'loaded 7 plugins' }]));
    expect(s.entries[s.entries.length - 1]).toMatchObject({ kind: 'note', note: { kind: 'trace' } });
  });
});

describe('reducer: plan, history, clear, pane data', () => {
  it('plan: stored on the turn, todos mapped from its steps', () => {
    const t = lastTurn(play('plan'));
    expect(t.plan?.title).toBe('Cut MEP openings');
    expect(t.todos.map(x => x.status)).toEqual(['pending', 'pending']);
  });

  it('quota and context are kept', () => {
    let s = play('quotaLow');
    s = run(s, host([{ type: 'context', context: { selectionCount: 8, activeView: '3D - Structure', warningCount: 2 } }]));
    expect(s.quota?.remaining).toBe(7);
    expect(s.context?.selectionCount).toBe(8);
  });

  it('history rebuilds turns', () => {
    const s = run(initialState(), host([{ type: 'history', messages: [
      { role: 'user', text: 'Count walls' }, { role: 'assistant', text: '14 walls.' }, { role: 'user', text: 'And doors?' }, { role: 'assistant', text: '6 doors.' },
    ] }]));
    const turns = s.entries.filter(e => e.kind === 'turn');
    expect(turns).toHaveLength(2);
    expect(lastTurn(s)).toMatchObject({ userText: 'And doors?', answer: '6 doors.', outcome: 'done' });
  });

  it('clear leaves one note', () => {
    const s = run(play('l1'), [{ type: 'clear' }]);
    expect(s.entries).toEqual([{ kind: 'note', note: expect.objectContaining({ text: 'Cleared.' }) }]);
    expect(s.running).toBe(false);
  });

  it('plugins keep their load issues', () => {
    const s = run(initialState(), host([{ type: 'plugins', plugins: [], loadIssues: [{ folder: 'x', summary: 'bad' }] } as unknown as FromHost]));
    expect(s.loadIssues).toHaveLength(1);
  });
});

describe('selectors', () => {
  it('turnStats counts steps and time', () => {
    const t = lastTurn(play('l1'));
    expect(turnStats(t).steps).toBe(1);
  });

  it('activeTurn is the running turn only', () => {
    const sc = SCENARIOS.l1;
    const s = run(initialState(), [{ type: 'send', text: sc.prompt, mode: 'build' }]);
    expect(activeTurn(s)?.userText).toBe(sc.prompt);
    expect(canSend(s)).toBe(false);
    expect(activeTurn(play('l1'))).toBeNull();
  });
});
