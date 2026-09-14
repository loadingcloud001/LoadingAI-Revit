import { PluginInfo, PluginLoadIssue, postToHost } from '../bridge';

const row = { display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '5px 8px', borderBottom: '1px solid #ececec', fontSize: 12 } as const;
const btn = 'px-2 py-1 border bg-white text-[11px]';

function tierStats(plugins: PluginInfo[], tier: string) {
  const tools = plugins.flatMap(p => p.tools).filter(t => t.tier === tier);
  const on = tools.filter(t => t.enabled).length;
  return { total: tools.length, on, allOn: tools.length > 0 && on === tools.length };
}

export function PluginShelf({ plugins, loadIssues = [] }: { plugins: PluginInfo[]; loadIssues?: PluginLoadIssue[] }) {
  if ((!plugins || plugins.length === 0) && loadIssues.length === 0) {
    return (
      <div style={{ padding: 8, fontSize: 11, color: '#6a6a6a' }}>
        No plugins. Core tools should appear after restart.
        <button type="button" className={`${btn} ml-2`} onClick={() => postToHost({ type: 'reloadPlugins' })}>Rescan</button>
      </div>
    );
  }

  const total = plugins.reduce((n, p) => n + p.tools.length, 0);
  const on = plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0);
  const l1 = tierStats(plugins, 'L1');
  const l2 = tierStats(plugins, 'L2');
  const l3 = tierStats(plugins, 'L3');
  const l3On = plugins.flatMap(p => p.tools).filter(t => t.tier === 'L3' && t.enabled).map(t => t.name);

  const toggleTier = (tier: string, allOn: boolean) =>
    postToHost({ type: 'setPluginTierEnabled', tier, enabled: !allOn });

  return (
    <div>
      <div className="px-2 py-1.5 text-[11px] flex items-center gap-2" style={{ borderBottom: '1px solid #d0d0d0' }}>
        <span className="font-semibold">Tools</span>
        <span className="text-[10px] text-muted-foreground">{on}/{total} on</span>
      </div>
      <div className="px-2 py-1.5 flex flex-wrap gap-1" style={{ borderBottom: '1px solid #d0d0d0' }}>
        <button type="button" className={btn} onClick={() => toggleTier('L1', l1.allOn)}>L1 {l1.allOn ? 'all off' : 'all on'} · {l1.on}/{l1.total}</button>
        <button type="button" className={btn} onClick={() => toggleTier('L2', l2.allOn)}>L2 {l2.allOn ? 'all off' : 'all on'} · {l2.on}/{l2.total}</button>
        <button type="button" className={btn} style={{ color: '#8a1f11' }} onClick={() => toggleTier('L3', l3.allOn)}>L3 {l3.allOn ? 'all off' : 'all on'} · {l3.on}/{l3.total}</button>
      </div>
      <div className="px-2 py-1 text-[10px]" style={{ color: '#6a6a6a', borderBottom: '1px solid #ececec' }}>
        Ticks apply to chat immediately. {l3On.length > 0 ? `L3 on (${l3On.length}): ${l3On.slice(0, 8).join(', ')}${l3On.length > 8 ? '…' : ''}.` : 'L3 (delete/move/create/save) default off.'}
      </div>
      {loadIssues.length > 0 && (
        <div className="px-2 py-1.5 text-[11px]" style={{ color: '#8a1f11', background: '#fff8f6', borderBottom: '1px solid #d0d0d0' }}>
          {loadIssues.map((issue, i) => (
            <div key={`${issue.pluginId || issue.folder}-${i}`} className="mb-1 last:mb-0">
              <div className="font-semibold">{issue.pluginId || issue.folder} failed to load</div>
              <div>{issue.summary}{issue.hint ? ` — ${issue.hint}` : ''}</div>
            </div>
          ))}
        </div>
      )}
      {plugins.map(p => {
        const allOn = p.tools.length > 0 && p.tools.every(t => t.enabled);
        const someOn = p.tools.some(t => t.enabled);
        return (
          <div key={p.id} style={{ borderBottom: '1px solid #d0d0d0' }}>
            <div style={{ ...row, background: '#f7f7f7' }}>
              <div className="min-w-0">
                <div className="truncate">{p.name} <span className="text-[10px] text-muted-foreground">{p.id}</span></div>
              </div>
              <label className="flex items-center gap-1 text-[11px] cursor-pointer shrink-0">
                <input
                  type="checkbox"
                  checked={allOn}
                  ref={el => { if (el) el.indeterminate = !allOn && someOn; }}
                  onChange={e => postToHost({ type: 'setPluginGroupEnabled', pluginId: p.id, enabled: e.target.checked })}
                />
                {allOn ? 'All on' : someOn ? 'Partial' : 'All off'}
              </label>
            </div>
            {p.tools.map(t => (
              <div key={t.name} style={{ ...row, background: t.enabled ? '#fff' : '#fafafa' }}>
                <div className="min-w-0 pr-2">
                  <div className="font-mono text-[11px] truncate">{t.name} <span style={{ color: t.tier === 'L3' ? '#8a1f11' : '#6a6a6a' }}>{t.tier}</span></div>
                </div>
                <label className="flex items-center gap-1 text-[11px] cursor-pointer shrink-0">
                  <input
                    type="checkbox"
                    checked={t.enabled}
                    onChange={e => postToHost({ type: 'setPluginEnabled', toolName: t.name, enabled: e.target.checked })}
                  />
                  {t.enabled ? 'On' : 'Off'}
                </label>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}
