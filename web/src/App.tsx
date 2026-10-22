import { useEffect, useMemo, useRef, useState, useCallback, type CSSProperties } from 'react';
import ReactMarkdown from 'react-markdown';
import { AppSettings, Attachment, ChatMode, PlanPayload, ApprovalPayload, PresetPayload, PresetFieldPayload, ProviderConfig, ProviderPreset, QuotaPayload, TodoPayload, ContextPayload, CheckpointInfo, PluginInfo, PluginLoadIssue, ProgressPayload, onHostMessage, postToHost, BUILTIN_PROVIDER_NAME, isBuiltinProvider, providersOnly } from './bridge';
import { parseInput } from './cli/inputParser';
import { SLASH_COMMANDS, LOCAL_COMMANDS, filterCommands } from './cli/slashCommands';
import { isNearBottom } from './lib/scroll';
import { filterMentions } from './cli/mentions';
import { PluginShelf } from './plugins/PluginShelf';
import { classifyResult, outcomeLabel, TranscriptSlice } from './transcript';
import { approvalLines, previewRows, previewWarnings, receiptLine, runKicker } from './lib/presetPreview';
import 'open-model-selector/styles.css';
import { ModelSelector } from 'open-model-selector';

type Message = { id: string; role: 'you' | 'assistant' | 'trace' | 'system' | 'tool_call' | 'tool_result' | 'plan' | 'approval' | 'preset' | 'reasoning'; text: string; attachments?: Attachment[]; plan?: PlanPayload; approval?: ApprovalPayload; preset?: PresetPayload; isStreaming?: boolean };

function isFilledPresetValue(value: unknown): boolean {
  if (value == null) return false;
  if (typeof value === 'string') return value.trim().length > 0;
  if (typeof value === 'number') return Number.isFinite(value);
  if (typeof value === 'boolean') return true;
  if (Array.isArray(value)) return value.length > 0;
  if (typeof value === 'object') {
    const box = value as { minMm?: unknown; maxMm?: unknown };
    return Array.isArray(box.minMm) && Array.isArray(box.maxMm);
  }
  return false;
}

function requiredPresetsFilled(preset: PresetPayload, values: Record<string, unknown>): boolean {
  return (preset.fields ?? []).every(f => !f.required || isFilledPresetValue(values[f.id]));
}

function formatPickSummary(field: PresetFieldPayload, value: unknown): string {
  if (field.kind === 'PickElements') {
    const ids = Array.isArray(value) ? value : [];
    return ids.length ? `${ids.length} element${ids.length === 1 ? '' : 's'}` : 'None';
  }
  if (value && typeof value === 'object' && Array.isArray((value as any).minMm) && Array.isArray((value as any).maxMm)) {
    const min = (value as any).minMm as number[];
    const max = (value as any).maxMm as number[];
    return `${min.map(n => Math.round(n)).join(',')} → ${max.map(n => Math.round(n)).join(',')} mm`;
  }
  return 'None';
}

function confirmState(messages: Message[], toolCallId: string, toolName: string): string {
  const marker = `(awaiting approval ${toolCallId})`;
  const presetMarker = `(awaiting preset ${toolCallId})`;
  let start = -1;
  for (let i = messages.length - 1; i >= 0; i--) {
    const text = messages[i].text ?? '';
    if (text.includes(marker) || text.includes(presetMarker)) { start = i; break; }
  }
  const from = start >= 0 ? start + 1 : 0;
  for (let i = from; i < messages.length; i++) {
    const m = messages[i];
    if (m.role !== 'tool_result') continue;
    const c = classifyResult(m.text);
    if (c.name !== toolName) continue;
    if (c.kind === 'outcome') return outcomeLabel(c.outcome);
    // A real result after the confirm ask means the confirm was accepted and the tool ran.
    return c.kind === 'ran' ? 'Confirmed' : 'Done';
  }
  return 'Awaiting outcome…';
}

function normalizeSettings(raw: any): AppSettings | null {
  if (!raw || typeof raw !== 'object') return null;
  const active = raw.activeProviderId ?? raw.ActiveProviderId ?? '';
  const provs = raw.providers ?? raw.Providers ?? [];
  if (!Array.isArray(provs)) return null;
  const providers: ProviderConfig[] = provs.map((p: any) => ({
    id: p.id ?? p.Id ?? Math.random().toString(36).slice(2, 10),
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKey: p.apiKey ?? p.ApiKey ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
  }));
  const plugins = raw.plugins ?? raw.Plugins ?? undefined;
  const autoApprove = raw.autoApprove === true || raw.AutoApprove === true;
  return { activeProviderId: active, providers, plugins, autoApprove };
}
function normalizePresets(raw: any): ProviderPreset[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((p: any) => ({
    id: p.id ?? p.Id ?? '',
    name: p.name ?? p.Name ?? '',
    baseUrl: p.baseUrl ?? p.BaseUrl ?? '',
    model: p.model ?? p.Model ?? '',
    apiKind: p.apiKind ?? p.ApiKind ?? 'openai',
    group: p.group ?? p.Group ?? 'custom',
    icon: p.icon ?? p.Icon ?? '＋',
    description: p.description ?? p.Description ?? '',
  }));
}

async function compressImage(file: File, maxSide = 1280, maxBytes = 2.5 * 1024 * 1024): Promise<Attachment> {
  const bitmap = await createImageBitmap(file);
  let { width, height } = bitmap;
  if (Math.max(width, height) > maxSide) {
    const r = maxSide / Math.max(width, height);
    width = Math.round(width * r); height = Math.round(height * r);
  }
  const canvas = document.createElement('canvas');
  canvas.width = width; canvas.height = height;
  const ctx = canvas.getContext('2d')!;
  ctx.drawImage(bitmap, 0, 0, width, height);
  const mime = file.type === 'image/png' && width * height < 360000 ? 'image/png' : 'image/jpeg';
  let quality = 0.82;
  let dataUrl = canvas.toDataURL(mime, quality);
  while (dataUrl.length * 0.75 > maxBytes && quality > 0.38) {
    quality -= 0.1;
    dataUrl = canvas.toDataURL('image/jpeg', quality);
  }
  if (dataUrl.length * 0.75 > maxBytes) throw new Error(`Image still > ${Math.round(maxBytes / 1024)}KB after compress`);
  try { bitmap.close(); } catch {}
  return { id: crypto.randomUUID(), name: file.name || `pasted-${Date.now()}.jpg`, mediaType: mime, dataUrl, width, height, sizeBytes: Math.round(dataUrl.length * 0.75) };
}

const kLabel: CSSProperties = { fontSize: 10, fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase', color: '#6a6a6a', marginBottom: 3 };
const rowStyle: CSSProperties = { padding: '8px 10px', borderBottom: '1px solid #ececec', fontSize: 12.5, background: '#f7f7f7' };
const inp = 'w-full px-2 py-1.5 border bg-white text-xs outline-none';
const btn = 'px-2 py-1.5 border bg-white text-xs';

function ProgressBar({ progress }: { progress: ProgressPayload }) {
  const pct = typeof progress.percent === 'number' ? Math.max(0, Math.min(100, progress.percent)) : null;
  const indeterminate = progress.indeterminate || pct == null;
  const isError = progress.phase === 'error' || progress.phase === 'cancelled';
  const fill = isError ? '#8a1f11' : '#2b2b2b';
  return (
    <div style={{ padding: '4px 10px 6px', borderBottom: '1px solid #d0d0d0', background: '#f3f3f3', flexShrink: 0 }} aria-live="polite">
      <div style={{ fontSize: 10, color: '#5c5c5c', marginBottom: 3, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {progress.label || (progress.phase === 'thinking' ? 'Thinking…' : 'Working…')}
        {!indeterminate && pct != null ? ` · ${pct}%` : ''}
      </div>
      <div style={{ height: 3, background: '#d0d0d0', overflow: 'hidden', position: 'relative' }}>
        {indeterminate
          ? <div style={{ position: 'absolute', height: '100%', width: '36%', background: fill, animation: 'lai-progress 1.1s ease-in-out infinite' }} />
          : <div style={{ height: '100%', width: `${pct}%`, background: fill, transition: 'width 160ms linear' }} />}
      </div>
    </div>
  );
}

export default function App() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const settingsRef = useRef<AppSettings | null>(null);
  const [catalog, setCatalog] = useState<ProviderPreset[]>([]);
  const [selectedProviderId, setSelectedProviderId] = useState<string>('');
  const [showSettings, setShowSettings] = useState(false);
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [loadIssues, setLoadIssues] = useState<PluginLoadIssue[]>([]);
  const [editing, setEditing] = useState<ProviderConfig | null>(null);
  const [editForm, setEditForm] = useState({ name: '', baseUrl: '', model: '', apiKey: '', apiKind: 'openai' as string, group: 'custom' as string, icon: '＋' });
  const [editSearch, setEditSearch] = useState('');
  const [catalogFilter, setCatalogFilter] = useState('');
  const [testState, setTestState] = useState<{ id: string; ok?: boolean; message?: string; loading?: boolean } | null>(null);
  const [modelsMap, setModelsMap] = useState<Record<string, string[]>>({});
  const [quota, setQuota] = useState<QuotaPayload | null>(null);
  const [modelsLoading, setModelsLoading] = useState<string | null>(null);

  const [mode, setMode] = useState<ChatMode>('build');
  const [runMode, setRunMode] = useState<ChatMode>('build');
  const stickToBottomRef = useRef(true);
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [reasoning, setReasoning] = useState<{ text: string; isStreaming?: boolean } | null>(null);
  const [currentPlan, setCurrentPlan] = useState<PlanPayload | null>(null);
  const [todos, setTodos] = useState<TodoPayload[]>([]);
  const [todoOpen, setTodoOpen] = useState(false);
  const [todoDismissed, setTodoDismissed] = useState(false);
  const [presetDescOpen, setPresetDescOpen] = useState<Record<string, boolean>>({});
  const [progress, setProgress] = useState<ProgressPayload | null>(null);
  const [pendingApproval, setPendingApproval] = useState<ApprovalPayload | null>(null);
  const [pendingPreset, setPendingPreset] = useState<PresetPayload | null>(null);
  const [presetValues, setPresetValues] = useState<Record<string, unknown>>({});
  const [presetPickBusy, setPresetPickBusy] = useState<string | null>(null);
  const [context, setContext] = useState<ContextPayload | null>(null);
  const [checkpoints, setCheckpoints] = useState<CheckpointInfo[]>([]);

  const [messages, setMessages] = useState<Message[]>([
    { id: 'welcome', role: 'system', text: 'LoadingAI. Default Build. /ask /plan /help' }
  ]);
  const [input, setInput] = useState('');
  const [isRunning, setIsRunning] = useState(false);
  const [status, setStatus] = useState('Ready');

  const scrollRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const composerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const [slashOpen, setSlashOpen] = useState(false);
  const [slashQuery, setSlashQuery] = useState('');
  const [slashIndex, setSlashIndex] = useState(0);
  const [mentionOpen, setMentionOpen] = useState(false);
  const [mentionQuery, setMentionQuery] = useState('');
  const [mentionIndex, setMentionIndex] = useState(0);
  const [mentionStart, setMentionStart] = useState<number | null>(null);
  const inputHistoryRef = useRef<string[]>([]);
  const historyIdxRef = useRef(-1);

  useEffect(() => { settingsRef.current = settings; }, [settings]);
  const modeRef = useRef<ChatMode>('build');
  const editingRef = useRef<ProviderConfig | null>(null);
  useEffect(() => { modeRef.current = mode; }, [mode]);
  useEffect(() => { editingRef.current = editing; }, [editing]);
  const streamedRef = useRef(false);
  const pendingDeltaRef = useRef<string>('');
  const idRef = useRef(0);
  const nextId = () => `m${++idRef.current}`;

  const addFiles = useCallback(async (files: File[]) => {
    const imgs = files.filter(f => f.type.startsWith('image/')).slice(0, 5 - attachments.length);
    if (imgs.length === 0) { setStatus('Images only (png/jpg/webp/gif)'); return; }
    if (attachments.length + imgs.length > 5) { setStatus('Max 5 images'); return; }
    for (const f of imgs) {
      try {
        if (f.size > 12 * 1024 * 1024) { setStatus(`${f.name} over 12MB`); continue; }
        const att = await compressImage(f);
        setAttachments(a => [...a, att]);
      } catch (e: any) { setStatus(`Compress failed: ${e?.message ?? String(e)}`); }
    }
  }, [attachments.length]);

  const handlePaste = useCallback(async (e: React.ClipboardEvent) => {
    const items = Array.from(e.clipboardData.items);
    const files: File[] = [];
    for (const it of items) if (it.type.startsWith('image/')) {
      const f = it.getAsFile(); if (f) files.push(f);
    }
    if (files.length) { e.preventDefault(); await addFiles(files); }
  }, [addFiles]);

  const handleDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault(); setIsDragging(false);
    const files = Array.from(e.dataTransfer.files);
    if (files.length) await addFiles(files);
  }, [addFiles]);

  useEffect(() => {
    const handler = (msg: any) => {
      try {
        if (msg.type === 'settings') {
          const norm = normalizeSettings(msg.settings);
          if (!norm) { setStatus('Settings parse error'); return; }
          setSettings(norm); settingsRef.current = norm; setSelectedProviderId(norm.activeProviderId);
          const active = norm.providers.find(p => p.id === norm.activeProviderId) ?? norm.providers[0];
          if (active) setStatus(`Active: ${active.name} · ${active.model}`);
          const ed = editingRef.current;
          if (ed) {
            const upd = norm.providers.find(p => p.id === ed.id);
            if (upd) { setEditing(upd); setEditForm({ name: upd.name, baseUrl: upd.baseUrl, model: upd.model, apiKey: upd.apiKey, apiKind: upd.apiKind ?? 'openai', group: upd.group ?? 'custom', icon: upd.icon ?? '＋' }); }
          }
        } else if (msg.type === 'plugins') {
          const list = Array.isArray(msg.plugins) ? msg.plugins as PluginInfo[] : [];
          setPlugins(list);
          setLoadIssues(Array.isArray(msg.loadIssues) ? msg.loadIssues as PluginLoadIssue[] : []);
        } else if (msg.type === 'quota') {
          const q = msg.quota as QuotaPayload | undefined;
          if (q) setQuota(q);
        } else if (msg.type === 'catalog') { setCatalog(normalizePresets(msg.presets)); }
        else if (msg.type === 'testResult') {
          setTestState({ id: msg.providerId ?? '', ok: !!msg.ok, message: String(msg.message ?? ''), loading: false });
          setStatus(msg.ok ? `✓ ${msg.message ?? 'OK'}` : `✗ ${msg.message ?? 'Test failed'}`);
          if (msg.models && Array.isArray(msg.models) && msg.providerId) setModelsMap(m => ({ ...m, [msg.providerId]: msg.models }));
        } else if (msg.type === 'models') {
          setModelsLoading(null);
          if (msg.error) setTestState({ id: msg.providerId, ok: false, message: msg.error, loading: false });
          else { setModelsMap(m => ({ ...m, [msg.providerId]: msg.models ?? [] })); setTestState({ id: msg.providerId, ok: true, message: `${(msg.models ?? []).length} models`, loading: false }); }
        } else if (msg.type === 'plan') {
          const plan: PlanPayload = msg.plan;
          setCurrentPlan(plan);
          if (plan.steps?.length) {
            const mapped: TodoPayload[] = plan.steps.map((s, i) => ({ id: `plan-${i}`, title: s.title, tool: s.tool, status: (s.status as any) ?? 'pending' }));
            setTodos(mapped);
            setTodoOpen(mapped.some(x => x.status === 'running' || x.status === 'pending'));
          }
        } else if (msg.type === 'todo') {
          const t = (msg as any).todos as TodoPayload[];
          const next = Array.isArray(t) ? t : [];
          setTodos(next);
          const busy = next.some(x => x.status === 'running' || x.status === 'pending');
          setTodoOpen(busy);
        } else if (msg.type === 'progress') {
          const p = (msg as any).progress as ProgressPayload | undefined;
          if (!p || p.phase === 'idle') { setProgress(null); return; }
          setProgress(p);
          if (p.phase === 'thinking' || p.phase === 'tool') setIsRunning(true);
          if (p.phase === 'complete' || p.phase === 'cancelled' || p.phase === 'error') {
            window.setTimeout(() => setProgress(cur => cur && cur.phase === p.phase ? null : cur), 700);
          }
        } else if (msg.type === 'context') {
          setContext((msg as any).context as ContextPayload);
        } else if (msg.type === 'checkpoints') {
          const list = ((msg as any).checkpoints as CheckpointInfo[]) ?? [];
          setCheckpoints(list);
          setMessages(m => [...m, { id: nextId(), role: 'system', text: list.length ? list.map(c => `${c.id}  ${c.preview ?? ''}`).join('\n') : 'No checkpoints. /resume to restore.' }]);
        } else if ((msg as any).type === 'history') {
          const hist = ((msg as any).messages as { role: string; text: string }[]) ?? [];
          setMessages([{ id: 'resumed', role: 'system', text: `Restored ${hist.length} messages` }, ...hist.map((h, i) => ({ id: `h${i}`, role: h.role === 'user' ? 'you' as const : h.role === 'assistant' ? 'assistant' as const : 'system' as const, text: h.text }))]);
          setIsRunning(false);
          setTodoDismissed(false);
        } else if (msg.type === 'tool_approval') {
          setPendingApproval(msg.approval);
          setMessages(m => [...m, { id: nextId(), role: 'approval', text: `Approval: ${msg.approval.toolName}`, approval: msg.approval }]);
        } else if (msg.type === 'tool_preset') {
          const preset = msg.preset;
          setPendingPreset(preset);
          setPresetValues({ ...(preset.values ?? {}) });
          setPresetPickBusy(null);
          setMessages(m => [...m, { id: nextId(), role: 'preset', text: `Preset: ${preset.toolName}`, preset }]);
        } else if (msg.type === 'presetPickResult') {
          setPresetPickBusy(null);
          const result = msg.result;
          if (msg.ok === false || result?.ok === false) {
            setMessages(m => [...m, { id: nextId(), role: 'system', text: `Pick failed: ${msg.error ?? result?.error ?? 'unknown'}` }]);
            return;
          }
          if (result?.cancelled) {
            setMessages(m => [...m, { id: nextId(), role: 'system', text: 'Pick cancelled.' }]);
            return;
          }
          setPresetValues(v => {
            const next = { ...v };
            if (Array.isArray(result?.ids)) next[msg.fieldId] = result!.ids;
            else if (result?.box) next[msg.fieldId] = result.box;
            return next;
          });
        } else if (msg.type === 'reasoning') {
          setReasoning({ text: String(msg.text ?? ''), isStreaming: !!msg.isStreaming });
          if (!msg.isStreaming) setMessages(m => [...m, { id: nextId(), role: 'reasoning', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'status') { setStatus(msg.text); }
        else if (msg.type === 'trace') { setMessages(m => [...m, { id: nextId(), role: 'trace', text: String(msg.text ?? '') }]); }
        else if (msg.type === 'tool_call') {
          streamedRef.current = false; pendingDeltaRef.current = '';
          setMessages(m => [...m, { id: nextId(), role: 'tool_call', text: String(msg.text ?? '') }]);
        }
        else if (msg.type === 'tool_result') {
          setMessages(m => [...m, { id: nextId(), role: 'tool_result', text: String(msg.text ?? '') }]);
          setPendingApproval(null);
          const text = String(msg.text ?? '');
          setPendingPreset(cur => {
            if (!cur) return cur;
            if (text.includes(cur.toolName) && (text.includes('Cancelled') || text.includes('denied') || text.includes('timeout') || text.includes('result:'))) {
              setPresetPickBusy(null);
              return null;
            }
            return cur;
          });
        }
        else if (msg.type === 'assistant') {
          const incoming = String(msg.text ?? '');
          setReasoning(null);
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && streamedRef.current) {
              streamedRef.current = false; pendingDeltaRef.current = '';
              if (!incoming || last.text === incoming || incoming.startsWith(last.text) || last.text.startsWith(incoming)) {
                return incoming && last.text !== incoming ? [...m.slice(0, -1), { ...last, text: incoming.length >= last.text.length ? incoming : last.text }] : m;
              }
              return [...m.slice(0, -1), { ...last, text: incoming }];
            }
            streamedRef.current = false; pendingDeltaRef.current = '';
            if (!incoming) return m;
            return [...m, { id: nextId(), role: 'assistant', text: incoming }];
          });
          setIsRunning(false);
          setProgress(null);
          const cur = settingsRef.current; const active = cur?.providers.find(p => p.id === cur.activeProviderId) ?? cur?.providers[0];
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'error') {
          setMessages(m => [...m, { id: nextId(), role: 'system', text: `Error: ${String(msg.text ?? '')}` }]);
          setIsRunning(false); streamedRef.current = false; pendingDeltaRef.current = '';
          setProgress(null);
          const cur = settingsRef.current; const active = cur?.providers.find(p => p.id === cur.activeProviderId);
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'delta') {
          const chunk = String(msg.text ?? ''); if (!chunk) return;
          const starting = !streamedRef.current; streamedRef.current = true; pendingDeltaRef.current += chunk;
          setMessages(m => {
            const last = m[m.length - 1];
            if (last && last.role === 'assistant' && !starting) return [...m.slice(0, -1), { ...last, text: last.text + chunk }];
            return [...m, { id: nextId(), role: 'assistant', text: chunk }];
          });
          setIsRunning(true);
        }
      } catch (e: any) { setMessages(m => [...m, { id: nextId(), role: 'system', text: `UI error: ${e?.message ?? String(e)}` }]); }
    };
    const off = onHostMessage(handler as any);
    postToHost({ type: 'requestSettings' });
    postToHost({ type: 'getCatalog' } as any);
    postToHost({ type: 'listPlugins' });
    const t = setTimeout(() => { postToHost({ type: 'requestSettings' }); postToHost({ type: 'getCatalog' } as any); postToHost({ type: 'listPlugins' }); }, 800);
    return () => { clearTimeout(t); off(); };
  }, []);

  useEffect(() => {
    const el = scrollRef.current;
    if (el && stickToBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [messages, isRunning, currentPlan, pendingApproval, pendingPreset, reasoning]);

  const providers = settings?.providers ?? [];
  const activeProvider = useMemo(() => providers.find(p => p.id === (selectedProviderId || settings?.activeProviderId)) ?? providers[0] ?? null, [providers, selectedProviderId, settings?.activeProviderId]);

  const handleSend = (overrideText?: string, overrideMode?: ChatMode) => {
    const raw = (typeof overrideText === 'string' ? overrideText : input);
    const parsed = parseInput(raw);
    const text = parsed.cleanText.trim();
    const atts = [...attachments];
    const isLocalSlash = !!parsed.slash && LOCAL_COMMANDS.includes(parsed.slash.name);
    if ((!text && atts.length === 0 && !isLocalSlash) || isRunning) {
      if (!isLocalSlash) return;
    }
    if (parsed.slash) {
      if (parsed.slash.name === 'clear') { stickToBottomRef.current = true; handleClear(); setInput(''); setSlashOpen(false); setMentionOpen(false); return; }
      if (parsed.slash.name === 'help') {
        stickToBottomRef.current = true;
        setMessages(m => [...m, { id: nextId(), role: 'system', text: `Commands: ${SLASH_COMMANDS.map(c => `${c.label} ${c.argsHint ?? ''} — ${c.description}`).join('\n')}\n\n@mentions: @selection @activeView @visible @warnings @levels @sheets` }]);
        setInput(''); setSlashOpen(false); return;
      }
      if (parsed.slash.name === 'checkpoints') { stickToBottomRef.current = true; postToHost({ type: 'listCheckpoints' } as any); setMessages(m => [...m, { id: nextId(), role: 'you', text: raw }]); setInput(''); setSlashOpen(false); return; }
    }
    const cur = settingsRef.current ?? settings;
    const active = cur?.providers.find(p => p.id === (selectedProviderId || cur?.activeProviderId)) ?? cur?.providers[0];
    if (!cur || !active) { setMessages(m => [...m, { id: nextId(), role: 'system', text: 'No provider configured. Open Settings to add one.' }]); setShowSettings(true); return; }
    if (!isBuiltinProvider(active.id) && !active.apiKey && !active.baseUrl.includes('localhost')) { setMessages(m => [...m, { id: nextId(), role: 'system', text: `Provider '${active.name}' has no API key.` }]); setShowSettings(true); return; }
    if (isBuiltinProvider(active.id) && quota && quota.ok !== false && quota.remaining <= 0) {
      setMessages(m => [...m, { id: nextId(), role: 'system', text: `${BUILTIN_PROVIDER_NAME} daily limit reached (${quota.limit} requests/day). ${quota.resetUtc ? `Try again after ${new Date(quota.resetUtc).toLocaleString()}, or ` : ''}add your own API key in Settings.` }]);
      setShowSettings(true); return;
    }
    if (selectedProviderId && selectedProviderId !== cur.activeProviderId) {
      const ns: AppSettings = { ...cur, activeProviderId: selectedProviderId };
      setSettings(ns); settingsRef.current = ns; postToHost({ type: 'saveSettings', settings: providersOnly(ns) });
    }
    const effMode: ChatMode = overrideMode ?? parsed.effectiveMode ?? mode;
    setRunMode(effMode);
    stickToBottomRef.current = true;
    if (raw.trim()) { inputHistoryRef.current = [raw.trim(), ...inputHistoryRef.current.filter(x => x !== raw.trim())].slice(0, 50); historyIdxRef.current = -1; }
    setMessages(m => [...m, { id: nextId(), role: 'you', text: raw.trim() || (atts.length ? '(image)' : ''), attachments: atts.length ? atts : undefined }]);
    setInput(''); setAttachments([]); setReasoning(null); setSlashOpen(false); setMentionOpen(false);
    setIsRunning(true); streamedRef.current = false; pendingDeltaRef.current = '';
    setTodoDismissed(false);
    setProgress({ phase: 'thinking', label: effMode === 'plan' ? 'Planning…' : 'Thinking…', indeterminate: true });
    setStatus(effMode === 'plan' ? 'Planning...' : effMode === 'ask' ? 'Thinking...' : 'Working...');
    postToHost({ type: 'send', text: text || raw.trim(), attachments: atts.length ? atts : undefined, mode: effMode, slash: parsed.slash?.name, mentions: parsed.mentions } as any);
  };
  const handleApprove = (approved: boolean) => {
    if (!pendingApproval) return;
    postToHost({ type: 'approveTool', toolCallId: pendingApproval.toolCallId, approved } as any);
    setMessages(m => [...m, { id: nextId(), role: 'system', text: approved ? `Approved ${pendingApproval.toolName}` : `Denied ${pendingApproval.toolName}` }]);
    setPendingApproval(null);
  };
  const handleSubmitPreset = (approved: boolean) => {
    if (!pendingPreset) return;
    if (approved && !requiredPresetsFilled(pendingPreset, presetValues)) return;
    postToHost({ type: 'submitPreset', toolCallId: pendingPreset.toolCallId, approved, values: approved ? presetValues : undefined });
    setMessages(m => [...m, { id: nextId(), role: 'system', text: receiptLine(pendingPreset, approved ? 'confirmed' : 'cancelled') }]);
    setPendingPreset(null);
    setPresetPickBusy(null);
  };
  const handlePresetPick = (fieldId: string) => {
    if (!pendingPreset || presetPickBusy) return;
    setPresetPickBusy(fieldId);
    postToHost({ type: 'presetPick', toolCallId: pendingPreset.toolCallId, fieldId });
  };
  const handleStop = () => { postToHost({ type: 'cancel' }); setStatus('Cancelling...'); setProgress({ phase: 'cancelled', label: 'Cancelled' }); streamedRef.current = false; setPendingPreset(null); setPresetPickBusy(null); };
  const handleClear = () => { setMessages([{ id: 'cleared', role: 'system', text: 'Cleared.' }]); streamedRef.current = false; pendingDeltaRef.current = ''; setCurrentPlan(null); setTodos([]); setTodoDismissed(false); setProgress(null); setPendingApproval(null); setPendingPreset(null); setPresetPickBusy(null); setPresetDescOpen({}); setReasoning(null); setContext(null); postToHost({ type: 'clear' }); };

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const v = e.target.value;
    setInput(v);
    const cursor = e.target.selectionStart ?? v.length;
    const before = v.slice(0, cursor);
    const slashMatch = before.match(/(^|\n|\s)\/([a-z]*)$/);
    if (slashMatch) {
      setSlashQuery(slashMatch[2] ?? '');
      setSlashOpen(true);
      setSlashIndex(0);
      setMentionOpen(false);
    } else {
      setSlashOpen(false);
      const mentionMatch = before.match(/@([a-zA-Z_]*)$/);
      if (mentionMatch) {
        setMentionQuery(mentionMatch[1] ?? '');
        setMentionOpen(true);
        setMentionIndex(0);
        setMentionStart(cursor - mentionMatch[0].length);
      } else {
        setMentionOpen(false);
      }
    }
  };

  const commitSlash = (name: string) => {
    const cur = input;
    const cursor = inputRef.current?.selectionStart ?? cur.length;
    const before = cur.slice(0, cursor);
    const after = cur.slice(cursor);
    const replaced = before.replace(/(^|\n|\s)\/[a-z]*$/, `$1/${name} `) + after;
    setInput(replaced);
    setSlashOpen(false);
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const commitMention = (id: string) => {
    if (mentionStart == null) return;
    const cur = input;
    const before = cur.slice(0, mentionStart);
    const after = cur.slice(inputRef.current?.selectionStart ?? cur.length);
    const replaced = before + `@${id} ` + after;
    setInput(replaced);
    setMentionOpen(false);
    setMentionStart(null);
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (slashOpen) {
      const list = filterCommands(slashQuery);
      if (e.key === 'ArrowDown') { e.preventDefault(); setSlashIndex(i => (i + 1) % Math.max(list.length, 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setSlashIndex(i => (i - 1 + list.length) % Math.max(list.length, 1)); return; }
      if (e.key === 'Enter' || e.key === 'Tab') {
        if (list[slashIndex]) { e.preventDefault(); commitSlash(list[slashIndex].name); return; }
      }
      if (e.key === 'Escape') { e.preventDefault(); setSlashOpen(false); return; }
    }
    if (mentionOpen) {
      const list = filterMentions(mentionQuery);
      if (e.key === 'ArrowDown') { e.preventDefault(); setMentionIndex(i => (i + 1) % Math.max(list.length, 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setMentionIndex(i => (i - 1 + list.length) % Math.max(list.length, 1)); return; }
      if (e.key === 'Enter' || e.key === 'Tab') {
        if (list[mentionIndex]) { e.preventDefault(); commitMention(list[mentionIndex].id); return; }
      }
      if (e.key === 'Escape') { e.preventDefault(); setMentionOpen(false); return; }
    }
    if (e.key === 'ArrowUp' && !e.shiftKey && input.trim() === '') {
      const hist = inputHistoryRef.current;
      if (hist.length) {
        const next = Math.min(historyIdxRef.current + 1, hist.length - 1);
        historyIdxRef.current = next;
        setInput(hist[next]);
        e.preventDefault();
        return;
      }
    }
    if (e.key === 'ArrowDown' && !e.shiftKey && historyIdxRef.current >= 0) {
      const hist = inputHistoryRef.current;
      const next = historyIdxRef.current - 1;
      if (next < 0) { historyIdxRef.current = -1; setInput(''); }
      else { historyIdxRef.current = next; setInput(hist[next]); }
      e.preventDefault();
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleSend(); }
  };

  const selectForEdit = (p: ProviderConfig) => { setEditing(p); setEditForm({ name: p.name, baseUrl: p.baseUrl, model: p.model, apiKey: p.apiKey, apiKind: p.apiKind ?? 'openai', group: p.group ?? 'custom', icon: p.icon ?? '＋' }); setTestState(null); };
  const handleAddFromPreset = (preset: ProviderPreset) => {
    const cur = settingsRef.current ?? settings; if (!cur) return;
    const exists = cur.providers.find(p => p.id === preset.id);
    const id = exists ? `${preset.id}-${Math.random().toString(36).slice(2, 4)}` : preset.id;
    const p: ProviderConfig = { id, name: preset.name, baseUrl: preset.baseUrl, model: preset.model, apiKey: '', apiKind: preset.apiKind, group: preset.group, icon: preset.icon };
    const ns: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(ns); settingsRef.current = ns; selectForEdit(p); postToHost({ type: 'saveSettings', settings: providersOnly(ns) }); setCatalogFilter('');
  };
  const handleAddCustom = () => {
    const cur = settingsRef.current ?? settings; if (!cur) return;
    const p: ProviderConfig = { id: Math.random().toString(36).slice(2, 8), name: 'Custom', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'custom', icon: '＋' };
    const ns: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(ns); settingsRef.current = ns; selectForEdit(p); postToHost({ type: 'saveSettings', settings: providersOnly(ns) });
  };
  const handleSave = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (isBuiltinProvider(editing.id)) { setStatus(`${BUILTIN_PROVIDER_NAME} is built in — no key to save.`); return; }
    if (!editForm.name.trim() || !editForm.baseUrl.trim() || !editForm.model.trim()) { setStatus('Name, Base URL and Model are required.'); return; }
    try { new URL(editForm.baseUrl.trim()); } catch { setStatus('Base URL is not a valid URL.'); return; }
    const upd: ProviderConfig = { ...editing, name: editForm.name.trim(), baseUrl: editForm.baseUrl.trim().replace(/\/$/, ''), model: editForm.model.trim(), apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    const ns: AppSettings = { ...cur, providers: cur.providers.map(p => p.id === editing.id ? upd : p) };
    setSettings(ns); settingsRef.current = ns; setEditing(upd); postToHost({ type: 'saveSettings', settings: providersOnly(ns) }); setStatus(`Saved ${upd.name}`);
  };
  const handleDelete = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (isBuiltinProvider(editing.id)) { setStatus(`Cannot delete the built-in ${BUILTIN_PROVIDER_NAME} provider.`); return; }
    if (cur.providers.length <= 1) { setStatus('Cannot delete the last provider.'); return; }
    const np = cur.providers.filter(p => p.id !== editing.id);
    const na = cur.activeProviderId === editing.id ? np[0].id : cur.activeProviderId;
    const ns: AppSettings = { ...cur, providers: np, activeProviderId: na };
    setSettings(ns); settingsRef.current = ns; setSelectedProviderId(na); setEditing(null); postToHost({ type: 'saveSettings', settings: providersOnly(ns) }); setStatus(`Deleted. Active: ${np.find(p => p.id === na)?.name ?? na}`);
  };
  const handleUse = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    const ns: AppSettings = { ...cur, activeProviderId: editing.id };
    setSettings(ns); settingsRef.current = ns; setSelectedProviderId(editing.id); postToHost({ type: 'saveSettings', settings: providersOnly(ns) }); setStatus(`Active: ${editing.name}`);
  };
  const handleTest = () => {
    if (!editing) return;
    const prov: ProviderConfig = { ...editing, name: editForm.name.trim() || editing.name, baseUrl: editForm.baseUrl.trim() || editing.baseUrl, model: editForm.model.trim() || editing.model, apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    setTestState({ id: prov.id, loading: true, message: 'Testing...' }); postToHost({ type: 'testProvider', provider: prov } as any);
  };
  const handleFetchModels = () => {
    if (!editing) return;
    const prov: ProviderConfig = { ...editing, name: editForm.name.trim() || editing.name, baseUrl: editForm.baseUrl.trim() || editing.baseUrl, model: editForm.model.trim() || editing.model, apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    setModelsLoading(prov.id); postToHost({ type: 'listModels', provider: prov } as any);
  };

  const filteredProvidersLeft = useMemo(() => {
    const f = editSearch.toLowerCase().trim();
    if (!f) return providers;
    return providers.filter(p => `${p.name} ${p.model} ${p.baseUrl}`.toLowerCase().includes(f));
  }, [providers, editSearch]);
  const catalogFiltered = useMemo(() => {
    const f = catalogFilter.toLowerCase().trim();
    if (!f) return catalog;
    return catalog.filter(c => `${c.name} ${c.model} ${c.description}`.toLowerCase().includes(f));
  }, [catalog, catalogFilter]);
  const editingModels = editing ? (modelsMap[editing.id] ?? []) : [];

  const connected = !!(isBuiltinProvider(activeProvider?.id) || activeProvider?.apiKey || activeProvider?.baseUrl.includes('localhost'));
  const quotaLine = isBuiltinProvider(activeProvider?.id) && quota
    ? (quota.ok === false
        ? (quota.message ?? `${BUILTIN_PROVIDER_NAME} is unavailable.`)
        : `${quota.remaining} / ${quota.limit} free requests left today`)
    : '';
  const ctxLine = context
    ? `Sel ${context.selectionCount}${context.selectionPreview ? ` · ${context.selectionPreview.slice(0, 40)}` : ''} · ${context.activeView} · warnings ${context.warningCount ?? 0}`
    : status;

  return (
    <div className="flex flex-col h-[100vh] text-[13px] antialiased" style={{ background: '#f3f3f3', color: '#111' }}>
      <div style={{ padding: '8px 10px 6px', borderBottom: '1px solid #d0d0d0', background: '#f3f3f3' }}>
        <div className="flex items-start gap-2">
          <div className="flex-1 min-w-0">
            <div style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.04em', textTransform: 'uppercase', color: '#444' }}>LoadingAI</div>
            <div style={{ fontSize: 12, color: '#111', marginTop: 2 }} className="truncate">
              {activeProvider ? `${activeProvider.model} · ${connected ? 'connected' : 'not set'}` : (settings ? 'No provider' : 'Loading…')}
            </div>
            {quotaLine && (
              <div style={{ fontSize: 11, marginTop: 2, color: quota?.ok === false ? '#b3261e' : '#5c5c5c' }} className="truncate">{quotaLine}</div>
            )}
          </div>
          <button
            type="button"
            className={btn}
            title="When on, the agent runs switched-on L2 and L3 tools without asking, and Revit warning dialogs during that work are dismissed. One undo per chat turn. Tools that are switched off stay off."
            style={settings?.autoApprove ? { background: '#8a1f11', color: '#fff', borderColor: '#8a1f11' } : undefined}
            onClick={() => {
              const cur = settingsRef.current;
              if (!cur) return;
              const next = !cur.autoApprove;
              if (next && !window.confirm('Auto-approve lets the agent change the model without asking, and dismisses Revit warning dialogs while it works. You can undo the whole turn once. Turn it on?')) return;
              const ns: AppSettings = { ...cur, autoApprove: next };
              setSettings(ns); settingsRef.current = ns;
              postToHost({ type: 'saveSettings', settings: providersOnly(ns) });
            }}
          >{settings?.autoApprove ? 'Auto-approve on' : 'Auto-approve'}</button>
          <button type="button" className={btn} onClick={() => setShowSettings(v => { const next = !v; if (!v) postToHost({ type: 'listPlugins' }); return next; })}>{showSettings ? 'Close' : 'Settings'}</button>
        </div>
      </div>

      <div style={{ fontSize: 11, color: '#5c5c5c', padding: '6px 10px', borderBottom: progress ? 'none' : '1px solid #d0d0d0', background: '#f3f3f3', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {isRunning ? (progress?.label || 'Working…') : ctxLine}
      </div>
      {progress && progress.phase !== 'idle' && <ProgressBar progress={progress} />}

      {showSettings && (
        <div className="border-b overflow-y-auto" style={{ maxHeight: '52vh', background: '#f3f3f3', borderColor: '#d0d0d0' }}>
          <div className="px-2 py-1.5 text-[11px] flex items-center gap-2" style={{ borderBottom: '1px solid #d0d0d0' }}>
            <span className="font-semibold">Providers</span>
            <span className="text-[10px] text-muted-foreground">{providers.length} · keys DPAPI</span>
            <span className="flex-1" />
            <button type="button" className={btn} onClick={handleAddCustom}>+ Custom</button>
            <button type="button" className={btn} onClick={handleClear}>Clear session</button>
          </div>
          <input value={editSearch} onChange={e => setEditSearch(e.target.value)} placeholder="Filter…" className={`${inp} m-2`} style={{ width: 'calc(100% - 16px)' }} />
          <div style={{ borderTop: '1px solid #d0d0d0' }}>
            {filteredProvidersLeft.map(p => {
              const isEditing = editing?.id === p.id;
              const isActive = p.id === settings?.activeProviderId;
              return (
                <button key={p.id} type="button" onClick={() => selectForEdit(p)} className="w-full text-left px-2.5 py-2" style={{ borderBottom: '1px solid #ececec', background: isEditing ? '#fff' : '#f7f7f7' }}>
                  <span className="text-xs">{isActive ? '● ' : ''}{p.name}</span>
                  <span className="block text-[11px] text-muted-foreground truncate">{p.model}</span>
                </button>
              );
            })}
          </div>
          <div className="p-2" style={{ borderTop: '1px solid #d0d0d0' }}>
            <div className="text-[11px] font-semibold mb-1">Presets</div>
            <input value={catalogFilter} onChange={e => setCatalogFilter(e.target.value)} placeholder="Filter presets…" className={`${inp} mb-1`} />
            <div className="max-h-24 overflow-y-auto" style={{ background: '#fff', border: '1px solid #d0d0d0' }}>
              {catalogFiltered.map(c => (
                <button key={c.id} type="button" onClick={() => handleAddFromPreset(c)} className="w-full text-left px-2 py-1 text-xs" style={{ borderBottom: '1px solid #ececec' }}>
                  {c.name} <span className="text-muted-foreground">{c.model}</span>
                </button>
              ))}
            </div>
          </div>
          {editing && (
            <div className="p-2 flex flex-col gap-1.5" style={{ borderTop: '1px solid #d0d0d0', background: '#fff' }}>
              {isBuiltinProvider(editing.id) ? (
                <>
                  <div className="text-[11px] font-semibold">{BUILTIN_PROVIDER_NAME}</div>
                  <div className="text-[11px] text-muted-foreground">Included free. No API key needed — 100 requests per day per device.</div>
                  <div className="text-[11px]" style={{ color: quota?.exhausted ? '#b3261e' : undefined }}>
                    {quota
                      ? (quota.ok === false
                          ? (quota.message ?? `${BUILTIN_PROVIDER_NAME} is unavailable.`)
                          : `${quota.remaining} / ${quota.limit} requests left today`)
                      : 'Checking today\u2019s allowance…'}
                    {quota?.resetUtc && quota.ok !== false ? ` · resets ${new Date(quota.resetUtc).toLocaleTimeString()}` : ''}
                  </div>
                  {testState && testState.id === editing.id && <div className="text-xs px-2 py-1" style={{ border: '1px solid #d0d0d0' }}>{testState.message}</div>}
                  <div className="flex gap-1.5">
                    <button type="button" className={btn} style={{ background: '#2b2b2b', color: '#fff', borderColor: '#2b2b2b' }} onClick={handleUse}>Use</button>
                    <button type="button" className={btn} onClick={handleTest} disabled={testState?.loading}>Test</button>
                  </div>
                </>
              ) : (
                <>
              <label className="text-[11px] font-semibold">Name<input value={editForm.name} onChange={e => setEditForm(f => ({ ...f, name: e.target.value }))} className={inp} /></label>
              <label className="text-[11px] font-semibold">Base URL<input value={editForm.baseUrl} onChange={e => setEditForm(f => ({ ...f, baseUrl: e.target.value }))} className={`${inp} font-mono`} /></label>
              <label className="text-[11px] font-semibold">API kind
                <select value={editForm.apiKind} onChange={e => setEditForm(f => ({ ...f, apiKind: e.target.value }))} className={inp}>
                  <option value="openai">OpenAI Compatible</option>
                  <option value="responses">Responses</option>
                  <option value="anthropic">Anthropic</option>
                </select>
              </label>
              <label className="text-[11px] font-semibold">Group
                <select value={editForm.group} onChange={e => setEditForm(f => ({ ...f, group: e.target.value }))} className={inp}>
                  <option value="cloud">Cloud</option>
                  <option value="gateway">Gateway</option>
                  <option value="local">Local</option>
                  <option value="custom">Custom</option>
                </select>
              </label>
              <label className="text-[11px] font-semibold">
                <span className="flex items-center">Model<button type="button" className={`${btn} ml-auto`} onClick={handleFetchModels} disabled={!!modelsLoading}>{modelsLoading === editing.id ? '…' : 'Fetch'}</button></span>
                <input value={editForm.model} onChange={e => setEditForm(f => ({ ...f, model: e.target.value }))} className={`${inp} font-mono`} />
              </label>
              {editingModels.length > 0 && (
                <div style={{ border: '1px solid #d0d0d0', background: '#fff' }}>
                  {editingModels.slice(0, 40).map(mid => (
                    <button key={mid} type="button" onClick={() => setEditForm(f => ({ ...f, model: mid }))} className="w-full text-left px-2 py-1 text-xs font-mono" style={{ borderBottom: '1px solid #ececec', background: editForm.model === mid ? '#f3f3f3' : '#fff' }}>{mid}</button>
                  ))}
                  {editingModels.length > 8 && (
                    <div className="p-1.5">
                      <ModelSelector models={editingModels.map(id => ({ id, name: id, provider: editing.name } as any))} value={editForm.model} onChange={(id: string) => setEditForm(f => ({ ...f, model: id }))} placeholder="Search models..." storageKey={`oms-${editing.id}`} />
                    </div>
                  )}
                </div>
              )}
              <label className="text-[11px] font-semibold">API Key
                <input value={editForm.apiKey} onChange={e => setEditForm(f => ({ ...f, apiKey: e.target.value }))} type="password" placeholder={editForm.baseUrl.includes('localhost') ? 'ollama or leave empty' : 'sk-...'} className={`${inp} font-mono`} />
                <span className="text-[10px] font-normal text-muted-foreground">DPAPI · %AppData%\LoadingAI\settings.json</span>
              </label>
              {testState && testState.id === editing.id && <div className="text-xs px-2 py-1" style={{ border: '1px solid #d0d0d0' }}>{testState.message}</div>}
              <div className="flex gap-1.5">
                <button type="button" className={btn} style={{ background: '#2b2b2b', color: '#fff', borderColor: '#2b2b2b' }} onClick={handleSave}>Save</button>
                <button type="button" className={btn} onClick={handleUse}>Use</button>
                <button type="button" className={btn} onClick={handleTest} disabled={testState?.loading}>Test</button>
                <button type="button" className={btn} style={{ color: '#8a1f11' }} onClick={handleDelete}>Delete</button>
              </div>
                </>
              )}
            </div>
          )}
          <PluginShelf plugins={plugins} loadIssues={loadIssues} />
        </div>
      )}

      {todos.length > 0 && !todoDismissed && (
        <div style={{ borderBottom: '1px solid #d0d0d0', background: '#fff', flexShrink: 0 }}>
          <div className="flex items-center gap-1 pl-2.5 pr-1 py-1">
            <button type="button" className="flex-1 text-left text-[11px] font-semibold" style={{ background: '#fff' }} onClick={() => setTodoOpen(v => !v)} aria-expanded={todoOpen}>
              <span>Todo · {todos.filter(t => t.status === 'done').length}/{todos.length}</span>
              <span className="text-[10px] font-normal ml-2" style={{ color: '#5c5c5c' }}>{todoOpen ? 'hide' : 'show'}</span>
            </button>
            <button type="button" className="text-[11px] px-1" style={{ color: '#5c5c5c' }} aria-label="Dismiss todo list" title="Dismiss for this run" onClick={() => setTodoDismissed(true)}>×</button>
          </div>
          {todoOpen && (
            <div style={{ maxHeight: 120, overflowY: 'auto', borderTop: '1px solid #ececec' }}>
              {todos.map(t => (
                <div key={t.id} className="px-2.5 py-1 text-xs flex gap-2 items-center" style={{ borderBottom: '1px solid #ececec' }}>
                  <span className="w-4 shrink-0">{t.status === 'done' ? '✓' : t.status === 'running' ? '●' : t.status === 'error' ? '✗' : '·'}</span>
                  <span className="flex-1 truncate">{t.title}</span>
                  {t.tool && t.tool !== t.title && <span className="font-mono text-[10px] shrink-0">{t.tool}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      <div ref={scrollRef} onScroll={e => { stickToBottomRef.current = isNearBottom(e.currentTarget); }} className="flex-1 overflow-y-auto" style={{ background: '#f7f7f7' }}>
        {reasoning && (
          <div style={rowStyle}>
            <div style={kLabel}>Think</div>
            <div className="whitespace-pre-wrap text-xs">{reasoning.text || '…'}</div>
          </div>
        )}
        {currentPlan && (
          <div style={{ ...rowStyle, background: '#fff' }}>
            <div style={kLabel}>Plan</div>
            <div className="font-semibold text-xs mb-1">{currentPlan.title ?? 'Plan'}</div>
            {currentPlan.steps?.map((s, i) => (
              <div key={i} className="text-xs py-0.5">{i + 1}. {s.title}{s.tool ? `  (${s.tool})` : ''}</div>
            ))}
            <div className="flex gap-1.5 mt-2">
              <button type="button" className={btn} style={{ background: '#2b2b2b', color: '#fff', borderColor: '#2b2b2b' }} onClick={() => { setMode('build'); setCurrentPlan(null); handleSend('Execute the plan.', 'build'); }}>Run</button>
              <button type="button" className={btn} onClick={() => setCurrentPlan(null)}>Edit</button>
            </div>
          </div>
        )}
        {(() => {
          const nodes: JSX.Element[] = [];
          let buf: Message[] = [];
          const flush = () => {
            if (buf.length === 0) return;
            nodes.push(<TranscriptSlice key={`slice-${buf[0].id}`} messages={buf} allMessages={messages} />);
            buf = [];
          };
          for (const m of messages) {
            if (m.role !== 'preset' && m.role !== 'approval') { buf.push(m); continue; }
            flush();
            let card: JSX.Element | null = null;
            if (m.role === 'preset' && m.preset) {
            const isPending = pendingPreset?.toolCallId === m.preset.toolCallId;
            const warnings = previewWarnings(m.preset);
            card = (
              <div key={m.id} style={{ borderTop: '2px solid #2b2b2b', borderBottom: '1px solid #d0d0d0', background: '#fff', padding: 10, fontSize: 12.5 }}>
                <div style={kLabel}>{runKicker(isPending)}</div>
                <div>{m.preset.toolName}{m.preset.tier ? ` · ${m.preset.tier}` : ''}</div>
                {warnings.map(w => (
                  <div key={w} className="text-[11px] mt-1" style={{ color: '#8a1f11' }}>{w}</div>
                ))}
                <div className="text-[11px] text-muted-foreground mt-1">
                  {isPending ? 'Options are in the Run panel below the messages.' : confirmState(messages, m.preset.toolCallId, m.preset.toolName)}
                </div>
              </div>
            );
            }
          if (m.role === 'approval' && m.approval) {
            const isPending = pendingApproval?.toolCallId === m.approval.toolCallId;
            card = (
              <div key={m.id} style={{ borderTop: '2px solid #8a1f11', borderBottom: '1px solid #d0d0d0', background: '#fff8f6', padding: 10, fontSize: 12.5 }}>
                <div style={{ ...kLabel, color: '#8a1f11' }}>Confirm L3</div>
                <div>{m.approval.toolName}{m.approval.tier ? ` · ${m.approval.tier}` : ''}</div>
                <div className="mt-1 text-[11px]" style={{ color: '#5c5c5c' }}>
                  {approvalLines(m.approval).map((line, i) => <div key={`${i}-${line}`} className="font-mono">{line}</div>)}
                </div>
                <div className="mt-1" style={{ color: '#5c5c5c' }}>After Allow, Revit will ask once more. Default is No.</div>
                {isPending ? (
                  <div className="grid grid-cols-2 gap-1.5 mt-2">
                    <button type="button" className={btn} onClick={() => handleApprove(false)}>Deny</button>
                    <button type="button" className={btn} style={{ background: '#8a1f11', color: '#fff', borderColor: '#8a1f11' }} onClick={() => handleApprove(true)}>Allow</button>
                  </div>
                ) : <div className="text-[11px] text-muted-foreground mt-1">{confirmState(messages, m.approval.toolCallId, m.approval.toolName)}</div>}
              </div>
            );
            }
            if (card) nodes.push(card);
          }
          flush();
          return nodes;
        })()}
        {isRunning && <div style={{ ...rowStyle, fontStyle: 'italic', color: '#6a6a6a' }}>{runMode === 'plan' ? 'Planning…' : runMode === 'ask' ? 'Thinking…' : 'Working…'}</div>}
      </div>

      {pendingPreset && (() => {
        const fields = pendingPreset.fields ?? [];
        const canConfirm = requiredPresetsFilled(pendingPreset, presetValues) && !presetPickBusy;
        return (
          <div style={{ borderTop: '2px solid #2b2b2b', background: '#fff', padding: 10, fontSize: 12.5 }}>
            <div className="flex items-center gap-2">
              <div style={{ ...kLabel, marginBottom: 0 }}>Run</div>
              <div className="flex-1">{pendingPreset.toolName}{pendingPreset.tier ? ` · ${pendingPreset.tier}` : ''}</div>
              {pendingPreset.description ? (
                <button
                  type="button"
                  className="px-1.5 py-0.5 border bg-white text-[10px]"
                  aria-expanded={!!presetDescOpen[pendingPreset.toolCallId]}
                  onClick={() => setPresetDescOpen(o => ({ ...o, [pendingPreset.toolCallId]: !o[pendingPreset.toolCallId] }))}
                >
                  {presetDescOpen[pendingPreset.toolCallId] ? 'Hide description' : 'Description'}
                </button>
              ) : null}
            </div>
            {presetDescOpen[pendingPreset.toolCallId] && pendingPreset.description ? (
              <div className="mt-1" style={{ color: '#5c5c5c' }}>{pendingPreset.description}</div>
            ) : null}

            {previewWarnings(pendingPreset, presetValues).map(w => (
              <div key={w} className="mt-1.5 text-[11px]" style={{ color: '#8a1f11' }}>{w}</div>
            ))}

            <div className="mt-2" style={{ maxHeight: '32vh', overflowY: 'auto' }}>
              <div style={kLabel}>{pendingPreset.preview?.summary ?? `${fields.length} settings to confirm`}</div>
              {previewRows(pendingPreset, presetValues).map((r, idx) => (
                <div key={`${idx}-${r.label}`} className="flex items-baseline gap-2 text-[11px]">
                  <span className="flex-1" style={{ color: '#5c5c5c' }}>{r.label}</span>
                  <span className="font-mono">{r.detail}</span>
                </div>
              ))}

            {fields.length > 0 && (
              <div className="mt-2 grid gap-2">
                {fields.map(f => (
                  <label key={f.id} className="grid gap-1">
                    <span className="text-[11px] font-semibold">{f.label || f.id}{f.required ? ' *' : ''}</span>
                    {f.kind === 'Boolean' ? (
                      <input type="checkbox" checked={!!presetValues[f.id]} onChange={e => setPresetValues(v => ({ ...v, [f.id]: e.target.checked }))} />
                    ) : f.kind === 'Select' ? (
                      <select className={inp} value={String(presetValues[f.id] ?? '')} onChange={e => setPresetValues(v => ({ ...v, [f.id]: e.target.value }))}>
                        <option value="">Select…</option>
                        {(f.options ?? []).map(o => <option key={o} value={o}>{o}</option>)}
                      </select>
                    ) : f.kind === 'Number' ? (
                      <input type="number" className={inp} value={presetValues[f.id] == null ? '' : String(presetValues[f.id])} onChange={e => setPresetValues(v => ({ ...v, [f.id]: e.target.value === '' ? '' : Number(e.target.value) }))} />
                    ) : f.kind === 'PickElements' || f.kind === 'PickBox' ? (
                      <div className="flex items-center gap-1.5">
                        <span className="flex-1 text-[11px]" style={{ color: '#5c5c5c' }}>{formatPickSummary(f, presetValues[f.id])}</span>
                        <button type="button" className={btn} disabled={!!presetPickBusy} onClick={() => handlePresetPick(f.id)}>{presetPickBusy === f.id ? 'Picking…' : 'Pick'}</button>
                      </div>
                    ) : (
                      <input type="text" className={inp} value={String(presetValues[f.id] ?? '')} onChange={e => setPresetValues(v => ({ ...v, [f.id]: e.target.value }))} />
                    )}
                  </label>
                ))}
              </div>
            )}
            </div>

            <div className="grid grid-cols-2 gap-1.5 mt-2">
              <button type="button" className={btn} onClick={() => handleSubmitPreset(false)}>Cancel</button>
              <button type="button" className={btn} style={{ background: '#2b2b2b', color: '#fff', borderColor: '#2b2b2b' }} disabled={!canConfirm} onClick={() => handleSubmitPreset(true)}>Confirm</button>
            </div>
          </div>
        );
      })()}

      <div
        ref={composerRef}
        onDragOver={e => { e.preventDefault(); setIsDragging(true); }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={handleDrop}
        onPaste={handlePaste}
        style={{ borderTop: '1px solid #d0d0d0', padding: 8, background: '#f3f3f3' }}
      >
        {isDragging && <div className="text-[11px] mb-1">Drop images here</div>}
        {attachments.length > 0 && (
          <div className="flex gap-1 mb-1 overflow-x-auto">
            {attachments.map(a => (
              <div key={a.id} className="relative shrink-0">
                <img src={a.dataUrl} alt={a.name} style={{ height: 48, width: 48, objectFit: 'cover', border: '1px solid #d0d0d0' }} />
                <button type="button" className="absolute top-0 right-0 text-[10px] bg-white border" onClick={() => setAttachments(v => v.filter(x => x.id !== a.id))}>×</button>
              </div>
            ))}
          </div>
        )}
        <div className="relative">
          {slashOpen && (
            <div className="absolute bottom-full left-0 right-0 mb-1 max-h-40 overflow-auto" style={{ background: '#fff', border: '1px solid #d0d0d0' }}>
              {filterCommands(slashQuery).map((c, idx) => (
                <button key={c.name} type="button" onClick={() => commitSlash(c.name)} className="w-full text-left px-2 py-1.5 text-xs" style={{ background: idx === slashIndex ? '#f3f3f3' : '#fff', borderBottom: '1px solid #ececec' }}>
                  <span className="font-mono font-semibold">{c.label}</span> <span className="text-muted-foreground">{c.description}</span>
                </button>
              ))}
            </div>
          )}
          {mentionOpen && (
            <div className="absolute bottom-full left-0 right-0 mb-1 max-h-40 overflow-auto" style={{ background: '#fff', border: '1px solid #d0d0d0' }}>
              {filterMentions(mentionQuery).map((m, idx) => (
                <button key={m.id} type="button" onClick={() => commitMention(m.id)} className="w-full text-left px-2 py-1.5 text-xs" style={{ background: idx === mentionIndex ? '#f3f3f3' : '#fff', borderBottom: '1px solid #ececec' }}>
                  <span className="font-mono font-semibold">{m.label}</span> <span className="text-muted-foreground">{m.description}</span>
                </button>
              ))}
            </div>
          )}
          <textarea
            ref={inputRef}
            value={input}
            onChange={handleInputChange}
            onKeyDown={handleKeyDown}
            placeholder="Describe the task"
            rows={2}
            disabled={isRunning}
            className="w-full resize-none outline-none text-xs"
            style={{ border: '1px solid #c8c8c8', background: '#fff', padding: 8, minHeight: 56 }}
          />
        </div>
        <div className="flex items-center gap-1.5 mt-1.5">
          <span className="text-[10.5px]" style={{ color: '#7a7a7a' }}>/plan  /ask  /hide  ·  @selection</span>
          <span className="flex-1" />
          <button type="button" className={btn} disabled={isRunning || attachments.length >= 5} onClick={() => fileInputRef.current?.click()}>Img</button>
          <input ref={fileInputRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif" multiple hidden onChange={async e => { const files = Array.from(e.target.files ?? []); if (files.length) await addFiles(files); if (fileInputRef.current) fileInputRef.current.value = ''; }} />
          {isRunning
            ? <button type="button" className={btn} onClick={handleStop}>Stop</button>
            : <button type="button" className={btn} style={{ background: '#2b2b2b', color: '#fff', borderColor: '#2b2b2b' }} disabled={!input.trim() && attachments.length === 0} onClick={() => handleSend()}>Send</button>}
        </div>
      </div>
    </div>
  );
}
