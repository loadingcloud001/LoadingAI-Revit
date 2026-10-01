import { describe, expect, it } from 'vitest';
import { formatApprovalArgs, previewRows, previewWarnings, receiptLine } from './presetPreview';
import type { PresetPayload } from '../bridge';

const preset = (over: Partial<PresetPayload> = {}): PresetPayload => ({
  toolCallId: 'call1',
  toolName: 'create_formwork_panels',
  tier: 'L3',
  fields: [
    { id: 'elementIds', label: 'Elements', kind: 'PickElements' },
    { id: 'thicknessMm', label: 'Sheet thickness (mm)', kind: 'Number' },
    { id: 'deleteExisting', label: 'Delete existing panels first', kind: 'Boolean' }
  ],
  values: { thicknessMm: 18, deleteExisting: false },
  ...over
});

describe('formatApprovalArgs', () => {
  it('formatApprovalArgs_EmitsReadableLines', () => {
    const lines = formatApprovalArgs('{"thicknessMm":18,"deleteExisting":false,"elementIds":[1,2]}');

    expect(lines).toContain('thicknessMm: 18');
    expect(lines).toContain('deleteExisting: false');
    expect(lines).toContain('elementIds: 2 items');
    // Review Focus 5: the approval card must stop dumping escaped JSON at the user.
    expect(lines.join('\n')).not.toContain('{');
  });

  it('formatApprovalArgs_HandlesNestedAndEmptyObjects', () => {
    const lines = formatApprovalArgs('{"outer":{"inner":1},"blank":{},"none":null,"list":[]}');

    expect(lines).toContain('outer.inner: 1');
    expect(lines).toContain('blank: (empty)');
    expect(lines).toContain('none: (none)');
    expect(lines).toContain('list: (empty)');
    expect(lines.join('\n')).not.toContain('{');
  });

  it('formatApprovalArgs_DegradesOnUnparseableArgs', () => {
    expect(formatApprovalArgs('not json')).toEqual(['(no arguments)']);
    expect(formatApprovalArgs('')).toEqual(['(no arguments)']);
  });
});

describe('previewRows', () => {
  it('previewRows_FallsBackToFieldRowsWhenNoPreview', () => {
    const rows = previewRows(preset());

    expect(rows.map(r => r.label)).toEqual([
      'Elements',
      'Sheet thickness (mm)',
      'Delete existing panels first'
    ]);
    expect(rows[1].detail).toBe('18');
    expect(rows[0].detail).toBe('-');
    expect(rows[2].detail).toBe('off');
  });

  it('previewRows_PrefersPreviewRowsWhenSent', () => {
    const rows = previewRows(preset({
      preview: {
        summary: '3 settings to confirm',
        rows: [{ label: 'Only this', detail: '99' }],
        warnings: []
      }
    }));

    expect(rows).toEqual([{ label: 'Only this', detail: '99' }]);
  });
});

describe('previewWarnings', () => {
  it('previewWarnings_EmptyWhenNoWarnings', () => {
    expect(previewWarnings(preset())).toEqual([]);
    expect(previewWarnings(preset({ preview: { summary: 's', rows: [], warnings: [] } }))).toEqual([]);
  });

  it('previewWarnings_SurfacesWarningsWhenSent', () => {
    const warnings = previewWarnings(preset({
      preview: {
        summary: 's',
        rows: [],
        warnings: ['Existing panels will be deleted first.']
      }
    }));

    expect(warnings).toEqual(['Existing panels will be deleted first.']);
  });
});

describe('receiptLine', () => {
  it('receiptLine_ShortAndStable', () => {
    expect(receiptLine(preset(), 'confirmed')).toBe('create_formwork_panels \u00b7 confirmed');
    expect(receiptLine(preset(), 'cancelled')).toBe('create_formwork_panels \u00b7 cancelled');
  });
});
