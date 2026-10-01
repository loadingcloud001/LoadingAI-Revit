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

/** Mirrors `ToolPresets.RenderDetail` in Core so the fallback reads the same as a real preview. */
export function renderValue(value: unknown): string {
  if (value === null || value === undefined) return NONE;
  if (typeof value === 'boolean') return value ? 'on' : 'off';
  if (typeof value === 'string') return value.length > 0 ? value : NONE;
  if (Array.isArray(value)) return `${value.length} item${value.length === 1 ? '' : 's'}`;
  if (isPlainObject(value)) return NONE;
  return String(value);
}

/** Preview rows when the host sent them; otherwise one row per field, resolved from values. */
export function previewRows(preset: PresetPayload): PreviewRowView[] {
  const rows = preset.preview?.rows;
  if (rows && rows.length > 0) return rows.map(r => ({ label: r.label, detail: r.detail }));

  return (preset.fields ?? []).map(f => ({
    label: f.label || f.id,
    detail: renderValue(preset.values?.[f.id])
  }));
}

export function previewWarnings(preset: PresetPayload): string[] {
  return preset.preview?.warnings ?? [];
}

export function receiptLine(preset: PresetPayload, outcome: 'confirmed' | 'cancelled'): string {
  return `${preset.toolName} \u00b7 ${outcome}`;
}
