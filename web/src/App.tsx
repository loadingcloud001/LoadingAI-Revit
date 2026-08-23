import { useEffect, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import { AppSettings, PluginInfo, ProviderConfig, onHostMessage, postToHost } from './bridge';
import { PluginShelf } from './plugins/PluginShelf';

type Message = { id: string; role: 'you' | 'assistant' | 'trace' | 'system' | 'tool_call' | 'tool_result'; text: string };

function normalizeSettings(raw: any): AppSettings | null {
  if (!raw || typeof raw !== 'object') return null;
  // Handle camelCase (from Host with fix) and PascalCase (from old Host)
  const active = raw.activeProviderId ?? raw.ActiveProviderId ?? '';
  const provs = raw.providers ?? raw.Providers ?? [];
  if (!Array.isArray(provs)) return null;
  const providers: ProviderConfig[] = provs.map((p: any) => ({
    id: p.id ?? p.Id ?? Math.random().toString(36).slice(2, 10),
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKey: p.apiKey ?? p.ApiKey ?? '',
  }));
  const plugins = raw.plugins ?? raw.Plugins ?? undefined;
  return { activeProviderId: active, providers, plugins };
}

export default function App() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const settingsRef = useRef<AppSettings | null>(null);
  const [selectedProviderId, setSelectedProviderId] = useState<string>('');
  const [showSettings, setShowSettings] = useState(false);
  const [editing, setEditing] = useState<ProviderConfig | null>(null);
  const [editForm, setEditForm] = useState({ name: '', baseUrl: '', model: '', apiKey: '' });

  const [messages, setMessages] = useState<Message[]>([
    { id: 'welcome', role: 'system', text: 'AI Chat ready. Select a provider, add your API key in Settings, then ask e.g. "How many walls are in this model?"' }
  ]);
  const [input, setInput] = useState('');
  const [isRunning, setIsRunning] = useState(false);
  const [status, setStatus] = useState('Ready');
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [activeTab, setActiveTab] = useState<'chat' | 'plugins'>('chat');
  const scrollRef = useRef<HTMLDivElement>(null);

  // Keep ref in sync
  useEffect(() => { settingsRef.current = settings; }, [settings]);

  // streaming flag to dedupe final assistant after deltas
  const streamedRef = useRef(false);
  const pendingDeltaRef = useRef<string>('');
  const idRef = useRef(0);
  const nextId = () => `m${++idRef.current}`;

  useEffect(() => {
    const handler = (msg: any) => {
      try {
        if (msg.type === 'settings') {
          const norm = normalizeSettings(msg.settings);
          if (!norm) {
            setStatus('Settings parse error');
            return;
          }
          setSettings(norm);
          settingsRef.current = norm;
          setSelectedProviderId(norm.activeProviderId);
          const active = norm.providers.find(p => p.id === norm.activeProviderId) ?? norm.providers[0];
          if (active) setStatus(`Active: ${active.name} | ${active.baseUrl} | ${active.model}`);
        } else if (msg.type === 'status') {
          setStatus(msg.text);
        } else if ((msg as any).type === 'plugins') {
          setPlugins(((msg as any).plugins as PluginInfo[]) ?? []);
        } else if (msg.type === 'trace') {
          setMessages(m => [...m, { id: nextId(), role: 'trace', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'tool_call') {
          streamedRef.current = false;
          pendingDeltaRef.current = '';
          setMessages(m => [...m, { id: nextId(), role: 'tool_call', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'tool_result') {
          setMessages(m => [...m, { id: nextId(), role: 'tool_result', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'assistant') {
          const incoming = String(msg.text ?? '');
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && streamedRef.current) {
              streamedRef.current = false;
              pendingDeltaRef.current = '';
              if (!incoming || last.text === incoming || incoming.startsWith(last.text) || last.text.startsWith(incoming)) {
                return incoming && last.text !== incoming
                  ? [...m.slice(0, -1), { ...last, text: incoming.length >= last.text.length ? incoming : last.text }]
                  : m;
              }
              return [...m.slice(0, -1), { ...last, text: incoming }];
            }
            streamedRef.current = false;
            pendingDeltaRef.current = '';
            if (!incoming) return m;
            return [...m, { id: nextId(), role: 'assistant', text: incoming }];
          });
          setIsRunning(false);
          const cur = settingsRef.current;
          const active = cur?.providers.find(p => p.id === cur.activeProviderId) ?? cur?.providers[0];
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'error') {
          setMessages(m => [...m, { id: nextId(), role: 'system', text: `Error: ${String(msg.text ?? '')}` }]);
          setIsRunning(false);
          streamedRef.current = false;
          pendingDeltaRef.current = '';
          const cur = settingsRef.current;
          const active = cur?.providers.find(p => p.id === cur.activeProviderId);
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'delta') {
          const chunk = String(msg.text ?? '');
          if (!chunk) return;
          const starting = !streamedRef.current;
          streamedRef.current = true;
          pendingDeltaRef.current += chunk;
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && !starting) {
              return [...m.slice(0, -1), { ...last, text: last.text + chunk }];
            }
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
    postToHost({ type: 'listPlugins' } as any);
    const t = setTimeout(() => { postToHost({ type: 'requestSettings' }); postToHost({ type: 'listPlugins' } as any); }, 800);
    return () => { clearTimeout(t); off(); };
  }, []);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [messages, isRunning]);

  const handleSend = () => {
    const text = input.trim();
    if (!text || isRunning) return;
    const cur = settingsRef.current ?? settings;
    const active = cur?.providers.find(p => p.id === (selectedProviderId || cur?.activeProviderId)) ?? cur?.providers[0];
    if (!cur || !active) {
      setMessages(m => [...m, { id: nextId(), role: 'system', text: 'No provider configured. Open Settings to add one.' }]);
      setShowSettings(true);
      return;
    }
    if (!active.apiKey) {
      setMessages(m => [...m, { id: nextId(), role: 'system', text: `Provider '${active.name}' has no API key. Open Settings and paste your key.` }]);
      setShowSettings(true);
      return;
    }
    // Switch active if dropdown changed
    if (selectedProviderId && selectedProviderId !== cur.activeProviderId) {
      const newSettings: AppSettings = { ...cur, activeProviderId: selectedProviderId };
      setSettings(newSettings);
      settingsRef.current = newSettings;
      postToHost({ type: 'saveSettings', settings: newSettings });
    }
    setMessages(m => [...m, { id: nextId(), role: 'you', text }]);
    setInput('');
    setIsRunning(true);
    streamedRef.current = false;
    pendingDeltaRef.current = '';
    setStatus('Working...');
    postToHost({ type: 'send', text });
  };

  const handleStop = () => {
    postToHost({ type: 'cancel' });
    setStatus('Cancelling...');
    streamedRef.current = false;
  };

  const handleClear = () => {
    setMessages([{ id: 'cleared', role: 'system', text: 'Cleared.' }]);
    streamedRef.current = false;
    pendingDeltaRef.current = '';
    postToHost({ type: 'clear' });
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const selectForEdit = (p: ProviderConfig) => {
    setEditing(p);
    setEditForm({ name: p.name, baseUrl: p.baseUrl, model: p.model, apiKey: p.apiKey });
  };

  const handleAdd = () => {
    const cur = settingsRef.current ?? settings;
    if (!cur) return;
    const p: ProviderConfig = { id: Math.random().toString(36).slice(2, 10), name: 'New Provider', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '' };
    const newSettings: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(newSettings);
    settingsRef.current = newSettings;
    selectForEdit(p);
    postToHost({ type: 'saveSettings', settings: newSettings });
  };

  const handleSave = () => {
    const cur = settingsRef.current ?? settings;
    if (!cur || !editing) return;
    if (!editForm.name.trim() || !editForm.baseUrl.trim() || !editForm.model.trim()) {
      setStatus('Name, Base URL and Model are required.');
      return;
    }
    try { new URL(editForm.baseUrl.trim()); } catch { setStatus('Base URL is not a valid URL.'); return; }
    const updated: ProviderConfig = { ...editing, name: editForm.name.trim(), baseUrl: editForm.baseUrl.trim().replace(/\/$/, ''), model: editForm.model.trim(), apiKey: editForm.apiKey.trim() };
    const newProviders = cur.providers.map(p => p.id === editing.id ? updated : p);
    const newSettings: AppSettings = { ...cur, providers: newProviders };
    setSettings(newSettings);
    settingsRef.current = newSettings;
    setEditing(updated);
    postToHost({ type: 'saveSettings', settings: newSettings });
    setStatus(`Saved ${updated.name}`);
  };

  const handleDelete = () => {
    const cur = settingsRef.current ?? settings;
    if (!cur || !editing) return;
    if (cur.providers.length <= 1) { setStatus('Cannot delete the last provider.'); return; }
    const newProviders = cur.providers.filter(p => p.id !== editing.id);
    const newActive = cur.activeProviderId === editing.id ? newProviders[0].id : cur.activeProviderId;
    const newSettings: AppSettings = { ...cur, providers: newProviders, activeProviderId: newActive };
    setSettings(newSettings);
    settingsRef.current = newSettings;
    setSelectedProviderId(newActive);
    setEditing(null);
    postToHost({ type: 'saveSettings', settings: newSettings });
    setStatus(`Deleted. Active: ${newProviders.find(p=>p.id===newActive)?.name ?? newActive}`);
  };

  const handleUse = () => {
    const cur = settingsRef.current ?? settings;
    if (!cur || !editing) return;
    const newSettings: AppSettings = { ...cur, activeProviderId: editing.id };
    setSettings(newSettings);
    settingsRef.current = newSettings;
    setSelectedProviderId(editing.id);
    postToHost({ type: 'saveSettings', settings: newSettings });
    setStatus(`Active: ${editing.name}`);
  };

  const handleProviderChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const id = e.target.value;
    setSelectedProviderId(id);
    const cur = settingsRef.current ?? settings;
    if (cur) {
      const newSettings: AppSettings = { ...cur, activeProviderId: id };
      setSettings(newSettings);
      settingsRef.current = newSettings;
      postToHost({ type: 'saveSettings', settings: newSettings });
      const active = newSettings.providers.find(p => p.id === id);
      if (active) setStatus(`Active: ${active.name} | ${active.baseUrl} | ${active.model}`);
    }
  };

  const providers = settings?.providers ?? [];
  const showSettingsPanel = showSettings; // always show panel when toggled, even if settings null, to reveal fallback

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', fontFamily: 'system-ui, sans-serif', background: '#fff' }}>
      <div style={{ display: 'flex', gap: 8, padding: 8, borderBottom: '1px solid #e5e7eb', alignItems: 'center' }}>
        <select value={selectedProviderId} onChange={handleProviderChange} style={{ flex: 1, padding: '6px 8px', borderRadius: 6, border: '1px solid #d1d5db' }}>
          {providers.length === 0 ? <option value="">{settings ? 'No providers' : 'Loading...'}</option> : providers.map(p => <option key={p.id} value={p.id}>{p.name} — {p.model}</option>)}
        </select>
        <button onClick={() => setShowSettings(v => !v)} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: showSettings ? '#f3f4f6' : '#fff', cursor: 'pointer', fontWeight: showSettings ? 600 : 400 }}>Settings</button>
        <button onClick={handleClear} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer' }}>Clear</button>
      </div>

      <div style={{ padding: '4px 8px', fontSize: 11, color: '#6b7280', borderBottom: '1px solid #f3f4f6', minHeight: 18 }}>{isRunning ? 'Working...' : status}</div>

      {/* Tabs: Chat | Plugins (MIT opensource shelf) */}
      <div style={{ display: 'flex', gap: 0, borderBottom: '1px solid #e5e7eb', background: '#fafafa' }}>
        <button onClick={() => setActiveTab('chat')} style={{ flex: 1, padding: '8px', border: 'none', borderBottom: activeTab === 'chat' ? '2px solid #2563eb' : '2px solid transparent', background: activeTab === 'chat' ? '#fff' : '#fafafa', fontWeight: activeTab === 'chat' ? 700 : 400, cursor: 'pointer', fontSize: 12 }}>
          Chat {messages.length > 1 ? `· ${messages.length}` : ''}
        </button>
        <button onClick={() => { setActiveTab('plugins'); postToHost({ type: 'listPlugins' } as any); }} style={{ flex: 1, padding: '8px', border: 'none', borderBottom: activeTab === 'plugins' ? '2px solid #2563eb' : '2px solid transparent', background: activeTab === 'plugins' ? '#fff' : '#fafafa', fontWeight: activeTab === 'plugins' ? 700 : 400, cursor: 'pointer', fontSize: 12 }}>
          Plugins {plugins.length ? `· ${plugins.length} · ${plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0)}/${plugins.reduce((n, p) => n + p.tools.length, 0)}` : ''} · MIT
        </button>
      </div>

      {showSettingsPanel && (
        <div style={{ borderBottom: '1px solid #e5e7eb', background: '#f9fafb', padding: 12 }}>
          <div style={{ fontWeight: 600, marginBottom: 8 }}>Providers (Bearer only) — stored at %AppData%\RevitAiPoc\settings.json</div>
          {!settings ? (
            <div style={{ color: '#b45309', fontSize: 12, padding: 8, background: '#fffbeb', border: '1px solid #fde68a', borderRadius: 6 }}>Loading settings... If this persists, click Clear or restart Revit. Check %AppData%\RevitAiPoc\settings.json is valid JSON.</div>
          ) : (
            <div style={{ display: 'flex', gap: 12 }}>
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 12, color: '#6b7280', marginBottom: 4 }}>Providers ({providers.length})</div>
                <div style={{ border: '1px solid #d1d5db', borderRadius: 6, maxHeight: 140, overflowY: 'auto', background: '#fff' }}>
                  {providers.map(p => (
                    <div key={p.id} onClick={() => selectForEdit(p)} style={{ padding: '6px 8px', cursor: 'pointer', background: editing?.id === p.id ? '#eff6ff' : '#fff', borderBottom: '1px solid #f3f4f6', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <span style={{ fontWeight: editing?.id === p.id ? 600 : 400 }}>{p.name}</span><span style={{ color: '#6b7280', fontSize: 11, marginLeft: 8, flexShrink: 0 }}>{p.model}</span>
                    </div>
                  ))}
                </div>
                <button onClick={handleAdd} style={{ marginTop: 8, padding: '6px 12px', borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer', width: '100%' }}>+ Add New</button>
              </div>
              <div style={{ flex: 1.4 }}>
                {editing ? (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                    <label style={{ fontSize: 12, display: 'flex', flexDirection: 'column', gap: 2 }}>Name<input value={editForm.name} onChange={e => setEditForm(f => ({ ...f, name: e.target.value }))} style={{ width: '100%', padding: 6, borderRadius: 6, border: '1px solid #d1d5db' }} /></label>
                    <label style={{ fontSize: 12, display: 'flex', flexDirection: 'column', gap: 2 }}>Base URL<input value={editForm.baseUrl} onChange={e => setEditForm(f => ({ ...f, baseUrl: e.target.value }))} placeholder="https://api.deepseek.com" style={{ width: '100%', padding: 6, borderRadius: 6, border: '1px solid #d1d5db' }} /></label>
                    <label style={{ fontSize: 12, display: 'flex', flexDirection: 'column', gap: 2 }}>Model<input value={editForm.model} onChange={e => setEditForm(f => ({ ...f, model: e.target.value }))} placeholder="deepseek-chat" style={{ width: '100%', padding: 6, borderRadius: 6, border: '1px solid #d1d5db' }} /></label>
                    <label style={{ fontSize: 12, display: 'flex', flexDirection: 'column', gap: 2 }}>API Key<input value={editForm.apiKey} onChange={e => setEditForm(f => ({ ...f, apiKey: e.target.value }))} type="password" placeholder="sk-..." style={{ width: '100%', padding: 6, borderRadius: 6, border: '1px solid #d1d5db' }} /></label>
                    <div style={{ display: 'flex', gap: 6, marginTop: 6 }}>
                      <button onClick={handleSave} style={{ flex: 1, padding: 6, borderRadius: 6, border: '1px solid #2563eb', background: '#2563eb', color: '#fff', cursor: 'pointer', fontWeight: 600 }}>Save</button>
                      <button onClick={handleUse} style={{ flex: 1, padding: 6, borderRadius: 6, border: '1px solid #d1d5db', background: '#fff', cursor: 'pointer' }}>Use</button>
                      <button onClick={handleDelete} style={{ padding: '6px 12px', borderRadius: 6, border: '1px solid #ef4444', background: '#fff', color: '#ef4444', cursor: 'pointer' }}>Delete</button>
                    </div>
                  </div>
                ) : <div style={{ color: '#6b7280', fontSize: 12, padding: 12, textAlign: 'center', border: '1px dashed #d1d5db', borderRadius: 6 }}>Select a provider on the left to edit, or click + Add New</div>}
              </div>
            </div>
          )}
        </div>
      )}

      {activeTab === 'chat' ? (
        <>
          <div ref={scrollRef} style={{ flex: 1, overflowY: 'auto', padding: 12, display: 'flex', flexDirection: 'column', gap: 10 }}>
            {messages.map(m => (
              <div key={m.id} style={{
                alignSelf: m.role === 'you' ? 'flex-end' : 'flex-start',
                maxWidth: '85%',
                padding: '8px 12px',
                borderRadius: 12,
                fontSize: 13,
                lineHeight: 1.4,
                background: m.role === 'you' ? '#2563eb'
                  : m.role === 'tool_call' ? '#eff6ff'
                  : m.role === 'tool_result' ? '#f0fdf4'
                  : m.role === 'trace' ? '#f3f4f6'
                  : m.role === 'system' ? '#fef3c7' : '#f9fafb',
                color: m.role === 'you' ? '#fff'
                  : m.role === 'tool_call' ? '#1e40af'
                  : m.role === 'tool_result' ? '#166534'
                  : m.role === 'trace' ? '#6b7280' : '#111827',
                border: m.role === 'tool_call' ? '1px solid #bfdbfe'
                  : m.role === 'tool_result' ? '1px solid #bbf7d0'
                  : m.role === 'trace' ? '1px dashed #d1d5db' : '1px solid #e5e7eb',
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word'
              }}>
                {m.role === 'trace' || m.role === 'tool_call' || m.role === 'tool_result' ? (
                  <div>
                    {m.role === 'tool_call' && <span style={{ fontWeight: 700, fontSize: 11, textTransform: 'uppercase', color: '#2563eb' }}>▶ Tool Call</span>}
                    {m.role === 'tool_result' && <span style={{ fontWeight: 700, fontSize: 11, textTransform: 'uppercase', color: '#16a34a' }}>◀ Tool Result</span>}
                    <div style={{ fontFamily: 'monospace', fontSize: 11, marginTop: 4 }}>{m.text}</div>
                  </div>
                ) : (
                  <ReactMarkdown>{m.text}</ReactMarkdown>
                )}
              </div>
            ))}
            {isRunning && <div style={{ fontSize: 12, color: '#6b7280', fontStyle: 'italic' }}>Thinking… {pendingDeltaRef.current.length > 0 ? `(${pendingDeltaRef.current.length} chars streamed)` : ''}</div>}
          </div>

          <div style={{ padding: 8, borderTop: '1px solid #e5e7eb', display: 'flex', gap: 8, alignItems: 'flex-end' }}>
            <textarea
              value={input}
              onChange={e => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="How many walls are in this model?  (Enter to send, Shift+Enter for newline)"
              rows={2}
              style={{ flex: 1, padding: 8, borderRadius: 8, border: '1px solid #d1d5db', resize: 'none', fontFamily: 'inherit', fontSize: 13 }}
              disabled={isRunning}
            />
            {isRunning ? (
              <button onClick={handleStop} style={{ padding: '10px 18px', borderRadius: 8, border: '1px solid #ef4444', background: '#ef4444', color: '#fff', cursor: 'pointer', fontWeight: 600 }}>Stop</button>
            ) : (
              <button onClick={handleSend} disabled={!input.trim()} style={{ padding: '10px 18px', borderRadius: 8, border: 'none', background: !input.trim() ? '#9ca3af' : '#2563eb', color: '#fff', cursor: 'pointer', fontWeight: 600 }}>Send</button>
            )}
          </div>
        </>
      ) : (
        <div style={{ flex: 1, overflowY: 'auto', background: '#f9fafb' }}>
          <PluginShelf plugins={plugins} />
        </div>
      )}
    </div>
  );
}
