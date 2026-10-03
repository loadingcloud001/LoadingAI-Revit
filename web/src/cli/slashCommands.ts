export type SlashCommand = {
  name: string;
  label: string;
  description: string;
  argsHint?: string;
  mode?: 'ask' | 'plan' | 'build';
};

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
