import { useState } from 'react';
import { PluginInfo, PluginLoadIssue, postToHost } from '../bridge';
import { copyText } from '../lib/clipboard';
import { Icon } from '../ui/Icon';
import { canTrust, diagnostics, groupAriaLabel, issueLines, sourceBadge, splitIssues, toolAriaLabel, verifiedLabel } from './pluginView';

function tierStats(plugins: PluginInfo[], tier: string) {
  const tools = plugins.flatMap(p => p.tools).filter(t => t.tier === tier);
  const on = tools.filter(t => t.enabled).length;
  return { total: tools.length, on, allOn: tools.length > 0 && on === tools.length };
}

/**
 * Tool switches (view 'tools') and plugin health (view 'plugins'). Ticks post straight to the
 * host; there is no Save step.
 */
export function PluginShelf({ plugins, loadIssues = [], view = 'tools' }: { plugins: PluginInfo[]; loadIssues?: PluginLoadIssue[]; view?: 'tools' | 'plugins' }) {
  const [filter, setFilter] = useState('');
  const [copied, setCopied] = useState(false);
  const { failures, notices } = splitIssues(loadIssues);

  if ((!plugins || plugins.length === 0) && loadIssues.length === 0) {
    return (
      <div className="muted" style={{ fontSize: 12 }}>
        No plugins found. Core tools appear after a restart.
        <button type="button" className="btn btn-sm" style={{ marginLeft: 8 }} onClick={() => postToHost({ type: 'reloadPlugins' })}>Rescan</button>
      </div>
    );
  }

  if (view === 'plugins') {
    return (
      <div>
        <div className="row" style={{ marginBottom: 8 }}>
          <span className="muted grow">{plugins.length} loaded{failures.length ? ` · ${failures.length} failed to load` : ''}</span>
          <button type="button" className="btn btn-sm" onClick={() => postToHost({ type: 'reloadPlugins' })}><Icon name="refresh" size={12} />Rescan</button>
          <button type="button" className="btn btn-sm" onClick={async () => { setCopied(await copyText(diagnostics(plugins, loadIssues))); window.setTimeout(() => setCopied(false), 1200); }}><Icon name="copy" size={12} />{copied ? 'Copied' : 'Copy diagnostics'}</button>
        </div>
        {failures.map((issue, i) => (
          <div key={`${issue.pluginId || issue.folder}-${i}`} className="issue">
            <div style={{ fontWeight: 600 }}>{issue.pluginId || issue.folder} failed to load</div>
            <div>{issue.summary}{issue.hint ? ` — ${issue.hint}` : ''}</div>
            {canTrust(issue) && (
              <button type="button" className="btn btn-sm" style={{ marginTop: 6 }} onClick={() => postToHost({ type: 'trustPlugin', folder: issue.folder, assembly: issue.assembly })}>
                Trust this plugin…
              </button>
            )}
            <details>
              <summary style={{ cursor: 'pointer', fontSize: 11.5 }}>Details</summary>
              {issueLines(issue).map((line, j) => <div key={j} className="mono" style={{ fontSize: 10.5, wordBreak: 'break-all' }}>{line}</div>)}
            </details>
          </div>
        ))}
        {notices.map((issue, i) => (
          <div key={`${issue.pluginId || issue.folder}-n${i}`} className="note" style={{ margin: '0 0 6px', padding: 0 }}>
            <b style={{ color: 'var(--text)' }}>{issue.pluginId || issue.folder}</b>: {issue.summary}. {issue.detail}
          </div>
        ))}
        <div className="list">
          {plugins.map(p => {
            const badge = sourceBadge(p);
            return (
              <div key={p.id} className="list-row">
                <div className="grow">
                  <div className="truncate">{p.name} <span className="faint mono" style={{ fontSize: 10.5 }}>{p.id} {p.version}</span></div>
                  <div style={{ fontSize: 11 }} className={badge.tone === 'ok' ? 'tone-ok' : 'tone-change'}>{badge.label} · {verifiedLabel(p)} · {p.tools.length} tools</div>
                </div>
                {p.removable && <button type="button" className="btn btn-sm" onClick={() => postToHost({ type: 'removePlugin', pluginId: p.id })}>Remove</button>}
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  const total = plugins.reduce((n, p) => n + p.tools.length, 0);
  const on = plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0);
  const tiers = (['L1', 'L2', 'L3'] as const).map(tier => ({ tier, ...tierStats(plugins, tier) }));
  const l3On = plugins.flatMap(p => p.tools).filter(t => t.tier === 'L3' && t.enabled).map(t => t.name);
  const f = filter.toLowerCase().trim();
  const toggleTier = (tier: string, allOn: boolean) => postToHost({ type: 'setPluginTierEnabled', tier, enabled: !allOn });

  return (
    <div>
      <div className="row" style={{ marginBottom: 6, flexWrap: 'wrap' }}>
        <span className="muted">{on}/{total} on</span>
        <span className="grow" />
        {tiers.map(t => (
          <button key={t.tier} type="button" className={`btn btn-sm ${t.tier === 'L3' ? 'tone-change' : ''}`} onClick={() => toggleTier(t.tier, t.allOn)} title={`${t.allOn ? 'Switch off' : 'Switch on'} every ${t.tier} tool`}>
            {t.tier} {t.on}/{t.total} · {t.allOn ? 'all off' : 'all on'}
          </button>
        ))}
      </div>
      <div className="faint" style={{ fontSize: 11.5, marginBottom: 6 }}>
        Switches apply to chat immediately. L1 reads, L2 changes parameters and views, L3 deletes, moves, creates or saves.{' '}
        {l3On.length > 0 ? <span className="tone-change">L3 on ({l3On.length}): {l3On.slice(0, 6).join(', ')}{l3On.length > 6 ? '…' : ''}.</span> : 'L3 (delete/move/create/save) is off by default.'}
      </div>
      {failures.length > 0 && <div className="issue">{failures.length} plugin{failures.length === 1 ? '' : 's'} failed to load. See the Plugins tab.</div>}
      <input className="input" placeholder="Filter tools" value={filter} onChange={e => setFilter(e.target.value)} style={{ marginBottom: 6 }} aria-label="Filter tools" />
      {plugins.map(p => {
        const tools = p.tools.filter(t => !f || t.name.toLowerCase().includes(f) || t.description.toLowerCase().includes(f));
        if (!tools.length) return null;
        const allOn = p.tools.length > 0 && p.tools.every(t => t.enabled);
        const someOn = p.tools.some(t => t.enabled);
        return (
          <div key={p.id} className="list" style={{ marginBottom: 6 }}>
            <div className="list-row head">
              <span className="grow truncate" style={{ fontWeight: 600 }}>{p.name}</span>
              <label className="toggle">
                <input
                  type="checkbox"
                  aria-label={groupAriaLabel(p)}
                  checked={allOn}
                  ref={el => { if (el) el.indeterminate = !allOn && someOn; }}
                  onChange={e => postToHost({ type: 'setPluginGroupEnabled', pluginId: p.id, enabled: e.target.checked })}
                />
                {allOn ? 'All on' : someOn ? 'Some on' : 'All off'}
              </label>
            </div>
            {tools.map(t => (
              <div key={t.name} className="list-row" style={{ opacity: t.enabled ? 1 : 0.7 }}>
                <span className="grow truncate mono" style={{ fontSize: 11 }} title={t.description}>{t.name}</span>
                {t.destructive && <span className="badge badge-danger" title="Deletes or replaces elements, or writes a file. Always asks.">destructive</span>}
                <span className={`badge ${t.tier === 'L3' ? 'badge-change' : ''}`}>{t.tier}</span>
                <label className="toggle">
                  <input type="checkbox" aria-label={toolAriaLabel(t)} checked={t.enabled} onChange={e => postToHost({ type: 'setPluginEnabled', toolName: t.name, enabled: e.target.checked })} />
                </label>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}
