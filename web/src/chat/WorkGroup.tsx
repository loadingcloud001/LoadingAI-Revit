import { useEffect, useState } from 'react';
import type { TodoPayload } from '../bridge';
import { copyText } from '../lib/clipboard';
import { formatMs, stepStateLabel, stepSummary } from '../lib/copy';
import { formatApprovalArgs } from '../lib/presetPreview';
import { turnStats } from '../state/selectors';
import type { Step, Turn } from '../state/types';
import { Icon, IconName } from '../ui/Icon';

function stateIcon(step: Step): { name: IconName; tone: string; spin?: boolean } {
  switch (step.state) {
    case 'running': return { name: 'loader', tone: 'tone-accent', spin: true };
    case 'review': return { name: 'dot', tone: 'tone-change' };
    case 'done': return { name: 'check', tone: 'tone-ok' };
    case 'denied':
    case 'failed': return { name: 'x', tone: 'tone-danger' };
    case 'timeout': return { name: 'history', tone: 'tone-change' };
    case 'cancelled': return { name: 'x', tone: 'faint' };
  }
}

function decisionText(step: Step): string | null {
  const d = step.review?.decided;
  if (!d) return null;
  return d === 'allowed' ? 'allowed' : d === 'denied' ? 'denied' : d === 'timeout' ? 'timed out' : 'cancelled';
}

function StepRow({ step, onRetry, onZoom }: { step: Step; onRetry?: (step: Step) => void; onZoom?: (src: string) => void }) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const icon = stateIcon(step);
  const ms = step.ms != null ? formatMs(step.ms) : step.endedAt != null ? formatMs(step.endedAt - step.startedAt) : '';
  const summary = step.state === 'done' ? stepSummary(step.resultText) : step.outcome === 'blocked' ? 'blocked' : stepStateLabel(step.state);
  const decided = decisionText(step);
  const args = step.argsText ? formatApprovalArgs(step.argsText) : [];
  const onCopy = async () => {
    setCopied(await copyText(`${step.name}\n${step.argsText}\n\n${step.resultText ?? ''}`));
    window.setTimeout(() => setCopied(false), 1200);
  };
  return (
    <div className="step">
      <span className={icon.tone} style={{ display: 'inline-flex', alignSelf: 'center' }}>
        <Icon name={icon.name} size={12} className={icon.spin ? 'spin' : undefined} />
      </span>
      <button type="button" className="row" style={{ border: 0, padding: 0, background: 'transparent', color: 'inherit', font: 'inherit', textAlign: 'left', cursor: 'pointer', minWidth: 0 }} aria-expanded={open} onClick={() => setOpen(v => !v)}>
        <span className="step-name">{step.name}</span>
        {step.tier === 'L3' && <span className="badge badge-change">L3</span>}
        {step.destructive && <span className="badge badge-danger" title="Deletes or replaces elements, or writes a file">destructive</span>}
        {step.partial && <span className="badge badge-change" title="Some of the work was done and some failed">partial</span>}
        {decided && <span className={`badge ${decided === 'allowed' ? 'badge-ok' : 'badge-danger'}`}>{decided}</span>}
        <span className="step-sum truncate">{summary}</span>
      </button>
      <span className="step-meta">{ms}</span>
      {step.image && (
        <button type="button" className="snap-thumb" aria-label="Open snapshot" onClick={() => onZoom?.(step.image!)}>
          <img src={step.image} alt={`Snapshot: ${step.resultText?.split('\n')[0] ?? step.name}`} />
        </button>
      )}
      {open && (
        <div className="step-detail">
          <h4>Input</h4>
          {args.length ? args.join('\n') : '(none)'}
          {step.resultText ? <><h4 style={{ marginTop: 6 }}>Output</h4>{step.resultText}</> : null}
          <div className="row" style={{ marginTop: 6 }}>
            <button type="button" className="btn btn-sm" onClick={onCopy}><Icon name="copy" size={12} />{copied ? 'Copied' : 'Copy'}</button>
            {onRetry && step.state === 'failed' && <button type="button" className="btn btn-sm" onClick={() => onRetry(step)}><Icon name="refresh" size={12} />Retry</button>}
          </div>
        </div>
      )}
    </div>
  );
}

function TodoRow({ todo }: { todo: TodoPayload }) {
  const icon: IconName = todo.status === 'done' ? 'check' : todo.status === 'running' ? 'loader' : todo.status === 'error' ? 'x' : 'circle';
  const tone = todo.status === 'done' ? 'tone-ok' : todo.status === 'error' ? 'tone-danger' : todo.status === 'running' ? 'tone-accent' : 'faint';
  return (
    <div className="step">
      <span className={tone} style={{ display: 'inline-flex', alignSelf: 'center' }}><Icon name={icon} size={12} className={todo.status === 'running' ? 'spin' : undefined} /></span>
      <span className="truncate" style={{ color: todo.status === 'done' ? 'var(--muted)' : undefined }}>{todo.title}</span>
      <span className="step-meta mono">{todo.tool ?? ''}</span>
    </div>
  );
}

/** Live list of what the agent did in a turn; folds to one line when the turn ends. */
export function WorkGroup({ turn, forceOpen, onRetryStep, children }: { turn: Turn; forceOpen: boolean; onRetryStep?: (step: Step) => void; children?: React.ReactNode }) {
  const running = turn.outcome === 'running';
  const [open, setOpen] = useState(running);
  const [zoom, setZoom] = useState<string | null>(null);
  useEffect(() => { if (running || forceOpen) setOpen(true); }, [running, forceOpen]);
  useEffect(() => { if (!running) setOpen(false); }, [running]);
  useEffect(() => {
    if (!zoom) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setZoom(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [zoom]);
  const stats = turnStats(turn);
  const hasWork = turn.steps.length > 0 || !!turn.reasoning || turn.todos.length > 0;
  if (!hasWork && !children) return null;
  const n = turn.steps.length;
  const label = running
    ? (n ? `Working · ${n} step${n === 1 ? '' : 's'}` : 'Working')
    : `Worked · ${n} step${n === 1 ? '' : 's'}${stats.ms ? ` · ${formatMs(stats.ms)}` : ''}`;
  const failed = turn.steps.filter(s => s.state === 'denied' || s.state === 'failed' || s.state === 'timeout').length;
  return (
    <div>
      {hasWork && (
        <button type="button" className="work-head" aria-expanded={open} onClick={() => setOpen(v => !v)}>
          {running ? <Icon name="loader" size={12} className="spin" /> : <Icon name={open ? 'chevronDown' : 'chevronRight'} size={12} />}
          <span>{label}</span>
          {failed > 0 && !running && <span className="badge badge-danger">{failed} not run</span>}
        </button>
      )}
      {open && hasWork && (
        <>
          {turn.reasoning && (
            <div className="thought">{turn.reasoning.streaming ? 'Thinking… ' : 'Thought: '}{turn.reasoning.text.slice(-400)}</div>
          )}
          {turn.todos.length > 0 && !turn.plan && (
            <div className="steps" style={{ marginBottom: 2 }}>{turn.todos.map(t => <TodoRow key={t.id} todo={t} />)}</div>
          )}
          <div className="steps">{turn.steps.map(s => <StepRow key={s.id} step={s} onRetry={onRetryStep} onZoom={setZoom} />)}</div>
        </>
      )}
      {children}
      {zoom && (
        <div className="snap-overlay" role="dialog" aria-label="Snapshot" onClick={() => setZoom(null)}>
          <img src={zoom} alt="Snapshot" />
        </div>
      )}
    </div>
  );
}
