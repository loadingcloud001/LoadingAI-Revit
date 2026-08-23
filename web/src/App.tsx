import { useEffect, useMemo, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import { AppSettings, PluginInfo, ProviderConfig, ProviderPreset, onHostMessage, postToHost } from './bridge';
import { PluginShelf } from './plugins/PluginShelf';
import { Search, ChevronDown, Check, Settings2, Trash2, Play, Copy, ExternalLink, Sparkles, Cloud, Cpu, Globe, Wrench, Loader2, PlugZap, Database, ShieldCheck } from 'lucide-react';
import 'open-model-selector/styles.css';
import { ModelSelector } from 'open-model-selector';

type Message = { id: string; role: 'you' | 'assistant' | 'trace' | 'system' | 'tool_call' | 'tool_result'; text: string };

function normalizeSettings(raw: any): AppSettings | null {
  if (!raw || typeof raw !== 'object') return null;
  const active = raw.activeProviderId ?? raw.ActiveProviderId ?? '';
  const provs = raw.providers ?? raw.Providers ?? [];
  if (!Array.isArray(provs)) return null;
  const providers: ProviderConfig[] = provs.map((p: any) => ({
    id: p.id ?? p.Id ?? Math.random().toString(36).slice(2, 10),
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKey: p.apiKey ?? p.ApiKey ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
  }));
  const plugins = raw.plugins ?? raw.Plugins ?? undefined;
  return { activeProviderId: active, providers, plugins };
}
function normalizePresets(raw: any): ProviderPreset[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((p: any) => ({
    id: p.id ?? p.Id ?? '',
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
    description: p.description ?? p.Description ?? '',
  }));
}

const GROUP_META: Record<string, { label: string; icon: any; color: string }> = {
  cloud: { label: 'Cloud', icon: Cloud, color: '#2563eb' },
  gateway: { label: 'Gateway', icon: Globe, color: '#7c3aed' },
  local: { label: 'Local', icon: Cpu, color: '#059669' },
  custom: { label: 'Custom', icon: Wrench, color: '#6b7280' },
};

function ProviderIcon({ group, icon, size = 14 }: { group?: string; icon?: string; size?: number }) {
  const g = (group ?? 'custom').toLowerCase();
  const IconComp = GROUP_META[g]?.icon ?? Wrench;
  // Use icon char if emoji-like, else lucide
  if (icon && icon.length <= 2 && !/^[a-z]/i.test(icon)) return <span style={{ fontSize: size, lineHeight: 1 }}>{icon}</span>;
  return <IconComp size={size} style={{ color: GROUP_META[g]?.color ?? '#6b7280' }} />;
}

export default function App() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const settingsRef = useRef<AppSettings | null>(null);
  const [catalog, setCatalog] = useState<ProviderPreset[]>([]);
  const [selectedProviderId, setSelectedProviderId] = useState<string>('');
  const [providerSearch, setProviderSearch] = useState('');
  const [selectorOpen, setSelectorOpen] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [editing, setEditing] = useState<ProviderConfig | null>(null);
  const [editForm, setEditForm] = useState({ name: '', baseUrl: '', model: '', apiKey: '', apiKind: 'openai' as string, group: 'custom' as string, icon: '＋' });
  const [editSearch, setEditSearch] = useState('');
  const [catalogFilter, setCatalogFilter] = useState('');
  const [testState, setTestState] = useState<{ id: string; ok?: boolean; message?: string; loading?: boolean } | null>(null);
  const [modelsMap, setModelsMap] = useState<Record<string, string[]>>({});
  const [modelsLoading, setModelsLoading] = useState<string | null>(null);

  const [messages, setMessages] = useState<Message[]>([
    { id: 'welcome', role: 'system', text: 'AI Chat ready. Select a provider, add your API key in Settings, then ask e.g. "How many walls are in this model?"' }
  ]);
  const [input, setInput] = useState('');
  const [isRunning, setIsRunning] = useState(false);
  const [status, setStatus] = useState('Ready');
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [activeTab, setActiveTab] = useState<'chat' | 'plugins'>('chat');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => { settingsRef.current = settings; }, [settings]);

  const streamedRef = useRef(false);
  const pendingDeltaRef = useRef<string>('');
  const idRef = useRef(0);
  const nextId = () => `m${++idRef.current}`;

  useEffect(() => {
    const handler = (msg: any) => {
      try {
        if (msg.type === 'settings') {
          const norm = normalizeSettings(msg.settings);
          if (!norm) { setStatus('Settings parse error'); return; }
          setSettings(norm);
          settingsRef.current = norm;
          setSelectedProviderId(norm.activeProviderId);
          const active = norm.providers.find(p => p.id === norm.activeProviderId) ?? norm.providers[0];
          if (active) setStatus(`Active: ${active.name} · ${active.model}`);
          if (editing) {
            const upd = norm.providers.find(p => p.id === editing.id);
            if (upd) { setEditing(upd); setEditForm({ name: upd.name, baseUrl: upd.baseUrl, model: upd.model, apiKey: upd.apiKey, apiKind: upd.apiKind ?? 'openai', group: upd.group ?? 'custom', icon: upd.icon ?? '＋' }); }
          }
        } else if (msg.type === 'catalog') {
          setCatalog(normalizePresets(msg.presets));
        } else if (msg.type === 'testResult') {
          setTestState({ id: msg.providerId ?? '', ok: !!msg.ok, message: String(msg.message ?? ''), loading: false });
          if (msg.ok) setStatus(`✓ ${msg.message ?? 'OK'}`);
          else setStatus(`✗ ${msg.message ?? 'Test failed'}`);
          if (msg.models && Array.isArray(msg.models) && msg.providerId) setModelsMap(m => ({ ...m, [msg.providerId]: msg.models }));
        } else if (msg.type === 'models') {
          setModelsLoading(null);
          if (msg.error) { setTestState({ id: msg.providerId, ok: false, message: msg.error, loading: false }); }
          else { setModelsMap(m => ({ ...m, [msg.providerId]: msg.models ?? [] })); setTestState({ id: msg.providerId, ok: true, message: `${(msg.models ?? []).length} models`, loading: false }); }
        } else if (msg.type === 'status') {
          setStatus(msg.text);
        } else if ((msg as any).type === 'plugins') {
          setPlugins(((msg as any).plugins as PluginInfo[]) ?? []);
        } else if (msg.type === 'trace') {
          setMessages(m => [...m, { id: nextId(), role: 'trace', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'tool_call') {
          streamedRef.current = false; pendingDeltaRef.current = '';
          setMessages(m => [...m, { id: nextId(), role: 'tool_call', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'tool_result') {
          setMessages(m => [...m, { id: nextId(), role: 'tool_result', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'assistant') {
          const incoming = String(msg.text ?? '');
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && streamedRef.current) {
              streamedRef.current = false; pendingDeltaRef.current = '';
              if (!incoming || last.text === incoming || incoming.startsWith(last.text) || last.text.startsWith(incoming)) {
                return incoming && last.text !== incoming ? [...m.slice(0, -1), { ...last, text: incoming.length >= last.text.length ? incoming : last.text }] : m;
              }
              return [...m.slice(0, -1), { ...last, text: incoming }];
            }
            streamedRef.current = false; pendingDeltaRef.current = '';
            if (!incoming) return m;
            return [...m, { id: nextId(), role: 'assistant', text: incoming }];
          });
          setIsRunning(false);
          const cur = settingsRef.current; const active = cur?.providers.find(p => p.id === cur.activeProviderId) ?? cur?.providers[0];
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'error') {
          setMessages(m => [...m, { id: nextId(), role: 'system', text: `Error: ${String(msg.text ?? '')}` }]);
          setIsRunning(false); streamedRef.current = false; pendingDeltaRef.current = '';
          const cur = settingsRef.current; const active = cur?.providers.find(p => p.id === cur.activeProviderId);
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'delta') {
          const chunk = String(msg.text ?? ''); if (!chunk) return;
          const starting = !streamedRef.current; streamedRef.current = true; pendingDeltaRef.current += chunk;
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && !starting) return [...m.slice(0, -1), { ...last, text: last.text + chunk }];
            return [...m, { id: nextId(), role: 'assistant', text: chunk }];
          });
          setIsRunning(true);
        }
      } catch (e: any) {
        setMessages(m => [...m, { id: nextId(), role: 'system', text: `UI error: ${e?.message ?? String(e)}` }]);
      }
    };
    const off = onHostMessage(handler as any);
    postToHost({ type: 'requestSettings' });
    postToHost({ type: 'getCatalog' } as any);
    postToHost({ type: 'listPlugins' } as any);
    const t = setTimeout(() => { postToHost({ type: 'requestSettings' }); postToHost({ type: 'getCatalog' } as any); postToHost({ type: 'listPlugins' } as any); }, 800);
    return () => { clearTimeout(t); off(); };
  }, [editing?.id]);

  useEffect(() => { scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' }); }, [messages, isRunning]);

  const providers = settings?.providers ?? [];
  const activeProvider = useMemo(() => providers.find(p => p.id === (selectedProviderId || settings?.activeProviderId)) ?? providers[0] ?? null, [providers, selectedProviderId, settings?.activeProviderId]);

  //Grouped providers for selector
  const grouped = useMemo(() => {
    const f = providerSearch.toLowerCase().trim();
    let list = providers;
    if (f) list = providers.filter(p => `${p.name} ${p.model} ${p.baseUrl} ${p.group}`.toLowerCase().includes(f));
    const groups: Record<string, ProviderConfig[]> = {};
    for (const p of list) {
      const g = (p.group ?? 'custom').toLowerCase();
      (groups[g] ??= []).push(p);
    }
    const order = ['cloud', 'gateway', 'local', 'custom'];
    return order.filter(g => groups[g]?.length).map(g => ({ group: g, items: groups[g] }));
  }, [providers, providerSearch]);

  const handleSend = () => {
    const text = input.trim(); if (!text || isRunning) return;
    const cur = settingsRef.current ?? settings;
    const active = cur?.providers.find(p => p.id === (selectedProviderId || cur?.activeProviderId)) ?? cur?.providers[0];
    if (!cur || !active) { setMessages(m => [...m, { id: nextId(), role: 'system', text: 'No provider configured. Open Settings to add one.' }]); setShowSettings(true); return; }
    if (!active.apiKey && !active.baseUrl.includes('localhost')) { setMessages(m => [...m, { id: nextId(), role: 'system', text: `Provider '${active.name}' has no API key. Open Settings and paste your key.` }]); setShowSettings(true); return; }
    if (selectedProviderId && selectedProviderId !== cur.activeProviderId) {
      const newSettings: AppSettings = { ...cur, activeProviderId: selectedProviderId };
      setSettings(newSettings); settingsRef.current = newSettings; postToHost({ type: 'saveSettings', settings: newSettings });
    }
    setMessages(m => [...m, { id: nextId(), role: 'you', text }]); setInput(''); setIsRunning(true); streamedRef.current = false; pendingDeltaRef.current = ''; setStatus('Working...'); postToHost({ type: 'send', text });
  };
  const handleStop = () => { postToHost({ type: 'cancel' }); setStatus('Cancelling...'); streamedRef.current = false; };
  const handleClear = () => { setMessages([{ id: 'cleared', role: 'system', text: 'Cleared.' }]); streamedRef.current = false; pendingDeltaRef.current = ''; postToHost({ type: 'clear' }); };
  const handleKeyDown = (e: React.KeyboardEvent) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleSend(); } };

  const selectForEdit = (p: ProviderConfig) => {
    setEditing(p);
    setEditForm({ name: p.name, baseUrl: p.baseUrl, model: p.model, apiKey: p.apiKey, apiKind: p.apiKind ?? 'openai', group: p.group ?? 'custom', icon: p.icon ?? '＋' });
    setTestState(null);
  };
  const handleAddFromPreset = (preset: ProviderPreset) => {
    const cur = settingsRef.current ?? settings; if (!cur) return;
    const exists = cur.providers.find(p => p.id === preset.id);
    const id = exists ? `${preset.id}-${Math.random().toString(36).slice(2, 4)}` : preset.id;
    const p: ProviderConfig = { id, name: preset.name, baseUrl: preset.baseUrl, model: preset.model, apiKey: '', apiKind: preset.apiKind, group: preset.group, icon: preset.icon };
    const newSettings: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(newSettings); settingsRef.current = newSettings; selectForEdit(p); postToHost({ type: 'saveSettings', settings: newSettings }); setCatalogFilter('');
  };
  const handleAddCustom = () => {
    const cur = settingsRef.current ?? settings; if (!cur) return;
    const p: ProviderConfig = { id: Math.random().toString(36).slice(2, 8), name: 'Custom', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'custom', icon: '＋' };
    const newSettings: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(newSettings); settingsRef.current = newSettings; selectForEdit(p); postToHost({ type: 'saveSettings', settings: newSettings });
  };
  const handleSave = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (!editForm.name.trim() || !editForm.baseUrl.trim() || !editForm.model.trim()) { setStatus('Name, Base URL and Model are required.'); return; }
    try { new URL(editForm.baseUrl.trim()); } catch { setStatus('Base URL is not a valid URL.'); return; }
    const updated: ProviderConfig = { ...editing, name: editForm.name.trim(), baseUrl: editForm.baseUrl.trim().replace(/\/$/, ''), model: editForm.model.trim(), apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    const newProviders = cur.providers.map(p => p.id === editing.id ? updated : p);
    const newSettings: AppSettings = { ...cur, providers: newProviders };
    setSettings(newSettings); settingsRef.current = newSettings; setEditing(updated); postToHost({ type: 'saveSettings', settings: newSettings }); setStatus(`Saved ${updated.name}`);
  };
  const handleDelete = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (cur.providers.length <= 1) { setStatus('Cannot delete the last provider.'); return; }
    const newProviders = cur.providers.filter(p => p.id !== editing.id);
    const newActive = cur.activeProviderId === editing.id ? newProviders[0].id : cur.activeProviderId;
    const newSettings: AppSettings = { ...cur, providers: newProviders, activeProviderId: newActive };
    setSettings(newSettings); settingsRef.current = newSettings; setSelectedProviderId(newActive); setEditing(null); postToHost({ type: 'saveSettings', settings: newSettings }); setStatus(`Deleted. Active: ${newProviders.find(p => p.id === newActive)?.name ?? newActive}`);
  };
  const handleUse = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    const newSettings: AppSettings = { ...cur, activeProviderId: editing.id };
    setSettings(newSettings); settingsRef.current = newSettings; setSelectedProviderId(editing.id); postToHost({ type: 'saveSettings', settings: newSettings }); setStatus(`Active: ${editing.name}`); setSelectorOpen(false);
  };
  const handleProviderChange = (id: string) => {
    setSelectedProviderId(id); setSelectorOpen(false);
    const cur = settingsRef.current ?? settings; if (cur) {
      const newSettings: AppSettings = { ...cur, activeProviderId: id };
      setSettings(newSettings); settingsRef.current = newSettings; postToHost({ type: 'saveSettings', settings: newSettings });
      const active = newSettings.providers.find(p => p.id === id); if (active) setStatus(`Active: ${active.name} · ${active.model}`);
    }
  };
  const handleTest = () => {
    if (!editing) return;
    const prov: ProviderConfig = { ...editing, name: editForm.name.trim() || editing.name, baseUrl: editForm.baseUrl.trim() || editing.baseUrl, model: editForm.model.trim() || editing.model, apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    setTestState({ id: prov.id, loading: true, message: 'Testing...' }); postToHost({ type: 'testProvider', provider: prov } as any);
  };
  const handleFetchModels = () => {
    if (!editing) return;
    const prov: ProviderConfig = { ...editing, name: editForm.name.trim() || editing.name, baseUrl: editForm.baseUrl.trim() || editing.baseUrl, model: editForm.model.trim() || editing.model, apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    setModelsLoading(prov.id); postToHost({ type: 'listModels', provider: prov } as any);
  };

  const filteredProvidersLeft = useMemo(() => {
    const f = editSearch.toLowerCase().trim();
    if (!f) return providers;
    return providers.filter(p => `${p.name} ${p.model} ${p.baseUrl}`.toLowerCase().includes(f));
  }, [providers, editSearch]);

  const catalogFiltered = useMemo(() => {
    const f = catalogFilter.toLowerCase().trim();
    if (!f) return catalog;
    return catalog.filter(c => `${c.name} ${c.model} ${c.description}`.toLowerCase().includes(f));
  }, [catalog, catalogFilter]);

  const editingModels = editing ? (modelsMap[editing.id] ?? []) : [];
  const showSettingsPanel = showSettings;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', fontFamily: 'system-ui, -apple-system, Segoe UI, sans-serif', background: '#fff', color: '#111827' }}>
      {/* Header: Provider Selector */}
      <div style={{ display: 'flex', gap: 6, padding: '8px 8px', borderBottom: '1px solid #e5e7eb', alignItems: 'center', background: '#ffffff' }}>
        <div style={{ flex: 1, position: 'relative' }}>
          <button onClick={() => setSelectorOpen(v => !v)} style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '7px 10px', borderRadius: 10, border: '1px solid #d1d5db', background: selectorOpen ? '#f9fafb' : '#fff', cursor: 'pointer', textAlign: 'left', boxShadow: selectorOpen ? '0 2px 8px rgba(0,0,0,0.07)' : 'none' }}>
            {activeProvider ? (
              <>
                <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: 22, height: 22, borderRadius: 6, background: GROUP_META[(activeProvider.group ?? 'custom').toLowerCase()]?.color ? `${GROUP_META[(activeProvider.group ?? 'custom').toLowerCase()].color}14` : '#f3f4f6', border: `1px solid ${GROUP_META[(activeProvider.group ?? 'custom').toLowerCase()]?.color ?? '#e5e7eb'}30`, flexShrink: 0 }}>
                  <ProviderIcon group={activeProvider.group} icon={activeProvider.icon} size={12} />
                </span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 12.5, fontWeight: 650, lineHeight: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{activeProvider.name}</span>
                  <span style={{ display: 'block', fontSize: 11, color: '#6b7280', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{activeProvider.model} · {activeProvider.apiKind ?? 'openai'}</span>
                </span>
                <span title={activeProvider.apiKey ? 'Key set' : activeProvider.baseUrl.includes('localhost') ? 'Local — no key needed' : 'No key'} style={{ width: 8, height: 8, borderRadius: 999, background: activeProvider.apiKey ? '#10b981' : activeProvider.baseUrl.includes('localhost') ? '#f59e0b' : '#ef4444', border: '1.5px solid #fff', boxShadow: '0 0 0 1px rgba(0,0,0,0.08)', flexShrink: 0 }} />
                <ChevronDown size={14} style={{ color: '#9ca3af', transform: selectorOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
              </>
            ) : (
              <span style={{ color: '#9ca3af', fontSize: 13 }}>{settings ? 'No providers' : 'Loading...'}</span>
            )}
          </button>

          {selectorOpen && (
            <div style={{ position: 'absolute', top: 'calc(100% + 6px)', left: 0, right: 0, background: '#fff', border: '1px solid #e5e7eb', borderRadius: 12, boxShadow: '0 12px 32px rgba(0,0,0,0.12)', zIndex: 50, overflow: 'hidden' }}>
              <div style={{ padding: 8, borderBottom: '1px solid #f3f4f6', display: 'flex', gap: 6, alignItems: 'center' }}>
                <Search size={14} style={{ color: '#9ca3af', marginLeft: 6 }} />
                <input autoFocus value={providerSearch} onChange={e => setProviderSearch(e.target.value)} placeholder="Search providers, models, URL..." style={{ flex: 1, border: 'none', outline: 'none', fontSize: 13, background: 'transparent' }} />
                {providerSearch && <button onClick={() => setProviderSearch('')} style={{ fontSize: 11, color: '#6b7280', background: '#f3f4f6', border: 'none', borderRadius: 6, padding: '2px 6px', cursor: 'pointer' }}>Clear</button>}
              </div>
              <div style={{ maxHeight: 280, overflowY: 'auto' }}>
                {grouped.length === 0 ? (
                  <div style={{ padding: 16, textAlign: 'center', color: '#9ca3af', fontSize: 12 }}>No matches</div>
                ) : grouped.map(g => (
                  <div key={g.group}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '6px 10px 4px', fontSize: 10, fontWeight: 700, letterSpacing: 0.5, textTransform: 'uppercase', color: GROUP_META[g.group]?.color ?? '#6b7280', background: '#fafafa', borderTop: g.group !== grouped[0].group ? '1px solid #f3f4f6' : 'none' }}>
                      {(() => { const I = GROUP_META[g.group].icon; return <I size={10} />; })()} {GROUP_META[g.group].label} · {g.items.length}
                    </div>
                    {g.items.map(p => {
                      const isActive = p.id === (selectedProviderId || settings?.activeProviderId);
                      return (
                        <button key={p.id} onClick={() => handleProviderChange(p.id)} style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px', border: 'none', background: isActive ? '#eff6ff' : '#fff', cursor: 'pointer', textAlign: 'left', borderBottom: '1px solid #f9fafb' }}>
                          <span style={{ width: 24, height: 24, borderRadius: 6, background: isActive ? '#dbeafe' : '#f9fafb', border: `1px solid ${isActive ? '#bfdbfe' : '#f3f4f6'}`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
                            <ProviderIcon group={p.group} icon={p.icon} size={12} />
                          </span>
                          <span style={{ flex: 1, minWidth: 0 }}>
                            <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                              <span style={{ fontSize: 12.5, fontWeight: isActive ? 700 : 500, color: '#111827', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{p.name}</span>
                              {isActive && <span style={{ background: '#2563eb', color: '#fff', fontSize: 9, fontWeight: 700, padding: '1px 5px', borderRadius: 999 }}>ACTIVE</span>}
                              {!p.apiKey && !p.baseUrl.includes('localhost') && <span title="No key" style={{ width: 6, height: 6, borderRadius: 999, background: '#ef4444' }} />}
                            </span>
                            <span style={{ display: 'block', fontSize: 11, color: '#6b7280', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{p.model}</span>
                          </span>
                          {isActive ? <Check size={14} style={{ color: '#2563eb' }} /> : <span style={{ fontSize: 10, color: '#9ca3af', border: '1px solid #e5e7eb', borderRadius: 4, padding: '1px 4px' }}>{p.apiKind ?? 'openai'}</span>}
                        </button>
                      );
                    })}
                  </div>
                ))}
                <div style={{ padding: 8, background: '#fafafa', borderTop: '1px solid #f3f4f6', display: 'flex', gap: 6 }}>
                  <button onClick={() => { setSelectorOpen(false); setShowSettings(true); }} style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '7px', borderRadius: 8, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer', fontSize: 12, fontWeight: 600 }}><Settings2 size={12} /> Manage providers</button>
                  <button onClick={() => { setSelectorOpen(false); handleAddCustom(); setShowSettings(true); }} style={{ padding: '7px 10px', borderRadius: 8, border: '1px dashed #d1d5db', background: '#fff', cursor: 'pointer', fontSize: 12 }}>+ New</button>
                </div>
              </div>
            </div>
          )}
        </div>
        <button onClick={() => setShowSettings(v => !v)} title="Providers & Settings" style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '7px 10px', borderRadius: 10, border: '1px solid #d1d5db', background: showSettings ? '#111827' : '#fff', color: showSettings ? '#fff' : '#111827', cursor: 'pointer', fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap' }}><Settings2 size={14} /> {showSettings ? 'Hide' : 'Providers'}</button>
        <button onClick={handleClear} title="Clear chat" style={{ padding: '7px 10px', borderRadius: 10, border: '1px solid #e5e7eb', background: '#fff', cursor: 'pointer', fontSize: 12, color: '#6b7280' }}>Clear</button>
      </div>

      <div style={{ padding: '4px 8px', fontSize: 11, color: isRunning ? '#2563eb' : '#6b7280', borderBottom: '1px solid #f3f4f6', minHeight: 18, display: 'flex', alignItems: 'center', gap: 6 }}>
        {isRunning ? <Loader2 size={11} style={{ animation: 'spin 1s linear infinite' } as any} /> : <span style={{ width: 6, height: 6, borderRadius: 999, background: activeProvider?.apiKey ? '#10b981' : '#f59e0b', display: 'inline-block' }} />}
        <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{isRunning ? 'Working…' : status}</span>
        {activeProvider && <span style={{ fontSize: 10, color: '#9ca3af', border: '1px solid #f3f4f6', borderRadius: 6, padding: '1px 6px', background: '#fafafa' }}>{activeProvider.baseUrl.replace(/^https?:\/\//, '').slice(0, 22)}</span>}
      </div>

      {/* Tabs */}
      <div style={{ display: 'flex', gap: 0, borderBottom: '1px solid #e5e7eb', background: '#fafafa' }}>
        <button onClick={() => setActiveTab('chat')} style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '8px', border: 'none', borderBottom: activeTab === 'chat' ? '2px solid #2563eb' : '2px solid transparent', background: activeTab === 'chat' ? '#fff' : '#fafafa', fontWeight: activeTab === 'chat' ? 700 : 400, cursor: 'pointer', fontSize: 12 }}>
          <Sparkles size={12} /> Chat {messages.length > 1 ? `· ${messages.length}` : ''}
        </button>
        <button onClick={() => { setActiveTab('plugins'); postToHost({ type: 'listPlugins' } as any); }} style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '8px', border: 'none', borderBottom: activeTab === 'plugins' ? '2px solid #2563eb' : '2px solid transparent', background: activeTab === 'plugins' ? '#fff' : '#fafafa', fontWeight: activeTab === 'plugins' ? 700 : 400, cursor: 'pointer', fontSize: 12 }}>
          <PlugZap size={12} /> Plugins {plugins.length ? `· ${plugins.length} · ${plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0)}/${plugins.reduce((n, p) => n + p.tools.length, 0)}` : ''} · MIT
        </button>
      </div>

      {showSettingsPanel && (
        <div style={{ borderBottom: '1px solid #e5e7eb', background: '#f9fafb', maxHeight: '55vh', display: 'flex', flexDirection: 'column' }}>
          <div style={{ padding: '10px 12px 8px', borderBottom: '1px solid #e5e7eb', background: '#fff' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{ fontWeight: 700, fontSize: 13, display: 'flex', alignItems: 'center', gap: 6 }}><Database size={14} /> Providers</span>
              <span style={{ fontSize: 11, color: '#6b7280', background: '#f3f4f6', borderRadius: 999, padding: '2px 7px' }}>{providers.length} configured</span>
              <span style={{ flex: 1 }} />
              <span style={{ fontSize: 10, color: '#9ca3af', display: 'flex', alignItems: 'center', gap: 4 }}><ShieldCheck size={10} /> DPAPI enc · %AppData%\RevitAiPoc\settings.json</span>
            </div>
          </div>

          {!settings ? (
            <div style={{ color: '#b45309', fontSize: 12, padding: 12, margin: 12, background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 10 }}>Loading settings...</div>
          ) : (
            <div style={{ display: 'flex', gap: 12, padding: 12, overflow: 'hidden', flex: 1, minHeight: 0 }}>
              {/* Left: list + catalog */}
              <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 10, minWidth: 0, overflow: 'hidden' }}>
                {/* Search */}
                <div style={{ position: 'relative' }}>
                  <Search size={12} style={{ position: 'absolute', left: 8, top: '50%', transform: 'translateY(-50%)', color: '#9ca3af' }} />
                  <input value={editSearch} onChange={e => setEditSearch(e.target.value)} placeholder="Filter configured..." style={{ width: '100%', padding: '6px 8px 6px 26px', borderRadius: 8, border: '1px solid #e5e7eb', fontSize: 12, outline: 'none', background: '#fff' }} />
                </div>
                <div style={{ border: '1px solid #e5e7eb', borderRadius: 10, overflow: 'hidden', background: '#fff', display: 'flex', flexDirection: 'column', maxHeight: 160 }}>
                  <div style={{ flex: 1, overflowY: 'auto' }}>
                    {filteredProvidersLeft.map(p => {
                      const isEditing = editing?.id === p.id;
                      const isActive = p.id === settings.activeProviderId;
                      return (
                        <button key={p.id} onClick={() => selectForEdit(p)} style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px', border: 'none', borderBottom: '1px solid #f9fafb', background: isEditing ? '#eff6ff' : isActive ? '#f0fdf4' : '#fff', cursor: 'pointer', textAlign: 'left' }}>
                          <span style={{ width: 26, height: 26, borderRadius: 7, background: isEditing ? '#dbeafe' : '#f9fafb', border: `1px solid ${isEditing ? '#bfdbfe' : '#f3f4f6'}`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
                            <ProviderIcon group={p.group} icon={p.icon} size={13} />
                          </span>
                          <span style={{ flex: 1, minWidth: 0 }}>
                            <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                              <span style={{ fontSize: 12.5, fontWeight: isEditing ? 700 : 500, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{p.name}</span>
                              {isActive && <span style={{ width: 6, height: 6, borderRadius: 999, background: '#10b981' }} title="Active" />}
                              {!p.apiKey && !p.baseUrl.includes('localhost') && <span style={{ fontSize: 9, color: '#ef4444', border: '1px solid #fecaca', background: '#fef2f2', borderRadius: 4, padding: '0 4px' }}>no key</span>}
                            </span>
                            <span style={{ display: 'block', fontSize: 11, color: '#6b7280', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{p.model}</span>
                          </span>
                          <span style={{ fontSize: 10, color: '#9ca3af' }}>{p.group}</span>
                        </button>
                      );
                    })}
                    {filteredProvidersLeft.length === 0 && <div style={{ padding: 12, textAlign: 'center', color: '#9ca3af', fontSize: 11 }}>No providers match filter</div>}
                  </div>
                  <button onClick={handleAddCustom} style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '7px', borderTop: '1px solid #f3f4f6', borderLeft: 'none', borderRight: 'none', borderBottom: 'none', background: '#fafafa', cursor: 'pointer', fontSize: 12, fontWeight: 600, color: '#374151' }}>+ Custom provider</button>
                </div>

                {/* Preset catalog */}
                <div style={{ border: '1px solid #e5e7eb', borderRadius: 10, background: '#fff', overflow: 'hidden', display: 'flex', flexDirection: 'column', flex: 1, minHeight: 140 }}>
                  <div style={{ padding: '7px 10px', fontSize: 11, fontWeight: 700, color: '#374151', background: '#fafafa', borderBottom: '1px solid #f3f4f6', display: 'flex', alignItems: 'center', gap: 6 }}>
                    <Globe size={12} /> Quick add — presets
                    <span style={{ flex: 1 }} />
                    <span style={{ position: 'relative', display: 'flex', alignItems: 'center' }}>
                      <Search size={10} style={{ position: 'absolute', left: 6, color: '#9ca3af' }} />
                      <input value={catalogFilter} onChange={e => setCatalogFilter(e.target.value)} placeholder="Filter presets..." style={{ padding: '3px 6px 3px 20px', borderRadius: 6, border: '1px solid #e5e7eb', fontSize: 11, outline: 'none', width: 110 }} />
                    </span>
                  </div>
                  <div style={{ padding: 8, display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6, overflowY: 'auto', flex: 1, alignContent: 'start' }}>
                    {catalogFiltered.map(c => {
                      const already = providers.some(p => p.id === c.id);
                      return (
                        <button key={c.id} onClick={() => handleAddFromPreset(c)} disabled={already && catalogFiltered.length > 1} title={already ? 'Already added — will create copy' : c.description} style={{ display: 'flex', flexDirection: 'column', gap: 4, padding: '8px 8px', borderRadius: 8, border: '1px solid #e5e7eb', background: already ? '#f9fafb' : '#fff', cursor: 'pointer', textAlign: 'left', opacity: already ? 0.7 : 1 }}>
                          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                            <span style={{ width: 22, height: 22, borderRadius: 6, background: `${GROUP_META[c.group]?.color ?? '#6b7280'}14`, border: `1px solid ${GROUP_META[c.group]?.color ?? '#e5e7eb'}30`, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                              <ProviderIcon group={c.group} icon={c.icon} size={11} />
                            </span>
                            <span style={{ fontSize: 12, fontWeight: 650 }}>{c.name}</span>
                            {already && <span style={{ marginLeft: 'auto', fontSize: 9, color: '#6b7280', background: '#f3f4f6', borderRadius: 4, padding: '0 4px' }}>added</span>}
                          </span>
                          <span style={{ fontSize: 10.5, color: '#6b7280', lineHeight: 1.3, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.model}</span>
                          <span style={{ fontSize: 10, color: '#9ca3af', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.description}</span>
                        </button>
                      );
                    })}
                    {catalogFiltered.length === 0 && <div style={{ gridColumn: '1 / -1', padding: 12, textAlign: 'center', color: '#9ca3af', fontSize: 11 }}>No presets match</div>}
                  </div>
                  <div style={{ padding: '6px 8px', fontSize: 10, color: '#9ca3af', borderTop: '1px solid #f3f4f6', background: '#fafafa' }}>Hermes-style: add via preset keeps baseUrl/apiKind/group correct. Local presets (Ollama/LM Studio) need no key.</div>
                </div>
              </div>

              {/* Right: edit form */}
              <div style={{ flex: 1.3, display: 'flex', flexDirection: 'column', gap: 8, minWidth: 0 }}>
                {editing ? (
                  <div style={{ border: '1px solid #e5e7eb', borderRadius: 10, background: '#fff', padding: 10, display: 'flex', flexDirection: 'column', gap: 8, overflowY: 'auto' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <span style={{ width: 28, height: 28, borderRadius: 8, background: `${GROUP_META[editForm.group]?.color ?? '#6b7280'}14`, border: `1px solid ${GROUP_META[editForm.group]?.color ?? '#e5e7eb'}30`, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                        <ProviderIcon group={editForm.group} icon={editForm.icon} size={14} />
                      </span>
                      <span style={{ fontWeight: 700, fontSize: 13 }}>{editing.name}</span>
                      <span style={{ fontSize: 11, color: '#6b7280', background: '#f3f4f6', borderRadius: 6, padding: '2px 6px' }}>{editForm.group} · {editForm.apiKind}</span>
                      <span style={{ flex: 1 }} />
                      <span style={{ width: 8, height: 8, borderRadius: 999, background: editForm.apiKey ? '#10b981' : editForm.baseUrl.includes('localhost') ? '#f59e0b' : '#ef4444' }} title={editForm.apiKey ? 'Key set' : 'No key'} />
                    </div>

                    <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>Name
                      <input value={editForm.name} onChange={e => setEditForm(f => ({ ...f, name: e.target.value }))} placeholder="DeepSeek" style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, outline: 'none' }} />
                    </label>

                    <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>Base URL
                      <input value={editForm.baseUrl} onChange={e => setEditForm(f => ({ ...f, baseUrl: e.target.value }))} placeholder="https://api.deepseek.com" style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, outline: 'none', fontFamily: 'monospace' }} />
                    </label>

                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                      <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>API Kind
                        <select value={editForm.apiKind} onChange={e => setEditForm(f => ({ ...f, apiKind: e.target.value }))} style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, background: '#fff' }}>
                          <option value="openai">OpenAI Compatible</option>
                          <option value="responses">Responses (/responses)</option>
                          <option value="anthropic">Anthropic (messages)</option>
                        </select>
                      </label>
                      <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>Group
                        <select value={editForm.group} onChange={e => setEditForm(f => ({ ...f, group: e.target.value }))} style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, background: '#fff' }}>
                          <option value="cloud">Cloud</option>
                          <option value="gateway">Gateway</option>
                          <option value="local">Local</option>
                          <option value="custom">Custom</option>
                        </select>
                      </label>
                    </div>

                    <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>Model <span style={{ fontWeight: 400, color: '#9ca3af' }}>· use Fetch to list</span>
                        <button onClick={handleFetchModels} disabled={!!modelsLoading} style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 4, fontSize: 11, padding: '3px 7px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer' }}>
                          {modelsLoading === editing.id ? <Loader2 size={11} style={{ animation: 'spin 1s linear infinite' } as any} /> : <Database size={11} />} Fetch
                        </button>
                      </span>
                      <input value={editForm.model} onChange={e => setEditForm(f => ({ ...f, model: e.target.value }))} placeholder={editingModels.length ? editingModels[0] : 'deepseek-chat'} style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, outline: 'none', fontFamily: 'monospace' }} />
                    </label>

                    {editingModels.length > 0 && (
                      <div style={{ border: '1px solid #e5e7eb', borderRadius: 8, overflow: 'hidden', background: '#fafafa' }}>
                        <div style={{ padding: '6px 8px', fontSize: 10, fontWeight: 700, color: '#6b7280', display: 'flex', alignItems: 'center', gap: 6, borderBottom: '1px solid #f3f4f6' }}>
                          <Database size={10} /> Available models · {editingModels.length}
                          <button onClick={() => setModelsMap(m => { const c = { ...m }; delete c[editing.id]; return c; })} style={{ marginLeft: 'auto', fontSize: 10, color: '#6b7280', background: '#fff', border: '1px solid #e5e7eb', borderRadius: 4, padding: '1px 5px', cursor: 'pointer' }}>Hide</button>
                        </div>
                        <div style={{ maxHeight: 96, overflowY: 'auto', background: '#fff' }}>
                          {editingModels.slice(0, 50).map(mid => (
                            <button key={mid} onClick={() => setEditForm(f => ({ ...f, model: mid }))} style={{ width: '100%', textAlign: 'left', padding: '5px 8px', border: 'none', borderBottom: '1px solid #f9fafb', background: editForm.model === mid ? '#eff6ff' : '#fff', cursor: 'pointer', fontSize: 11.5, fontFamily: 'monospace', display: 'flex', alignItems: 'center', gap: 6 }}>
                              <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{mid}</span>
                              {editForm.model === mid && <Check size={11} style={{ color: '#2563eb' }} />}
                            </button>
                          ))}
                        </div>
                        {/* open-model-selector alternative: show fancy selector when many models */}
                        {editingModels.length > 8 && (
                          <div style={{ padding: 6, borderTop: '1px solid #f3f4f6', background: '#fafafa' }}>
                            <div style={{ fontSize: 10, color: '#6b7280', marginBottom: 4 }}>Or pick via polished selector (open-model-selector):</div>
                            <ModelSelector
                              models={editingModels.map(id => ({ id, name: id, provider: editing.name } as any))}
                              value={editForm.model}
                              onChange={(id: string) => setEditForm(f => ({ ...f, model: id }))}
                              placeholder="Search model..."
                              storageKey={`oms-${editing.id}`}
                            />
                          </div>
                        )}
                      </div>
                    )}

                    <label style={{ fontSize: 11, color: '#374151', display: 'flex', flexDirection: 'column', gap: 3, fontWeight: 600 }}>API Key
                      <input value={editForm.apiKey} onChange={e => setEditForm(f => ({ ...f, apiKey: e.target.value }))} type="password" placeholder={editForm.baseUrl.includes('localhost') ? 'ollama (or empty)' : 'sk-...'} style={{ padding: '7px 8px', borderRadius: 8, border: '1px solid #d1d5db', fontSize: 12, outline: 'none', fontFamily: 'monospace' }} />
                      <span style={{ fontSize: 10, color: '#9ca3af', fontWeight: 400 }}>{editForm.baseUrl.includes('localhost') ? 'Local endpoint can be empty or "ollama".' : 'Stored DPAPI encrypted at %AppData%\\RevitAiPoc\\settings.json'}</span>
                    </label>

                    {testState && testState.id === editing.id && (
                      <div style={{ padding: '7px 8px', borderRadius: 8, fontSize: 11, display: 'flex', alignItems: 'center', gap: 6, background: testState.loading ? '#eff6ff' : testState.ok ? '#f0fdf4' : '#fef2f2', border: `1px solid ${testState.loading ? '#bfdbfe' : testState.ok ? '#bbf7d0' : '#fecaca'}`, color: testState.loading ? '#1e40af' : testState.ok ? '#166534' : '#991b1b' }}>
                        {testState.loading ? <Loader2 size={12} style={{ animation: 'spin 1s linear infinite' } as any} /> : testState.ok ? <ShieldCheck size={12} /> : <span>✗</span>}
                        <span style={{ flex: 1, wordBreak: 'break-word' }}>{testState.message}</span>
                        {testState.ok && <Check size={12} />}
                      </div>
                    )}

                    <div style={{ display: 'flex', gap: 6, marginTop: 2 }}>
                      <button onClick={handleSave} style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, padding: '8px', borderRadius: 8, border: '1px solid #2563eb', background: '#2563eb', color: '#fff', cursor: 'pointer', fontWeight: 700, fontSize: 12 }}><Copy size={12} /> Save</button>
                      <button onClick={handleUse} style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '8px 10px', borderRadius: 8, border: '1px solid #10b981', background: '#10b981', color: '#fff', cursor: 'pointer', fontWeight: 700, fontSize: 12 }}><Play size={12} /> Use</button>
                      <button onClick={handleTest} disabled={testState?.loading} style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '8px 10px', borderRadius: 8, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer', fontSize: 12, whiteSpace: 'nowrap' }}>
                        {testState?.loading && testState.id === editing.id ? <Loader2 size={12} style={{ animation: 'spin 1s linear infinite' } as any} /> : <PlugZap size={12} />} Test
                      </button>
                    </div>
                    <div style={{ display: 'flex', gap: 6 }}>
                      <button onClick={() => {
                        const url = editForm.baseUrl; if (url) try { window.open(editForm.baseUrl, '_blank'); } catch { }
                        void url;
                      }} style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 4, padding: '6px', borderRadius: 8, border: '1px solid #e5e7eb', background: '#fafafa', cursor: 'pointer', fontSize: 11, color: '#6b7280' }}><ExternalLink size={11} /> Open Base URL</button>
                      <button onClick={handleDelete} style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '6px 10px', borderRadius: 8, border: '1px solid #fecaca', background: '#fff', color: '#ef4444', cursor: 'pointer', fontSize: 12 }}><Trash2 size={12} /> Delete</button>
                    </div>
                  </div>
                ) : (
                  <div style={{ flex: 1, border: '1px dashed #d1d5db', borderRadius: 10, background: '#fff', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: 16, textAlign: 'center', gap: 8 }}>
                    <div style={{ width: 36, height: 36, borderRadius: 10, background: '#eff6ff', border: '1px solid #dbeafe', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><Settings2 size={16} style={{ color: '#2563eb' }} /></div>
                    <div style={{ fontWeight: 600, fontSize: 13 }}>Select a provider to edit</div>
                    <div style={{ fontSize: 11, color: '#6b7280', maxWidth: 220 }}>Choose on the left, or quick-add from presets. Local providers (Ollama/LM Studio) need no API key — just start the local server.</div>
                    <div style={{ display: 'flex', gap: 6, marginTop: 4 }}>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 10, color: '#6b7280', background: '#fafafa', border: '1px solid #f3f4f6', borderRadius: 999, padding: '2px 7px' }}><span style={{ width: 6, height: 6, borderRadius: 999, background: '#10b981' }} /> Has key</span>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 10, color: '#6b7280', background: '#fafafa', border: '1px solid #f3f4f6', borderRadius: 999, padding: '2px 7px' }}><span style={{ width: 6, height: 6, borderRadius: 999, background: '#ef4444' }} /> Needs key</span>
                    </div>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      )}

      {activeTab === 'chat' ? (
        <>
          <div ref={scrollRef} style={{ flex: 1, overflowY: 'auto', padding: 12, display: 'flex', flexDirection: 'column', gap: 10, background: '#fcfcfc' }}>
            {messages.map(m => (
              <div key={m.id} style={{
                alignSelf: m.role === 'you' ? 'flex-end' : 'flex-start',
                maxWidth: '85%',
                padding: '8px 12px',
                borderRadius: 12,
                fontSize: 13, lineHeight: 1.45,
                background: m.role === 'you' ? '#2563eb' : m.role === 'tool_call' ? '#eff6ff' : m.role === 'tool_result' ? '#f0fdf4' : m.role === 'trace' ? '#f3f4f6' : m.role === 'system' ? '#fef3c7' : '#ffffff',
                color: m.role === 'you' ? '#fff' : m.role === 'tool_call' ? '#1e40af' : m.role === 'tool_result' ? '#166534' : m.role === 'trace' ? '#6b7280' : '#111827',
                border: m.role === 'tool_call' ? '1px solid #bfdbfe' : m.role === 'tool_result' ? '1px solid #bbf7d0' : m.role === 'trace' ? '1px dashed #d1d5db' : '1px solid #e5e7eb',
                whiteSpace: 'pre-wrap', wordBreak: 'break-word', boxShadow: m.role === 'assistant' ? '0 1px 2px rgba(0,0,0,0.04)' : 'none'
              }}>
                {m.role === 'trace' || m.role === 'tool_call' || m.role === 'tool_result' ? (
                  <div>
                    {m.role === 'tool_call' && <span style={{ fontWeight: 700, fontSize: 11, textTransform: 'uppercase', color: '#2563eb' }}>▶ Tool Call</span>}
                    {m.role === 'tool_result' && <span style={{ fontWeight: 700, fontSize: 11, textTransform: 'uppercase', color: '#16a34a' }}>◀ Tool Result</span>}
                    <div style={{ fontFamily: 'ui-monospace, SFMono-Regular, monospace', fontSize: 11, marginTop: 4, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{m.text}</div>
                  </div>
                ) : (
                  <ReactMarkdown>{m.text}</ReactMarkdown>
                )}
              </div>
            ))}
            {isRunning && <div style={{ fontSize: 12, color: '#6b7280', fontStyle: 'italic', display: 'flex', alignItems: 'center', gap: 6 }}><Loader2 size={12} style={{ animation: 'spin 1s linear infinite' } as any} /> Thinking… {pendingDeltaRef.current.length > 0 ? `(${pendingDeltaRef.current.length} chars streamed)` : ''}</div>}
          </div>
          <div style={{ padding: 8, borderTop: '1px solid #e5e7eb', display: 'flex', gap: 8, alignItems: 'flex-end', background: '#fff' }}>
            <textarea value={input} onChange={e => setInput(e.target.value)} onKeyDown={handleKeyDown} placeholder="How many walls are in this model?  (Enter to send, Shift+Enter for newline)" rows={2} style={{ flex: 1, padding: '8px 10px', borderRadius: 10, border: '1px solid #d1d5db', resize: 'none', fontFamily: 'inherit', fontSize: 13, outline: 'none', background: '#fff' }} disabled={isRunning} />
            {isRunning ? (
              <button onClick={handleStop} style={{ padding: '10px 18px', borderRadius: 10, border: '1px solid #ef4444', background: '#ef4444', color: '#fff', cursor: 'pointer', fontWeight: 700 }}>Stop</button>
            ) : (
              <button onClick={handleSend} disabled={!input.trim()} style={{ padding: '10px 18px', borderRadius: 10, border: 'none', background: !input.trim() ? '#9ca3af' : '#2563eb', color: '#fff', cursor: 'pointer', fontWeight: 700 }}>Send</button>
            )}
          </div>
        </>
      ) : (
        <div style={{ flex: 1, overflowY: 'auto', background: '#f9fafb' }}>
          <PluginShelf plugins={plugins} />
        </div>
      )}

      {/* click-away for provider selector */}
      {selectorOpen && <div onClick={() => setSelectorOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />}
      <style>{`@keyframes spin{from{transform:rotate(0deg)}to{transform:rotate(360deg)}}`}</style>
    </div>
  );
}
