import { useEffect, useRef, useState } from 'react';
import type { ApprovalScope, PresetFieldPayload, PresetPayload, ReviewPayload } from '../bridge';
import { effectLabel } from '../lib/copy';
import type { HostCaps } from '../lib/permissions';
import { approvalLines, previewRows, previewWarnings } from '../lib/presetPreview';
import type { PendingReview, Step } from '../state/types';
import { Icon } from '../ui/Icon';

type Props = {
  step: Step;
  pending: PendingReview;
  caps: HostCaps;
  onApprove: (toolCallId: string, approved: boolean, opts?: { scope?: ApprovalScope; reason?: string }) => void;
  onEdit: (toolCallId: string, args: Record<string, unknown>) => void;
  onSubmitPreset: (approved: boolean, override?: Record<string, unknown>) => void;
  onValue: (fieldId: string, value: unknown) => void;
  onPick: (fieldId: string) => void;
};

function isFilled(value: unknown): boolean {
  if (value == null) return false;
  if (typeof value === 'string') return value.trim().length > 0;
  if (typeof value === 'number') return Number.isFinite(value);
  if (typeof value === 'boolean') return true;
  if (Array.isArray(value)) return value.length > 0;
  if (typeof value === 'object') {
    const box = value as { minMm?: unknown; maxMm?: unknown };
    return Array.isArray(box.minMm) && Array.isArray(box.maxMm);
  }
  return false;
}

function pickSummary(field: PresetFieldPayload, value: unknown): string {
  if (field.kind === 'PickElements') {
    const ids = Array.isArray(value) ? value : [];
    return ids.length ? `${ids.length} element${ids.length === 1 ? '' : 's'}` : 'Nothing picked';
  }
  const box = value as { minMm?: number[]; maxMm?: number[] } | null;
  if (box && Array.isArray(box.minMm) && Array.isArray(box.maxMm)) {
    return `${box.minMm.map(n => Math.round(n)).join(',')} → ${box.maxMm.map(n => Math.round(n)).join(',')} mm`;
  }
  return 'Nothing picked';
}

function dryRunField(preset: PresetPayload): PresetFieldPayload | undefined {
  return (preset.fields ?? []).find(f => f.kind === 'Boolean' && /^dry_?run$/i.test(f.id));
}

function Head({ tier, name }: { tier?: string; name: string }) {
  const l3 = tier === 'L3';
  return (
    <div className="card-head">
      <span className={l3 ? 'tone-change' : 'tone-accent'} style={{ fontWeight: 600 }}>Review change</span>
      {tier && <span className={`badge ${l3 ? 'badge-change' : ''}`}>{tier}</span>}
      <span className="grow" />
      <span className="mono faint truncate" style={{ maxWidth: '55%' }}>{name}</span>
    </div>
  );
}

/** The one place the pane asks before a change. Content comes from the host's preview, never from the model. */
export function ReviewCard({ step, pending, caps, onApprove, onEdit, onSubmitPreset, onValue, onPick }: Props) {
  const review = step.review!;
  if (review.kind === 'card' && review.card) {
    const card = review.card;
    if (card.fields?.length) {
      return <PresetForm preset={cardToPreset(card)} pending={pending} caps={caps} onSubmitPreset={onSubmitPreset} onValue={onValue} onPick={onPick} />;
    }
    return <HostCard key={card.toolCallId} card={card} onApprove={onApprove} onEdit={onEdit} />;
  }
  if (review.kind === 'approval' && review.approval) {
    const a = review.approval;
    const lines = approvalLines(a);
    const [first, ...rest] = lines;
    return (
      <div className={`card ${a.tier === 'L3' ? 'card-change' : 'card-accent'}`} role="group" aria-label={`Review ${a.toolName}`}>
        <Head tier={a.tier} name={a.toolName} />
        <div className="card-body">
          <div style={{ fontWeight: 600 }}>{first}</div>
          {rest.map((l, i) => <div key={`${i}-${l}`} className="muted mono" style={{ fontSize: 11 }}>{l}</div>)}
          {!caps.has('singleGate') && <div className="faint" style={{ marginTop: 5, fontSize: 11.5 }}>After Allow, Revit will ask once more. Default is No.</div>}
        </div>
        <div className="card-actions">
          <button type="button" className="btn" onClick={() => onApprove(review.toolCallId, false)}>Deny</button>
          <span className="grow" />
          <button type="button" className={a.tier === 'L3' ? 'btn btn-change' : 'btn btn-primary'} onClick={() => onApprove(review.toolCallId, true)} title={`Allow ${a.toolName}`}>
            <Icon name="check" size={13} />{effectLabel(a.summary, a.toolName)}
          </button>
        </div>
      </div>
    );
  }

  const preset = review.preset!;
  return <PresetForm preset={preset} pending={pending} caps={caps} onSubmitPreset={onSubmitPreset} onValue={onValue} onPick={onPick} />;
}

/** The host's review of a Run form, in the shape the form renders. */
function cardToPreset(card: ReviewPayload): PresetPayload {
  return {
    toolCallId: card.toolCallId,
    toolName: card.toolName,
    tier: card.tier,
    description: card.description,
    fields: card.fields ?? [],
    values: card.values,
    preview: { summary: card.summary, rows: card.rows, warnings: card.warnings },
  };
}

function prettyArgs(args: string): string {
  try { return JSON.stringify(JSON.parse(args), null, 2); } catch { return args; }
}

/**
 * The host's single review (ADR-0017): what will change, the warnings, and the ways to answer. Deny can carry a
 * reason the model sees; inputs can be edited before allowing; Allow can cover this session or always.
 */
function HostCard({ card, onApprove, onEdit }: { card: ReviewPayload; onApprove: Props['onApprove']; onEdit: Props['onEdit'] }) {
  const [showDesc, setShowDesc] = useState(false);
  const [menu, setMenu] = useState(false);
  const [mode, setMode] = useState<'view' | 'edit' | 'deny'>('view');
  const [draft, setDraft] = useState(() => prettyArgs(card.args));
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const ref = useRef<HTMLDivElement>(null);
  const l3 = card.tier === 'L3';
  const lines = card.summary.split('\n').map(l => l.trimEnd()).filter(Boolean);
  const [first, ...rest] = lines;
  const canScope = card.canSession || card.canAlways;

  // The host posts the card again once it accepted edited inputs.
  useEffect(() => { setDraft(prettyArgs(card.args)); setMode('view'); setError(''); }, [card.args]);

  // Bring the card into view and focus it, unless the person is typing somewhere else.
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.scrollIntoView?.({ block: 'nearest' });
    const a = document.activeElement;
    if (!a || a === document.body || !(a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement)) el.focus({ preventScroll: true });
  }, []);

  const applyEdit = () => {
    try {
      const parsed: unknown = JSON.parse(draft);
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) { setError('The inputs must be a JSON object.'); return; }
      setError('');
      onEdit(card.toolCallId, parsed as Record<string, unknown>);
    } catch (e: any) {
      setError(`That is not valid JSON: ${e?.message ?? 'parse error'}`);
    }
  };

  const allow = (scope?: ApprovalScope) => { setMenu(false); onApprove(card.toolCallId, true, { scope }); };

  return (
    <div
      ref={ref}
      tabIndex={-1}
      className={`card ${l3 ? 'card-change' : 'card-accent'}`}
      role="group"
      aria-label={`Review ${card.toolName}`}
      onKeyDown={e => {
        if (e.key !== 'Escape') return;
        if (menu) { e.stopPropagation(); setMenu(false); } else if (mode !== 'view') { e.stopPropagation(); setMode('view'); }
      }}
    >
      <div className="card-head">
        <span className={l3 ? 'tone-change' : 'tone-accent'} style={{ fontWeight: 600 }}>Review change</span>
        {card.tier && <span className={`badge ${l3 ? 'badge-change' : ''}`}>{card.tier}</span>}
        {card.destructive && <span className="badge badge-danger" title="Deletes or replaces elements, or writes a file">destructive</span>}
        {card.edited && <span className="badge">inputs edited</span>}
        <span className="grow" />
        <span className="mono faint truncate" style={{ maxWidth: '50%' }}>{card.toolName}</span>
      </div>
      <div className="card-body">
        {first && <div style={{ fontWeight: 600 }}>{first}</div>}
        {rest.map((l, i) => <div key={`${i}-${l}`} className="muted mono" style={{ fontSize: 11 }}>{l}</div>)}
        {card.description && (
          <button type="button" className="btn btn-ghost btn-sm" style={{ padding: 0, height: 18 }} aria-expanded={showDesc} onClick={() => setShowDesc(v => !v)}>
            <Icon name={showDesc ? 'chevronDown' : 'chevronRight'} size={12} />About this tool
          </button>
        )}
        {showDesc && <div className="muted" style={{ fontSize: 11.5 }}>{card.description}</div>}
        {card.warnings.map(w => <div key={w} className="warn-line"><Icon name="alert" size={12} /> {w}</div>)}
        {card.rows.length > 0 && (
          <div style={{ maxHeight: '30vh', overflowY: 'auto', marginTop: 4 }}>
            <div className="kv">
              {card.rows.map((r, i) => [<span key={`l${i}`}>{r.label}</span>, <span key={`d${i}`}>{r.detail}</span>])}
            </div>
          </div>
        )}
        {mode === 'edit' && (
          <div style={{ marginTop: 8, display: 'grid', gap: 4 }}>
            <label className="field">
              <span>Inputs (JSON). The host checks them again before it asks.</span>
              <textarea className="textarea mono" rows={Math.min(12, Math.max(4, draft.split('\n').length))} value={draft} spellCheck={false} onChange={e => { setDraft(e.target.value); setError(''); }} />
            </label>
            {error && <div className="warn-line" role="alert">{error}</div>}
            <div className="row">
              <button type="button" className="btn btn-sm" onClick={applyEdit}>Update review</button>
              <button type="button" className="btn btn-ghost btn-sm" onClick={() => { setDraft(prettyArgs(card.args)); setError(''); setMode('view'); }}>Discard edits</button>
            </div>
          </div>
        )}
        {mode === 'deny' && (
          <form
            style={{ marginTop: 8, display: 'grid', gap: 4 }}
            onSubmit={e => { e.preventDefault(); onApprove(card.toolCallId, false, { reason }); }}
          >
            <label className="field">
              <span>Tell the agent why (optional). It reads this and tries something else.</span>
              <input type="text" className="input" value={reason} maxLength={300} autoFocus onChange={e => setReason(e.target.value)} placeholder="For example: only the walls on Level 2" />
            </label>
            <div className="row">
              <button type="submit" className="btn btn-sm btn-danger">Deny{reason.trim() ? ' with reason' : ''}</button>
              <button type="button" className="btn btn-ghost btn-sm" onClick={() => setMode('view')}>Back</button>
            </div>
          </form>
        )}
      </div>
      <div className="card-actions">
        <button type="button" className="btn" onClick={() => onApprove(card.toolCallId, false)}>Deny</button>
        <button type="button" className="btn btn-ghost btn-sm" aria-expanded={mode === 'deny'} onClick={() => setMode(m => m === 'deny' ? 'view' : 'deny')}>Say why…</button>
        <button type="button" className="btn btn-ghost btn-sm" aria-expanded={mode === 'edit'} onClick={() => setMode(m => m === 'edit' ? 'view' : 'edit')}><Icon name="pencil" size={11} />Edit inputs</button>
        <span className="grow" />
        <span className="split">
          <button type="button" className={l3 ? 'btn btn-change' : 'btn btn-primary'} onClick={() => allow()} title={`Allow ${card.toolName} once`}>
            <Icon name="check" size={13} />{effectLabel(card.summary, card.toolName)}
          </button>
          {canScope && (
            <button
              type="button"
              className={`${l3 ? 'btn btn-change' : 'btn btn-primary'} split-caret`}
              aria-haspopup="menu"
              aria-expanded={menu}
              aria-label="More ways to allow"
              onClick={() => setMenu(v => !v)}
            ><Icon name="chevronDown" size={12} /></button>
          )}
          {menu && (
            <div className="popover" role="menu" style={{ bottom: 'calc(100% + 4px)', right: 0, minWidth: 230 }}>
              <button type="button" role="menuitem" className="menu-item" onClick={() => allow('once')}><span>Allow once</span></button>
              {card.canSession && (
                <button type="button" role="menuitem" className="menu-item" onClick={() => allow('session')}>
                  <span>Allow for this session<small>{card.toolName} stops asking until you close Revit.</small></span>
                </button>
              )}
              {card.canAlways && (
                <button type="button" role="menuitem" className="menu-item" onClick={() => allow('always')}>
                  <span>Always allow this tool<small>Remove it later in Settings → Tools.</small></span>
                </button>
              )}
            </div>
          )}
        </span>
      </div>
    </div>
  );
}

function PresetForm({ preset, pending, caps, onSubmitPreset, onValue, onPick }: {
  preset: PresetPayload;
  pending: PendingReview;
  caps: HostCaps;
  onSubmitPreset: Props['onSubmitPreset'];
  onValue: Props['onValue'];
  onPick: Props['onPick'];
}) {
  const [showDesc, setShowDesc] = useState(false);
  const values = pending.values;
  const fields = preset.fields ?? [];
  const canConfirm = fields.every(f => !f.required || isFilled(values[f.id])) && !pending.pickBusy;
  const dry = dryRunField(preset);
  const l3 = preset.tier === 'L3';
  const summary = preset.preview?.summary;
  return (
    <div className={`card ${l3 ? 'card-change' : 'card-accent'}`} role="group" aria-label={`Review ${preset.toolName}`}>
      <Head tier={preset.tier} name={preset.toolName} />
      <div className="card-body">
        {summary && <div style={{ fontWeight: 600 }}>{summary}</div>}
        {preset.description && (
          <button type="button" className="btn btn-ghost btn-sm" style={{ padding: 0, height: 18 }} aria-expanded={showDesc} onClick={() => setShowDesc(v => !v)}>
            <Icon name={showDesc ? 'chevronDown' : 'chevronRight'} size={12} />About this tool
          </button>
        )}
        {showDesc && <div className="muted" style={{ fontSize: 11.5 }}>{preset.description}</div>}
        {previewWarnings(preset, values).map(w => <div key={w} className="warn-line"><Icon name="alert" size={12} /> {w}</div>)}
        <div style={{ maxHeight: '38vh', overflowY: 'auto', marginTop: 4 }}>
          <div className="kv">
            {previewRows(preset, values).map((r, i) => [<span key={`l${i}`}>{r.label}</span>, <span key={`d${i}`}>{r.detail}</span>])}
          </div>
          {fields.length > 0 && (
            <div style={{ display: 'grid', gap: 7, marginTop: 8 }}>
              {fields.map(f => (
                <label key={f.id} className="field">
                  <span>{f.label || f.id}{f.required ? ' *' : ''}</span>
                  {f.kind === 'Boolean' ? (
                    <span className="toggle"><input type="checkbox" checked={!!values[f.id]} onChange={e => onValue(f.id, e.target.checked)} />{values[f.id] ? 'On' : 'Off'}</span>
                  ) : f.kind === 'Select' ? (
                    <select className="select" value={String(values[f.id] ?? '')} onChange={e => onValue(f.id, e.target.value)}>
                      <option value="">Select…</option>
                      {(f.options ?? []).map(o => <option key={o} value={o}>{o}</option>)}
                    </select>
                  ) : f.kind === 'Number' ? (
                    <input type="number" className="input" value={values[f.id] == null ? '' : String(values[f.id])} onChange={e => onValue(f.id, e.target.value === '' ? '' : Number(e.target.value))} />
                  ) : f.kind === 'PickElements' || f.kind === 'PickBox' ? (
                    <span className="row">
                      <span className="grow muted">{pickSummary(f, values[f.id])}</span>
                      <button type="button" className="btn btn-sm" disabled={!!pending.pickBusy} onClick={() => onPick(f.id)}>{pending.pickBusy === f.id ? 'Picking in Revit…' : 'Pick in Revit'}</button>
                    </span>
                  ) : (
                    <input type="text" className="input" value={String(values[f.id] ?? '')} onChange={e => onValue(f.id, e.target.value)} />
                  )}
                </label>
              ))}
            </div>
          )}
        </div>
        {l3 && !caps.has('singleGate') && <div className="faint" style={{ marginTop: 6, fontSize: 11.5 }}>After Confirm, Revit will ask once more. Default is No. A dry run skips both.</div>}
      </div>
      <div className="card-actions">
        <button type="button" className="btn" onClick={() => onSubmitPreset(false)}>Cancel</button>
        {dry && !values[dry.id] && (
          <button type="button" className="btn" disabled={!canConfirm} onClick={() => onSubmitPreset(true, { [dry.id]: true })}>Dry run</button>
        )}
        <span className="grow" />
        <button type="button" className={l3 ? 'btn btn-change' : 'btn btn-primary'} disabled={!canConfirm} onClick={() => onSubmitPreset(true)}>
          <Icon name="check" size={13} />{values[dry?.id ?? ''] ? 'Run dry run' : effectLabel(summary, preset.toolName)}
        </button>
      </div>
    </div>
  );
}
