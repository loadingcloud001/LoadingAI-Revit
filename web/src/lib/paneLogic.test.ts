import { describe, expect, it } from 'vitest';
import type { Turn } from '../state/types';
import { effectLabel, errorCopy, formatMs, quotaLine, stepSummary, turnTitle } from './copy';
import { autoApproveFor, availableLevels, modeFor, nextLevel, resolveLevel } from './permissions';
import { EMPTY_QUEUE, enqueue, onTurnEnd, removeItem, resume } from './queue';

const none = new Set<string>();

describe('permissions', () => {
  it('hides Auto-edit until the host supports it', () => {
    expect(availableLevels(none).map(l => l.id)).toEqual(['readonly', 'plan', 'confirm', 'bypass']);
    expect(availableLevels(new Set(['autoEdit'])).map(l => l.id)).toContain('autoEdit');
  });

  it('maps levels to modes; only Bypass turns the host switch on', () => {
    expect(modeFor('readonly')).toBe('ask');
    expect(modeFor('plan')).toBe('plan');
    expect(modeFor('confirm')).toBe('build');
    expect(modeFor('bypass')).toBe('build');
    expect(autoApproveFor('bypass')).toBe(true);
    expect(autoApproveFor('confirm')).toBe(false);
  });

  it('shows Bypass when the host switch is on, and never restores Bypass from local storage', () => {
    expect(resolveLevel('plan', true, none)).toBe('bypass');
    expect(resolveLevel('bypass', false, none)).toBe('confirm');
    expect(resolveLevel('plan', false, none)).toBe('plan');
    expect(resolveLevel('autoEdit', false, none)).toBe('confirm');
    expect(resolveLevel(null, false, none)).toBe('confirm');
  });

  it('cycles through the offered levels', () => {
    expect(nextLevel('confirm', none)).toBe('bypass');
    expect(nextLevel('bypass', none)).toBe('readonly');
  });
});

describe('copy', () => {
  it('formats durations', () => {
    expect(formatMs(420)).toBe('0.4s');
    expect(formatMs(65000)).toBe('1m 05s');
  });

  it('summarises the first line of a result', () => {
    expect(stepSummary("Walls: 14 on level 'Level 2'\nTotal length: 78400 mm")).toBe("Walls: 14 on level 'Level 2'");
  });

  it('turns provider errors into what failed and what to do', () => {
    expect(errorCopy('Error: 401 Unauthorized: invalid API key')).toMatchObject({ title: 'The model provider refused the API key', action: 'settings' });
    expect(errorCopy('LoadingAI daily limit reached (100 requests/day)').title).toBe('Free requests used up for today');
    expect(errorCopy('weird thing').detail).toBe('weird thing');
  });

  it('keeps quota calm until it runs low', () => {
    expect(quotaLine({ limit: 100, used: 3, remaining: 97 })).toEqual({ text: '97/100 free today', tone: 'muted' });
    expect(quotaLine({ limit: 100, used: 93, remaining: 7 })).toEqual({ text: 'Running low · 7 left today', tone: 'change' });
    expect(quotaLine({ limit: 100, used: 100, remaining: 0 })?.tone).toBe('danger');
  });

  it('names the effect on the Allow button', () => {
    expect(effectLabel('Split 8 beams into 36 pieces\nLevel 2', 'split')).toBe('Split 8 beams into 36 pieces');
    expect(effectLabel(undefined, 'delete_elements')).toBe('Run delete_elements');
    expect(effectLabel('A very long summary line that keeps on going', 'x').length).toBeLessThanOrEqual(28);
  });

  it('titles a folded turn', () => {
    expect(turnTitle('  Colour   the walls ')).toBe('Colour the walls');
  });
});

function turn(partial: Partial<Turn>): Turn {
  return { id: 't', mode: 'build', userText: '', steps: [], notes: [], todos: [], answer: '', answerStreaming: false, outcome: 'done', startedAt: 0, ...partial };
}

describe('queue', () => {
  it('sends the next item when a turn ends cleanly', () => {
    let q = enqueue(enqueue(EMPTY_QUEUE, 'a', 'Tag the beams'), 'b', 'Then dimension');
    const r = onTurnEnd(q, turn({}));
    expect(r.send?.text).toBe('Tag the beams');
    expect(r.queue.items.map(i => i.id)).toEqual(['b']);
  });

  it('pauses after an error, Stop, or a denied change', () => {
    const q = enqueue(EMPTY_QUEUE, 'a', 'next');
    expect(onTurnEnd(q, turn({ outcome: 'error' })).queue.paused).toBe(true);
    expect(onTurnEnd(q, turn({ outcome: 'cancelled' })).queue.reason).toBe('Paused after Stop');
    const denied = turn({ steps: [{ id: 's', name: 'x', argsText: '', state: 'denied', startedAt: 0 }] });
    const r = onTurnEnd(q, denied);
    expect(r.send).toBeNull();
    expect(onTurnEnd(resume(r.queue), turn({})).send?.text).toBe('next');
  });

  it('ignores blank text and unpauses when emptied', () => {
    expect(enqueue(EMPTY_QUEUE, 'a', '   ').items).toHaveLength(0);
    const q = { ...enqueue(EMPTY_QUEUE, 'a', 'x'), paused: true };
    expect(removeItem(q, 'a').paused).toBe(false);
  });
});
