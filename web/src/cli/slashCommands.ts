export type SlashCommand = {
  name: string;
  label: string;
  description: string;
  argsHint?: string;
  mode?: 'ask' | 'plan' | 'build';
};

export const SLASH_COMMANDS: SlashCommand[] = [
  { name: 'plan', label: '/plan', description: '只讀規劃，生成 Todo 待批准', argsHint: '<目標>', mode: 'plan' },
  { name: 'build', label: '/build', description: '可寫執行，自主調用工具', argsHint: '<目標>', mode: 'build' },
  { name: 'ask', label: '/ask', description: '只讀問答，不改模型', argsHint: '<問題>', mode: 'ask' },
  { name: 'clear', label: '/clear', description: '清空會話與 Todo' },
  { name: 'help', label: '/help', description: '顯示所有命令與 @提及' },
  { name: 'export', label: '/export', description: '快捷匯出', argsHint: 'ifc|dwg|pdf' },
  { name: 'isolate', label: '/isolate', description: '隔離當前選中', argsHint: '[類別]' },
  { name: 'hide', label: '/hide', description: '隱藏當前選中' },
  { name: 'resume', label: '/resume', description: '恢復上次會話', argsHint: '[checkpoint id]' },
  { name: 'checkpoints', label: '/checkpoints', description: '列出可恢復檢查點' },
  { name: 'history', label: '/history', description: '顯示會話歷史長度' },
  { name: 'models', label: '/models', description: '列出當前提供商模型' },
];

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
