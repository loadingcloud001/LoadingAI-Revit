import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import { copyText } from '../lib/clipboard';
import { errorCopy, turnTitle } from '../lib/copy';
import type { ApprovalScope } from '../bridge';
import type { HostCaps } from '../lib/permissions';
import type { Note, PendingReview, Step, Turn as TurnT } from '../state/types';
import { Icon } from '../ui/Icon';
import { ChangeReceipt } from './ChangeReceipt';
import { ReviewCard } from './ReviewCard';
import { WorkGroup } from './WorkGroup';

export type TurnHandlers = {
  onApprove: (toolCallId: string, approved: boolean, opts?: { scope?: ApprovalScope; reason?: string }) => void;
  onEdit: (toolCallId: string, args: Record<string, unknown>) => void;
  onSelect: (ids: number[]) => void;
  onShow: (ids: number[]) => void;
  onIsolate: (ids: number[]) => void;
  onRetryStep: (step: Step) => void;
  onSubmitPreset: (approved: boolean, override?: Record<string, unknown>) => void;
  onValue: (fieldId: string, value: unknown) => void;
  onPick: (fieldId: string) => void;
  onRunPlan: () => void;
  onEditText: (text: string) => void;
  onOpenSettings: () => void;
  onRetry: (text: string) => void;
};

export function ErrorNote({ note, onOpenSettings, onRetry }: { note: Note; onOpenSettings?: () => void; onRetry?: () => void }) {
  const c = errorCopy(note.text);
  return (
    <div className="card card-danger" role="alert">
      <div className="card-head tone-danger"><Icon name="alert" size={13} /><span style={{ fontWeight: 600 }}>{c.title}</span></div>
      <div className="card-body">
        <div className="muted">{c.detail}</div>
        {c.detail !== note.text && <div className="faint mono" style={{ marginTop: 3, fontSize: 10.5, wordBreak: 'break-word' }}>{note.text}</div>}
      </div>
      {(c.action === 'settings' && onOpenSettings) || (c.action === 'retry' && onRetry) ? (
        <div className="card-actions">
          {c.action === 'settings' && onOpenSettings && <button type="button" className="btn btn-sm" onClick={onOpenSettings}>Open settings</button>}
          {c.action === 'retry' && onRetry && <button type="button" className="btn btn-sm" onClick={onRetry}><Icon name="refresh" size={12} />Retry</button>}
        </div>
      ) : null}
    </div>
  );
}

export function NoteRow({ note }: { note: Note }) {
  if (note.kind === 'steer') return <div className="note note-steer">You added: {note.text}</div>;
  return <div className={`note ${note.kind === 'trace' ? 'note-trace' : ''}`}>{note.text}</div>;
}

function PlanCard({ turn, isLast, onRunPlan, onEditText }: { turn: TurnT; isLast: boolean; onRunPlan: () => void; onEditText: (t: string) => void }) {
  const [discarded, setDiscarded] = useState(false);
  const plan = turn.plan!;
  if (discarded) return null;
  const text = `${plan.title}\n${(plan.steps ?? []).map((s, i) => `${i + 1}. ${s.title}`).join('\n')}`;
  return (
    <div className="card card-accent">
      <div className="card-head"><Icon name="list" size={13} /><span style={{ fontWeight: 600 }}>{plan.title || 'Plan'}</span></div>
      <div className="card-body">
        <ol style={{ margin: 0, paddingLeft: 18 }}>
          {(plan.steps ?? []).map((s, i) => (
            <li key={i} style={{ padding: '1px 0' }}>{s.title}{s.tool ? <span className="mono faint"> · {s.tool}</span> : null}</li>
          ))}
        </ol>
      </div>
      {isLast && turn.outcome !== 'running' && (
        <div className="card-actions">
          <button type="button" className="btn btn-primary btn-sm" onClick={onRunPlan}><Icon name="send" size={12} />Run plan</button>
          <button type="button" className="btn btn-sm" onClick={() => onEditText(text)}><Icon name="pencil" size={12} />Edit</button>
          <button type="button" className="btn btn-ghost btn-sm" onClick={() => setDiscarded(true)}>Discard</button>
        </div>
      )}
    </div>
  );
}

export function Turn({ turn, isLast, pending, caps, h }: { turn: TurnT; isLast: boolean; pending: PendingReview | null; caps: HostCaps; h: TurnHandlers }) {
  const running = turn.outcome === 'running';
  const [expanded, setExpanded] = useState(false);
  const [copied, setCopied] = useState(false);
  const reviewStep = pending ? turn.steps.find(s => s.id === pending.stepId && s.state === 'review') : undefined;

  if (!isLast && !running && !expanded) {
    const n = turn.steps.length;
    return (
      <div className="turn">
        <button type="button" className="turn-folded" aria-expanded={false} onClick={() => setExpanded(true)}>
          <Icon name="chevronRight" size={12} />
          <span className="grow truncate" style={{ color: 'var(--text)' }}>{turnTitle(turn.userText || turn.answer)}</span>
          {turn.outcome === 'error' && <span className="badge badge-danger">error</span>}
          {turn.outcome === 'cancelled' && <span className="badge">stopped</span>}
          {n > 0 && <span className="faint" style={{ fontSize: 11 }}>{n} step{n === 1 ? '' : 's'}</span>}
        </button>
      </div>
    );
  }

  const errors = turn.notes.filter(n => n.kind === 'error');
  const others = turn.notes.filter(n => n.kind !== 'error');
  const onCopy = async () => {
    setCopied(await copyText(turn.answer));
    window.setTimeout(() => setCopied(false), 1200);
  };

  return (
    <div className="turn">
      {!isLast && (
        <button type="button" className="turn-folded" aria-expanded onClick={() => setExpanded(false)} style={{ paddingBottom: 0 }}>
          <Icon name="chevronDown" size={12} /><span className="faint" style={{ fontSize: 11 }}>Fold</span>
        </button>
      )}
      {(turn.userText || turn.attachments?.length) ? (
        <div className="user-msg">
          {turn.userText}
          {turn.attachments?.length ? <div>{turn.attachments.map(a => <img key={a.id} src={a.dataUrl} alt={a.name} />)}</div> : null}
        </div>
      ) : null}
      <WorkGroup turn={turn} forceOpen={!!reviewStep} onRetryStep={isLast && !running ? h.onRetryStep : undefined}>
        {reviewStep && pending && (
          <ReviewCard step={reviewStep} pending={pending} caps={caps} onApprove={h.onApprove} onEdit={h.onEdit} onSubmitPreset={h.onSubmitPreset} onValue={h.onValue} onPick={h.onPick} />
        )}
      </WorkGroup>
      {turn.plan && <PlanCard turn={turn} isLast={isLast} onRunPlan={h.onRunPlan} onEditText={h.onEditText} />}
      {others.map(n => <NoteRow key={n.id} note={n} />)}
      {turn.answer && (
        <div className="answer"><ReactMarkdown>{turn.answer}</ReactMarkdown></div>
      )}
      {(turn.rolledBack || turn.changes) && (
        <ChangeReceipt
          changes={turn.changes ?? { added: 0, modified: 0, deleted: 0, byCategory: [] }}
          rolledBack={turn.rolledBack}
          actions={{ onSelect: h.onSelect, onShow: h.onShow, onIsolate: h.onIsolate }}
          busy={isLast ? running : false}
        />
      )}
      {errors.map(n => (
        <ErrorNote key={n.id} note={n} onOpenSettings={h.onOpenSettings} onRetry={isLast && turn.userText ? () => h.onRetry(turn.userText) : undefined} />
      ))}
      {!running && (turn.answer || turn.userText) && (
        <div className="turn-actions">
          {turn.answer && <button type="button" className="btn btn-ghost btn-sm" onClick={onCopy}><Icon name="copy" size={12} />{copied ? 'Copied' : 'Copy'}</button>}
          {isLast && turn.userText && <button type="button" className="btn btn-ghost btn-sm" onClick={() => h.onEditText(turn.userText)}><Icon name="pencil" size={12} />Edit and resend</button>}
        </div>
      )}
    </div>
  );
}
