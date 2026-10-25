import { useCallback, useEffect, useRef, useState } from 'react';
import type { Attachment, ProviderConfig } from '../bridge';
import { filterMentions } from '../cli/mentions';
import { filterCommands } from '../cli/slashCommands';
import type { HostCaps } from '../lib/permissions';
import type { QueueState } from '../lib/queue';
import type { PermissionLevel } from '../state/types';
import { Icon } from '../ui/Icon';
import { ModelPicker, PermissionPicker } from './Pickers';

async function compressImage(file: File, maxSide = 1280, maxBytes = 2.5 * 1024 * 1024): Promise<Attachment> {
  const bitmap = await createImageBitmap(file);
  let { width, height } = bitmap;
  if (Math.max(width, height) > maxSide) {
    const r = maxSide / Math.max(width, height);
    width = Math.round(width * r); height = Math.round(height * r);
  }
  const canvas = document.createElement('canvas');
  canvas.width = width; canvas.height = height;
  canvas.getContext('2d')!.drawImage(bitmap, 0, 0, width, height);
  const mime = file.type === 'image/png' && width * height < 360000 ? 'image/png' : 'image/jpeg';
  let quality = 0.82;
  let dataUrl = canvas.toDataURL(mime, quality);
  while (dataUrl.length * 0.75 > maxBytes && quality > 0.38) {
    quality -= 0.1;
    dataUrl = canvas.toDataURL('image/jpeg', quality);
  }
  if (dataUrl.length * 0.75 > maxBytes) throw new Error(`still over ${Math.round(maxBytes / 1024)} KB after compressing`);
  try { bitmap.close(); } catch { /* older WebView2 */ }
  return { id: crypto.randomUUID(), name: file.name || `pasted-${Date.now()}.jpg`, mediaType: mime, dataUrl, width, height, sizeBytes: Math.round(dataUrl.length * 0.75) };
}

type Props = {
  running: boolean;
  level: PermissionLevel;
  caps: HostCaps;
  providers: ProviderConfig[];
  activeProvider: ProviderConfig | null;
  queue: QueueState;
  draft: { text: string; nonce: number } | null;
  onSubmit: (text: string, attachments: Attachment[]) => boolean;
  onStop: () => void;
  onLevel: (l: PermissionLevel) => void;
  onCycleLevel: () => void;
  onModel: (id: string) => void;
  onManageModels: () => void;
  /** The host can deliver a message to the running turn. */
  canSteer: boolean;
  onQueueSteer: (id: string) => void;
  onQueueRemove: (id: string) => void;
  onQueueResume: () => void;
  onQueueClear: () => void;
  onNote: (text: string) => void;
};

export function Composer(p: Props) {
  const [text, setText] = useState('');
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [dragging, setDragging] = useState(false);
  const [menu, setMenu] = useState<{ kind: 'slash' | 'mention'; query: string; index: number; start: number } | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const history = useRef<string[]>([]);
  const historyIdx = useRef(-1);

  useEffect(() => {
    if (!p.draft) return;
    setText(p.draft.text);
    window.setTimeout(() => inputRef.current?.focus(), 0);
  }, [p.draft]);

  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 160)}px`;
  }, [text]);

  const addFiles = useCallback(async (files: File[]) => {
    const room = 5 - attachments.length;
    const imgs = files.filter(f => f.type.startsWith('image/')).slice(0, Math.max(0, room));
    if (!imgs.length) { p.onNote(room <= 0 ? 'Up to 5 images per message.' : 'Only images can be attached (png, jpg, webp, gif).'); return; }
    for (const f of imgs) {
      if (f.size > 12 * 1024 * 1024) { p.onNote(`${f.name} is over 12 MB.`); continue; }
      try {
        const att = await compressImage(f);
        setAttachments(a => [...a, att]);
      } catch (e: any) { p.onNote(`Couldn't attach ${f.name}: ${e?.message ?? String(e)}`); }
    }
  }, [attachments.length, p]);

  const items = menu ? (menu.kind === 'slash' ? filterCommands(menu.query).map(c => ({ id: c.name, label: c.label, hint: c.description })) : filterMentions(menu.query).map(m => ({ id: m.id, label: m.label, hint: m.description }))) : [];

  const commit = (id: string) => {
    if (!menu) return;
    const cursor = inputRef.current?.selectionStart ?? text.length;
    const insert = menu.kind === 'slash' ? `/${id} ` : `@${id} `;
    const next = text.slice(0, menu.start) + insert + text.slice(cursor);
    setText(next);
    setMenu(null);
    window.setTimeout(() => inputRef.current?.focus(), 0);
  };

  const onChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const v = e.target.value;
    setText(v);
    const cursor = e.target.selectionStart ?? v.length;
    const before = v.slice(0, cursor);
    const slash = before.match(/(^|\n|\s)\/([a-z]*)$/);
    if (slash) { setMenu({ kind: 'slash', query: slash[2] ?? '', index: 0, start: cursor - (slash[2]?.length ?? 0) - 1 }); return; }
    const mention = before.match(/@([a-zA-Z_]*)$/);
    if (mention) { setMenu({ kind: 'mention', query: mention[1] ?? '', index: 0, start: cursor - mention[0].length }); return; }
    setMenu(null);
  };

  const submit = () => {
    const raw = text;
    if (!raw.trim() && attachments.length === 0) return;
    if (p.onSubmit(raw, attachments)) {
      if (raw.trim()) { history.current = [raw.trim(), ...history.current.filter(x => x !== raw.trim())].slice(0, 50); }
      historyIdx.current = -1;
      setText('');
      if (!p.running) setAttachments([]);
      setMenu(null);
    }
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === '.' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); p.onCycleLevel(); return; }
    if (menu && items.length) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setMenu({ ...menu, index: (menu.index + 1) % items.length }); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setMenu({ ...menu, index: (menu.index - 1 + items.length) % items.length }); return; }
      if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); commit(items[menu.index].id); return; }
    }
    if (e.key === 'Escape') {
      if (menu) { e.preventDefault(); setMenu(null); return; }
      if (p.running) { e.preventDefault(); p.onStop(); return; }
    }
    if (e.key === 'ArrowUp' && !e.shiftKey && text.trim() === '' && history.current.length) {
      e.preventDefault();
      historyIdx.current = Math.min(historyIdx.current + 1, history.current.length - 1);
      setText(history.current[historyIdx.current]);
      return;
    }
    if (e.key === 'ArrowDown' && !e.shiftKey && historyIdx.current >= 0) {
      e.preventDefault();
      historyIdx.current -= 1;
      setText(historyIdx.current < 0 ? '' : history.current[historyIdx.current]);
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); }
  };

  return (
    <div
      className="composer"
      onDragOver={e => { e.preventDefault(); setDragging(true); }}
      onDragLeave={() => setDragging(false)}
      onDrop={async e => { e.preventDefault(); setDragging(false); const files = Array.from(e.dataTransfer.files); if (files.length) await addFiles(files); }}
      onPaste={async e => {
        const files = Array.from(e.clipboardData.items).filter(i => i.type.startsWith('image/')).map(i => i.getAsFile()).filter((f): f is File => !!f);
        if (files.length) { e.preventDefault(); await addFiles(files); }
      }}
    >
      {p.queue.items.length > 0 && (
        <div className="queue" aria-label="Queued messages">
          {p.queue.paused && <span className="chip tone-change" style={{ borderStyle: 'solid' }}>{p.queue.reason ?? 'Paused'}</span>}
          {p.queue.items.map(i => (
            <span key={i.id} className="chip" title={i.text}>
              <span className="faint">queued</span><span className="truncate" style={{ maxWidth: 150 }}>{i.text}</span>
              {p.canSteer && p.running && !p.queue.paused && (
                <button type="button" className="chip-act" title="Hand this to the agent now, without waiting for the turn to end" aria-label={`Send now: ${i.text}`} onClick={() => p.onQueueSteer(i.id)}>Send now</button>
              )}
              <button type="button" aria-label={`Remove queued message: ${i.text}`} style={{ border: 0, background: 'transparent', color: 'inherit', cursor: 'pointer', padding: 0, display: 'inline-flex' }} onClick={() => p.onQueueRemove(i.id)}><Icon name="x" size={11} /></button>
            </span>
          ))}
          {p.queue.paused && <button type="button" className="chip" onClick={p.onQueueResume}>Resume</button>}
          {p.queue.paused && <button type="button" className="chip" onClick={p.onQueueClear}>Clear</button>}
        </div>
      )}
      {attachments.length > 0 && (
        <div className="attachments">
          {attachments.map(a => (
            <figure key={a.id}>
              <img src={a.dataUrl} alt={a.name} />
              <button type="button" aria-label={`Remove ${a.name}`} onClick={() => setAttachments(v => v.filter(x => x.id !== a.id))}>×</button>
            </figure>
          ))}
        </div>
      )}
      <div style={{ position: 'relative' }}>
        {menu && items.length > 0 && (
          <div className="popover" role="listbox" style={{ bottom: 'calc(100% + 4px)', left: 0, right: 0, maxHeight: 200, overflowY: 'auto' }}>
            {items.map((it, idx) => (
              <button key={it.id} type="button" role="option" aria-selected={idx === menu.index} className="menu-item" onMouseDown={e => { e.preventDefault(); commit(it.id); }}>
                <span className="mono" style={{ minWidth: 92 }}>{it.label}</span><span className="muted truncate">{it.hint}</span>
              </button>
            ))}
          </div>
        )}
        <textarea
          ref={inputRef}
          value={text}
          onChange={onChange}
          onKeyDown={onKeyDown}
          rows={2}
          aria-label="Message"
          placeholder={p.running ? (p.canSteer ? 'Add a follow-up. Enter queues it; Send now steers the agent.' : 'Add a follow-up. Enter queues it.') : 'Ask, or describe a change · / and @'}
        />
        {dragging && <div className="drop-hint">Drop images to attach</div>}
      </div>
      <div className="composer-bar">
        <PermissionPicker level={p.level} caps={p.caps} onPick={p.onLevel} />
        <ModelPicker providers={p.providers} active={p.activeProvider} onPick={p.onModel} onManage={p.onManageModels} />
        <span className="grow" />
        <button type="button" className="icon-btn" aria-label="Attach image" title="Attach image" disabled={attachments.length >= 5} onClick={() => fileRef.current?.click()}><Icon name="image" size={15} /></button>
        <input ref={fileRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif" multiple hidden onChange={async e => { const files = Array.from(e.target.files ?? []); if (files.length) await addFiles(files); if (fileRef.current) fileRef.current.value = ''; }} />
        {p.running ? (
          <>
            {text.trim() && <button type="button" className="btn btn-sm" onClick={submit}>Queue</button>}
            <button type="button" className="btn btn-sm btn-stop" onClick={p.onStop} title="Stop (Esc)"><Icon name="stop" size={11} />Stop</button>
          </>
        ) : (
          <button type="button" className="btn btn-sm btn-primary" disabled={!text.trim() && attachments.length === 0} onClick={submit} aria-label="Send"><Icon name="arrowUp" size={13} />Send</button>
        )}
      </div>
    </div>
  );
}
