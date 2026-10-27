import type {
  AppSettings, ApprovalPayload, Attachment, ChangeCategory, ChatMode, CheckpointInfo, ContextPayload, FromHost,
  PlanPayload, PluginInfo, PluginLoadIssue, PresetPayload, ProgressPayload, ProviderPreset, QuotaPayload, ReviewPayload,
  TodoPayload, ToolOutcome,
} from '../bridge';

/** The five user-owned permission levels (spec, ADR-0017). The model can never set this. */
export type PermissionLevel = 'readonly' | 'plan' | 'confirm' | 'autoEdit' | 'bypass';

/** Lifecycle of one tool call inside a turn. */
export type StepState = 'running' | 'review' | 'done' | 'failed' | 'denied' | 'timeout' | 'cancelled';

export type ReviewDecision = 'allowed' | 'denied' | 'timeout' | 'cancelled';

/**
 * A confirm the host is waiting on. `card` is the host's single in-chat review (ADR-0017); `approval` and
 * `preset` are the older two-card messages, kept so scripted demos and older hosts still render.
 */
export type Review = {
  kind: 'approval' | 'preset' | 'card';
  toolCallId: string;
  approval?: ApprovalPayload;
  preset?: PresetPayload;
  card?: ReviewPayload;
  decided?: ReviewDecision;
};

export type Step = {
  id: string;
  /** The host's id for this call (tool_start, tool_review, tool_end). Absent for steps built from legacy text. */
  callId?: string;
  name: string;
  tier?: string;
  destructive?: boolean;
  argsText: string;
  state: StepState;
  review?: Review;
  resultText?: string;
  outcome?: ToolOutcome;
  /** Host-measured run time. Falls back to endedAt - startedAt. */
  ms?: number;
  /** Some of the work was done and some failed. */
  partial?: boolean;
  /** Snapshot data URL from tool_end (P1). */
  image?: string;
  startedAt: number;
  endedAt?: number;
};

/** A message you sent mid-turn is a `steer` note. */
export type NoteKind = 'system' | 'trace' | 'error' | 'steer';
export type Note = { id: string; kind: NoteKind; text: string };

export type TurnOutcome = 'running' | 'done' | 'cancelled' | 'error';

/** What the turn changed in the model, as Revit reported it (turn_changes). */
export type TurnChanges = {
  added: number;
  modified: number;
  deleted: number;
  idsTruncated?: boolean;
  byCategory: ChangeCategory[];
};

export type Turn = {
  id: string;
  mode: ChatMode;
  userText: string;
  attachments?: Attachment[];
  steps: Step[];
  notes: Note[];
  reasoning?: { text: string; streaming: boolean };
  plan?: PlanPayload;
  todos: TodoPayload[];
  answer: string;
  answerStreaming: boolean;
  outcome: TurnOutcome;
  startedAt: number;
  endedAt?: number;
  /** The host's id for the turn (turn_start). Later host messages for the turn carry it. */
  hostId?: string;
  /** The permission level the turn started under. */
  level?: string;
  /** True once the host sent turn_start: step lifecycle then comes from tool_start / tool_end only. */
  structured?: boolean;
  changes?: TurnChanges;
  /** The turn's writes were undone (denied, failed or stopped after a write). */
  rolledBack?: boolean;
};

/** The transcript is turns plus standalone notes (welcome, /help, messages outside a turn). */
export type Entry = { kind: 'turn'; turn: Turn } | { kind: 'note'; note: Note };

/** Live state of the Run form while its review is pending. */
export type PendingReview = {
  stepId: string;
  toolCallId: string;
  kind: 'approval' | 'preset' | 'card';
  values: Record<string, unknown>;
  pickBusy: string | null;
};

export type ProviderTest = { id: string; ok?: boolean; message?: string; loading?: boolean };

export type PaneState = {
  entries: Entry[];
  running: boolean;
  pendingReview: PendingReview | null;
  progress: ProgressPayload | null;
  status: string;
  context: ContextPayload | null;
  quota: QuotaPayload | null;
  settings: AppSettings | null;
  catalog: ProviderPreset[];
  plugins: PluginInfo[];
  loadIssues: PluginLoadIssue[];
  checkpoints: CheckpointInfo[];
  suggestions: string[];
  providerTest: ProviderTest | null;
  models: Record<string, string[]>;
  modelsLoading: string | null;
  /** What the host announced it can do (host_caps). Empty for a host that announced nothing. */
  caps: string[];
  /** Revit's UI theme, once the host has said (theme). */
  hostDark: boolean | null;
  seq: number;
};

export type PaneAction =
  | { type: 'host'; msg: FromHost; at?: number }
  | { type: 'send'; text: string; mode: ChatMode; attachments?: Attachment[]; at?: number }
  | { type: 'note'; kind: NoteKind; text: string }
  | { type: 'decide'; toolCallId: string; decision: ReviewDecision }
  | { type: 'presetValue'; fieldId: string; value: unknown }
  | { type: 'presetPickStart'; fieldId: string }
  | { type: 'stop' }
  | { type: 'clear' }
  | { type: 'settingsLocal'; settings: AppSettings }
  | { type: 'providerTest'; test: ProviderTest | null }
  | { type: 'modelsLoading'; providerId: string | null };
