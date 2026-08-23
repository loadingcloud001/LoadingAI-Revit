export type ProviderConfig = {
  id: string;
  name: string;
  baseUrl: string;
  model: string;
  apiKey: string;
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

export type ToHost =
  | { type: 'send'; text: string }
  | { type: 'clear' }
  | { type: 'cancel' }
  | { type: 'saveSettings'; settings: AppSettings }
  | { type: 'requestSettings' }
  | { type: 'listPlugins' }
  | { type: 'setPluginEnabled'; toolName: string; enabled: boolean }
  | { type: 'setPluginGroupEnabled'; pluginId: string; enabled: boolean }
  | { type: 'removePlugin'; pluginId: string }
  | { type: 'reloadPlugins' };

export type FromHost =
  | { type: 'status'; text: string }
  | { type: 'trace'; text: string }
  | { type: 'tool_call'; text: string }
  | { type: 'tool_result'; text: string }
  | { type: 'assistant'; text: string }
  | { type: 'settings'; settings: AppSettings }
  | { type: 'plugins'; plugins: PluginInfo[] }
  | { type: 'history'; messages: { role: string; text: string }[] }
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
          { id: 'deepseek', name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', apiKey: '' },
          { id: 'openai', name: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini', apiKey: '' },
          { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1', apiKey: 'ollama' }
        ]
      };
      setTimeout(() => window.dispatchEvent(new CustomEvent('host-message', { detail: { type: 'settings', settings: mock } })), 100);
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
