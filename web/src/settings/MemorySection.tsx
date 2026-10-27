import { useEffect, useState } from 'react';
import { Note, postToHost } from '../bridge';
import { copyText } from '../lib/clipboard';
import type { PaneState } from '../state/types';
import { Icon } from '../ui/Icon';

/** Matches NoteStore.MaxNoteChars on the host. */
const NOTE_MAX = 300;

type Scope = 'project' | 'global';

type Props = { state: PaneState };

/** One list of notes: the project's or the global one. */
function NoteList({ scope, title, subtitle, notes, empty, disabled }: { scope: Scope; title: string; subtitle?: string; notes: Note[]; empty: string; disabled?: boolean }) {
  const [text, setText] = useState('');
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null);
  const [clearing, setClearing] = useState(false);

  const add = () => {
    const body = text.trim();
    if (!body) return;
    postToHost({ type: 'saveNote', scope, text: body.slice(0, NOTE_MAX) });
    setText('');
  };

  const save = (id: string) => {
    const body = (editing?.text ?? '').trim();
    if (!body) return;
    postToHost({ type: 'saveNote', scope, id, text: body.slice(0, NOTE_MAX) });
    setEditing(null);
  };

  return (
    <div style={{ marginBottom: 12 }}>
      <div className="row" style={{ marginBottom: 4 }}>
        <h3 style={{ margin: 0 }}>{title}</h3>
        {subtitle && <span className="faint truncate">{subtitle}</span>}
        <span className="grow" />
        <button type="button" className="btn btn-sm btn-ghost tone-danger" onClick={() => setClearing(true)} disabled={!notes.length || disabled}>Clear all</button>
      </div>
      {clearing && (
        <div className="bypass-bar" role="status" style={{ margin: '4px 0', flexWrap: 'wrap' }}>
          <Icon name="alert" size={12} />
          <span>Delete every {scope === 'project' ? 'project' : 'global'} note?</span>
          <button type="button" onClick={() => setClearing(false)}>Cancel</button>
          <button type="button" onClick={() => { postToHost({ type: 'clearNotes', scope }); setClearing(false); }}>Delete all</button>
        </div>
      )}
      <div className="list">
        {notes.length === 0 ? (
          <div className="list-row"><span className="faint">{disabled ? empty : 'No notes yet.'}</span></div>
        ) : notes.map(n => (
          <div key={n.id} className="list-row" style={{ flexWrap: 'wrap' }}>
            {editing?.id === n.id ? (
              <>
                <input
                  className="input grow"
                  value={editing.text}
                  maxLength={NOTE_MAX}
                  autoFocus
                  aria-label={`Edit the note: ${n.text}`}
                  onChange={e => setEditing({ ...editing, text: e.target.value })}
                  onKeyDown={e => { if (e.key === 'Enter') save(n.id); if (e.key === 'Escape') setEditing(null); }}
                />
                <button type="button" className="btn btn-sm" onClick={() => save(n.id)}>Save</button>
                <button type="button" className="btn btn-sm btn-ghost" onClick={() => setEditing(null)}>Cancel</button>
              </>
            ) : (
              <>
                <span className="grow" style={{ minWidth: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{n.text}</span>
                {n.source === 'agent' && <span className="badge badge-change" title="The agent wrote this note">agent</span>}
                <button type="button" className="btn btn-sm" aria-label={`Edit the note: ${n.text}`} onClick={() => setEditing({ id: n.id, text: n.text })}>Edit</button>
                <button type="button" className="btn btn-sm btn-ghost tone-danger" aria-label={`Delete the note: ${n.text}`} onClick={() => postToHost({ type: 'deleteNote', scope, id: n.id })}>Delete</button>
              </>
            )}
          </div>
        ))}
      </div>
      <div className="row" style={{ marginTop: 6, flexWrap: 'wrap' }}>
        <input
          className="input grow"
          style={{ minWidth: 120 }}
          value={text}
          maxLength={NOTE_MAX}
          placeholder="Add a note"
          aria-label={`Add a ${scope} note`}
          disabled={disabled}
          onChange={e => setText(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') add(); }}
        />
        <button type="button" className="btn btn-sm" onClick={add} disabled={!text.trim() || disabled}><Icon name="plus" size={12} />Add</button>
        <span className="faint" style={{ fontSize: 11 }} role="status">{text.length}/{NOTE_MAX}</span>
      </div>
    </div>
  );
}

/** The saved procedures and the folder the person's own files go in. */
function PlaybooksBlock({ playbooks }: { playbooks: PaneState['playbooks'] }) {
  const [copied, setCopied] = useState(false);
  useEffect(() => { postToHost({ type: 'listPlaybooks' }); }, []);

  return (
    <div>
      <h3>Playbooks</h3>
      <div className="faint" style={{ fontSize: 11.5 }}>Type /name in the chat to run one. Your own procedures are the .md files in this folder.</div>
      <div className="row" style={{ margin: '4px 0' }}>
        <span className="mono truncate grow" style={{ fontSize: 11 }} title={playbooks.folder}>{playbooks.folder || 'No folder yet'}</span>
        <button type="button" className="btn btn-sm" disabled={!playbooks.folder} onClick={async () => { setCopied(await copyText(playbooks.folder)); window.setTimeout(() => setCopied(false), 1200); }}>
          <Icon name="copy" size={12} />{copied ? 'Copied' : 'Copy path'}
        </button>
      </div>
      {playbooks.issues.map((issue, i) => <div key={i} className="issue">{issue}</div>)}
      <div className="list">
        {playbooks.items.length === 0 ? (
          <div className="list-row"><span className="faint">No playbooks yet.</span></div>
        ) : playbooks.items.map(pb => (
          <div key={pb.name} className="list-row" style={{ flexWrap: 'wrap' }}>
            <div className="grow" style={{ minWidth: 0 }}>
              <div className="mono truncate">/{pb.name}</div>
              <div className="muted" style={{ fontSize: 11 }}>{pb.description}</div>
            </div>
            {pb.mode && <span className="badge">{pb.mode}</span>}
            <span className={`badge ${pb.source === 'user' ? 'badge-ok' : ''}`}>{pb.source}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/** The saved notes and the saved procedures: what the agent remembers between chats. */
export function MemorySection({ state }: Props) {
  const notes = state.notes;
  useEffect(() => { postToHost({ type: 'listNotes' }); }, []);

  return (
    <div>
      <h3>Notes</h3>
      <div className="faint" style={{ fontSize: 11.5 }}>The agent reads these at the start of every chat.</div>
      {notes?.issues?.map((issue, i) => <div key={i} className="issue">{issue}</div>)}
      <div style={{ marginTop: 8 }}>
        <NoteList
          scope="project"
          title="This project"
          subtitle={notes?.projectTitle}
          notes={notes?.project ?? []}
          empty="Open a saved model to keep project notes."
          disabled={notes ? !notes.hasProject : false}
        />
        <NoteList
          scope="global"
          title="All projects"
          notes={notes?.global ?? []}
          empty="No notes yet."
        />
      </div>
      <PlaybooksBlock playbooks={state.playbooks} />
    </div>
  );
}
