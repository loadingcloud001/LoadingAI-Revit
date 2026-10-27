import { useEffect, useState } from 'react';
import { postToHost } from '../bridge';
import { copyText } from '../lib/clipboard';
import { availableLevels, HostCaps } from '../lib/permissions';
import { diagnostics } from '../plugins/pluginView';
import { PluginShelf } from '../plugins/PluginShelf';
import type { PaneState, PermissionLevel, ProviderTest } from '../state/types';
import { Icon } from '../ui/Icon';
import { ModelsSection } from './ModelsSection';
import { MemorySection } from './MemorySection';

export type ThemePref = 'dark' | 'light' | 'system';
export type Tab = 'models' | 'agent' | 'memory' | 'tools' | 'plugins' | 'about';
const TABS: { id: Tab; label: string }[] = [
  { id: 'models', label: 'Models' },
  { id: 'agent', label: 'Agent' },
  { id: 'memory', label: 'Memory' },
  { id: 'tools', label: 'Tools' },
  { id: 'plugins', label: 'Plugins' },
  { id: 'about', label: 'About' },
];

type Props = {
  state: PaneState;
  caps: HostCaps;
  level: PermissionLevel;
  theme: ThemePref;
  initialTab?: Tab;
  onBack: () => void;
  onSave: PaneSaveFn;
  onLevel: (l: PermissionLevel) => void;
  onTheme: (t: ThemePref) => void;
  onClear: () => void;
  onTest: (t: ProviderTest | null) => void;
  onModelsLoading: (id: string | null) => void;
};
type PaneSaveFn = Parameters<typeof ModelsSection>[0]['onSave'];

/** Matches UserInstructions.MaxChars on the host. */
const INSTRUCTIONS_MAX = 2000;

function InstructionsEditor({ saved, onSave }: { saved: string; onSave: (text: string) => void }) {
  const [draft, setDraft] = useState(saved);
  const [justSaved, setJustSaved] = useState(false);
  // A save from elsewhere (or the host trimming the text) replaces the draft only when the person is not mid-edit.
  useEffect(() => { setDraft(d => (d === saved ? d : saved)); }, [saved]);
  const dirty = draft.trim() !== saved.trim();
  return (
    <div style={{ display: 'grid', gap: 6 }}>
      <label className="field">
        <span>Instructions the agent follows in every chat</span>
        <textarea
          className="textarea"
          rows={5}
          maxLength={INSTRUCTIONS_MAX}
          value={draft}
          placeholder="For example: Answer in English. Use millimetres. Our level names start with L, like L01."
          onChange={e => { setDraft(e.target.value); setJustSaved(false); }}
        />
      </label>
      <div className="row">
        <button type="button" className="btn btn-sm" disabled={!dirty} onClick={() => { onSave(draft.trim()); setJustSaved(true); }}>Save instructions</button>
        {dirty && <button type="button" className="btn btn-ghost btn-sm" onClick={() => setDraft(saved)}>Discard</button>}
        <span className="grow" />
        <span className="faint" style={{ fontSize: 11 }} role="status">{justSaved && !dirty ? 'Saved' : `${draft.length}/${INSTRUCTIONS_MAX}`}</span>
      </div>
      <div className="faint" style={{ fontSize: 11.5 }}>These go into the agent&apos;s system prompt. They cannot loosen permissions: a review still waits for you.</div>
    </div>
  );
}

function AlwaysAllowed({ tools, onRemove }: { tools: string[]; onRemove: (name: string) => void }) {
  return (
    <div style={{ marginBottom: 10 }}>
      <h3>Always allowed</h3>
      {tools.length === 0 ? (
        <div className="faint" style={{ fontSize: 11.5 }}>None. Choose &quot;Always allow this tool&quot; on a review card to add one.</div>
      ) : (
        <div className="list">
          {tools.map(name => (
            <div key={name} className="list-row">
              <span className="grow truncate mono" style={{ fontSize: 11 }}>{name}</span>
              <button type="button" className="btn btn-sm" aria-label={`Stop always allowing ${name}`} onClick={() => onRemove(name)}>Remove</button>
            </div>
          ))}
        </div>
      )}
      <div className="faint" style={{ fontSize: 11.5, marginTop: 4 }}>These tools run without a review at Ask-before-changes and Auto-edit. Delete, replace and file-writing tools can never be added.</div>
    </div>
  );
}

export function SettingsPage(p: Props) {
  const [tab, setTab] = useState<Tab>(p.initialTab ?? 'models');
  const [copied, setCopied] = useState(false);
  const failed = p.state.loadIssues.filter(i => !i.notice).length;
  return (
    <>
      <div className="topbar">
        <button type="button" className="icon-btn" aria-label="Back to chat" onClick={p.onBack}><Icon name="arrowLeft" size={15} /></button>
        <span className="brand">Settings</span>
      </div>
      <div className="settings">
        <div className="tabs" role="tablist" aria-label="Settings sections">
          {TABS.map(t => (
            <button key={t.id} type="button" role="tab" className="tab" aria-selected={tab === t.id} onClick={() => { setTab(t.id); if (t.id === 'tools' || t.id === 'plugins') postToHost({ type: 'listPlugins' }); }}>
              {t.label}{t.id === 'plugins' && failed > 0 ? <span className="tone-danger"> · {failed}</span> : null}
            </button>
          ))}
        </div>
        <div className="section" role="tabpanel">
          {tab === 'models' && (
            <ModelsSection
              settings={p.state.settings}
              catalog={p.state.catalog}
              quota={p.state.quota}
              test={p.state.providerTest}
              models={p.state.models}
              modelsLoading={p.state.modelsLoading}
              onSave={p.onSave}
              onTest={p.onTest}
              onModelsLoading={p.onModelsLoading}
            />
          )}
          {tab === 'agent' && (
            <div style={{ display: 'grid', gap: 10 }}>
              <h3>Permissions</h3>
              <label className="field">
                <span>Permission level</span>
                <select className="select" value={p.level} onChange={e => p.onLevel(e.target.value as PermissionLevel)}>
                  {availableLevels(p.caps).map(l => <option key={l.id} value={l.id}>{l.label}: {l.hint}</option>)}
                </select>
              </label>
              <div className="faint" style={{ fontSize: 11.5 }}>
                {p.caps.has('levels')
                  ? 'The model can never change this. The add-in stores the level and enforces it on every tool call.'
                  : 'The model can never change this. Bypass is stored by the add-in; the other levels are stored in this pane.'}
              </div>
              {p.caps.has('changes') && p.state.settings && (
                <label className="toggle">
                  <input
                    type="checkbox"
                    checked={p.state.settings.selectChangedAfterTurn !== false}
                    onChange={e => p.onSave({ ...p.state.settings!, selectChangedAfterTurn: e.target.checked })}
                  />
                  Select changed elements in Revit after each turn
                </label>
              )}
              {p.caps.has('instructions') && p.state.settings && (
                <>
                  <h3>Custom instructions</h3>
                  <InstructionsEditor saved={p.state.settings.instructions ?? ''} onSave={text => p.onSave({ ...p.state.settings!, instructions: text })} />
                </>
              )}
              <h3>Appearance</h3>
              <label className="field">
                <span>Theme</span>
                <select className="select" value={p.theme} onChange={e => p.onTheme(e.target.value as ThemePref)}>
                  <option value="dark">Dark</option>
                  <option value="light">Light</option>
                  <option value="system">{p.caps.has('theme') ? 'Follow Revit' : 'Follow Windows'}</option>
                </select>
              </label>
              <h3>Session</h3>
              <div className="row">
                <button type="button" className="btn btn-sm" onClick={p.onClear}>Clear session</button>
                <span className="faint" style={{ fontSize: 11.5 }}>Clears the chat and the agent's memory of it. The model is not touched.</span>
              </div>
            </div>
          )}
          {tab === 'memory' && <MemorySection state={p.state} />}
          {tab === 'tools' && (
            <>
              {p.caps.has('reviewScopes') && p.state.settings && (
                <AlwaysAllowed
                  tools={p.state.settings.alwaysAllowTools ?? []}
                  onRemove={name => p.onSave({ ...p.state.settings!, alwaysAllowTools: (p.state.settings!.alwaysAllowTools ?? []).filter(n => n !== name) })}
                />
              )}
              <PluginShelf plugins={p.state.plugins} loadIssues={p.state.loadIssues} view="tools" />
            </>
          )}
          {tab === 'plugins' && <PluginShelf plugins={p.state.plugins} loadIssues={p.state.loadIssues} view="plugins" />}
          {tab === 'about' && (
            <div style={{ display: 'grid', gap: 8 }}>
              <h3>LoadingAI for Revit</h3>
              <div className="muted">An AI chat pane for Revit where every capability is a plugin. Independent project, not affiliated with Autodesk.</div>
              <div className="kv"><span>Logs</span><span>%AppData%\LoadingAI\logs</span><span>Settings</span><span>%AppData%\LoadingAI\settings.json</span><span>Plugins</span><span>{p.state.plugins.length} loaded</span></div>
              <div className="row">
                <button type="button" className="btn btn-sm" onClick={async () => { setCopied(await copyText(diagnostics(p.state.plugins, p.state.loadIssues))); window.setTimeout(() => setCopied(false), 1200); }}><Icon name="copy" size={12} />{copied ? 'Copied' : 'Copy diagnostics'}</button>
              </div>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
