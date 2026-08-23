export type ProviderConfig = {
  id: string;
  name: string;
  baseUrl: string;
  model: string;
  apiKey: string;
  apiKind?: string;
  group?: string;
  icon?: string;
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
};

export type PluginToolInfo = {
  name: string;
  tier: string;
  description: string;
  enabledByDefault: boolean;
  enabled: boolean;
};

export type PluginInfo = {
  id: string;
  name: string;
  version: string;
  license: string;
  author: string;
  verified: boolean;
  folderPath: string;
  tools: PluginToolInfo[];
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

export type ToHost =
  | { type: 'send'; text: string; attachments?: Attachment[]; mode?: ChatMode }
  | { type: 'clear' }
  | { type: 'cancel' }
  | { type: 'saveSettings'; settings: AppSettings }
  | { type: 'requestSettings' }
  | { type: 'getCatalog' }
  | { type: 'testProvider'; provider?: ProviderConfig; providerId?: string }
  | { type: 'listModels'; provider?: ProviderConfig; providerId?: string }
  | { type: 'listPlugins' }
  | { type: 'approveTool'; toolCallId: string; approved: boolean; reason?: string }
  | { type: 'setPluginEnabled'; toolName: string; enabled: boolean }
  | { type: 'setPluginGroupEnabled'; pluginId: string; enabled: boolean }
  | { type: 'removePlugin'; pluginId: string }
  | { type: 'reloadPlugins' };

export type PlanPayload = {
  title: string;
  description?: string;
  steps: { title: string; description?: string; tool?: string; status?: 'pending' | 'running' | 'done' | 'error' }[];
  isStreaming?: boolean;
};

export type ApprovalPayload = {
  toolCallId: string;
  toolName: string;
  tier: string;
  args: string;
  description?: string;
};

export type FromHost =
  | { type: 'status'; text: string }
  | { type: 'trace'; text: string }
  | { type: 'tool_call'; text: string }
  | { type: 'tool_result'; text: string }
  | { type: 'tool_approval'; approval: ApprovalPayload }
  | { type: 'plan'; plan: PlanPayload }
  | { type: 'reasoning'; text: string; isStreaming?: boolean }
  | { type: 'suggestions'; suggestions: string[] }
  | { type: 'assistant'; text: string; attachments?: Attachment[] }
  | { type: 'settings'; settings: AppSettings }
  | { type: 'catalog'; presets: ProviderPreset[] }
  | { type: 'testResult'; ok: boolean; message: string; providerId?: string; models?: string[] }
  | { type: 'models'; providerId: string; models: string[]; error?: string }
  | { type: 'plugins'; plugins: PluginInfo[] }
  | { type: 'history'; messages: { role: string; text: string; attachments?: Attachment[] }[] }
  | { type: 'error'; text: string }
  | { type: 'delta'; text: string };

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
        activeProviderId: 'deepseek',
        providers: [
          { id: 'deepseek', name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', apiKey: '', apiKind: 'openai', group: 'cloud', icon: '◆' },
          { id: 'openai', name: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '', apiKind: 'openai', group: 'cloud', icon: '◉' },
          { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1', apiKey: 'ollama', apiKind: 'openai', group: 'local', icon: '⬢' },
          { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'anthropic/claude-sonnet-4', apiKey: '', apiKind: 'openai', group: 'gateway', icon: '◎' },
          { id: 'opencode', name: 'OpenCode Zen', baseUrl: 'https://opencode.ai/zen/go/v1', model: 'muse-spark-1.2-contributor', apiKey: '', apiKind: 'responses', group: 'gateway', icon: '⚡' },
        ]
      };
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'settings', settings: mock } })), 100);
      const mockPresets: any[] = [
        { id: 'deepseek', name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', apiKind: 'openai', group: 'cloud', icon: '◆', description: 'DeepSeek V3 / R1' },
        { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'anthropic/claude-sonnet-4', apiKind: 'openai', group: 'gateway', icon: '◎', description: '300+ models gateway' },
        { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1', apiKind: 'openai', group: 'local', icon: '⬢', description: 'Local Ollama' },
      ];
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'catalog', presets: mockPresets } })), 120);
    }
    if ((msg as any).type === 'testProvider') {
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'testResult', ok: true, message: 'Mock OK — 12 models', providerId: (msg as any).providerId ?? (msg as any).provider?.id } })), 600);
    }
    if ((msg as any).type === 'listModels') {
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'models', providerId: (msg as any).providerId ?? (msg as any).provider?.id ?? 'deepseek', models: ['deepseek-chat', 'deepseek-reasoner', 'gpt-4o-mini'] } })), 400);
    }
  }
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
