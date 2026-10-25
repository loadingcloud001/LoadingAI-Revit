import { useEffect, useRef, useState } from 'react';
import { isBuiltinProvider, ProviderConfig } from '../bridge';
import { availableLevels, HostCaps, levelInfo } from '../lib/permissions';
import type { PermissionLevel } from '../state/types';
import { Icon, IconName } from '../ui/Icon';

const LEVEL_ICON: Record<PermissionLevel, IconName> = { readonly: 'eye', plan: 'list', confirm: 'shield', autoEdit: 'pencil', bypass: 'bolt' };

function usePopover() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc); };
  }, [open]);
  return { open, setOpen, ref };
}

export function PermissionPicker({ level, caps, onPick }: { level: PermissionLevel; caps: HostCaps; onPick: (l: PermissionLevel) => void }) {
  const { open, setOpen, ref } = usePopover();
  const info = levelInfo(level);
  const tone = info.tone === 'danger' ? 'tone-danger' : info.tone === 'change' ? 'tone-change' : '';
  return (
    <div ref={ref} style={{ position: 'relative', minWidth: 0 }}>
      <button type="button" className={`btn btn-sm ${tone}`} aria-haspopup="listbox" aria-expanded={open} title="Permission level (Ctrl+.)" onClick={() => setOpen(v => !v)} style={{ maxWidth: 170 }}>
        <Icon name={LEVEL_ICON[level]} size={12} /><span className="truncate">{info.label}</span><Icon name="chevronDown" size={11} />
      </button>
      {open && (
        <div className="popover" role="listbox" aria-label="Permission level" style={{ bottom: 28, left: 0, width: 250 }}>
          {availableLevels(caps).map(l => (
            <button key={l.id} type="button" role="option" aria-selected={l.id === level} className="menu-item" onClick={() => { setOpen(false); onPick(l.id); }}>
              <span className={l.tone === 'danger' ? 'tone-danger' : l.tone === 'change' ? 'tone-change' : 'muted'} style={{ paddingTop: 1 }}><Icon name={LEVEL_ICON[l.id]} size={14} /></span>
              <span className="grow">
                <span className={l.tone === 'danger' ? 'tone-danger' : l.tone === 'change' ? 'tone-change' : undefined}>{l.label}</span>
                <small>{l.hint}</small>
              </span>
              {l.id === level && <Icon name="check" size={13} />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function ModelPicker({ providers, active, onPick, onManage }: { providers: ProviderConfig[]; active: ProviderConfig | null; onPick: (id: string) => void; onManage: () => void }) {
  const { open, setOpen, ref } = usePopover();
  const usable = (p: ProviderConfig) => isBuiltinProvider(p.id) || !!p.apiKey || p.baseUrl.includes('localhost');
  return (
    <div ref={ref} style={{ position: 'relative', minWidth: 0 }}>
      <button type="button" className="btn btn-sm btn-ghost" aria-haspopup="listbox" aria-expanded={open} title={active ? `${active.name} · ${active.model}` : 'Choose a model'} onClick={() => setOpen(v => !v)} style={{ maxWidth: 130 }}>
        <span className="truncate">{active ? active.name : 'No model'}</span><Icon name="chevronDown" size={11} />
      </button>
      {open && (
        <div className="popover" role="listbox" aria-label="Model" style={{ bottom: 28, left: 0, width: 240 }}>
          {providers.map(p => (
            <button key={p.id} type="button" role="option" aria-selected={p.id === active?.id} className="menu-item" disabled={!usable(p)} onClick={() => { setOpen(false); onPick(p.id); }} style={{ opacity: usable(p) ? 1 : 0.55 }}>
              <span className="grow">
                <span>{p.name}</span>
                <small className="mono">{p.model}{usable(p) ? '' : ' · no key'}</small>
              </span>
              {p.id === active?.id && <Icon name="check" size={13} />}
            </button>
          ))}
          <div style={{ borderTop: '1px solid var(--border)', margin: '4px 0' }} />
          <button type="button" className="menu-item" onClick={() => { setOpen(false); onManage(); }}><Icon name="settings" size={13} />Manage models…</button>
        </div>
      )}
    </div>
  );
}
