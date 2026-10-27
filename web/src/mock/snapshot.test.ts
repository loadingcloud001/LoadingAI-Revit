import { describe, expect, it } from 'vitest';
import { SCENARIOS } from './scenarios';

describe('the snapshot scenario', () => {
  it('plays a capture_view step that carries a PNG data URL', () => {
    const step = SCENARIOS.snapshot.steps.find(x => x.msg.type === 'tool_end');
    expect(step?.msg.type).toBe('tool_end');
    const image = step?.msg.type === 'tool_end' ? step.msg.image : undefined;
    expect(image).toMatch(/^data:image\/png;base64,[A-Za-z0-9+/=]+$/);
  });

  it('starts the turn so the step has a host turn to live in', () => {
    expect(SCENARIOS.snapshot.steps[0].msg.type).toBe('turn_start');
  });
});
