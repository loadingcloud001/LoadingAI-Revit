import type { PluginInfo, PluginLoadIssue, PluginToolInfo } from '../bridge';

export type Badge = { label: string; tone: 'ok' | 'warn' };

/** Where a plugin's trust comes from, in words an engineer can act on. */
export function sourceBadge(p: Pick<PluginInfo, 'source'>): Badge {
  switch (p.source) {
    case 'shipped': return { label: 'Shipped', tone: 'ok' };
    case 'trusted': return { label: 'Trusted by you', tone: 'warn' };
    case 'dev': return { label: 'Dev folder', tone: 'warn' };
    default: return { label: 'Unknown source', tone: 'warn' };
  }
}

/** `verified` in plugin.json is the author's own claim, so only a shipped plugin earns the label. */
export function verifiedLabel(p: Pick<PluginInfo, 'source' | 'verified'>): 'Verified' | 'Unverified' {
  return p.source === 'shipped' && p.verified ? 'Verified' : 'Unverified';
}

export function toolAriaLabel(t: Pick<PluginToolInfo, 'name' | 'tier'>): string {
  return `Enable ${t.name} (${t.tier})`;
}

export function groupAriaLabel(p: Pick<PluginInfo, 'name'>): string {
  return `Enable every tool in ${p.name}`;
}

export function splitIssues(issues: PluginLoadIssue[]): { failures: PluginLoadIssue[]; notices: PluginLoadIssue[] } {
  return { failures: issues.filter(i => !i.notice), notices: issues.filter(i => i.notice) };
}

/** Only a refused DLL can be trusted; the host sets the code, the pane never reads the summary. */
export function canTrust(issue: Pick<PluginLoadIssue, 'code'>): boolean {
  return issue.code === 'allowlist-refusal';
}

/** Detail rows for one issue. The hash is shown for the engineer to read; the pane never sends it. */
export function issueLines(issue: PluginLoadIssue): string[] {
  const lines = [`Folder: ${issue.folder}`];
  if (issue.assembly) lines.push(`Assembly: ${issue.assembly}`);
  if (issue.sha256) lines.push(`SHA-256: ${issue.sha256}`);
  if (issue.detail) {
    lines.push(...issue.detail.split('\n').map(l => l.trimEnd()).filter(l => l.length > 0).slice(0, 12));
  }
  return lines;
}

/** Plain text for a bug report. */
export function diagnostics(plugins: PluginInfo[], issues: PluginLoadIssue[]): string {
  const out: string[] = ['LoadingAI plugins'];
  for (const p of plugins) {
    const on = p.tools.filter(t => t.enabled).length;
    out.push(`- ${p.id} ${p.version} [${sourceBadge(p).label}, ${verifiedLabel(p)}] ${on}/${p.tools.length} tools on - ${p.folderPath}`);
  }
  if (issues.length > 0) {
    out.push('', 'Load issues');
    for (const i of issues) {
      out.push(`- ${i.pluginId || i.folder}: ${i.summary}${i.hint ? ` (${i.hint})` : ''}`);
      for (const line of issueLines(i)) out.push(`    ${line}`);
    }
  }
  return out.join('\n');
}
