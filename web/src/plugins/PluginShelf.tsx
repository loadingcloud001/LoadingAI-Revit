import { PluginInfo, postToHost } from '../bridge';

const row = { display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '5px 8px', borderBottom: '1px solid #ececec', fontSize: 12 } as const;

export function PluginShelf({ plugins }: { plugins: PluginInfo[] }) {
  if (!plugins || plugins.length === 0) {
    return (
      <div style={{ padding: 8, fontSize: 11, color: '#6a6a6a' }}>
        沒有插件。Core 工具應在重啟後出現。
        <button type="button" className="px-2 py-1.5 border bg-white text-xs ml-2" onClick={() => postToHost({ type: 'reloadPlugins' })}>重掃</button>
      </div>
    );
  }

  const total = plugins.reduce((n, p) => n + p.tools.length, 0);
  const on = plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0);

  return (
    <div>
      <div className="px-2 py-1.5 text-[11px] flex items-center gap-2" style={{ borderBottom: '1px solid #d0d0d0' }}>
        <span className="font-semibold">工具</span>
        <span className="text-[10px] text-muted-foreground">{on}/{total} 開 · L3 預設關</span>
        <span className="flex-1" />
        <button type="button" className="px-2 py-1.5 border bg-white text-xs" onClick={() => postToHost({ type: 'listPlugins' })}>重新整理</button>
        <button type="button" className="px-2 py-1.5 border bg-white text-xs" onClick={() => postToHost({ type: 'reloadPlugins' })}>重掃磁碟</button>
      </div>
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
                {allOn ? '全開' : someOn ? '部分' : '全關'}
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
                  {t.enabled ? '開' : '關'}
                </label>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}
