import { useState } from 'react';
import type { PresetFieldPayload, PresetPayload } from '../bridge';
import { effectLabel } from '../lib/copy';
import type { HostCaps } from '../lib/permissions';
import { approvalLines, previewRows, previewWarnings } from '../lib/presetPreview';
import type { PendingReview, Step } from '../state/types';
import { Icon } from '../ui/Icon';

type Props = {
  step: Step;
  pending: PendingReview;
  caps: HostCaps;
  onApprove: (toolCallId: string, approved: boolean) => void;
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
export function ReviewCard({ step, pending, caps, onApprove, onSubmitPreset, onValue, onPick }: Props) {
  const review = step.review!;
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
