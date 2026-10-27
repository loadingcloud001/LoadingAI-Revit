import { SCENARIOS, type ScriptStep } from './mock/scenarios';
export type ProviderConfig = {
  id: string;
  name: string;
  baseUrl: string;
  model: string;
  apiKey: string;
  apiKind?: string;
  group?: string;
  icon?: string;
  /** True when the model can read images, false when it cannot, null/undefined to guess from the model name. */
  supportsImages?: boolean | null;
};

export type ProviderPreset = {
  id: string;
  name: string;
  baseUrl: string;
  model: string;
  apiKind: string;
  group: string;
  icon: string;
  description: string;
};

export type AppSettings = {
  activeProviderId: string;
  providers: ProviderConfig[];
  plugins?: Record<string, boolean>;
  /** True exactly when permissionLevel is bypass. Kept for hosts from before levels. */
  autoApprove?: boolean;
  /** The user's permission level, stored by the host (ADR-0017). The model cannot set this. */
  permissionLevel?: 'readonly' | 'plan' | 'confirm' | 'autoEdit' | 'bypass';
  /** Tools the user chose "Always allow" for. The pane can remove entries; only the host adds them. */
  alwaysAllowTools?: string[];
  /** Select the added and modified elements in Revit after a turn that changed the model. */
  selectChangedAfterTurn?: boolean;
  /** Standing instructions for the agent, written into its system prompt. */
  instructions?: string;
};

/** Must match DefaultLlm.Id / DisplayName in C#. */
export const BUILTIN_PROVIDER_ID = 'loadingai';
export const BUILTIN_PROVIDER_NAME = 'LoadingAI';
export function isBuiltinProvider(id?: string) {
  return (id ?? '').toLowerCase() === BUILTIN_PROVIDER_ID;
}

/** saveSettings is providers only; tool flags go through setPluginEnabled / setPluginTierEnabled. */
export function providersOnly(s: AppSettings): AppSettings {
  const { plugins: _plugins, ...rest } = s;
  return rest;
}

export type PluginToolInfo = {
  name: string;
  tier: string;
  description: string;
  enabledByDefault: boolean;
  enabled: boolean;
  /** Deletes or replaces elements, or writes a file. Never offered "Always allow". */
  destructive?: boolean;
};

export type PluginInfo = {
  id: string;
  name: string;
  version: string;
  license: string;
  author: string;
  verified: boolean;
  folderPath: string;
  /** Where the host's trust for this DLL comes from. Display only. */
  source?: 'shipped' | 'trusted' | 'dev' | 'unknown';
  removable?: boolean;
  tools: PluginToolInfo[];
};

export type PluginLoadIssue = {
  folder: string;
  pluginId: string;
  assembly: string;
  summary: string;
  detail: string;
  hint: string;
  sha256?: string;
  /** Stable reason code; the pane keys on this, never on the wording. */
  code?: string;
  /** A note about a plugin that did load, not a failure. */
  notice?: boolean;
};

export type Attachment = {
  id: string;
  name: string;
  mediaType: string;
  dataUrl: string;
  sizeBytes: number;
  width?: number;
  height?: number;
};

export type ChatMode = 'ask' | 'plan' | 'build' | 'auto';

/** How long an Allow lasts. The host honours session and always only where the card offered them. */
export type ApprovalScope = 'once' | 'session' | 'always';

export type ToHost =
  | { type: 'send'; text: string; attachments?: Attachment[]; mode?: ChatMode; slash?: string; mentions?: string[] }
  | { type: 'clear' }
  | { type: 'cancel' }
  | { type: 'saveSettings'; settings: AppSettings }
  | { type: 'requestSettings' }
  | { type: 'getCatalog' }
  | { type: 'testProvider'; provider?: ProviderConfig; providerId?: string }
  | { type: 'listModels'; provider?: ProviderConfig; providerId?: string }
  | { type: 'listPlugins' }
  | { type: 'approveTool'; toolCallId: string; approved: boolean; scope?: ApprovalScope; reason?: string; args?: Record<string, unknown> }
  | { type: 'submitPreset'; toolCallId: string; approved: boolean; values?: Record<string, unknown> }
  | { type: 'presetPick'; toolCallId: string; fieldId: string }
  | { type: 'setPluginEnabled'; toolName: string; enabled: boolean }
  | { type: 'setPluginGroupEnabled'; pluginId: string; enabled: boolean }
  | { type: 'setPluginTierEnabled'; tier: string; enabled: boolean }
  | { type: 'saveToolSettings' }
  | { type: 'removePlugin'; pluginId: string }
  | { type: 'reloadPlugins' }
  | { type: 'trustPlugin'; folder: string; assembly: string }
  | { type: 'listCheckpoints' }
  | { type: 'resumeCheckpoint'; id: string }
  | { type: 'steer'; text: string }
  | { type: 'selectElements'; ids: number[] }
  | { type: 'showElements'; ids: number[] }
  | { type: 'isolateElements'; ids: number[] }
  | { type: 'listNotes' }
  | { type: 'saveNote'; scope: 'project' | 'global'; id?: string; text: string }
  | { type: 'deleteNote'; scope: 'project' | 'global'; id: string }
  | { type: 'clearNotes'; scope: 'project' | 'global' }
  | { type: 'listPlaybooks' };

export type PlanPayload = {
  title: string;
  description?: string;
  steps: { title: string; description?: string; tool?: string; status?: 'pending' | 'running' | 'done' | 'error' }[];
  isStreaming?: boolean;
};

export type TodoPayload = {
  id: string;
  title: string;
  tool?: string;
  status: 'pending' | 'running' | 'done' | 'error';
  error?: string;
};

export type ContextPayload = {
  selectionCount: number;
  selectionPreview?: string;
  activeView?: string;
  visibleCount?: number;
  warningCount?: number;
  warningPreview?: string;
};

/** Host-owned run/tool progress. Idle when phase is idle. */
export type ProgressPayload = {
  phase: 'idle' | 'thinking' | 'tool' | 'complete' | 'cancelled' | 'error' | string;
  label: string;
  tool?: string;
  percent?: number | null;
  current?: number;
  total?: number;
  indeterminate?: boolean;
};

export type CheckpointInfo = { id: string; createdAt: string; mode?: string; preview: string; messageCount: number };

export type ApprovalPayload = {
  toolCallId: string;
  toolName: string;
  tier: string;
  args: string;
  description?: string;
  summary?: string;
};

export type PresetFieldKind = 'String' | 'Number' | 'Boolean' | 'Select' | 'PickElements' | 'PickBox';

export type PresetFieldPayload = {
  id: string;
  label: string;
  kind: PresetFieldKind;
  required?: boolean;
  options?: string[];
  category?: string;
  prompt?: string;
  /** Red line shown in the Run panel when this field's live value is truthy. */
  warning?: string;
};

export type PreviewRowView = { label: string; detail: string };

/** Mirrors FromHostMessage.PreviewPayload. Absent means no preview is available. */
export type PreviewPayload = {
  summary: string;
  rows: PreviewRowView[];
  warnings: string[];
};

export type PresetPayload = {
  toolCallId: string;
  toolName: string;
  tier?: string;
  description?: string;
  fields: PresetFieldPayload[];
  values?: Record<string, unknown>;
  preview?: PreviewPayload;
};

/** The one in-chat review card (ADR-0017). Plain tools carry a summary or rows; tools with a Run form add fields. */
export type ReviewPayload = {
  toolCallId: string;
  toolName: string;
  tier: string;
  destructive: boolean;
  description?: string;
  summary: string;
  rows: PreviewRowView[];
  warnings: string[];
  canAlways: boolean;
  canSession: boolean;
  /** Raw argument JSON the card's edit box starts from. */
  args: string;
  fields?: PresetFieldPayload[];
  values?: Record<string, unknown>;
  /** True after the user edited the inputs and the host accepted them. */
  edited?: boolean;
};

export type ChangeCategory = { category: string; added: number; modified: number; deleted: number; ids: number[] };

export type ToolOutcome = 'ran' | 'denied' | 'cancelled' | 'timeout' | 'blocked' | 'error';

export type PresetPickResult = {
  toolCallId: string;
  fieldId: string;
  result?: {
    ok?: boolean;
    cancelled?: boolean;
    error?: string;
    ids?: string[];
    box?: { minMm?: number[]; maxMm?: number[] };
  };
  ok?: boolean;
  error?: string;
};

export type QuotaPayload = {
  providerId?: string;
  limit: number;
  used: number;
  remaining: number;
  resetUtc?: string;
  exhausted?: boolean;
  ok?: boolean;
  message?: string;
};

/** One saved note. Mirrors LoadingAI.Llm.Note as the host serialises it. */
export type Note = { id: string; text: string; createdUtc: string; source: string };

/** The notes of both scopes. hasProject is false with no open model. */
export type NotesPayload = {
  projectTitle?: string;
  hasProject: boolean;
  project: Note[];
  global: Note[];
  issues: string[];
};

/** One saved procedure. mode is absent when the playbook sets no mode. */
export type PlaybookInfo = {
  name: string;
  description: string;
  mode?: string;
  source: string;
  tools: string[];
};

export type FromHost =
  | { type: 'status'; text: string }
  | { type: 'trace'; text: string }
  | { type: 'tool_call'; text: string }
  | { type: 'tool_result'; text: string }
  | { type: 'tool_approval'; approval: ApprovalPayload }
  | { type: 'tool_preset'; preset: PresetPayload }
  | { type: 'presetPickResult'; toolCallId: string; fieldId: string; result?: PresetPickResult['result']; ok?: boolean; error?: string }
  | { type: 'plan'; plan: PlanPayload }
  | { type: 'todo'; todos: TodoPayload[] }
  | { type: 'progress'; progress: ProgressPayload }
  | { type: 'context'; context: ContextPayload }
  | { type: 'checkpoints'; checkpoints: CheckpointInfo[] }
  | { type: 'reasoning'; text: string; isStreaming?: boolean }
  | { type: 'suggestions'; suggestions: string[] }
  | { type: 'assistant'; text: string; attachments?: Attachment[] }
  | { type: 'settings'; settings: AppSettings }
  | { type: 'catalog'; presets: ProviderPreset[] }
  | { type: 'testResult'; ok: boolean; message: string; providerId?: string; models?: string[] }
  | { type: 'models'; providerId: string; models: string[]; error?: string }
  | { type: 'plugins'; plugins: PluginInfo[] }
  | { type: 'quota'; quota: QuotaPayload }
  | { type: 'history'; messages: { role: string; text: string; attachments?: Attachment[] }[] }
  | { type: 'error'; text: string }
  | { type: 'delta'; text: string }
  | { type: 'host_caps'; caps: string[] }
  | { type: 'theme'; dark: boolean }
  | { type: 'turn_start'; turnId: string; mode: string; level: string }
  | { type: 'turn_end'; turnId: string; outcome: string; rolledBack?: boolean }
  | { type: 'tool_start'; turnId: string; toolCallId: string; name: string; tier: string; destructive: boolean; args: string; startedAt: number }
  | { type: 'tool_end'; toolCallId: string; ok: boolean; outcome: ToolOutcome; summary: string; text: string; ms: number; partial?: boolean; image?: string }
  | { type: 'tool_review'; review: ReviewPayload }
  | { type: 'turn_changes'; turnId: string; added: number; modified: number; deleted: number; idsTruncated?: boolean; byCategory: ChangeCategory[] }
  | { type: 'steer_ack'; text: string; deliveredAt?: number }
  | { type: 'notes'; notes: NotesPayload }
  | { type: 'playbooks'; items: PlaybookInfo[]; issues: string[]; folder: string };

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (msg: unknown) => void;
        addEventListener: (event: string, handler: (e: { data: unknown }) => void) => void;
      };
    };
  }
}

/** The saved procedures the browser mock host knows about. */
const MOCK_PLAYBOOKS: PlaybookInfo[] = [
  { name: 'beam-split', description: 'Split beams at columns safely: dry run, review, split, check the receipt.', mode: 'build', source: 'starter', tools: ['resolve_names', 'find_elements_by_filter', 'split_beams_by_columns', 'capture_view'] },
  { name: 'export-package', description: 'Export the chosen sheets to PDF and DWG, and the model to IFC, into one folder.', mode: 'build', source: 'starter', tools: ['get_sheets_views', 'export_pdf', 'export_dwg', 'export_ifc'] },
  { name: 'model-qa', description: 'Check warnings, model health and room problems, then show the worst area.', mode: 'build', source: 'starter', tools: ['get_warnings', 'get_model_health_score', 'audit_issue_set', 'get_rooms', 'capture_view'] },
  { name: 'sheet-setup', description: 'Create sheets from a list, place a view on each and apply a view template.', mode: 'build', source: 'starter', tools: ['get_sheets_views', 'resolve_names', 'create_sheet', 'place_view_on_sheet', 'set_view_template'] },
];
const MOCK_PLAYBOOK_FOLDER = 'C:/Users/you/AppData/Roaming/LoadingAI/Playbooks';

export function postToHost(msg: ToHost) {
  if (window.chrome?.webview) {
    try {
      // Send as object for WebView2 to serialize; fallback to string if needed
      window.chrome.webview.postMessage(msg);
    } catch {
      try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch {}
    }
  } else {
    console.log('postToHost (no webview):', msg);
    if (playDemo(msg)) return;
    // For dev in browser, echo back mock
    if (msg.type === 'send') {
      setTimeout(() => {
        window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'trace', text: `Tool get_model_summary: {}` } }));
        setTimeout(() => {
          window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'assistant', text: `Mock reply to: ${msg.text}` } }));
        }, 500);
      }, 300);
    }
    if (msg.type === 'requestSettings') {
      const mock: AppSettings = {
        activeProviderId: BUILTIN_PROVIDER_ID,
        providers: [
          { id: BUILTIN_PROVIDER_ID, name: BUILTIN_PROVIDER_NAME, baseUrl: '', model: BUILTIN_PROVIDER_NAME, apiKey: '••••••••', apiKind: 'openai', group: 'cloud', icon: '✦' },
          { id: 'deepseek', name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', apiKey: '', apiKind: 'openai', group: 'cloud', icon: '◆' },
          { id: 'openai', name: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'cloud', icon: '◉', supportsImages: true },
          { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1', apiKey: 'ollama', apiKind: 'openai', group: 'local', icon: '⬢', supportsImages: false },
          { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'anthropic/claude-sonnet-4', apiKey: '', apiKind: 'openai', group: 'gateway', icon: '◎' },
          { id: 'opencode', name: 'OpenCode Zen', baseUrl: 'https://opencode.ai/zen/go/v1', model: 'muse-spark-1.2-contributor', apiKey: '', apiKind: 'responses', group: 'gateway', icon: '⚡' },
        ]
      };
      const demo = new URLSearchParams(window.location.search).get('demo');
      const caps = demo ? SCENARIOS[demo]?.caps : undefined;
      if (caps) {
        Object.assign(mock, { permissionLevel: 'confirm', alwaysAllowTools: ['rename_view', 'set_parameter'], selectChangedAfterTurn: true, instructions: '' });
        setTimeout(() => emit({ type: 'host_caps', caps }), 60);
        setTimeout(() => emit({ type: 'theme', dark: new URLSearchParams(window.location.search).get('revit') !== 'light' }), 80);
      }
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'settings', settings: mock } })), 100);
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'quota', quota: { providerId: BUILTIN_PROVIDER_ID, limit: 100, used: 3, remaining: 97, ok: true, resetUtc: new Date(Date.now() + 86400000).toISOString() } } })), 140);
      const mockPresets: any[] = [
        { id: 'deepseek', name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', apiKind: 'openai', group: 'cloud', icon: '◆', description: 'DeepSeek V3 / R1' },
        { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'anthropic/claude-sonnet-4', apiKind: 'openai', group: 'gateway', icon: '◎', description: '300+ models gateway' },
        { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1', apiKind: 'openai', group: 'local', icon: '⬢', description: 'Local Ollama' },
      ];
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'catalog', presets: mockPresets } })), 120);
      // The real host sends the playbooks with the caps, so the "/" menu has them from the first keypress.
      setTimeout(() => emit({ type: 'playbooks', items: MOCK_PLAYBOOKS, issues: [], folder: MOCK_PLAYBOOK_FOLDER }), 130);
    }
    if ((msg as any).type === 'testProvider') {
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'testResult', ok: true, message: 'Mock OK — 12 models', providerId: (msg as any).providerId ?? (msg as any).provider?.id } })), 600);
    }
    if ((msg as any).type === 'listModels') {
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'models', providerId: (msg as any).providerId ?? (msg as any).provider?.id ?? 'deepseek', models: ['deepseek-chat', 'deepseek-reasoner', 'gpt-4o-mini'] } })), 400);
    }
    if (msg.type === 'listNotes') {
      const notes: NotesPayload = {
        hasProject: true,
        projectTitle: 'Demo Tower',
        project: [{ id: 'n1', text: 'Level 1 is the lobby.', createdUtc: '2026-01-02T03:04:05Z', source: 'user' }],
        global: [{ id: 'n2', text: 'Answer in English.', createdUtc: '2026-01-02T03:05:05Z', source: 'user' }],
        issues: [],
      };
      setTimeout(() => emit({ type: 'notes', notes }), 200);
    }
    if (msg.type === 'listPlaybooks') {
      setTimeout(() => emit({ type: 'playbooks', items: MOCK_PLAYBOOKS, issues: [], folder: MOCK_PLAYBOOK_FOLDER }), 200);
    }
  }
}

function emit(detail: FromHost) {
  window.dispatchEvent(new CustomEvent('host-message', { detail }));
}

function playSteps(steps: ScriptStep[]) {
  for (const st of steps) setTimeout(() => emit(st.msg), st.at);
}

/** Browser-only mock: `?demo=<scenario>` replays a scripted host turn (see mock/scenarios.ts). */
function playDemo(msg: ToHost): boolean {
  const name = new URLSearchParams(window.location.search).get('demo');
  const sc = name ? SCENARIOS[name] : undefined;
  if (!sc) return false;
  if (msg.type === 'send') { playSteps(sc.steps); return true; }
  if (msg.type === 'approveTool' && sc.gate && msg.toolCallId === sc.gate.toolCallId && msg.args && sc.gate.edit) {
    playSteps(sc.gate.edit);
    return true;
  }
  if ((msg.type === 'approveTool' || msg.type === 'submitPreset') && sc.gate && msg.toolCallId === sc.gate.toolCallId) {
    playSteps(msg.approved ? sc.gate.allow : sc.gate.deny);
    return true;
  }
  if (msg.type === 'steer') {
    emit({ type: 'steer_ack', text: msg.text, deliveredAt: Date.now() });
    if (name === 'steer') {
      playSteps([
        { at: 400, msg: { type: 'tool_end', toolCallId: 's1', ok: true, outcome: 'ran', summary: 'Model summary', text: 'get_model_summary result: 1,204 elements', ms: 400 } },
        { at: 700, msg: { type: 'assistant', text: 'Got it, I will keep the audit to walls only.' } },
        { at: 750, msg: { type: 'turn_end', turnId: 'T1', outcome: 'done' } },
      ]);
    }
    return true;
  }
  if (msg.type === 'cancel') return true;
  return false;
}

export function onHostMessage(handler: (msg: FromHost) => void): () => void {
  if (window.chrome?.webview) {
    const listener = (e: { data: unknown }) => {
      let data: any = (e as any).data;
      if (typeof data === 'string') {
        try { data = JSON.parse(data); } catch {}
      }
      handler(data as FromHost);
    };
    window.chrome.webview.addEventListener('message', listener);
    return () => {
      try { (window.chrome!.webview as any).removeEventListener?.('message', listener); } catch {}
    };
  }
  const fn = (e: Event) => handler((e as CustomEvent).detail as FromHost);
  window.addEventListener('host-message' as any, fn);
  return () => window.removeEventListener('host-message' as any, fn);
}
