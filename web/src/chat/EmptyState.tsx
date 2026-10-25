import { useState } from 'react';
import type { PluginInfo, QuotaPayload } from '../bridge';
import { quotaLine } from '../lib/copy';
import { Icon, IconName } from '../ui/Icon';

type Starter = { plugin: string; icon: IconName; text: string };

/** Starter prompts per plugin id; only plugins with a switched-on tool are offered. */
const STARTERS: Starter[] = [
  { plugin: 'core-tools', icon: 'ruler', text: 'How long are the walls on this level?' },
  { plugin: 'core-tools', icon: 'palette', text: 'Colour the walls in this view by type' },
  { plugin: 'structure-cuts', icon: 'scissors', text: 'Split the beams at the columns on this level' },
  { plugin: 'opening-tools', icon: 'box', text: 'Cut openings where ducts and pipes cross walls' },
  { plugin: 'view-tools', icon: 'sheet', text: 'Make a sheet set for the floor plans' },
  { plugin: 'export-tools', icon: 'export', text: 'Export the current view to PDF' },
  { plugin: 'qs-formwork', icon: 'ruler', text: 'Estimate the formwork area of the selected elements' },
];

export function startersFor(plugins: PluginInfo[], suggestions: string[]): Starter[] {
  if (suggestions.length) return suggestions.slice(0, 4).map(text => ({ plugin: '', icon: 'spark' as IconName, text }));
  const on = new Set(plugins.filter(p => p.tools.some(t => t.enabled)).map(p => p.id));
  const list = plugins.length ? STARTERS.filter(s => on.has(s.plugin)) : STARTERS;
  return list.slice(0, 4);
}

const INTRO_KEY = 'lai.introDone';

function readIntro(): boolean {
  try { return window.localStorage.getItem(INTRO_KEY) === '1'; } catch { return true; }
}

export function EmptyState({ plugins, suggestions, quota, onPick }: { plugins: PluginInfo[]; suggestions: string[]; quota: QuotaPayload | null; onPick: (text: string) => void }) {
  const [introDone, setIntroDone] = useState(readIntro);
  const tools = plugins.flatMap(p => p.tools);
  const on = tools.filter(t => t.enabled).length;
  const l3 = tools.filter(t => t.tier === 'L3');
  const l3On = l3.filter(t => t.enabled).length;
  const q = quotaLine(quota);
  const dismiss = () => {
    setIntroDone(true);
    try { window.localStorage.setItem(INTRO_KEY, '1'); } catch { /* storage blocked */ }
  };
  return (
    <div className="empty">
      {!introDone && (
        <div className="intro" style={{ margin: '0 0 12px' }}>
          <h2>Welcome to LoadingAI</h2>
          <p>Ask about the open model or tell it what to change. It works with Revit's own tools.</p>
          <p><b style={{ color: 'var(--text)' }}>You stay in control.</b> Reads run freely. Any change waits in a review card until you allow it, and each turn is one Ctrl+Z. The picker under the text box sets how much it may do alone.</p>
          <p>The built-in model is free for 100 requests a day. Add your own key any time in Settings.</p>
          <button type="button" className="btn btn-primary btn-sm" onClick={dismiss}>Got it</button>
        </div>
      )}
      <h2>What should we do in this model?</h2>
      <div className="muted">Reads run freely. Changes ask first. Each turn is one undo step.</div>
      <div style={{ marginTop: 6 }}>
        {startersFor(plugins, suggestions).map(s => (
          <button key={s.text} type="button" className="starter" onClick={() => onPick(s.text)}>
            <Icon name={s.icon} size={15} /><span>{s.text}</span>
          </button>
        ))}
      </div>
      <div className="faint" style={{ marginTop: 10, fontSize: 11.5 }}>
        {tools.length ? `${on}/${tools.length} tools on in ${plugins.length} plugins · L3 ${l3On ? `${l3On}/${l3.length} on` : 'off'}` : 'Loading tools…'}
        {q ? <> · <span className={q.tone === 'change' ? 'tone-change' : q.tone === 'danger' ? 'tone-danger' : undefined}>{q.text}</span></> : null}
      </div>
    </div>
  );
}
