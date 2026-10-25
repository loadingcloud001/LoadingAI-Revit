import { useEffect, useRef, useState } from 'react';
import { isBuiltinProvider, postToHost } from './bridge';
import { Transcript } from './chat/Transcript';
import type { TurnHandlers } from './chat/Turn';
import { Composer } from './composer/Composer';
import { quotaLine } from './lib/copy';
import { levelInfo, needsWarning, nextLevel, warningText } from './lib/permissions';
import { SettingsPage, ThemePref } from './settings/SettingsPage';
import { liveStatus } from './state/selectors';
import type { PermissionLevel } from './state/types';
import { usePane } from './state/usePane';
import { Icon } from './ui/Icon';

const THEME_KEY = 'lai.theme';

function readTheme(): ThemePref {
  try {
    const v = window.localStorage.getItem(THEME_KEY);
    return v === 'light' || v === 'system' ? v : 'dark';
  } catch { return 'dark'; }
}

function useTheme(): [ThemePref, (t: ThemePref) => void] {
  const [pref, setPref] = useState<ThemePref>(readTheme);
  useEffect(() => {
    const apply = () => {
      const dark = pref === 'dark' || (pref === 'system' && window.matchMedia?.('(prefers-color-scheme: dark)').matches);
      document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    };
    apply();
    if (pref !== 'system') return;
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)');
    mq?.addEventListener?.('change', apply);
    return () => mq?.removeEventListener?.('change', apply);
  }, [pref]);
  return [pref, (t: ThemePref) => { setPref(t); try { window.localStorage.setItem(THEME_KEY, t); } catch { /* storage blocked */ } }];
}

export default function App() {
  const [page, setPage] = useState<'chat' | 'settings'>('chat');
  const openSettingsRef = useRef(() => setPage('settings'));
  const pane = usePane({ openSettings: () => openSettingsRef.current() });
  const { state } = pane;
  const [theme, setTheme] = useTheme();
  const [askLevel, setAskLevel] = useState<PermissionLevel | null>(null);
  const [draft, setDraft] = useState<{ text: string; nonce: number } | null>(null);

  const pickLevel = (l: PermissionLevel) => {
    if (l === pane.level) return;
    if (needsWarning(l)) setAskLevel(l);
    else pane.setLevel(l);
  };

  const ctx = state.context;
  const activeProvider = pane.activeProvider;
  const quota = isBuiltinProvider(activeProvider?.id) ? quotaLine(state.quota) : null;

  const h: TurnHandlers = {
    onApprove: pane.approve,
    onSubmitPreset: pane.submitPreset,
    onValue: pane.setPresetValue,
    onPick: pane.pick,
    onRunPlan: () => pane.submit('/build Execute the plan.'),
    onEditText: text => setDraft({ text, nonce: Date.now() }),
    onOpenSettings: () => setPage('settings'),
    onRetry: text => pane.submit(text),
  };

  if (page === 'settings') {
    return (
      <div className="app">
        <SettingsPage
          state={state}
          caps={pane.caps}
          level={pane.level}
          theme={theme}
          onBack={() => setPage('chat')}
          onSave={pane.saveSettings}
          onLevel={pickLevel}
          onTheme={setTheme}
          onClear={() => { pane.clear(); setPage('chat'); }}
          onTest={t => pane.dispatch({ type: 'providerTest', test: t })}
          onModelsLoading={id => pane.dispatch({ type: 'modelsLoading', providerId: id })}
        />
        {askLevel && <LevelDialog level={askLevel} onCancel={() => setAskLevel(null)} onOk={() => { pane.setLevel(askLevel); setAskLevel(null); }} />}
      </div>
    );
  }

  return (
    <div className="app">
      <div className="topbar">
        <span className="brand">LoadingAI</span>
        {ctx ? (
          <span className="chip" title={ctx.selectionPreview || undefined} style={{ minWidth: 0 }}>
            <span className="truncate">{ctx.activeView ?? 'No view'}</span>
            <span>· {ctx.selectionCount} sel</span>
            {(ctx.warningCount ?? 0) > 0 && <span className="tone-change">· {ctx.warningCount} warn</span>}
          </span>
        ) : null}
        <span className="grow" />
        {quota && quota.tone !== 'muted' && <span className={`chip ${quota.tone === 'danger' ? 'tone-danger' : 'tone-change'}`}>{quota.text}</span>}
        <button type="button" className="icon-btn" aria-label="Saved sessions" title="Saved sessions (/checkpoints)" onClick={() => postToHost({ type: 'listCheckpoints' })}><Icon name="history" size={15} /></button>
        <button type="button" className="icon-btn" aria-label="Settings" title="Settings" onClick={() => { postToHost({ type: 'listPlugins' }); setPage('settings'); }}><Icon name="settings" size={15} /></button>
      </div>
      {state.running && (
        <>
          <div className="status-line" role="status" aria-live="polite">
            <Icon name={state.pendingReview ? 'dot' : 'loader'} size={12} className={state.pendingReview ? 'tone-change' : 'spin'} />
            <span className={`truncate ${state.pendingReview ? 'tone-change' : ''}`}>{liveStatus(state)}</span>
          </div>
          {!state.pendingReview && <div className="status-bar"><i /></div>}
        </>
      )}

      <Transcript
        entries={state.entries}
        pending={state.pendingReview}
        caps={pane.caps}
        plugins={state.plugins}
        suggestions={state.suggestions}
        quota={isBuiltinProvider(activeProvider?.id) ? state.quota : null}
        h={h}
        onStarter={text => setDraft({ text, nonce: Date.now() })}
      />

      {pane.level === 'bypass' && (
        <div className="bypass-bar" role="status">
          <Icon name="bolt" size={12} /><span>Bypass on · changes run without asking</span>
          <button type="button" onClick={() => pane.setLevel('confirm')}>Turn off</button>
        </div>
      )}
      <Composer
        running={state.running}
        level={pane.level}
        caps={pane.caps}
        providers={state.settings?.providers ?? []}
        activeProvider={activeProvider}
        queue={pane.queue}
        draft={draft}
        onSubmit={pane.submit}
        onStop={pane.stop}
        onLevel={pickLevel}
        onCycleLevel={() => pickLevel(nextLevel(pane.level, pane.caps))}
        onModel={id => state.settings && pane.saveSettings({ ...state.settings, activeProviderId: id })}
        onManageModels={() => setPage('settings')}
        onQueueRemove={pane.queueActions.remove}
        onQueueResume={pane.queueActions.resume}
        onQueueClear={pane.queueActions.clear}
        onNote={text => pane.dispatch({ type: 'note', kind: 'system', text })}
      />
      {askLevel && <LevelDialog level={askLevel} onCancel={() => setAskLevel(null)} onOk={() => { pane.setLevel(askLevel); setAskLevel(null); }} />}
    </div>
  );
}

/** In-pane confirmation for the levels that skip reviews (ADR-0016 wording). Cancel is the default. */
function LevelDialog({ level, onCancel, onOk }: { level: PermissionLevel; onCancel: () => void; onOk: () => void }) {
  const info = levelInfo(level);
  const cancelRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { cancelRef.current?.focus(); }, []);
  return (
    <div className="dialog-back" role="dialog" aria-modal="true" aria-labelledby="lvl-title" onKeyDown={e => { if (e.key === 'Escape') onCancel(); }}>
      <div className="dialog">
        <h2 id="lvl-title" className={info.tone === 'danger' ? 'tone-danger' : 'tone-change'}>Turn on {info.label}?</h2>
        <p>{warningText(level)}</p>
        <div className="row">
          <button ref={cancelRef} type="button" className="btn" onClick={onCancel}>Cancel</button>
          <span className="grow" />
          <button type="button" className={info.tone === 'danger' ? 'btn btn-danger' : 'btn btn-change'} onClick={onOk}>Turn on {info.label}</button>
        </div>
      </div>
    </div>
  );
}
