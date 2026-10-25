import type { FromHost } from '../bridge';

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
  gate?: { toolCallId: string; allow: ScriptStep[]; deny: ScriptStep[] };
};

const args = (o: unknown) => JSON.stringify(o);

const splitArgs = args({ level: 'Level 2', dryRun: false, minFraction: 0.02 });

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
};

export function userTextOf(name: string): string {
  return SCENARIOS[name]?.prompt ?? '';
}
