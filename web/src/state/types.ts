import type {
  AppSettings, ApprovalPayload, Attachment, ChatMode, CheckpointInfo, ContextPayload, FromHost,
  PlanPayload, PluginInfo, PluginLoadIssue, PresetPayload, ProgressPayload, ProviderPreset, QuotaPayload, TodoPayload,
} from '../bridge';

/** The five user-owned permission levels (spec, ADR-0017). The model can never set this. */
export type PermissionLevel = 'readonly' | 'plan' | 'confirm' | 'autoEdit' | 'bypass';

/** Lifecycle of one tool call inside a turn. */
export type StepState = 'running' | 'review' | 'done' | 'failed' | 'denied' | 'timeout' | 'cancelled';

export type ReviewDecision = 'allowed' | 'denied' | 'timeout' | 'cancelled';

/** A confirm the host is waiting on: the L3 approval card or a Run form. */
export type Review = {
  kind: 'approval' | 'preset';
  toolCallId: string;
  approval?: ApprovalPayload;
  preset?: PresetPayload;
  decided?: ReviewDecision;
};

export type Step = {
  id: string;
  name: string;
  tier?: string;
  argsText: string;
  state: StepState;
  review?: Review;
  resultText?: string;
  startedAt: number;
  endedAt?: number;
};

export type NoteKind = 'system' | 'trace' | 'error';
export type Note = { id: string; kind: NoteKind; text: string };

export type TurnOutcome = 'running' | 'done' | 'cancelled' | 'error';

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
};

/** The transcript is turns plus standalone notes (welcome, /help, messages outside a turn). */
export type Entry = { kind: 'turn'; turn: Turn } | { kind: 'note'; note: Note };

/** Live state of the Run form while its review is pending. */
export type PendingReview = {
  stepId: string;
  toolCallId: string;
  kind: 'approval' | 'preset';
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
