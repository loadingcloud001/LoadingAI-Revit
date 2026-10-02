import type { PresetPayload, PreviewRowView } from '../bridge';

/**
 * Pure formatting for the preset surface. No React, no `window`, no host bridge — that is
 * what keeps these testable and keeps the pane component thin.
 */

const NONE = '-';
const EMPTY = '(empty)';
const NO_ARGS = '(no arguments)';

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Readable lines from an args JSON blob. The approval card must not dump escaped JSON. */
export function formatApprovalArgs(argsJson: string): string[] {
  const root = parseObject(argsJson);
  if (!root) return [NO_ARGS];

  const out: string[] = [];
  for (const [key, value] of Object.entries(root)) flatten(key, value, out);
  return out.length ? out : [NO_ARGS];
}

function parseObject(json: string): Record<string, unknown> | null {
  if (!json) return null;
  try {
    const parsed: unknown = JSON.parse(json);
    return isPlainObject(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function flatten(path: string, value: unknown, out: string[]): void {
  if (value === null || value === undefined) {
    out.push(`${path}: (none)`);
    return;
  }
  if (Array.isArray(value)) {
    out.push(value.length === 0 ? `${path}: ${EMPTY}` : `${path}: ${value.length} item${value.length === 1 ? '' : 's'}`);
    return;
  }
  if (isPlainObject(value)) {
    const entries = Object.entries(value);
    if (entries.length === 0) {
      out.push(`${path}: ${EMPTY}`);
      return;
    }
    for (const [key, nested] of entries) flatten(`${path}.${key}`, nested, out);
    return;
  }
  out.push(`${path}: ${String(value)}`);
}

/**
 * Renders one field value for the fallback rows. Matches `ToolPresets.RenderDetail` on the host
 * for the shapes a form actually produces (missing, null, boolean, number, string, array,
 * object). It is a close relative rather than an exact clone: the host prints a JSON number from
 * its raw text, so a float can format differently on the two paths.
 */
export function renderValue(value: unknown): string {
  if (value === null || value === undefined) return NONE;
  if (typeof value === 'boolean') return value ? 'on' : 'off';
  if (typeof value === 'string') return value.length > 0 ? value : NONE;
  if (Array.isArray(value)) return `${value.length} item${value.length === 1 ? '' : 's'}`;
  if (isPlainObject(value)) return NONE;
  return String(value);
}

/**
 * Truthiness for preview warnings, matching `ToolPresets.IsTruthyForWarning` on the host. More
 * liberal than a gate check on purpose: a false negative leaves a destructive setting unflagged,
 * while a false positive costs one extra line of caution.
 */
function isTruthy(value: unknown): boolean {
  if (value === null || value === undefined) return false;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;
  if (typeof value === 'string') {
    const s = value.trim();
    return s.length > 0 && s !== '0' && s.toLowerCase() !== 'false';
  }
  if (Array.isArray(value)) return value.length > 0;
  return true;
}

/**
 * Rows for the Run panel. Pass the form's live values so the summary follows what the user is
 * editing; without them this falls back to the host's snapshot, then to the field declarations.
 */
export function previewRows(preset: PresetPayload, liveValues?: Record<string, unknown>): PreviewRowView[] {
  if (liveValues) return rowsFromFields(preset, liveValues);

  const rows = preset.preview?.rows;
  if (rows && rows.length > 0) return rows.map(r => ({ label: r.label, detail: r.detail }));

  return rowsFromFields(preset, preset.values ?? {});
}

function rowsFromFields(preset: PresetPayload, values: Record<string, unknown>): PreviewRowView[] {
  return (preset.fields ?? []).map(f => ({
    label: f.label || f.id,
    detail: renderValue(values[f.id])
  }));
}

/**
 * Warnings for the Run panel. With live values the warning is recomputed from the field's own
 * `warning`, so ticking "delete existing first" raises the red line immediately rather than at
 * the next host round trip.
 */
export function previewWarnings(preset: PresetPayload, liveValues?: Record<string, unknown>): string[] {
  if (liveValues) {
    return (preset.fields ?? [])
      .filter(f => !!f.warning && isTruthy(liveValues[f.id]))
      .map(f => f.warning as string);
  }
  return preset.preview?.warnings ?? [];
}

/**
 * Heading for the transcript receipt. Stop and Cancel clear the pending preset immediately, so the
 * heading must not assert that the tool ran.
 */
export function runKicker(isPending: boolean): 'Run' | 'Result' {
  return isPending ? 'Run' : 'Result';
}

export function receiptLine(preset: PresetPayload, outcome: 'confirmed' | 'cancelled'): string {
  return `${preset.toolName} \u00b7 ${outcome}`;
}
