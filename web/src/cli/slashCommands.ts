import type { PlaybookInfo } from '../bridge';

export type SlashCommand = {
  name: string;
  label: string;
  description: string;
  argsHint?: string;
  mode?: 'ask' | 'plan' | 'build';
};

/** The saved procedures the host knows about, as it sends them. */
export type { PlaybookInfo };

/** The chat mode a playbook may pin. The host sends the mode as a plain string. */
export type PlaybookMode = 'ask' | 'plan' | 'build';

/** The mode of a playbook, or null when it sets none (the level decides then). */
export function playbookMode(mode?: string | null): PlaybookMode | null {
  return mode === 'ask' || mode === 'plan' || mode === 'build' ? mode : null;
}

export const SLASH_COMMANDS: SlashCommand[] = [
  { name: 'plan', label: '/plan', description: 'Read-only plan, build a todo list', argsHint: '<goal>', mode: 'plan' },
  { name: 'build', label: '/build', description: 'Write mode, run tools', argsHint: '<goal>', mode: 'build' },
  { name: 'ask', label: '/ask', description: 'Read-only Q&A, do not change the model', argsHint: '<question>', mode: 'ask' },
  { name: 'clear', label: '/clear', description: 'Clear the session and todos' },
  { name: 'help', label: '/help', description: 'List commands and @mentions' },
  { name: 'export', label: '/export', description: 'Quick export', argsHint: 'ifc|dwg|pdf' },
  { name: 'isolate', label: '/isolate', description: 'Isolate current selection', argsHint: '[category]' },
  { name: 'hide', label: '/hide', description: 'Hide current selection' },
  { name: 'resume', label: '/resume', description: 'Resume last session', argsHint: '[checkpoint id]' },
  { name: 'checkpoints', label: '/checkpoints', description: 'List checkpoints' },
];

/** `clear`, `help` and `checkpoints` are handled in the pane (`checkpoints` posts `listCheckpoints`); `resume` is sent to the host as a send with slash 'resume' (no LLM call). */
export const LOCAL_COMMANDS = ['clear', 'help', 'checkpoints', 'resume'];
/** Sent to the model as natural language. */
export const PASSTHROUGH_COMMANDS = ['export', 'isolate', 'hide'];

export function findSlash(input: string): { cmd: SlashCommand | null; args: string; raw: string } {
  const s = input.trimStart();
  if (!s.startsWith('/')) return { cmd: null, args: '', raw: s };
  const m = s.match(/^\/([a-zA-Z_]+)\s*(.*)$/);
  if (!m) return { cmd: null, args: '', raw: s };
  const name = m[1].toLowerCase();
  const cmd = SLASH_COMMANDS.find(c => c.name === name) ?? null;
  return { cmd, args: (m[2] ?? '').trim(), raw: s };
}

export function filterCommands(query: string): SlashCommand[] {
  const q = query.toLowerCase().trim().replace(/^\//, '');
  if (!q) return SLASH_COMMANDS;
  return SLASH_COMMANDS.filter(c => c.name.includes(q) || c.description.toLowerCase().includes(q) || c.label.includes(q));
}

/**
 * The slash menu: the built-in commands first, then the person's playbooks. A playbook also
 * matches on its description, because its name is less known, and it never shadows a built-in
 * command.
 */
export function filterCommandsWith(query: string, playbooks: PlaybookInfo[]): SlashCommand[] {
  const builtIn = filterCommands(query);
  const q = query.toLowerCase().trim().replace(/^\//, '');
  const taken = new Set(SLASH_COMMANDS.map(c => c.name));
  const pbs = playbooks
    .filter(p => !taken.has(p.name) && (!q || p.name.includes(q) || p.description.toLowerCase().includes(q)))
    .map(p => ({
      name: p.name,
      label: `/${p.name}`,
      description: `Playbook · ${p.description}`,
      argsHint: '[note]',
      mode: playbookMode(p.mode) ?? undefined,
    }));
  return [...builtIn, ...pbs];
}

/**
 * A playbook command: `/name` with the rest of the line as the note for this run. A built-in
 * command is never a playbook, so `/plan` stays `/plan`.
 */
export function playbookCommand(input: string, playbooks: PlaybookInfo[]): { name: string; extra: string; mode: PlaybookMode | null } | null {
  const m = input.trimStart().match(/^\/([a-z0-9][a-z0-9-]*)\s*([\s\S]*)$/);
  if (!m || SLASH_COMMANDS.some(c => c.name === m[1])) return null;
  const pb = playbooks.find(p => p.name === m[1]);
  return pb ? { name: pb.name, extra: m[2].trim(), mode: playbookMode(pb.mode) } : null;
}
