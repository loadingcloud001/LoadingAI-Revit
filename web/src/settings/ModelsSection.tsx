import { useEffect, useMemo, useState } from 'react';
import 'open-model-selector/styles.css';
import { ModelSelector } from 'open-model-selector';
import { AppSettings, BUILTIN_PROVIDER_NAME, isBuiltinProvider, postToHost, ProviderConfig, ProviderPreset, QuotaPayload } from '../bridge';
import type { ProviderTest } from '../state/types';
import { Icon } from '../ui/Icon';

type Props = {
  settings: AppSettings | null;
  catalog: ProviderPreset[];
  quota: QuotaPayload | null;
  test: ProviderTest | null;
  models: Record<string, string[]>;
  modelsLoading: string | null;
  onSave: (s: AppSettings) => void;
  onTest: (test: ProviderTest | null) => void;
  onModelsLoading: (id: string | null) => void;
};

type Form = { name: string; baseUrl: string; model: string; apiKey: string; apiKind: string; group: string; icon: string };

function toForm(p: ProviderConfig): Form {
  return { name: p.name, baseUrl: p.baseUrl, model: p.model, apiKey: p.apiKey, apiKind: p.apiKind ?? 'openai', group: p.group ?? 'custom', icon: p.icon ?? '＋' };
}

function connected(p: ProviderConfig): boolean {
  return isBuiltinProvider(p.id) || !!p.apiKey || p.baseUrl.includes('localhost');
}

export function ModelsSection({ settings, catalog, quota, test, models, modelsLoading, onSave, onTest, onModelsLoading }: Props) {
  const providers = settings?.providers ?? [];
  const [editingId, setEditingId] = useState<string | null>(null);
  const editing = providers.find(p => p.id === editingId) ?? null;
  const [form, setForm] = useState<Form | null>(null);
  const [presetFilter, setPresetFilter] = useState('');
  const [showPresets, setShowPresets] = useState(false);
  const [message, setMessage] = useState('');

  useEffect(() => { setForm(editing ? toForm(editing) : null); }, [editingId]); // eslint-disable-line react-hooks/exhaustive-deps

  const presets = useMemo(() => {
    const f = presetFilter.toLowerCase().trim();
    return f ? catalog.filter(c => `${c.name} ${c.model} ${c.description}`.toLowerCase().includes(f)) : catalog;
  }, [catalog, presetFilter]);

  if (!settings) return <div className="muted">Loading settings…</div>;

  const fromForm = (base: ProviderConfig): ProviderConfig => form
    ? { ...base, name: form.name.trim() || base.name, baseUrl: form.baseUrl.trim().replace(/\/$/, '') || base.baseUrl, model: form.model.trim() || base.model, apiKey: form.apiKey.trim(), apiKind: form.apiKind, group: form.group, icon: form.icon }
    : base;

  const use = (id: string) => { onSave({ ...settings, activeProviderId: id }); setMessage('Model switched.'); };
  const add = (p: ProviderConfig) => { onSave({ ...settings, providers: [...providers, p] }); setEditingId(p.id); };
  const addPreset = (preset: ProviderPreset) => {
    const exists = providers.some(p => p.id === preset.id);
    add({ id: exists ? `${preset.id}-${Math.random().toString(36).slice(2, 5)}` : preset.id, name: preset.name, baseUrl: preset.baseUrl, model: preset.model, apiKey: '', apiKind: preset.apiKind, group: preset.group, icon: preset.icon });
    setShowPresets(false); setPresetFilter('');
  };
  const addCustom = () => add({ id: Math.random().toString(36).slice(2, 8), name: 'Custom', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'custom', icon: '＋' });
  const save = () => {
    if (!editing || !form) return;
    if (!form.name.trim() || !form.baseUrl.trim() || !form.model.trim()) { setMessage('Name, base URL and model are required.'); return; }
    try { new URL(form.baseUrl.trim()); } catch { setMessage('The base URL is not a valid URL.'); return; }
    onSave({ ...settings, providers: providers.map(p => p.id === editing.id ? fromForm(p) : p) });
    setMessage(`Saved ${form.name.trim()}.`);
  };
  const remove = () => {
    if (!editing) return;
    if (providers.length <= 1) { setMessage("You can't delete the last model."); return; }
    const rest = providers.filter(p => p.id !== editing.id);
    onSave({ ...settings, providers: rest, activeProviderId: settings.activeProviderId === editing.id ? rest[0].id : settings.activeProviderId });
    setEditingId(null);
    setMessage('Deleted.');
  };
  const runTest = () => {
    if (!editing) return;
    onTest({ id: editing.id, loading: true, message: 'Testing…' });
    postToHost({ type: 'testProvider', provider: fromForm(editing) });
  };
  const fetchModels = () => {
    if (!editing) return;
    onModelsLoading(editing.id);
    postToHost({ type: 'listModels', provider: fromForm(editing) });
  };
  const list = editing ? models[editing.id] ?? [] : [];

  return (
    <div>
      <h3>Your models</h3>
      {providers.map(p => {
        const active = p.id === settings.activeProviderId;
        const open = p.id === editingId;
        return (
          <div key={p.id}>
            <button type="button" className="prov" aria-current={active} aria-expanded={open} onClick={() => setEditingId(open ? null : p.id)} style={open ? { borderRadius: '5px 5px 0 0', marginBottom: 0 } : undefined}>
              <div className="row">
                <span style={{ fontWeight: 600 }}>{p.name}</span>
                {active && <span className="badge badge-ok">in use</span>}
                <span className="grow" />
                <span className={connected(p) ? 'tone-ok' : 'faint'} style={{ fontSize: 11 }}>{connected(p) ? '● ready' : 'no key'}</span>
              </div>
              <div className="mono muted truncate" style={{ fontSize: 11 }}>
                {isBuiltinProvider(p.id) && quota ? (quota.ok === false ? (quota.message ?? `${BUILTIN_PROVIDER_NAME} is unavailable.`) : `${quota.remaining}/${quota.limit} free requests left today`) : p.model}
              </div>
            </button>
            {open && form && (
              <div className="prov-edit">
                {isBuiltinProvider(p.id) ? (
                  <div className="muted">Included free. No API key needed. 100 requests per day per device.{quota?.resetUtc && quota.ok !== false ? ` Resets ${new Date(quota.resetUtc).toLocaleTimeString()}.` : ''}</div>
                ) : (
                  <>
                    <label className="field"><span>Name</span><input className="input" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></label>
                    <label className="field"><span>Base URL</span><input className="input mono" value={form.baseUrl} onChange={e => setForm({ ...form, baseUrl: e.target.value })} /></label>
                    <div className="row" style={{ gap: 8 }}>
                      <label className="field grow"><span>API kind</span>
                        <select className="select" value={form.apiKind} onChange={e => setForm({ ...form, apiKind: e.target.value })}>
                          <option value="openai">OpenAI compatible</option>
                          <option value="responses">Responses</option>
                          <option value="anthropic">Anthropic</option>
                        </select>
                      </label>
                      <label className="field grow"><span>Group</span>
                        <select className="select" value={form.group} onChange={e => setForm({ ...form, group: e.target.value })}>
                          <option value="cloud">Cloud</option>
                          <option value="gateway">Gateway</option>
                          <option value="local">Local</option>
                          <option value="custom">Custom</option>
                        </select>
                      </label>
                    </div>
                    <label className="field">
                      <span className="row">Model<span className="grow" /><button type="button" className="btn btn-sm" onClick={fetchModels} disabled={!!modelsLoading}>{modelsLoading === p.id ? 'Fetching…' : 'Fetch list'}</button></span>
                      <input className="input mono" value={form.model} onChange={e => setForm({ ...form, model: e.target.value })} />
                    </label>
                    {list.length > 0 && (
                      list.length > 8
                        ? <ModelSelector models={list.map(id => ({ id, name: id, provider: p.name } as any))} value={form.model} onChange={(id: string) => setForm({ ...form, model: id })} placeholder="Search models…" storageKey={`oms-${p.id}`} />
                        : <div className="list">{list.map(id => <button key={id} type="button" className="menu-item mono" style={{ fontSize: 11 }} aria-selected={form.model === id} onClick={() => setForm({ ...form, model: id })}>{id}</button>)}</div>
                    )}
                    <label className="field">
                      <span>API key</span>
                      <input className="input mono" type="password" autoComplete="off" value={form.apiKey} placeholder={form.baseUrl.includes('localhost') ? 'Leave empty for a local server' : 'sk-…'} onChange={e => setForm({ ...form, apiKey: e.target.value })} />
                      <span className="faint" style={{ fontSize: 11 }}>Stored with Windows DPAPI in %AppData%\LoadingAI\settings.json.</span>
                    </label>
                  </>
                )}
                {test && test.id === p.id && <div className={test.loading ? 'muted' : test.ok ? 'tone-ok' : 'tone-danger'} style={{ fontSize: 12 }}>{test.message}</div>}
                <div className="row" style={{ flexWrap: 'wrap' }}>
                  {!active && <button type="button" className="btn btn-sm btn-primary" onClick={() => use(p.id)}>Use</button>}
                  {!isBuiltinProvider(p.id) && <button type="button" className="btn btn-sm" onClick={save}>Save</button>}
                  <button type="button" className="btn btn-sm" onClick={runTest} disabled={!!test?.loading}>Test</button>
                  <span className="grow" />
                  {!isBuiltinProvider(p.id) && <button type="button" className="btn btn-sm btn-ghost tone-danger" onClick={remove}>Delete</button>}
                </div>
              </div>
            )}
          </div>
        );
      })}
      {message && <div className="muted" style={{ fontSize: 12, margin: '4px 0' }} role="status">{message}</div>}
      <div className="row" style={{ marginTop: 8 }}>
        <button type="button" className="btn btn-sm" onClick={() => setShowPresets(v => !v)} aria-expanded={showPresets}><Icon name="plus" size={12} />Add from presets</button>
        <button type="button" className="btn btn-sm" onClick={addCustom}><Icon name="plus" size={12} />Custom</button>
      </div>
      {showPresets && (
        <div style={{ marginTop: 6 }}>
          <input className="input" placeholder="Filter presets" value={presetFilter} onChange={e => setPresetFilter(e.target.value)} aria-label="Filter presets" />
          <div className="list" style={{ marginTop: 4, maxHeight: 220, overflowY: 'auto' }}>
            {presets.map(c => (
              <button key={c.id} type="button" className="menu-item" onClick={() => addPreset(c)}>
                <span className="grow"><span>{c.name}</span><small className="mono">{c.model}{c.description ? ` · ${c.description}` : ''}</small></span>
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
