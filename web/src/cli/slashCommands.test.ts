import { describe, expect, it } from 'vitest';
import { LOCAL_COMMANDS, PASSTHROUGH_COMMANDS, SLASH_COMMANDS } from './slashCommands';

describe('slash commands', () => {
  it('every advertised command has a handler', () => {
    for (const c of SLASH_COMMANDS) {
      const handled = !!c.mode || LOCAL_COMMANDS.includes(c.name) || PASSTHROUGH_COMMANDS.includes(c.name);
      expect(handled, `/${c.name} is advertised but nothing handles it`).toBe(true);
    }
  });
  it('does not advertise history or models', () => {
    const names = SLASH_COMMANDS.map(c => c.name);
    expect(names).not.toContain('history');
    expect(names).not.toContain('models');
  });
});
