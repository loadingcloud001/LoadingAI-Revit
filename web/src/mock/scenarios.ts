import type { FromHost, ReviewPayload } from '../bridge';

/**
 * Scripted host conversations, built from what ChatSession / ApprovalGate post today. They are
 * the reducer test fixtures and, with `?demo=<name>`, what the browser mock host replays.
 * `at` is milliseconds after Send. A `gate` step pauses the script until the pane answers the
 * review; `allow` / `deny` hold the two continuations.
 */
export type ScriptStep = { at: number; msg: FromHost };
export type Scenario = {
  label: string;
  prompt: string;
  steps: ScriptStep[];
  /** Capabilities the mock host announces. Present for the structured (ADR-0017) scenarios. */
  caps?: string[];
  /** `edit` answers an approveTool that carries edited args; `denyReason` an approveTool(false) with a reason. */
  gate?: { toolCallId: string; allow: ScriptStep[]; deny: ScriptStep[]; edit?: ScriptStep[] };
};

export const ALL_CAPS = ['singleGate', 'levels', 'autoEdit', 'reviewScopes', 'structuredTurns', 'changes', 'steer', 'theme', 'instructions'];

const args = (o: unknown) => JSON.stringify(o);

const splitArgs = args({ level: 'Level 2', dryRun: false, minFraction: 0.02 });

/** A tiny 64x48 PNG, so the snapshot demo has a thumbnail to show without shipping an asset. */
const SNAPSHOT_PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAWklEQVR42u3ZsQkAIAwEwIwiFg7ldE7nHO4gCkoOvn64lPkotX2dAAAAAAC4AuhjPpUdwJELHekBAAAAAAAAAAAAAAAAAAAAAAAAAADIA/j7vW6hAQAAAEgBWIJD8XzuN00pAAAAAElFTkSuQmCC';

export const SCENARIOS: Record<string, Scenario> = {
  l1: {
    label: 'L1 read: wall metrics',
    prompt: 'How long are the walls on Level 2?',
    steps: [
      { at: 0, msg: { type: 'progress', progress: { phase: 'thinking', label: 'Thinking…', indeterminate: true } } },
      { at: 400, msg: { type: 'tool_call', text: `get_wall_metrics: ${args({ level: 'Level 2' })}` } },
      { at: 900, msg: { type: 'tool_result', text: "get_wall_metrics result: Walls: 14 on level 'Level 2'\nTotal length: 78400 mm\nTotal area: 266.56 m²\nTotal volume: 51.408 m³" } },
      { at: 1300, msg: { type: 'assistant', text: 'Level 2 has **14 walls**, 78.4 m long in total (266.56 m², 51.4 m³).' } },
      { at: 1300, msg: { type: 'progress', progress: { phase: 'idle', label: '' } } },
    ],
  },

  streaming: {
    label: 'Streaming answer',
    prompt: 'Explain the levels',
    steps: [
      { at: 200, msg: { type: 'delta', text: 'There are ' } },
      { at: 400, msg: { type: 'delta', text: 'four levels: ' } },
      { at: 600, msg: { type: 'delta', text: 'Level 1 to Level 4.' } },
      { at: 800, msg: { type: 'assistant', text: 'There are four levels: Level 1 to Level 4.' } },
    ],
  },

  l2Preset: {
    label: 'L2 Run form: colour by type',
    prompt: 'Colour the walls by type',
    steps: [
      { at: 300, msg: { type: 'tool_preset', preset: {
        toolCallId: 'p1', toolName: 'color_by_parameter', tier: 'L2', description: 'Colour elements in a view by a parameter value.',
        fields: [
          { id: 'parameter', label: 'Parameter', kind: 'String', required: true },
          { id: 'palette', label: 'Palette', kind: 'Select', options: ['heat', 'rainbow', 'mono'] },
          { id: 'elements', label: 'Elements', kind: 'PickElements', category: 'Walls' },
        ],
        values: { parameter: 'Type', palette: 'heat' },
        preview: { summary: 'Colour 42 walls in {3D} by Type', rows: [{ label: 'View', detail: '{3D}' }, { label: 'Values', detail: '4' }], warnings: [] },
      } } },
      { at: 300, msg: { type: 'tool_call', text: `color_by_parameter: ${args({ parameter: 'Type' })} (awaiting preset p1)` } },
    ],
    gate: {
      toolCallId: 'p1',
      allow: [
        { at: 100, msg: { type: 'tool_call', text: `color_by_parameter: ${args({ parameter: 'Type', palette: 'heat' })}` } },
        { at: 600, msg: { type: 'tool_result', text: "color_by_parameter result: Colored 42 element(s) in '{3D}' by 'Type' (4 values). Palette heat." } },
        { at: 900, msg: { type: 'assistant', text: 'Coloured 42 walls by type with the heat palette.' } },
      ],
      deny: [
        { at: 100, msg: { type: 'tool_result', text: 'color_by_parameter Cancelled by user.' } },
        { at: 400, msg: { type: 'assistant', text: 'Okay, I left the colours as they were.' } },
      ],
    },
  },

  l3Approval: {
    label: 'L3 approval: split beams',
    prompt: 'Split the Level 2 beams at the columns',
    steps: [
      { at: 300, msg: { type: 'tool_call', text: `get_beams: ${args({ level: 'Level 2' })}` } },
      { at: 600, msg: { type: 'tool_result', text: 'get_beams result: 8 beams on Level 2' } },
      { at: 900, msg: { type: 'tool_approval', approval: {
        toolCallId: 'a1', toolName: 'split_beams_at_columns', tier: 'L3', args: splitArgs,
        description: 'Split beams at column faces.', summary: 'Split 8 beams into 36 pieces\nLevel 2 · parameters kept',
      } } },
      { at: 900, msg: { type: 'tool_call', text: `split_beams_at_columns: ${splitArgs} (awaiting approval a1)` } },
    ],
    gate: {
      toolCallId: 'a1',
      allow: [
        { at: 800, msg: { type: 'tool_result', text: 'split_beams_at_columns result: Split 8 beams into 36 pieces. Native split preserved parameters.' } },
        { at: 1100, msg: { type: 'assistant', text: 'Split 8 beams into 36 pieces. Undo with Ctrl+Z.' } },
      ],
      deny: [
        { at: 100, msg: { type: 'tool_result', text: 'split_beams_at_columns denied by user' } },
        { at: 400, msg: { type: 'assistant', text: 'Understood, nothing was split.' } },
      ],
    },
  },

  l3Timeout: {
    label: 'L3 approval timeout',
    prompt: 'Delete the unused levels',
    steps: [
      { at: 200, msg: { type: 'tool_approval', approval: { toolCallId: 't1', toolName: 'delete_elements', tier: 'L3', args: args({ ids: [1, 2] }) } } },
      { at: 200, msg: { type: 'tool_call', text: `delete_elements: ${args({ ids: [1, 2] })} (awaiting approval t1)` } },
      { at: 1500, msg: { type: 'tool_result', text: 'delete_elements timed out waiting for approval (180s)' } },
      { at: 1800, msg: { type: 'assistant', text: 'The approval timed out, so nothing was deleted.' } },
    ],
  },

  dryRun: {
    label: 'Dry run (no review)',
    prompt: 'Dry-run the beam split',
    steps: [
      { at: 300, msg: { type: 'tool_call', text: `split_beams_at_columns: ${args({ level: 'Level 2', dryRun: true })}` } },
      { at: 800, msg: { type: 'tool_result', text: 'split_beams_at_columns result: Dry-run. Would split 8 beams into 36 pieces. No changes made.' } },
      { at: 1000, msg: { type: 'assistant', text: 'A real run would split 8 beams into 36 pieces.' } },
    ],
  },

  toolError: {
    label: 'Tool argument error',
    prompt: 'Tag everything',
    steps: [
      { at: 300, msg: { type: 'tool_result', text: 'Tool argument error: category is required' } },
      { at: 600, msg: { type: 'assistant', text: 'I need a category to tag.' } },
    ],
  },

  providerError: {
    label: 'Provider error',
    prompt: 'Hello',
    steps: [
      { at: 400, msg: { type: 'error', text: '401 Unauthorized: invalid API key' } },
    ],
  },

  plan: {
    label: 'Plan mode',
    prompt: '/plan cut openings where ducts cross walls',
    steps: [
      { at: 400, msg: { type: 'plan', plan: { title: 'Cut MEP openings', steps: [
        { title: 'Find duct and pipe clashes with walls', tool: 'find_clashes' },
        { title: 'Create openings at each clash', tool: 'create_openings_from_clash' },
      ] } } },
      { at: 700, msg: { type: 'assistant', text: 'Here is the plan. Run it when ready.' } },
    ],
  },

  quotaLow: {
    label: 'Free quota running low',
    prompt: 'Hi',
    steps: [
      { at: 0, msg: { type: 'quota', quota: { providerId: 'loadingai', limit: 100, used: 93, remaining: 7, ok: true } } },
      { at: 300, msg: { type: 'assistant', text: 'Hello! What should we do in this model?' } },
    ],
  },

  snapshot: {
    label: 'Snapshot thumbnail on a step',
    prompt: 'Capture the Level 2 view',
    caps: ALL_CAPS,
    steps: [
      { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'bypass' } },
      { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'v1', name: 'capture_view', tier: 'L1', destructive: false, args: args({ level: 'Level 2' }), startedAt: 200 } },
      { at: 700, msg: { type: 'tool_end', toolCallId: 'v1', ok: true, outcome: 'ran', summary: 'Snapshot of Level 2', text: 'Snapshot of Level 2', ms: 500, image: SNAPSHOT_PNG } },
      { at: 800, msg: { type: 'assistant', text: 'Here is the current Level 2 view.' } },
      { at: 850, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
  },
};

const splitReview: ReviewPayload = {
  toolCallId: 'c2', toolName: 'split_beams_at_columns', tier: 'L3', destructive: false,
  description: 'Split beams at column faces.',
  summary: 'Split 8 beams into 36 pieces\nLevel 2 · parameters kept',
  rows: [{ label: 'Level', detail: 'Level 2' }, { label: 'Beams', detail: '8' }, { label: 'New pieces', detail: '36' }],
  warnings: ['The whole turn is one undo step.'],
  canAlways: true, canSession: true, args: splitArgs,
};

const deleteReview: ReviewPayload = {
  toolCallId: 'd1', toolName: 'delete_elements', tier: 'L3', destructive: true,
  summary: 'Delete 12 elements\n9 unused view filters · 3 empty tags',
  rows: [{ label: 'Elements', detail: '12' }, { label: 'Dependents', detail: 'up to 4 more may go with them' }],
  warnings: ['Deleting cannot be partly undone once other work follows. Ctrl+Z undoes the whole turn.'],
  canAlways: false, canSession: false, args: args({ ids: [101, 102, 103] }),
};

const colourReview: ReviewPayload = {
  toolCallId: 'f1', toolName: 'color_by_parameter', tier: 'L2', destructive: false,
  description: 'Colour elements in a view by a parameter value.',
  summary: 'Colour 42 walls in {3D} by Type',
  rows: [{ label: 'View', detail: '{3D}' }, { label: 'Values', detail: '4' }],
  warnings: [],
  canAlways: false, canSession: false, args: args({ parameter: 'Type', palette: 'heat' }),
  fields: [
    { id: 'parameter', label: 'Parameter', kind: 'String', required: true },
    { id: 'palette', label: 'Palette', kind: 'Select', options: ['heat', 'rainbow', 'mono'] },
    { id: 'dryRun', label: 'Dry run', kind: 'Boolean' },
  ],
  values: { parameter: 'Type', palette: 'heat', dryRun: false },
};

const wallChanges = {
  type: 'turn_changes' as const, turnId: 'T1', added: 0, modified: 42, deleted: 0,
  byCategory: [{ category: 'Walls', added: 0, modified: 42, deleted: 0, ids: Array.from({ length: 42 }, (_, i) => 5000 + i) }],
};

SCENARIOS.reviewL3 = {
  label: 'Host review: L3 card with scopes, deny reason, edit inputs',
  prompt: 'Split the Level 2 beams at the columns',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'confirm' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'c1', name: 'get_beams', tier: 'L1', destructive: false, args: args({ level: 'Level 2' }), startedAt: 200 } },
    { at: 500, msg: { type: 'tool_end', toolCallId: 'c1', ok: true, outcome: 'ran', summary: '8 beams on Level 2', text: 'get_beams result: 8 beams on Level 2', ms: 300 } },
    { at: 700, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'c2', name: 'split_beams_at_columns', tier: 'L3', destructive: false, args: splitArgs, startedAt: 700 } },
    { at: 750, msg: { type: 'tool_review', review: splitReview } },
  ],
  gate: {
    toolCallId: 'c2',
    allow: [
      { at: 600, msg: { type: 'tool_end', toolCallId: 'c2', ok: true, outcome: 'ran', summary: 'Split 8 beams into 36 pieces', text: 'split_beams_at_columns result: Split 8 beams into 36 pieces.', ms: 600 } },
      { at: 700, msg: { type: 'turn_changes', turnId: 'T1', added: 28, modified: 8, deleted: 0, byCategory: [{ category: 'Structural Framing', added: 28, modified: 8, deleted: 0, ids: Array.from({ length: 36 }, (_, i) => 9000 + i) }] } },
      { at: 800, msg: { type: 'assistant', text: 'Split 8 beams into 36 pieces. Undo with Ctrl+Z.' } },
      { at: 850, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
    deny: [
      { at: 100, msg: { type: 'tool_end', toolCallId: 'c2', ok: false, outcome: 'denied', summary: 'Denied', text: 'Cancelled by user (denied): only the Level 3 beams', ms: 0 } },
      { at: 400, msg: { type: 'assistant', text: 'Understood. I will leave Level 2 alone.' } },
      { at: 450, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
    edit: [
      { at: 150, msg: { type: 'tool_review', review: { ...splitReview, edited: true, args: args({ level: 'Level 3', dryRun: false, minFraction: 0.02 }), summary: 'Split 5 beams into 21 pieces\nLevel 3 · parameters kept', rows: [{ label: 'Level', detail: 'Level 3' }, { label: 'Beams', detail: '5' }, { label: 'New pieces', detail: '21' }] } } },
    ],
  },
};

SCENARIOS.reviewDestructive = {
  label: 'Host review: destructive delete, no scopes',
  prompt: 'Delete the unused filters and empty tags',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'confirm' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'd1', name: 'delete_elements', tier: 'L3', destructive: true, args: args({ ids: [101, 102, 103] }), startedAt: 200 } },
    { at: 250, msg: { type: 'tool_review', review: deleteReview } },
  ],
  gate: {
    toolCallId: 'd1',
    allow: [
      { at: 500, msg: { type: 'tool_end', toolCallId: 'd1', ok: true, outcome: 'ran', summary: 'Deleted 14 elements', text: 'delete_elements result: Deleted 12 elements (14 with dependents).', ms: 500 } },
      { at: 600, msg: { type: 'turn_changes', turnId: 'T1', added: 0, modified: 0, deleted: 14, byCategory: [{ category: 'View Filters', added: 0, modified: 0, deleted: 9, ids: [] }, { category: 'Tags', added: 0, modified: 0, deleted: 5, ids: [] }] } },
      { at: 700, msg: { type: 'assistant', text: 'Deleted 12 elements and 2 dependents.' } },
      { at: 750, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
    deny: [
      { at: 100, msg: { type: 'tool_end', toolCallId: 'd1', ok: false, outcome: 'denied', summary: 'Denied', text: 'Cancelled by user (denied).', ms: 0 } },
      { at: 300, msg: { type: 'assistant', text: 'Nothing was deleted.' } },
      { at: 350, msg: { type: 'turn_end', turnId: 'T1', outcome: 'error', rolledBack: true } },
    ],
  },
};

SCENARIOS.reviewForm = {
  label: 'Host review: Run form (colour by parameter)',
  prompt: 'Colour the walls by type',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'confirm' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'f1', name: 'color_by_parameter', tier: 'L2', destructive: false, args: '{}', startedAt: 200 } },
    { at: 250, msg: { type: 'tool_review', review: colourReview } },
  ],
  gate: {
    toolCallId: 'f1',
    allow: [
      { at: 400, msg: { type: 'tool_end', toolCallId: 'f1', ok: true, outcome: 'ran', summary: 'Coloured 42 elements', text: "color_by_parameter result: Colored 42 element(s) in '{3D}' by 'Type' (4 values).", ms: 400 } },
      { at: 500, msg: wallChanges },
      { at: 600, msg: { type: 'assistant', text: 'Coloured 42 walls by type.' } },
      { at: 650, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
    deny: [
      { at: 100, msg: { type: 'tool_end', toolCallId: 'f1', ok: false, outcome: 'cancelled', summary: 'Cancelled', text: 'Cancelled by user.', ms: 0 } },
      { at: 300, msg: { type: 'assistant', text: 'Okay, no colours were applied.' } },
      { at: 350, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
    ],
  },
};

SCENARIOS.receipt = {
  label: 'Change receipt after an auto-run turn',
  prompt: 'Set Comments on every wall to "checked"',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'bypass' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'r1', name: 'set_parameter', tier: 'L2', destructive: false, args: args({ parameter: 'Comments', value: 'checked' }), startedAt: 200 } },
    { at: 700, msg: { type: 'tool_end', toolCallId: 'r1', ok: true, outcome: 'ran', summary: 'Set Comments on 42 walls', text: 'set_parameter result: Set Comments on 42 elements.', ms: 500 } },
    { at: 800, msg: { ...wallChanges, added: 3, modified: 39, byCategory: [{ category: 'Walls', added: 3, modified: 39, deleted: 0, ids: wallChanges.byCategory[0].ids }, { category: 'Doors', added: 0, modified: 0, deleted: 2, ids: [] }], deleted: 2 } },
    { at: 900, msg: { type: 'assistant', text: 'Set Comments on 42 walls.' } },
    { at: 950, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
  ],
};

SCENARIOS.blocked = {
  label: 'Read-only level blocks a change',
  prompt: 'Delete level 5',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'ask', level: 'readonly' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'b1', name: 'delete_elements', tier: 'L3', destructive: true, args: args({ ids: [7] }), startedAt: 200 } },
    { at: 300, msg: { type: 'tool_end', toolCallId: 'b1', ok: false, outcome: 'blocked', summary: 'Blocked at Read only', text: 'Blocked: the permission level is Read only. Ask the user to change it.', ms: 0 } },
    { at: 600, msg: { type: 'assistant', text: 'Read only is on, so I did not delete anything. Switch to Ask before changes to let me try.' } },
    { at: 650, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
  ],
};

SCENARIOS.rolledBack = {
  label: 'A later failure rolls the turn back',
  prompt: 'Rename the sheets then renumber them',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'bypass' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'x1', name: 'rename_sheets', tier: 'L2', destructive: false, args: '{}', startedAt: 200 } },
    { at: 500, msg: { type: 'tool_end', toolCallId: 'x1', ok: true, outcome: 'ran', summary: 'Renamed 6 sheets', text: 'rename_sheets result: Renamed 6 sheets.', ms: 300 } },
    { at: 600, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 'x2', name: 'renumber_sheets', tier: 'L2', destructive: false, args: '{}', startedAt: 600 } },
    { at: 900, msg: { type: 'tool_end', toolCallId: 'x2', ok: false, outcome: 'error', summary: 'Failed', text: 'renumber_sheets failed: sheet number A-101 already exists', ms: 300 } },
    { at: 1000, msg: { type: 'assistant', text: 'The renumber failed, so I undid the renames too. Nothing changed.' } },
    { at: 1050, msg: { type: 'turn_end', turnId: 'T1', outcome: 'error', rolledBack: true } },
  ],
};

SCENARIOS.steer = {
  label: 'Steer a running turn',
  prompt: 'Audit the model',
  caps: ALL_CAPS,
  steps: [
    { at: 0, msg: { type: 'turn_start', turnId: 'T1', mode: 'build', level: 'confirm' } },
    { at: 200, msg: { type: 'tool_start', turnId: 'T1', toolCallId: 's1', name: 'get_model_summary', tier: 'L1', destructive: false, args: '{}', startedAt: 200 } },
    // The turn stays open until the person acts: the mock ends it on the first steer.
  ],
  gate: {
    toolCallId: 's1',
    allow: [],
    deny: [],
  },
};

export function userTextOf(name: string): string {
  return SCENARIOS[name]?.prompt ?? '';
}
