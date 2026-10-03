import { describe, expect, it } from 'vitest';
import { approvalLines, formatApprovalArgs, previewRows, previewWarnings, receiptLine, runKicker } from './presetPreview';
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

describe('live values in the Run panel', () => {
  // The panel is the surface a person confirms. Showing the host's snapshot while they edit the
  // form means the summary can say "delete off" over a checked Delete box, or "stats" over a
  // delete_all selection.

  it('previewRows_LiveValuesWinOverHostSnapshot', () => {
    const p = preset({
      preview: {
        summary: '3 settings to confirm',
        rows: [{ label: 'Sheet thickness (mm)', detail: '18' }],
        warnings: []
      }
    });

    const rows = previewRows(p, { thicknessMm: 22, deleteExisting: false });

    expect(rows.find(r => r.label === 'Sheet thickness (mm)')?.detail).toBe('22');
  });

  it('previewWarnings_LiveValueTurnsWarningOn', () => {
    const p = preset({
      fields: [
        {
          id: 'deleteExisting',
          label: 'Delete existing panels first',
          kind: 'Boolean',
          warning: 'Existing panels will be deleted first.'
        }
      ]
    });

    expect(previewWarnings(p, { deleteExisting: false })).toEqual([]);
    expect(previewWarnings(p, { deleteExisting: true })).toEqual(['Existing panels will be deleted first.']);
  });

  it('previewWarnings_LiveZeroDoesNotWarn', () => {
    const p = preset({
      fields: [
        { id: 'continueFrom', label: 'Continue from', kind: 'Number', warning: 'Existing panels will be deleted first.' }
      ]
    });

    expect(previewWarnings(p, { continueFrom: 0 })).toEqual([]);
    expect(previewWarnings(p, { continueFrom: '0' })).toEqual([]);
  });
});

describe('runKicker', () => {
  it('runKicker_NeverClaimsCancelledRunRan', () => {
    // Stop and Cancel clear the pending preset immediately, so the heading must not assert that
    // the tool executed.
    expect(runKicker(true)).toBe('Run');
    expect(runKicker(false)).toBe('Result');
  });
});

describe('approvalLines', () => {
  it('prefers the host summary, one line per row', () => {
    expect(approvalLines({ args: '{"a":1}', summary: 'Delete 3 elements\n\nUndo with Ctrl+Z.' }))
      .toEqual(['Delete 3 elements', 'Undo with Ctrl+Z.']);
  });

  it('falls back to the flattened arguments', () => {
    expect(approvalLines({ args: '{"limit":5}' })).toEqual(['limit: 5']);
    expect(approvalLines({ args: '{}', summary: '   ' })).toEqual(['(no arguments)']);
  });
});
