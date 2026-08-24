import { useEffect, useRef, useState } from 'react';

type Props = {
  logs: string[];
  open: boolean;
  onClose: () => void;
  onSend?: (text: string) => void;
};

export function Terminal({ logs, open, onClose, onSend }: Props) {
  const [input, setInput] = useState('');
  const listRef = useRef<HTMLPreElement>(null);

  useEffect(() => {
    if (listRef.current) listRef.current.scrollTop = listRef.current.scrollHeight;
  }, [logs, open]);

  if (!open) return null;

  const handleSend = () => {
    const t = input.trim();
    if (!t) return;
    setInput('');
    onSend?.(t);
  };

  return (
    <div className="border-t bg-slate-900 text-slate-100 flex flex-col" style={{ height: 148 }}>
      <div className="flex items-center gap-2 px-2 py-1 bg-slate-800 border-b border-slate-700 text-xs">
        <span className="font-semibold">Terminal</span>
        <span className="text-slate-400">· {logs.length} lines · fallback (run npm i xterm to enable xterm.js)</span>
        <span className="flex-1" />
        <button onClick={() => {}} className="px-2 py-0.5 rounded bg-slate-700 hover:bg-slate-600 text-xs">Clear</button>
        <button onClick={onClose} className="px-2 py-0.5 rounded bg-slate-700 hover:bg-slate-600 text-xs">Close</button>
      </div>
      <pre ref={listRef} className="flex-1 overflow-auto p-2 text-xs font-mono whitespace-pre-wrap break-words">{logs.length ? logs.slice(-300).join('\n') : 'Revit AI Terminal — type /help for commands, Esc to close'}</pre>
      <div className="flex gap-1 p-1 bg-slate-800 border-t border-slate-700">
        <span className="px-1 text-xs text-slate-400 select-none">$</span>
        <input
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') handleSend(); if (e.key === 'Escape') onClose(); }}
          placeholder="type /help, /clear, /resume ... (Enter to send)"
          className="flex-1 bg-slate-900 border border-slate-700 rounded px-2 py-1 text-xs outline-none focus:border-sky-500"
        />
        <button onClick={handleSend} className="px-2 py-1 rounded bg-sky-600 hover:bg-sky-500 text-xs font-semibold">Send</button>
      </div>
    </div>
  );
}
