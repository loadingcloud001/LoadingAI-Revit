import { describe, expect, it } from 'vitest';
import { filterCommandsWith, LOCAL_COMMANDS, PASSTHROUGH_COMMANDS, playbookCommand, SLASH_COMMANDS } from './slashCommands';

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

describe('playbook commands', () => {
  it('lists playbooks after the built-in commands and matches by name', () => {
    const pbs = [{ name: 'model-qa', description: 'Check warnings', mode: 'build', source: 'starter', tools: [] }];
    const all = filterCommandsWith('', pbs);
    expect(all.findIndex(c => c.name === 'model-qa')).toBeGreaterThan(all.findIndex(c => c.name === 'checkpoints'));
    // Built-in commands still match on their description ("/ask ... the model"), and come first.
    const model = filterCommandsWith('model', pbs).map(c => c.name);
    expect(model[model.length - 1]).toBe('model-qa');
    expect(filterCommandsWith('warn', pbs).map(c => c.name)).toEqual(['model-qa']);
  });

  it('recognises a playbook command and splits its extra text', () => {
    const pbs = [{ name: 'model-qa', description: 'd', mode: 'build', source: 'user', tools: [] }];
    expect(playbookCommand('/model-qa only Level 2', pbs)).toEqual({ name: 'model-qa', extra: 'only Level 2', mode: 'build' });
    expect(playbookCommand('/plan x', pbs)).toBeNull();
    expect(playbookCommand('model-qa', pbs)).toBeNull();
  });
});
