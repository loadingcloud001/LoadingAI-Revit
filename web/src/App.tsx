import { useEffect, useMemo, useRef, useState, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import { AppSettings, Attachment, ChatMode, PlanPayload, ApprovalPayload, PluginInfo, ProviderConfig, ProviderPreset, TodoPayload, ContextPayload, CheckpointInfo, onHostMessage, postToHost } from './bridge';
import { PluginShelf } from './plugins/PluginShelf';
import { Terminal } from './components/Terminal';
import { parseInput } from './cli/inputParser';
import { SLASH_COMMANDS, filterCommands } from './cli/slashCommands';
import { filterMentions } from './cli/mentions';
import { Search, ChevronDown, Check, Settings2, Trash2, Play, Copy, ExternalLink, Sparkles, Cloud, Cpu, Globe, Wrench, Loader2, PlugZap, Database, ShieldCheck, Lightbulb, ClipboardList, ShieldAlert, Zap, Image as ImageIcon, X, Paperclip, SendHorizontal, Eye, FileSearch, Hammer, MessageCircleQuestion, ChevronRight, ChevronUp, Brain, ThumbsUp, ThumbsDown, Terminal as TerminalIcon, History, RotateCcw } from 'lucide-react';
import 'open-model-selector/styles.css';
import { ModelSelector } from 'open-model-selector';

// Reuse pattern from assistant-ui SimpleImageAttachmentAdapter + ai-elements Plan/Confirmation (MIT) — adapted for 340px WebView2
type Message = { id: string; role: 'you' | 'assistant' | 'trace' | 'system' | 'tool_call' | 'tool_result' | 'plan' | 'approval' | 'reasoning'; text: string; attachments?: Attachment[]; plan?: PlanPayload; approval?: ApprovalPayload; isStreaming?: boolean };

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
  return { activeProviderId: active, providers, plugins };
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

const GROUP_META: Record<string, { label: string; icon: any; color: string }> = {
  cloud: { label: 'Cloud', icon: Cloud, color: '#2563eb' },
  gateway: { label: 'Gateway', icon: Globe, color: '#7c3aed' },
  local: { label: 'Local', icon: Cpu, color: '#059669' },
  custom: { label: 'Custom', icon: Wrench, color: '#6b7280' },
};
function ProviderIcon({ group, icon, size = 14 }: { group?: string; icon?: string; size?: number }) {
  const g = (group ?? 'custom').toLowerCase();
  const IconComp = GROUP_META[g]?.icon ?? Wrench;
  if (icon && icon.length <= 2 && !/^[a-z]/i.test(icon)) return <span style={{ fontSize: size, lineHeight: 1 }}>{icon}</span>;
  return <IconComp size={size} style={{ color: GROUP_META[g]?.color ?? '#6b7280' }} />;
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

export default function App() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const settingsRef = useRef<AppSettings | null>(null);
  const [catalog, setCatalog] = useState<ProviderPreset[]>([]);
  const [selectedProviderId, setSelectedProviderId] = useState<string>('');
  const [providerSearch, setProviderSearch] = useState('');
  const [selectorOpen, setSelectorOpen] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [editing, setEditing] = useState<ProviderConfig | null>(null);
  const [editForm, setEditForm] = useState({ name: '', baseUrl: '', model: '', apiKey: '', apiKind: 'openai' as string, group: 'custom' as string, icon: '＋' });
  const [editSearch, setEditSearch] = useState('');
  const [catalogFilter, setCatalogFilter] = useState('');
  const [testState, setTestState] = useState<{ id: string; ok?: boolean; message?: string; loading?: boolean } | null>(null);
  const [modelsMap, setModelsMap] = useState<Record<string, string[]>>({});
  const [modelsLoading, setModelsLoading] = useState<string | null>(null);

  // Advanced chat state
  const [mode, setMode] = useState<ChatMode>('build');
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [reasoning, setReasoning] = useState<{ text: string; isStreaming?: boolean } | null>(null);
  const [currentPlan, setCurrentPlan] = useState<PlanPayload | null>(null);
  const [todos, setTodos] = useState<TodoPayload[]>([]);
  const [pendingApproval, setPendingApproval] = useState<ApprovalPayload | null>(null);
  const [suggestions, setSuggestions] = useState<string[]>([]);
  const [context, setContext] = useState<ContextPayload | null>(null);
  const [terminalOpen, setTerminalOpen] = useState(false);
  const [terminalLogs, setTerminalLogs] = useState<string[]>([]);
  const [checkpoints, setCheckpoints] = useState<CheckpointInfo[]>([]);

  const [messages, setMessages] = useState<Message[]>([
    { id: 'welcome', role: 'system', text: 'AI Chat ready. Plan · Ask · Build 模式已啟用，支援截圖/圖片多模態。選擇提供商並輸入 API Key 後即可開始，例如 “這個模型有多少牆？” 或直接貼上截圖提問。' }
  ]);
  const [input, setInput] = useState('');
  const [isRunning, setIsRunning] = useState(false);
  const [status, setStatus] = useState('Ready');
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [activeTab, setActiveTab] = useState<'chat' | 'plugins'>('chat');
  const scrollRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const composerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  // CLI slash/@ state
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

  // Attachment helpers reused from assistant-ui pattern
  const addFiles = useCallback(async (files: File[]) => {
    const imgs = files.filter(f => f.type.startsWith('image/')).slice(0, 5 - attachments.length);
    if (imgs.length === 0) { setStatus('僅支持圖片（png/jpg/webp/gif）'); return; }
    if (attachments.length + imgs.length > 5) { setStatus('最多 5 張圖片'); return; }
    for (const f of imgs) {
      try {
        if (f.size > 12 * 1024 * 1024) { setStatus(`${f.name} 超過 12MB`); continue; }
        const att = await compressImage(f);
        setAttachments(a => [...a, att]);
      } catch (e: any) { setStatus(`壓縮失敗: ${e?.message ?? String(e)}`); }
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
          }
        } else if (msg.type === 'todo') {
          const t = (msg as any).todos as TodoPayload[];
          setTodos(Array.isArray(t) ? t : []);
          if (Array.isArray(t) && t.length) setTerminalLogs(l => [...l, `todo: ${t.map(x => `${x.title}[${x.status}]`).join(' | ')}`].slice(-300));
        } else if (msg.type === 'context') {
          setContext((msg as any).context as ContextPayload);
        } else if (msg.type === 'checkpoints') {
          setCheckpoints(((msg as any).checkpoints as CheckpointInfo[]) ?? []);
        } else if ((msg as any).type === 'history') {
          const hist = ((msg as any).messages as { role: string; text: string }[]) ?? [];
          setMessages([{ id: 'resumed', role: 'system', text: `已恢復 ${hist.length} 條歷史` }, ...hist.map((h, i) => ({ id: `h${i}`, role: h.role === 'user' ? 'you' as const : h.role === 'assistant' ? 'assistant' as const : 'system' as const, text: h.text }))]);
          setIsRunning(false);
        } else if (msg.type === 'tool_approval') {
          setPendingApproval(msg.approval);
          setMessages(m => [...m, { id: nextId(), role: 'approval', text: `Approval: ${msg.approval.toolName}`, approval: msg.approval }]);
        } else if (msg.type === 'reasoning') {
          setReasoning({ text: String(msg.text ?? ''), isStreaming: !!msg.isStreaming });
          if (!msg.isStreaming) setMessages(m => [...m, { id: nextId(), role: 'reasoning', text: String(msg.text ?? '') }]);
        } else if (msg.type === 'suggestions') {
          setSuggestions(Array.isArray(msg.suggestions) ? msg.suggestions : []);
        } else if (msg.type === 'status') { setStatus(msg.text); }
        else if ((msg as any).type === 'plugins') { setPlugins(((msg as any).plugins as PluginInfo[]) ?? []); }
        else if (msg.type === 'trace') { setMessages(m => [...m, { id: nextId(), role: 'trace', text: String(msg.text ?? '') }]); }
        else if (msg.type === 'tool_call') {
          streamedRef.current = false; pendingDeltaRef.current = '';
          const txt = String(msg.text ?? '');
          setMessages(m => [...m, { id: nextId(), role: 'tool_call', text: txt }]);
          setTerminalLogs(l => [...l, `▶ ${txt}`].slice(-300));
        }
        else if (msg.type === 'tool_result') {
          const txt = String(msg.text ?? '');
          setMessages(m => [...m, { id: nextId(), role: 'tool_result', text: txt }]); setPendingApproval(null);
          setTerminalLogs(l => [...l, `◀ ${txt}`].slice(-300));
        }
        else if (msg.type === 'assistant') {
          const incoming = String(msg.text ?? '');
          setSuggestions([]); setReasoning(null);
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
          // Heuristic suggestions after assistant
          if (incoming && !String(msg.text ?? '').includes('Error')) {
            const md = modeRef.current;
            setSuggestions(md === 'plan' ? ['按剛才的計劃執行', '調整方案', '先匯出報告'] : md === 'ask' ? ['解釋更多', '匯出為表格'] : ['繼續', '撤銷', '匯出 IFC']);
          }
          const cur = settingsRef.current; const active = cur?.providers.find(p => p.id === cur.activeProviderId) ?? cur?.providers[0];
          setStatus(active ? `Active: ${active.name}` : 'Ready');
        } else if (msg.type === 'error') {
          setMessages(m => [...m, { id: nextId(), role: 'system', text: `Error: ${String(msg.text ?? '')}` }]);
          setIsRunning(false); streamedRef.current = false; pendingDeltaRef.current = '';
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
    postToHost({ type: 'listPlugins' } as any);
    const t = setTimeout(() => { postToHost({ type: 'requestSettings' }); postToHost({ type: 'getCatalog' } as any); postToHost({ type: 'listPlugins' } as any); }, 800);
    return () => { clearTimeout(t); off(); };
  }, []);

  useEffect(() => { scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' }); }, [messages, isRunning, currentPlan, pendingApproval, reasoning]);

  const providers = settings?.providers ?? [];
  const activeProvider = useMemo(() => providers.find(p => p.id === (selectedProviderId || settings?.activeProviderId)) ?? providers[0] ?? null, [providers, selectedProviderId, settings?.activeProviderId]);
  const grouped = useMemo(() => {
    const f = providerSearch.toLowerCase().trim();
    let list = providers;
    if (f) list = providers.filter(p => `${p.name} ${p.model} ${p.baseUrl} ${p.group}`.toLowerCase().includes(f));
    const groups: Record<string, ProviderConfig[]> = {};
    for (const p of list) { const g = (p.group ?? 'custom').toLowerCase(); (groups[g] ??= []).push(p); }
    const order = ['cloud', 'gateway', 'local', 'custom'];
    return order.filter(g => groups[g]?.length).map(g => ({ group: g, items: groups[g] }));
  }, [providers, providerSearch]);

  const handleSend = (overrideText?: string) => {
    const raw = (typeof overrideText === 'string' ? overrideText : input);
    const parsed = parseInput(raw);
    const text = parsed.cleanText.trim();
    const atts = [...attachments];
    // empty check allowing slash commands that don't need text
    const isLocalSlash = parsed.slash && ['clear', 'help', 'checkpoints', 'resume'].includes(parsed.slash.name);
    if ((!text && atts.length === 0 && !isLocalSlash) || isRunning) {
      // still need to handle local slash like /help even if text empty? Already handled via parsed but we send to host for those too
      if (!isLocalSlash) return;
    }
    // handle local slash without LLM
    if (parsed.slash) {
      if (parsed.slash.name === 'clear') { handleClear(); setInput(''); setSlashOpen(false); setMentionOpen(false); return; }
      if (parsed.slash.name === 'help') {
        setMessages(m => [...m, { id: nextId(), role: 'system', text: `可用命令：${SLASH_COMMANDS.map(c => `${c.label} ${c.argsHint ?? ''} — ${c.description}`).join('\n')}\n\n@提及：@selection @activeView @visible @warnings @levels @sheets` }]);
        setInput(''); setSlashOpen(false); return;
      }
      if (parsed.slash.name === 'checkpoints') { postToHost({ type: 'listCheckpoints' } as any); setMessages(m => [...m, { id: nextId(), role: 'you', text: raw }]); setInput(''); setSlashOpen(false); setTerminalOpen(true); setTerminalLogs(l => [...l, `$ ${raw}`]); return; }
    }
    const cur = settingsRef.current ?? settings;
    const active = cur?.providers.find(p => p.id === (selectedProviderId || cur?.activeProviderId)) ?? cur?.providers[0];
    if (!cur || !active) { setMessages(m => [...m, { id: nextId(), role: 'system', text: 'No provider configured. Open Providers to add one.' }]); setShowSettings(true); return; }
    if (!active.apiKey && !active.baseUrl.includes('localhost')) { setMessages(m => [...m, { id: nextId(), role: 'system', text: `Provider '${active.name}' has no API key.` }]); setShowSettings(true); return; }
    if (selectedProviderId && selectedProviderId !== cur.activeProviderId) {
      const ns: AppSettings = { ...cur, activeProviderId: selectedProviderId };
      setSettings(ns); settingsRef.current = ns; postToHost({ type: 'saveSettings', settings: ns });
    }
    // determine effective mode from slash
    let effMode = mode;
    if (parsed.effectiveMode) effMode = parsed.effectiveMode;
    // push history for up arrow
    if (raw.trim()) { inputHistoryRef.current = [raw.trim(), ...inputHistoryRef.current.filter(x => x !== raw.trim())].slice(0, 50); historyIdxRef.current = -1; }
    setMessages(m => [...m, { id: nextId(), role: 'you', text: raw.trim() || (atts.length ? '（圖片）' : ''), attachments: atts.length ? atts : undefined }]);
    setInput(''); setAttachments([]); setSuggestions([]); setReasoning(null); setSlashOpen(false); setMentionOpen(false);
    setIsRunning(true); streamedRef.current = false; pendingDeltaRef.current = '';
    setStatus(effMode === 'plan' ? 'Planning...' : effMode === 'ask' ? 'Thinking...' : 'Working...');
    setTerminalLogs(l => [...l, `$ ${raw.trim()} [${effMode}]`].slice(-300));
    postToHost({ type: 'send', text: text || raw.trim(), attachments: atts.length ? atts : undefined, mode: effMode, slash: parsed.slash?.name, mentions: parsed.mentions } as any);
  };
  const handleApprove = (approved: boolean) => {
    if (!pendingApproval) return;
    postToHost({ type: 'approveTool', toolCallId: pendingApproval.toolCallId, approved } as any);
    setMessages(m => [...m, { id: nextId(), role: 'system', text: approved ? `已批准 ${pendingApproval.toolName}` : `已拒絕 ${pendingApproval.toolName}` }]);
    setPendingApproval(null);
  };
  const handleStop = () => { postToHost({ type: 'cancel' }); setStatus('Cancelling...'); streamedRef.current = false; setTerminalLogs(l => [...l, '— cancelled —'].slice(-300)); };
  const handleClear = () => { setMessages([{ id: 'cleared', role: 'system', text: 'Cleared.' }]); streamedRef.current = false; pendingDeltaRef.current = ''; setCurrentPlan(null); setTodos([]); setPendingApproval(null); setSuggestions([]); setReasoning(null); setContext(null); postToHost({ type: 'clear' }); setTerminalLogs(l => [...l, '— cleared —'].slice(-300)); };

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const v = e.target.value;
    setInput(v);
    const cursor = e.target.selectionStart ?? v.length;
    const before = v.slice(0, cursor);
    // slash detection: at start or after space, starts with /
    const slashMatch = before.match(/(^|\n|\s)\/([a-z]*)$/);
    if (slashMatch) {
      setSlashQuery(slashMatch[2] ?? '');
      setSlashOpen(true);
      setSlashIndex(0);
      setMentionOpen(false);
    } else {
      setSlashOpen(false);
      // mention detection
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
      if (e.key === 'ArrowDown') { e.preventDefault(); setSlashIndex(i => (i + 1) % list.length); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setSlashIndex(i => (i - 1 + list.length) % list.length); return; }
      if (e.key === 'Enter' || e.key === 'Tab') {
        if (list[slashIndex]) { e.preventDefault(); commitSlash(list[slashIndex].name); return; }
      }
      if (e.key === 'Escape') { e.preventDefault(); setSlashOpen(false); return; }
    }
    if (mentionOpen) {
      const list = filterMentions(mentionQuery);
      if (e.key === 'ArrowDown') { e.preventDefault(); setMentionIndex(i => (i + 1) % list.length); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setMentionIndex(i => (i - 1 + list.length) % list.length); return; }
      if (e.key === 'Enter' || e.key === 'Tab') {
        if (list[mentionIndex]) { e.preventDefault(); commitMention(list[mentionIndex].id); return; }
      }
      if (e.key === 'Escape') { e.preventDefault(); setMentionOpen(false); return; }
    }
    if (e.key === 'ArrowUp' && !e.shiftKey && input.trim() === '') {
      // history
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
    setSettings(ns); settingsRef.current = ns; selectForEdit(p); postToHost({ type: 'saveSettings', settings: ns }); setCatalogFilter('');
  };
  const handleAddCustom = () => {
    const cur = settingsRef.current ?? settings; if (!cur) return;
    const p: ProviderConfig = { id: Math.random().toString(36).slice(2, 8), name: 'Custom', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'custom', icon: '＋' };
    const ns: AppSettings = { ...cur, providers: [...cur.providers, p] };
    setSettings(ns); settingsRef.current = ns; selectForEdit(p); postToHost({ type: 'saveSettings', settings: ns });
  };
  const handleSave = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (!editForm.name.trim() || !editForm.baseUrl.trim() || !editForm.model.trim()) { setStatus('Name, Base URL and Model are required.'); return; }
    try { new URL(editForm.baseUrl.trim()); } catch { setStatus('Base URL is not a valid URL.'); return; }
    const upd: ProviderConfig = { ...editing, name: editForm.name.trim(), baseUrl: editForm.baseUrl.trim().replace(/\/$/, ''), model: editForm.model.trim(), apiKey: editForm.apiKey.trim(), apiKind: editForm.apiKind, group: editForm.group, icon: editForm.icon };
    const ns: AppSettings = { ...cur, providers: cur.providers.map(p => p.id === editing.id ? upd : p) };
    setSettings(ns); settingsRef.current = ns; setEditing(upd); postToHost({ type: 'saveSettings', settings: ns }); setStatus(`Saved ${upd.name}`);
  };
  const handleDelete = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    if (cur.providers.length <= 1) { setStatus('Cannot delete the last provider.'); return; }
    const np = cur.providers.filter(p => p.id !== editing.id);
    const na = cur.activeProviderId === editing.id ? np[0].id : cur.activeProviderId;
    const ns: AppSettings = { ...cur, providers: np, activeProviderId: na };
    setSettings(ns); settingsRef.current = ns; setSelectedProviderId(na); setEditing(null); postToHost({ type: 'saveSettings', settings: ns }); setStatus(`Deleted. Active: ${np.find(p => p.id === na)?.name ?? na}`);
  };
  const handleUse = () => {
    const cur = settingsRef.current ?? settings; if (!cur || !editing) return;
    const ns: AppSettings = { ...cur, activeProviderId: editing.id };
    setSettings(ns); settingsRef.current = ns; setSelectedProviderId(editing.id); postToHost({ type: 'saveSettings', settings: ns }); setStatus(`Active: ${editing.name}`); setSelectorOpen(false);
  };
  const handleProviderChange = (id: string) => {
    setSelectedProviderId(id); setSelectorOpen(false);
    const cur = settingsRef.current ?? settings; if (cur) {
      const ns: AppSettings = { ...cur, activeProviderId: id };
      setSettings(ns); settingsRef.current = ns; postToHost({ type: 'saveSettings', settings: ns });
      const a = ns.providers.find(p => p.id === id); if (a) setStatus(`Active: ${a.name} · ${a.model}`);
    }
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

  const modeMeta: Record<ChatMode, { label: string; icon: any; color: string; desc: string; placeholder: string }> = {
    ask: { label: 'Ask', icon: MessageCircleQuestion, color: '#2563eb', desc: '只問不改', placeholder: '提問，例如：這個視圖有哪些標註類型？' },
    plan: { label: 'Plan', icon: ClipboardList, color: '#d97706', desc: '只讀規劃', placeholder: '描述目標，例如：規劃如何批次標註所有房間 — 將生成步驟' },
    build: { label: 'Build', icon: Hammer, color: '#059669', desc: '可寫執行', placeholder: '描述任務 + 可貼圖，例如：隱藏所有連結模型並導出 DWG' },
    auto: { label: 'Auto', icon: Zap, color: '#7c3aed', desc: '自動判斷', placeholder: '你想做什麼？' },
  };

  return (
    <div className="flex flex-col h-[100vh] bg-background text-foreground text-[13px] antialiased">
      {/* Header: Provider + Mode */}
      <div className="flex flex-col gap-2 p-2 border-b bg-white/80 backdrop-blur sticky top-0 z-30">
        <div className="flex gap-2 items-center">
          <div className="flex-1 relative">
            <button onClick={() => setSelectorOpen(v => !v)} className={`w-full flex items-center gap-2 px-2.5 py-1.5 rounded-xl border bg-white text-left shadow-sm transition ${selectorOpen ? 'border-primary ring-2 ring-primary/20' : 'border-border hover:border-primary/30'}`}>
              {activeProvider ? (
                <>
                  <span className="w-7 h-7 rounded-lg flex items-center justify-center border bg-muted shrink-0" style={{ background: `${GROUP_META[(activeProvider.group ?? 'custom').toLowerCase()]?.color}12` }}>
                    <ProviderIcon group={activeProvider.group} icon={activeProvider.icon} size={13} />
                  </span>
                  <span className="flex-1 min-w-0">
                    <span className="block text-xs font-semibold leading-none truncate">{activeProvider.name}</span>
                    <span className="block text-[11px] text-muted-foreground truncate">{activeProvider.model} · {activeProvider.apiKind ?? 'openai'}</span>
                  </span>
                  <span className={`w-2 h-2 rounded-full shrink-0 ${activeProvider.apiKey ? 'bg-emerald-500' : activeProvider.baseUrl.includes('localhost') ? 'bg-amber-500' : 'bg-red-500'} border-2 border-white shadow`} />
                  <ChevronDown size={14} className={`text-muted-foreground transition ${selectorOpen ? 'rotate-180' : ''}`} />
                </>
              ) : <span className="text-muted-foreground text-xs">{settings ? 'No providers' : 'Loading...'}</span>}
            </button>
            {selectorOpen && (
              <div className="absolute top-[calc(100%+8px)] left-0 right-0 bg-white border rounded-xl shadow-xl z-50 overflow-hidden animate-in fade-in">
                <div className="p-2 border-b flex items-center gap-2">
                  <Search size={14} className="text-muted-foreground ml-1" />
                  <input autoFocus value={providerSearch} onChange={e => setProviderSearch(e.target.value)} placeholder="搜索提供商、模型、URL..." className="flex-1 outline-none text-xs bg-transparent" />
                  {providerSearch && <button onClick={() => setProviderSearch('')} className="text-[11px] bg-muted px-2 py-0.5 rounded-md">清除</button>}
                </div>
                <div className="max-h-72 overflow-y-auto">
                  {grouped.length === 0 ? <div className="p-6 text-center text-muted-foreground text-xs">無匹配</div> : grouped.map(g => (
                    <div key={g.group}>
                      <div className="flex items-center gap-1.5 px-3 py-1.5 text-[10px] font-bold tracking-wider uppercase bg-muted/50" style={{ color: GROUP_META[g.group]?.color }}>
                        {(() => { const I = GROUP_META[g.group].icon; return <I size={10} />; })()} {GROUP_META[g.group].label} · {g.items.length}
                      </div>
                      {g.items.map(p => {
                        const isActive = p.id === (selectedProviderId || settings?.activeProviderId);
                        return (
                          <button key={p.id} onClick={() => handleProviderChange(p.id)} className={`w-full flex items-center gap-2 px-3 py-2 text-left border-b border-muted/50 hover:bg-muted/50 ${isActive ? 'bg-primary/5' : 'bg-white'}`}>
                            <span className={`w-7 h-7 rounded-lg flex items-center justify-center border shrink-0 ${isActive ? 'bg-primary/10 border-primary/20' : 'bg-muted border-border'}`}><ProviderIcon group={p.group} icon={p.icon} size={12} /></span>
                            <span className="flex-1 min-w-0">
                              <span className="flex items-center gap-2"><span className={`text-xs truncate ${isActive ? 'font-bold' : 'font-medium'}`}>{p.name}</span>{isActive && <span className="bg-primary text-primary-foreground text-[9px] font-bold px-1.5 py-0.5 rounded-full">ACTIVE</span>}{!p.apiKey && !p.baseUrl.includes('localhost') && <span className="w-1.5 h-1.5 rounded-full bg-red-500" />}</span>
                              <span className="block text-[11px] text-muted-foreground truncate">{p.model}</span>
                            </span>
                            {isActive ? <Check size={14} className="text-primary" /> : <span className="text-[10px] text-muted-foreground border rounded px-1">{p.apiKind ?? 'openai'}</span>}
                          </button>
                        );
                      })}
                    </div>
                  ))}
                  <div className="p-2 bg-muted/30 flex gap-2">
                    <button onClick={() => { setSelectorOpen(false); setShowSettings(true); }} className="flex-1 flex items-center justify-center gap-1.5 py-1.5 rounded-lg border bg-white text-xs font-semibold"><Settings2 size={12} /> 管理</button>
                    <button onClick={() => { setSelectorOpen(false); handleAddCustom(); setShowSettings(true); }} className="px-3 py-1.5 rounded-lg border border-dashed bg-white text-xs">+ New</button>
                  </div>
                </div>
              </div>
            )}
          </div>
          <button onClick={() => setTerminalOpen(v => !v)} className={`flex items-center gap-1 px-2.5 py-1.5 rounded-xl border text-xs font-semibold ${terminalOpen ? 'bg-slate-900 text-white border-slate-900' : 'bg-white hover:bg-muted'}`} title="Terminal (xterm)"><TerminalIcon size={14} />{terminalOpen ? '隱藏' : '終端'}</button>
          <button onClick={() => { postToHost({ type: 'listCheckpoints' } as any); setTerminalOpen(true); }} className="flex items-center gap-1 px-2 py-1.5 rounded-xl border bg-white text-xs hover:bg-muted" title="Checkpoints"><History size={12} />恢復</button>
          <button onClick={() => setShowSettings(v => !v)} className={`flex items-center gap-1.5 px-3 py-1.5 rounded-xl border text-xs font-semibold whitespace-nowrap ${showSettings ? 'bg-foreground text-background border-foreground' : 'bg-white hover:bg-muted'}`}><Settings2 size={14} />{showSettings ? '收起' : '管理'}</button>
          <button onClick={handleClear} className="px-3 py-1.5 rounded-xl border bg-white text-xs text-muted-foreground hover:text-foreground">清除</button>
        </div>

        {/* Mode Switcher — Kilo/Hermes inspired */}
        <div className="flex items-center gap-1.5">
          <div className="flex-1 flex bg-muted p-1 rounded-xl gap-1">
            {(Object.keys(modeMeta) as ChatMode[]).filter(k => k !== 'auto').map(m => {
              const meta = modeMeta[m];
              const active = mode === m;
              const Icon = meta.icon;
              return (
                <button key={m} onClick={() => setMode(m)} className={`flex-1 flex items-center justify-center gap-1.5 py-1.5 rounded-lg text-xs font-semibold transition ${active ? 'bg-white shadow text-foreground border' : 'text-muted-foreground hover:text-foreground'}`} style={active ? { borderColor: meta.color, color: meta.color } : {}}>
                  <Icon size={12} /> {meta.label}
                </button>
              );
            })}
            <button onClick={() => setMode('auto')} className={`px-2.5 py-1.5 rounded-lg text-xs font-semibold ${mode === 'auto' ? 'bg-white shadow border' : 'text-muted-foreground'}`} title="自動判斷">Auto</button>
          </div>
          <div className="hidden sm:flex items-center gap-1 text-[11px] text-muted-foreground">
            <span className="w-1.5 h-1.5 rounded-full" style={{ background: modeMeta[mode].color }} />{modeMeta[mode].desc}
          </div>
        </div>
      </div>

      {/* Context Bar — auto injected */}
      {context && (
        <div className="mx-2 mt-2 flex items-center gap-2 px-2 py-1.5 rounded-lg bg-slate-50 border text-xs">
          <span className="w-2 h-2 rounded-full bg-emerald-500 animate-pulse" />
          <span className="truncate">已選 {context.selectionCount}{context.selectionPreview ? ` · ${context.selectionPreview.slice(0, 60)}` : ''} | {context.activeView} 可見 {context.visibleCount ?? 0} | 警告 {context.warningCount ?? 0}</span>
          <span className="ml-auto text-[10px] text-muted-foreground hidden sm:inline">Auto Context · @提及可精確引用</span>
        </div>
      )}
      {/* Todo bar */}
      {todos.length > 0 && (
        <div className="mx-2 mt-2 rounded-xl border bg-white shadow-sm overflow-hidden">
          <div className="flex items-center gap-2 px-2 py-1.5 bg-muted/50 border-b text-xs font-semibold">
            <ClipboardList size={12} /> Todo · {todos.filter(t => t.status === 'done').length}/{todos.length}
            <span className="ml-auto text-[10px] flex gap-1">
              {todos.map(t => <span key={t.id} className={`w-1.5 h-1.5 rounded-full ${t.status === 'done' ? 'bg-emerald-500' : t.status === 'running' ? 'bg-blue-500 animate-pulse' : t.status === 'error' ? 'bg-red-500' : 'bg-gray-300'}`} title={t.title} />)}
            </span>
          </div>
          <div className="max-h-28 overflow-auto p-1 flex flex-col gap-1">
            {todos.map(t => (
              <div key={t.id} className={`flex items-center gap-2 px-2 py-1 rounded-lg border text-xs ${t.status === 'done' ? 'bg-emerald-50 border-emerald-200' : t.status === 'running' ? 'bg-blue-50 border-blue-200' : t.status === 'error' ? 'bg-red-50 border-red-200' : 'bg-muted/20'}`}>
                <span className={`w-5 h-5 rounded-full flex items-center justify-center text-[10px] font-bold border bg-white`}>{t.status === 'done' ? '✓' : t.status === 'running' ? '●' : t.status === 'error' ? '✗' : '·'}</span>
                <span className="flex-1 truncate">{t.title}</span>
                {t.tool && <span className="text-[10px] font-mono bg-white border rounded px-1">{t.tool}</span>}
              </div>
            ))}
          </div>
        </div>
      )}
      {/* Checkpoints quick */}
      {checkpoints.length > 0 && !isRunning && (
        <div className="mx-2 mt-2 flex items-center gap-2 px-2 py-1 rounded-lg bg-blue-50 border border-blue-200 text-xs">
          <History size={12} className="text-blue-600" />
          <span>{checkpoints.length} checkpoints</span>
          <span className="flex-1 truncate text-muted-foreground">{checkpoints[0]?.preview?.slice(0, 40)}</span>
          <button onClick={() => postToHost({ type: 'resumeCheckpoint', id: checkpoints[0].id } as any)} className="px-2 py-0.5 rounded bg-white border text-xs flex items-center gap-1"><RotateCcw size={10} />恢復最新</button>
        </div>
      )}

      {/* Mode banner */}
      {mode === 'plan' && (
        <div className="mx-2 mt-2 flex items-center gap-2 px-3 py-2 rounded-xl bg-amber-50 border border-amber-200 text-amber-800 text-xs">
          <ClipboardList size={14} className="text-amber-600 shrink-0" />
          <span className="flex-1"><b>Plan 模式</b> — 唯讀規劃，不修改模型，將輸出步驟與所需工具，待你批准後再 Build 執行</span>
          <span className="text-[10px] bg-amber-100 border border-amber-200 rounded-full px-1.5 py-0.5 font-bold">READ-ONLY</span>
        </div>
      )}
      {mode === 'ask' && (
        <div className="mx-2 mt-2 flex items-center gap-2 px-3 py-2 rounded-xl bg-blue-50 border border-blue-200 text-blue-800 text-xs">
          <MessageCircleQuestion size={14} className="text-blue-600" />
          <span><b>Ask 模式</b> — 只讀查詢（L1），可檢查模型但不修改</span>
        </div>
      )}

      <div className="px-2 py-1.5 text-[11px] text-muted-foreground border-b flex items-center gap-2 min-h-[22px] bg-muted/20">
        {isRunning ? <Loader2 size={11} className="animate-spin text-primary" /> : <span className={`w-1.5 h-1.5 rounded-full ${activeProvider?.apiKey || activeProvider?.baseUrl.includes('localhost') ? 'bg-emerald-500' : 'bg-amber-500'}`} />}
        <span className="flex-1 truncate">{isRunning ? '處理中…' : status}</span>
        {activeProvider && <span className="text-[10px] bg-muted border rounded-full px-1.5 py-0.5 max-w-[150px] truncate">{activeProvider.baseUrl.replace(/^https?:\/\//, '')}</span>}
      </div>

      <div className="flex gap-0 border-b bg-muted/30">
        <button onClick={() => setActiveTab('chat')} className={`flex-1 flex items-center justify-center gap-1.5 py-2 text-xs font-semibold border-b-2 ${activeTab === 'chat' ? 'border-primary bg-white' : 'border-transparent bg-muted/30 text-muted-foreground'}`}>
          <Sparkles size={12} /> 對話 {messages.length > 1 ? `· ${messages.length}` : ''}
        </button>
        <button onClick={() => { setActiveTab('plugins'); postToHost({ type: 'listPlugins' } as any); }} className={`flex-1 flex items-center justify-center gap-1.5 py-2 text-xs font-semibold border-b-2 ${activeTab === 'plugins' ? 'border-primary bg-white' : 'border-transparent bg-muted/30 text-muted-foreground'}`}>
          <PlugZap size={12} /> 插件 {plugins.length ? `· ${plugins.length} · ${plugins.reduce((n, p) => n + p.tools.filter(t => t.enabled).length, 0)}/${plugins.reduce((n, p) => n + p.tools.length, 0)}` : ''} · MIT
        </button>
      </div>

      {showSettings && (
        <div className="border-b bg-muted/20 max-h-[52vh] flex flex-col">
          <div className="px-3 py-2 bg-white border-b flex items-center gap-2">
            <span className="font-bold text-xs flex items-center gap-1.5"><Database size={14} /> 提供商</span>
            <span className="text-[11px] bg-muted rounded-full px-2 py-0.5">{providers.length} 已配置</span>
            <span className="flex-1" />
            <span className="text-[10px] text-muted-foreground flex items-center gap-1"><ShieldCheck size={10} /> DPAPI 加密</span>
          </div>
          {!settings ? <div className="m-3 p-3 bg-amber-50 border border-amber-200 rounded-xl text-xs text-amber-800">載入中...</div> : (
            <div className="flex gap-3 p-3 overflow-hidden flex-1 min-h-0">
              <div className="flex-1 flex flex-col gap-2 min-w-0 overflow-hidden">
                <div className="relative">
                  <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-muted-foreground" />
                  <input value={editSearch} onChange={e => setEditSearch(e.target.value)} placeholder="過濾已配置..." className="w-full pl-7 pr-2 py-1.5 rounded-lg border bg-white text-xs outline-none focus:border-primary" />
                </div>
                <div className="border rounded-xl bg-white overflow-hidden flex flex-col max-h-40">
                  <div className="flex-1 overflow-y-auto">
                    {filteredProvidersLeft.map(p => {
                      const isEditing = editing?.id === p.id; const isActive = p.id === settings.activeProviderId;
                      return (
                        <button key={p.id} onClick={() => selectForEdit(p)} className={`w-full flex items-center gap-2 px-2.5 py-2 text-left border-b border-muted/30 ${isEditing ? 'bg-primary/5' : isActive ? 'bg-emerald-50' : 'bg-white hover:bg-muted/30'}`}>
                          <span className={`w-7 h-7 rounded-lg flex items-center justify-center border shrink-0 ${isEditing ? 'bg-primary/10 border-primary/20' : 'bg-muted'}`}><ProviderIcon group={p.group} icon={p.icon} size={13} /></span>
                          <span className="flex-1 min-w-0">
                            <span className="flex items-center gap-1.5"><span className={`text-xs truncate ${isEditing ? 'font-bold' : 'font-medium'}`}>{p.name}</span>{isActive && <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" />}{!p.apiKey && !p.baseUrl.includes('localhost') && <span className="text-[9px] text-red-600 bg-red-50 border border-red-200 rounded px-1">需 Key</span>}</span>
                            <span className="block text-[11px] text-muted-foreground truncate">{p.model}</span>
                          </span>
                          <span className="text-[10px] text-muted-foreground">{p.group}</span>
                        </button>
                      );
                    })}
                  </div>
                  <button onClick={handleAddCustom} className="flex items-center justify-center gap-1.5 py-1.5 border-t bg-muted/30 text-xs font-semibold">+ 自訂提供商</button>
                </div>
                <div className="border rounded-xl bg-white overflow-hidden flex flex-col flex-1 min-h-[140px]">
                  <div className="px-2.5 py-1.5 text-[11px] font-bold bg-muted/50 border-b flex items-center gap-1.5">
                    <Globe size={12} /> 快速添加 — 預設
                    <span className="flex-1" />
                    <span className="relative flex items-center">
                      <Search size={10} className="absolute left-1.5 text-muted-foreground" />
                      <input value={catalogFilter} onChange={e => setCatalogFilter(e.target.value)} placeholder="過濾預設..." className="pl-5 pr-2 py-1 rounded-md border bg-white text-[11px] outline-none w-24" />
                    </span>
                  </div>
                  <div className="p-2 grid grid-cols-2 gap-1.5 overflow-y-auto flex-1 content-start">
                    {catalogFiltered.map(c => {
                      const already = providers.some(p => p.id === c.id);
                      return (
                        <button key={c.id} onClick={() => handleAddFromPreset(c)} className={`flex flex-col gap-1 p-2 rounded-lg border text-left ${already ? 'bg-muted/50 opacity-70' : 'bg-white hover:border-primary/30'}`}>
                          <span className="flex items-center gap-1.5"><span className="w-5 h-5 rounded-md flex items-center justify-center border text-[11px]" style={{ background: `${GROUP_META[c.group]?.color}14`, borderColor: `${GROUP_META[c.group]?.color}30` }}><ProviderIcon group={c.group} icon={c.icon} size={10} /></span><span className="text-xs font-semibold truncate">{c.name}</span>{already && <span className="ml-auto text-[9px] bg-muted rounded px-1">已添加</span>}</span>
                          <span className="text-[11px] text-muted-foreground truncate">{c.model}</span>
                          <span className="text-[10px] text-muted-foreground/70 truncate">{c.description}</span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              </div>
              <div className="flex-[1.3] flex flex-col gap-2 min-w-0">
                {editing ? (
                  <div className="border rounded-xl bg-white p-2.5 flex flex-col gap-2 overflow-y-auto">
                    <div className="flex items-center gap-2">
                      <span className="w-7 h-7 rounded-lg flex items-center justify-center border" style={{ background: `${GROUP_META[editForm.group]?.color}14`, borderColor: `${GROUP_META[editForm.group]?.color}30` }}><ProviderIcon group={editForm.group} icon={editForm.icon} size={13} /></span>
                      <span className="font-bold text-xs truncate">{editing.name}</span>
                      <span className="text-[11px] bg-muted rounded-full px-1.5 py-0.5">{editForm.group} · {editForm.apiKind}</span>
                      <span className="flex-1" />
                      <span className={`w-2 h-2 rounded-full ${editForm.apiKey ? 'bg-emerald-500' : editForm.baseUrl.includes('localhost') ? 'bg-amber-500' : 'bg-red-500'}`} />
                    </div>
                    <label className="text-[11px] font-semibold flex flex-col gap-1">名稱<input value={editForm.name} onChange={e => setEditForm(f => ({ ...f, name: e.target.value }))} className="px-2 py-1.5 rounded-lg border bg-white text-xs outline-none focus:border-primary" /></label>
                    <label className="text-[11px] font-semibold flex flex-col gap-1">Base URL<input value={editForm.baseUrl} onChange={e => setEditForm(f => ({ ...f, baseUrl: e.target.value }))} className="px-2 py-1.5 rounded-lg border bg-white text-xs outline-none font-mono focus:border-primary" /></label>
                    <div className="grid grid-cols-2 gap-2">
                      <label className="text-[11px] font-semibold flex flex-col gap-1">API 類型<select value={editForm.apiKind} onChange={e => setEditForm(f => ({ ...f, apiKind: e.target.value }))} className="px-2 py-1.5 rounded-lg border bg-white text-xs"><option value="openai">OpenAI Compatible</option><option value="responses">Responses</option><option value="anthropic">Anthropic</option></select></label>
                      <label className="text-[11px] font-semibold flex flex-col gap-1">分組<select value={editForm.group} onChange={e => setEditForm(f => ({ ...f, group: e.target.value }))} className="px-2 py-1.5 rounded-lg border bg-white text-xs"><option value="cloud">Cloud</option><option value="gateway">Gateway</option><option value="local">Local</option><option value="custom">Custom</option></select></label>
                    </div>
                    <label className="text-[11px] font-semibold flex flex-col gap-1">
                      <span className="flex items-center gap-1.5">模型 <span className="font-normal text-muted-foreground">· Fetch 列出</span><button onClick={handleFetchModels} disabled={!!modelsLoading} className="ml-auto flex items-center gap-1 text-[11px] px-2 py-0.5 rounded-md border bg-white">{modelsLoading === editing.id ? <Loader2 size={11} className="animate-spin" /> : <Database size={11} />} 取得</button></span>
                      <input value={editForm.model} onChange={e => setEditForm(f => ({ ...f, model: e.target.value }))} className="px-2 py-1.5 rounded-lg border bg-white text-xs font-mono outline-none focus:border-primary" />
                    </label>
                    {editingModels.length > 0 && (
                      <div className="border rounded-lg overflow-hidden bg-muted/20">
                        <div className="px-2 py-1 text-[10px] font-bold text-muted-foreground flex items-center gap-1 border-b bg-muted/30"><Database size={10} /> 可用模型 · {editingModels.length}<button onClick={() => setModelsMap(m => { const c = { ...m }; delete c[editing.id]; return c; })} className="ml-auto text-[10px] bg-white border rounded px-1">隱藏</button></div>
                        <div className="max-h-24 overflow-y-auto bg-white">
                          {editingModels.slice(0, 50).map(mid => (
                            <button key={mid} onClick={() => setEditForm(f => ({ ...f, model: mid }))} className={`w-full text-left px-2 py-1 border-b border-muted/20 text-xs font-mono flex items-center gap-1 ${editForm.model === mid ? 'bg-primary/5' : 'bg-white hover:bg-muted/30'}`}><span className="flex-1 truncate">{mid}</span>{editForm.model === mid && <Check size={11} className="text-primary" />}</button>
                          ))}
                        </div>
                        {editingModels.length > 8 && (
                          <div className="p-1.5 bg-muted/20 border-t">
                            <div className="text-[10px] text-muted-foreground mb-1">精緻選擇器（open-model-selector）：</div>
                            <ModelSelector models={editingModels.map(id => ({ id, name: id, provider: editing.name } as any))} value={editForm.model} onChange={(id: string) => setEditForm(f => ({ ...f, model: id }))} placeholder="搜尋模型..." storageKey={`oms-${editing.id}`} />
                          </div>
                        )}
                      </div>
                    )}
                    <label className="text-[11px] font-semibold flex flex-col gap-1">API Key<input value={editForm.apiKey} onChange={e => setEditForm(f => ({ ...f, apiKey: e.target.value }))} type="password" placeholder={editForm.baseUrl.includes('localhost') ? 'ollama 或留空' : 'sk-...'} className="px-2 py-1.5 rounded-lg border bg-white text-xs font-mono outline-none focus:border-primary" /><span className="text-[10px] font-normal text-muted-foreground">{editForm.baseUrl.includes('localhost') ? '本地可留空或 ollama' : 'DPAPI 加密儲存於 %AppData%\\RevitAiPoc\\settings.json'}</span></label>
                    {testState && testState.id === editing.id && (
                      <div className={`px-2 py-1.5 rounded-lg text-xs flex items-center gap-1.5 border ${testState.loading ? 'bg-blue-50 border-blue-200 text-blue-700' : testState.ok ? 'bg-emerald-50 border-emerald-200 text-emerald-700' : 'bg-red-50 border-red-200 text-red-700'}`}>
                        {testState.loading ? <Loader2 size={12} className="animate-spin" /> : testState.ok ? <ShieldCheck size={12} /> : <span>✗</span>}<span className="flex-1 break-words">{testState.message}</span>
                      </div>
                    )}
                    <div className="flex gap-1.5">
                      <button onClick={handleSave} className="flex-1 flex items-center justify-center gap-1 py-1.5 rounded-lg bg-primary text-primary-foreground text-xs font-bold"><Copy size={12} /> 保存</button>
                      <button onClick={handleUse} className="flex items-center gap-1 px-3 py-1.5 rounded-lg bg-emerald-600 text-white text-xs font-bold"><Play size={12} /> 設為當前</button>
                      <button onClick={handleTest} disabled={testState?.loading} className="flex items-center gap-1 px-2.5 py-1.5 rounded-lg border bg-white text-xs"><PlugZap size={12} /> 測試</button>
                    </div>
                    <div className="flex gap-1.5">
                      <button onClick={() => { try { window.open(editForm.baseUrl, '_blank'); } catch {} }} className="flex-1 flex items-center justify-center gap-1 py-1 rounded-lg bg-muted border text-xs text-muted-foreground"><ExternalLink size={11} /> 開啟 Base URL</button>
                      <button onClick={handleDelete} className="flex items-center gap-1 px-2.5 py-1 rounded-lg border border-red-200 bg-white text-red-600 text-xs"><Trash2 size={12} /> 刪除</button>
                    </div>
                  </div>
                ) : <div className="flex-1 border border-dashed rounded-xl bg-white flex flex-col items-center justify-center p-4 text-center gap-2"><div className="w-9 h-9 rounded-xl bg-primary/10 border border-primary/20 flex items-center justify-center"><Settings2 size={16} className="text-primary" /></div><div className="font-semibold text-xs">選擇左側提供商以編輯</div><div className="text-[11px] text-muted-foreground max-w-[220px]">或從預設快速添加；本地 Ollama/LM Studio 無需 Key，直接啟動本地服務即可</div></div>}
              </div>
            </div>
          )}
        </div>
      )}

      {activeTab === 'chat' ? (
        <>
          <div ref={scrollRef} className="flex-1 overflow-y-auto p-3 flex flex-col gap-3 bg-[#fcfcfc]">
            {reasoning && (
              <div className="rounded-xl border bg-amber-50/70 border-amber-200 overflow-hidden">
                <div className="flex items-center gap-1.5 px-3 py-1.5 bg-amber-100/70 border-b border-amber-200 text-xs font-semibold text-amber-800"><Brain size={12} /> 思考中 {reasoning.isStreaming && <Loader2 size={11} className="animate-spin" />}</div>
                <div className="p-2.5 text-xs leading-relaxed text-amber-900 whitespace-pre-wrap">{reasoning.text || '...'}</div>
              </div>
            )}
            {currentPlan && (
              <div className="rounded-xl border bg-white shadow-sm overflow-hidden">
                <div className="flex items-center gap-2 px-3 py-2 border-b bg-gradient-to-r from-amber-50 to-white">
                  <ClipboardList size={14} className="text-amber-600" />
                  <span className="font-bold text-xs">{currentPlan.title ?? 'Plan'}</span>
                  {currentPlan.isStreaming && <span className="text-[10px] bg-amber-100 border border-amber-200 rounded-full px-1.5 py-0.5 flex items-center gap-1"><Loader2 size={10} className="animate-spin" /> 生成中</span>}
                  <span className="ml-auto text-[11px] text-muted-foreground">{currentPlan.steps?.length ?? 0} 步驟</span>
                </div>
                {currentPlan.description && <div className="px-3 py-1.5 text-xs text-muted-foreground border-b bg-amber-50/30">{currentPlan.description}</div>}
                <div className="p-2 flex flex-col gap-1.5">
                  {currentPlan.steps?.map((s, i) => (
                    <div key={i} className="flex gap-2 p-2 rounded-lg border bg-muted/20">
                      <span className={`w-6 h-6 rounded-full flex items-center justify-center text-xs font-bold shrink-0 ${s.status === 'done' ? 'bg-emerald-100 text-emerald-700 border border-emerald-200' : s.status === 'running' ? 'bg-blue-100 text-blue-700 border border-blue-200' : 'bg-white border'}`}>{i + 1}</span>
                      <span className="flex-1 min-w-0">
                        <span className="block text-xs font-semibold truncate">{s.title}</span>
                        {s.description && <span className="block text-[11px] text-muted-foreground line-clamp-2">{s.description}</span>}
                        {s.tool && <span className="inline-flex items-center gap-1 text-[10px] bg-white border rounded-full px-1.5 py-0.5 mt-1 font-mono">{s.tool}</span>}
                      </span>
                      <span className={`text-[10px] px-1.5 py-0.5 rounded-full border h-fit ${s.status === 'done' ? 'bg-emerald-50 border-emerald-200 text-emerald-700' : s.status === 'running' ? 'bg-blue-50 border-blue-200 text-blue-700 animate-pulse' : 'bg-white'}`}>{s.status ?? 'pending'}</span>
                    </div>
                  ))}
                </div>
                <div className="px-3 py-2 border-t bg-muted/20 flex gap-2">
                  <button onClick={() => { setMode('build'); setCurrentPlan(null); handleSend('按剛才的計劃執行。'); }} className="flex-1 flex items-center justify-center gap-1 py-1.5 rounded-lg bg-emerald-600 text-white text-xs font-bold"><Hammer size={12} /> 批准並執行</button>
                  <button onClick={() => setCurrentPlan(null)} className="px-3 py-1.5 rounded-lg border bg-white text-xs">調整</button>
                </div>
              </div>
            )}

            {messages.map(m => {
              const isUser = m.role === 'you';
              const isPlan = m.role === 'plan';
              const isApproval = m.role === 'approval';
              const isReasoning = m.role === 'reasoning';
              if (isPlan && m.plan) {
                return (
                  <div key={m.id} className="rounded-xl border bg-white shadow-sm overflow-hidden">
                    <div className="px-3 py-2 bg-amber-50 border-b flex items-center gap-2"><ClipboardList size={14} className="text-amber-600" /><span className="font-bold text-xs">{m.plan.title}</span>{m.isStreaming && <Loader2 size={12} className="animate-spin text-amber-600" />}</div>
                    <div className="p-2 flex flex-col gap-1">
                      {m.plan.steps.map((s, i) => (
                        <div key={i} className="flex gap-2 p-2 rounded-lg bg-muted/20 border text-xs"><span className="w-5 h-5 rounded-full bg-white border flex items-center justify-center text-[11px] font-bold">{i + 1}</span><span className="flex-1"><span className="font-semibold">{s.title}</span>{s.description && <span className="block text-muted-foreground text-[11px]">{s.description}</span>}</span></div>
                      ))}
                    </div>
                  </div>
                );
              }
              if (isApproval && m.approval) {
                const isPending = pendingApproval?.toolCallId === m.approval.toolCallId;
                return (
                  <div key={m.id} className="rounded-xl border bg-white shadow-sm overflow-hidden border-amber-200">
                    <div className="px-3 py-2 bg-amber-50 border-b flex items-center gap-2"><ShieldAlert size={14} className="text-amber-600" /><span className="font-bold text-xs">需要確認 — {m.approval.toolName}</span><span className="ml-auto text-[10px] bg-amber-100 border border-amber-200 rounded-full px-1.5 py-0.5 font-bold">{m.approval.tier}</span></div>
                    <div className="p-3 flex flex-col gap-2">
                      <div className="text-xs font-mono bg-muted p-2 rounded-lg border break-all">{m.approval.args}</div>
                      {m.approval.description && <div className="text-xs text-muted-foreground">{m.approval.description}</div>}
                      {isPending ? (
                        <div className="flex gap-2">
                          <button onClick={() => handleApprove(true)} className="flex-1 flex items-center justify-center gap-1 py-1.5 rounded-lg bg-emerald-600 text-white text-xs font-bold"><ThumbsUp size={12} /> 批准</button>
                          <button onClick={() => handleApprove(false)} className="flex-1 flex items-center justify-center gap-1 py-1.5 rounded-lg border bg-white text-xs font-bold"><ThumbsDown size={12} /> 拒絕</button>
                        </div>
                      ) : <div className="text-xs text-muted-foreground text-center">（已處理）</div>}
                    </div>
                  </div>
                );
              }
              if (isReasoning) {
                return (
                  <div key={m.id} className="rounded-xl border bg-amber-50/50 border-amber-200 overflow-hidden">
                    <div className="px-3 py-1.5 bg-amber-100/50 border-b flex items-center gap-1.5 text-xs font-semibold text-amber-800"><Brain size={12} /> 推理過程</div>
                    <div className="p-2.5 text-xs text-muted-foreground whitespace-pre-wrap">{m.text}</div>
                  </div>
                );
              }
              return (
                <div key={m.id} className={`max-w-[85%] px-3 py-2 rounded-2xl text-xs leading-relaxed whitespace-pre-wrap break-words shadow-sm border ${isUser ? 'self-end bg-primary text-primary-foreground border-primary' : m.role === 'tool_call' ? 'self-start bg-blue-50 border-blue-200 text-blue-900' : m.role === 'tool_result' ? 'self-start bg-emerald-50 border-emerald-200 text-emerald-900' : m.role === 'trace' ? 'self-start bg-muted border-dashed text-muted-foreground' : m.role === 'system' ? 'self-start bg-amber-50 border-amber-200' : 'self-start bg-white'}`}>
                  {(m.role === 'tool_call' || m.role === 'tool_result' || m.role === 'trace') ? (
                    <div>
                      {m.role === 'tool_call' && <span className="font-bold text-[11px] uppercase text-primary">▶ Tool Call</span>}
                      {m.role === 'tool_result' && <span className="font-bold text-[11px] uppercase text-emerald-600">◀ Tool Result</span>}
                      <div className="font-mono text-[11px] mt-1 break-all">{m.text}</div>
                    </div>
                  ) : isUser ? (
                    <div>
                      {m.text && <div>{m.text}</div>}
                      {m.attachments && m.attachments.length > 0 && (
                        <div className="flex flex-wrap gap-1.5 mt-2">
                          {m.attachments.map(a => (
                            <img key={a.id} src={a.dataUrl} alt={a.name} className="max-w-[180px] max-h-[120px] rounded-lg border border-white/30 object-cover" />
                          ))}
                        </div>
                      )}
                    </div>
                  ) : <ReactMarkdown>{m.text}</ReactMarkdown>}
                </div>
              );
            })}
            {isRunning && <div className="text-xs text-muted-foreground italic flex items-center gap-1.5"><Loader2 size={12} className="animate-spin" /> {mode === 'plan' ? '規劃中…' : mode === 'ask' ? '思考中…' : '執行中…'} {pendingDeltaRef.current.length > 0 ? `(${pendingDeltaRef.current.length} chars)` : ''}</div>}
            {suggestions.length > 0 && !isRunning && (
              <div className="flex flex-wrap gap-1.5">
                {suggestions.map(s => (
                  <button key={s} onClick={() => handleSend(s)} className="flex items-center gap-1 px-2.5 py-1 rounded-full bg-white border shadow-sm text-xs hover:border-primary/30 hover:bg-primary/5">
                    <Lightbulb size={11} className="text-amber-500" />{s}
                  </button>
                ))}
              </div>
            )}
          </div>
          <Terminal open={terminalOpen} logs={terminalLogs} onClose={() => setTerminalOpen(false)} onSend={txt => { setInput(txt); setTimeout(() => handleSend(txt), 0); }} />

          {/* Composer with multimodal — assistant-ui attachment pattern */}
          <div ref={composerRef} onDragOver={e => { e.preventDefault(); setIsDragging(true); }} onDragLeave={() => setIsDragging(false)} onDrop={handleDrop} onPaste={handlePaste} className={`p-2 border-t bg-white relative ${isDragging ? 'bg-primary/5' : ''}`}>
            {isDragging && <div className="absolute inset-0 bg-primary/5 border-2 border-dashed border-primary/30 rounded-xl flex items-center justify-center pointer-events-none z-10"><span className="bg-white border rounded-full px-3 py-1 text-xs font-semibold shadow flex items-center gap-1.5"><ImageIcon size={12} /> 拖放圖片至此</span></div>}
            {attachments.length > 0 && (
              <div className="flex gap-2 overflow-x-auto pb-2 mb-2 border-b">
                {attachments.map(a => (
                  <div key={a.id} className="relative shrink-0">
                    <img src={a.dataUrl} alt={a.name} className="h-16 w-16 object-cover rounded-lg border" />
                    <button onClick={() => setAttachments(v => v.filter(x => x.id !== a.id))} className="absolute -top-1 -right-1 w-5 h-5 rounded-full bg-foreground text-background flex items-center justify-center"><X size={10} /></button>
                    <span className="absolute -bottom-1 -right-1 text-[9px] bg-white border rounded-full px-1">{Math.round(a.sizeBytes / 1024)}KB</span>
                  </div>
                ))}
                <button onClick={() => setAttachments([])} className="h-16 px-2 rounded-lg border border-dashed bg-muted/30 text-xs text-muted-foreground">清空</button>
              </div>
            )}
            <div className="flex gap-2 items-end">
              <div className="flex-1 relative">
                {slashOpen && (
                  <div className="absolute bottom-[calc(100%+6px)] left-0 right-0 bg-white border rounded-xl shadow-lg z-20 overflow-hidden max-h-48">
                    <div className="px-2 py-1 text-[10px] font-bold text-muted-foreground bg-muted/30 border-b">/ 命令 · {filterCommands(slashQuery).length} 匹配</div>
                    <div className="overflow-auto max-h-40">
                      {filterCommands(slashQuery).map((c, idx) => (
                        <button key={c.name} onClick={() => commitSlash(c.name)} className={`w-full text-left px-2 py-1.5 flex items-center gap-2 border-b border-muted/20 ${idx === slashIndex ? 'bg-primary/10' : 'bg-white hover:bg-muted/30'}`}>
                          <span className="font-mono text-xs font-bold">{c.label}</span>
                          <span className="text-xs text-muted-foreground truncate">{c.description}</span>
                          {c.argsHint && <span className="ml-auto text-[10px] bg-muted rounded px-1">{c.argsHint}</span>}
                        </button>
                      ))}
                      {filterCommands(slashQuery).length === 0 && <div className="p-2 text-xs text-muted-foreground">無匹配，輸入 /help 查看全部</div>}
                    </div>
                  </div>
                )}
                {mentionOpen && (
                  <div className="absolute bottom-[calc(100%+6px)] left-0 right-0 bg-white border rounded-xl shadow-lg z-20 overflow-hidden max-h-48">
                    <div className="px-2 py-1 text-[10px] font-bold text-muted-foreground bg-muted/30 border-b">@ 提及 · {filterMentions(mentionQuery).length} 匹配</div>
                    <div className="overflow-auto max-h-40">
                      {filterMentions(mentionQuery).map((m, idx) => (
                        <button key={m.id} onClick={() => commitMention(m.id)} className={`w-full text-left px-2 py-1.5 flex items-center gap-2 border-b border-muted/20 ${idx === mentionIndex ? 'bg-primary/10' : 'bg-white hover:bg-muted/30'}`}>
                          <span className="font-mono text-xs font-bold">{m.label}</span>
                          <span className="text-xs text-muted-foreground">{m.description}</span>
                        </button>
                      ))}
                    </div>
                  </div>
                )}
                <textarea
                  ref={inputRef}
                  value={input}
                  onChange={handleInputChange}
                  onKeyDown={handleKeyDown}
                  placeholder={modeMeta[mode].placeholder + ' · 輸入 / 查看命令，@ 引用上下文 (↑ 歷史)'}
                  rows={2}
                  className="w-full px-3 py-2 pr-10 rounded-xl border bg-white resize-none outline-none focus:border-primary focus:ring-2 focus:ring-primary/20 text-xs"
                  disabled={isRunning}
                />
                <span className="absolute right-2 bottom-2 text-[10px] text-muted-foreground bg-muted rounded-full px-1.5 py-0.5">{modeMeta[mode].label}</span>
              </div>
              <div className="flex flex-col gap-1">
                <button onClick={() => fileInputRef.current?.click()} disabled={isRunning || attachments.length >= 5} title="添加圖片 (最多5張)" className="w-8 h-8 rounded-lg border bg-white flex items-center justify-center hover:bg-muted disabled:opacity-50">
                  <Paperclip size={14} />
                </button>
                <input ref={fileInputRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif" multiple hidden onChange={async e => { const files = Array.from(e.target.files ?? []); if (files.length) await addFiles(files); if (fileInputRef.current) fileInputRef.current.value = ''; }} />
                {isRunning ? <button onClick={handleStop} className="px-3 py-2 rounded-xl bg-red-500 text-white font-bold text-xs">停止</button> : <button onClick={handleSend} disabled={(!input.trim() && attachments.length === 0)} className="w-8 h-8 rounded-xl bg-primary text-primary-foreground flex items-center justify-center disabled:bg-muted disabled:text-muted-foreground"><SendHorizontal size={14} /></button>}
              </div>
            </div>
            <div className="flex items-center gap-1.5 mt-1.5 text-[10px] text-muted-foreground">
              <span className="flex items-center gap-1"><Eye size={10} /> 多模態：截圖後 Ctrl+V 貼上</span>
              <span className="flex-1" />
              <span className="flex items-center gap-1"><FileSearch size={10} /> {mode === 'plan' ? 'Plan 只讀規劃' : mode === 'ask' ? 'Ask 只讀查詢' : 'Build 可寫'} · {providers.length} 提供商</span>
            </div>
            {/* Quick suggestion chips in composer when idle */}
            {!isRunning && !input && attachments.length === 0 && (
              <div className="flex gap-1.5 mt-2 overflow-x-auto pb-1 scrollbar-none">
                {[
                  { label: '統計牆', prompt: '這個模型有多少面牆？按類型分組' },
                  { label: '隱藏連結', prompt: '隱藏所有連結模型' },
                  { label: '匯出 DWG', prompt: '匯出當前視圖為 DWG 到桌面' },
                  { label: '截圖提問', prompt: '截圖提問：幫我看看這張圖的問題' },
                ].map(c => (
                  <button key={c.label} onClick={() => setInput(c.prompt)} className="shrink-0 px-2.5 py-1 rounded-full bg-muted border text-xs hover:bg-white flex items-center gap-1">
                    <Zap size={10} />{c.label}
                  </button>
                ))}
              </div>
            )}
          </div>
        </>
      ) : (
        <div className="flex-1 overflow-y-auto bg-muted/20">
          <PluginShelf plugins={plugins} />
        </div>
      )}

      {selectorOpen && <div onClick={() => setSelectorOpen(false)} className="fixed inset-0 z-40" />}
      <style>{`@keyframes spin{from{transform:rotate(0deg)}to{transform:rotate(360deg)}} .scrollbar-none::-webkit-scrollbar{display:none} .scrollbar-none{-ms-overflow-style:none;scrollbar-width:none}`}</style>
    </div>
  );
}
