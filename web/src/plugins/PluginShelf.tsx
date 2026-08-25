import { PluginInfo } from '../bridge';
import { postToHost } from '../bridge';

export function PluginShelf({ plugins, onRefresh }: { plugins: PluginInfo[]; onRefresh?: () => void }) {
  if (!plugins || plugins.length === 0) {
    return (
      <div style={{ padding: 16, color: '#6b7280', fontSize: 13 }}>
        No plugins discovered. Core built-in tools should appear after restart.
        <div style={{ marginTop: 8 }}>
          <button onClick={() => postToHost({ type: 'reloadPlugins' })} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer' }}>Reload</button>
        </div>
      </div>
    );
  }

  const totalTools = plugins.reduce((n, p) => n + p.tools.length, 0);
  const enabledTools = plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0);

  return (
    <div style={{ padding: 12, display: 'flex', flexDirection: 'column', gap: 12, overflowY: 'auto' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div style={{ fontWeight: 700, fontSize: 13 }}>Plugins — MIT Opensource</div>
        <div style={{ fontSize: 11, color: '#6b7280' }}>{plugins.length} plugins · {enabledTools}/{totalTools} tools enabled</div>
      </div>

      <div style={{ display: 'flex', gap: 8 }}>
        <button onClick={() => postToHost({ type: 'listPlugins' })} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer', fontSize: 12 }}>Refresh</button>
        <button onClick={() => postToHost({ type: 'reloadPlugins' })} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer', fontSize: 12 }}>Rescan Disk</button>
        <span style={{ fontSize: 11, color: '#6b7280', alignSelf: 'center' }}>Drop DLL+manifest to %AppData%\LoadingAI\Plugins\&lt;id&gt;\ then Rescan (restart required for new DLLs)</span>
      </div>

      <div style={{ fontSize: 11, color: '#6b7280', background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 6, padding: 8 }}>
        Toggle = instant (no restart). L3 (red) defaults OFF. <span style={{ background: '#fef3c7', padding: '1px 4px', borderRadius: 4 }}>Unverified</span> = local drop not PR-reviewed.
      </div>

      {plugins.map(p => {
        const allEnabled = p.tools.every(t => t.enabled);
        const someEnabled = p.tools.some(t => t.enabled);
        return (
          <div key={p.id} style={{ border: '1px solid #e5e7eb', borderRadius: 8, background: '#fff', overflow: 'hidden' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '8px 12px', background: p.id === 'core' ? '#f9fafb' : '#fff' }}>
              <div>
                <div style={{ fontWeight: 600, fontSize: 13, display: 'flex', gap: 6, alignItems: 'center' }}>
                  {p.name} <span style={{ fontSize: 11, color: '#6b7280' }}>{p.id} v{p.version}</span>
                  {p.verified ? <span style={{ fontSize: 10, background: '#d1fae5', color: '#065f46', padding: '2px 6px', borderRadius: 10 }}>Verified</span>
                    : <span style={{ fontSize: 10, background: '#fef3c7', color: '#92400e', padding: '2px 6px', borderRadius: 10, border: '1px solid #fde68a' }}>Unverified</span>}
                  <span style={{ fontSize: 10, color: '#6b7280' }}>{p.license} · {p.author}</span>
                </div>
                <div style={{ fontSize: 11, color: '#6b7280' }}>{p.folderPath}</div>
              </div>
              <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 12, cursor: 'pointer' }}>
                  <input
                    type="checkbox"
                    checked={allEnabled}
                    ref={el => { if (el) el.indeterminate = !allEnabled && someEnabled; }}
                    onChange={e => postToHost({ type: 'setPluginGroupEnabled', pluginId: p.id, enabled: e.target.checked })}
                  />
                  {allEnabled ? 'All on' : someEnabled ? 'Partial' : 'All off'}
                </label>
                {p.id !== 'core' && (
                  <button onClick={() => { if (confirm(`Remove plugin ${p.id}? Requires restart.`)) postToHost({ type: 'removePlugin', pluginId: p.id }); }}
                    style={{ padding: '4px 8px', borderRadius: 6, border: '1px solid #fecaca', background: '#fff', color: '#dc2626', fontSize: 11, cursor: 'pointer' }}>Remove</button>
                )}
              </div>
            </div>

            <div style={{ borderTop: '1px solid #f3f4f6' }}>
              {p.tools.map(t => (
                <div key={t.name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '6px 12px', borderBottom: '1px solid #f3f4f6', background: t.enabled ? '#fff' : '#fafafa' }}>
                  <div style={{ flex: 1 }}>
                    <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                      <span style={{ fontFamily: 'monospace', fontSize: 12, fontWeight: 600 }}>{t.name}</span>
                      <span style={{
                        fontSize: 10, padding: '2px 6px', borderRadius: 10,
                        background: t.tier === 'L3' ? '#fee2e2' : t.tier === 'L2' ? '#fef3c7' : '#d1fae5',
                        color: t.tier === 'L3' ? '#991b1b' : t.tier === 'L2' ? '#92400e' : '#065f46',
                        border: '1px solid #e5e7eb'
                      }}>{t.tier}</span>
                      {!t.enabledByDefault && <span style={{ fontSize: 10, color: '#92400e' }}>default off</span>}
                    </div>
                    <div style={{ fontSize: 11, color: '#6b7280', marginTop: 2 }}>{t.description}</div>
                  </div>
                  <label style={{ display: 'flex', alignItems: 'center', gap: 6, cursor: 'pointer', marginLeft: 12 }}>
                    <input
                      type="checkbox"
                      checked={t.enabled}
                      onChange={e => postToHost({ type: 'setPluginEnabled', toolName: t.name, enabled: e.target.checked })}
                    />
                    <span style={{ fontSize: 12, color: t.enabled ? '#111827' : '#9ca3af' }}>{t.enabled ? 'Enabled' : 'Disabled'}</span>
                  </label>
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}
