import { useState } from 'react';
import type { ChangeCategory } from '../bridge';
import type { TurnChanges } from '../state/types';
import { Icon } from '../ui/Icon';

const ID_CHIP_CAP = 50;

export type ChangeActions = {
  onSelect: (ids: number[]) => void;
  onShow: (ids: number[]) => void;
  onIsolate: (ids: number[]) => void;
};

/** Every id the turn added or modified, once each. Deleted elements have no ids. */
export function changedIds(changes: TurnChanges): number[] {
  const seen = new Set<number>();
  for (const c of changes.byCategory) for (const id of c.ids) seen.add(id);
  return [...seen];
}

export function changesTotal(c: TurnChanges): number {
  return c.added + c.modified + c.deleted;
}

function plural(n: number, one: string, many: string) {
  return `${n} ${n === 1 ? one : many}`;
}

/** "3 added, 2 modified, 1 removed" with zero parts left out. */
export function changesLine(c: { added: number; modified: number; deleted: number }): string {
  const parts: string[] = [];
  if (c.added) parts.push(`${c.added} added`);
  if (c.modified) parts.push(`${c.modified} modified`);
  if (c.deleted) parts.push(`${c.deleted} removed`);
  return parts.join(', ');
}

function CategoryRow({ cat }: { cat: ChangeCategory }) {
  const [open, setOpen] = useState(false);
  const shown = cat.ids.slice(0, ID_CHIP_CAP);
  const more = cat.ids.length - shown.length;
  return (
    <li className="receipt-cat">
      <button
        type="button"
        className="receipt-cat-btn"
        aria-expanded={open}
        disabled={cat.ids.length === 0}
        onClick={() => setOpen(v => !v)}
      >
        <Icon name={open ? 'chevronDown' : 'chevronRight'} size={11} />
        <span className="grow truncate">{cat.category}</span>
        <span className="faint">{changesLine(cat)}</span>
      </button>
      {open && (
        <div className="receipt-ids" aria-label={`${cat.category} element ids`}>
          {shown.map(id => <span key={id} className="chip mono">{id}</span>)}
          {more > 0 && <span className="faint" style={{ fontSize: 11 }}>+{more} more</span>}
        </div>
      )}
    </li>
  );
}

/**
 * What the turn changed in the model, as Revit reported it (ADR-0017). The buttons only change what you see:
 * selection, zoom and temporary isolation never edit the model. Hidden after a rollback, because the changes are gone.
 */
export function ChangeReceipt({ changes, rolledBack, actions, busy }: { changes: TurnChanges; rolledBack?: boolean; actions: ChangeActions; busy?: boolean }) {
  if (rolledBack) {
    return <div className="note" role="status">Rolled back. This turn&apos;s changes were undone, so nothing in the model changed.</div>;
  }
  const total = changesTotal(changes);
  if (total === 0) return null;
  const ids = changedIds(changes);
  const canAct = ids.length > 0 && !busy;
  return (
    <div className="card" role="group" aria-label="Changes in the model">
      <div className="card-head">
        <Icon name="sheet" size={13} />
        <span style={{ fontWeight: 600 }}>Changed {plural(total, 'element', 'elements')}</span>
        <span className="grow" />
        <span className="faint">{changesLine(changes)}</span>
      </div>
      <div className="card-body">
        {changes.byCategory.length > 0 && (
          <ul className="receipt-cats">
            {changes.byCategory.map(c => <CategoryRow key={c.category} cat={c} />)}
          </ul>
        )}
        {changes.deleted > 0 && <div className="faint" style={{ fontSize: 11.5, marginTop: 3 }}>Removed counts include elements deleted along with what you asked for (dependents).</div>}
        {changes.idsTruncated && <div className="faint" style={{ fontSize: 11.5, marginTop: 3 }}>Counts are exact. Ids are listed for the first 2000 elements of each category.</div>}
      </div>
      {ids.length > 0 && (
        <div className="card-actions">
          <button type="button" className="btn btn-sm" disabled={!canAct} onClick={() => actions.onSelect(ids)} title="Select the added and modified elements in Revit">Select changed</button>
          <button type="button" className="btn btn-sm" disabled={!canAct} onClick={() => actions.onShow(ids)} title="Zoom the view to the changed elements">Zoom to</button>
          <button type="button" className="btn btn-sm" disabled={!canAct} onClick={() => actions.onIsolate(ids)} title="Temporarily isolate the changed elements in the active view">Isolate</button>
        </div>
      )}
    </div>
  );
}
