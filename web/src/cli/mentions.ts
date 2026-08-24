export type Mention = {
  id: string;
  label: string;
  description: string;
  prefix: string;
};

// Static catalogue; HostPage will auto-inject runtime counts via context bar, but mentions provide stable shortcuts
export const MENTIONS: Mention[] = [
  { id: 'selection', label: '@selection', description: '當前選中構件', prefix: '@selection' },
  { id: 'activeView', label: '@activeView', description: '活動視圖', prefix: '@activeView' },
  { id: 'visible', label: '@visible', description: '當前視圖可見構件', prefix: '@visible' },
  { id: 'warnings', label: '@warnings', description: '模型警告', prefix: '@warnings' },
  { id: 'levels', label: '@levels', description: '標高與軸網', prefix: '@levels' },
  { id: 'sheets', label: '@sheets', description: '圖紙', prefix: '@sheets' },
  { id: 'rooms', label: '@rooms', description: '房間', prefix: '@rooms' },
  { id: 'walls', label: '@walls', description: '牆', prefix: '@walls' },
];

export function filterMentions(query: string): Mention[] {
  const q = query.toLowerCase().trim().replace(/^@/, '');
  if (!q) return MENTIONS;
  return MENTIONS.filter(m => m.id.includes(q) || m.label.toLowerCase().includes(q) || m.description.includes(q));
}

export function extractMentions(text: string): string[] {
  const re = /@([a-zA-Z_][a-zA-Z0-9_]*)/g;
  const out: string[] = [];
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    const id = m[1].toLowerCase();
    if (MENTIONS.some(x => x.id === id) && !out.includes(id)) out.push(id);
  }
  return out;
}
