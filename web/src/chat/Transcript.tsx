import { useEffect, useRef } from 'react';
import type { PluginInfo, QuotaPayload } from '../bridge';
import type { HostCaps } from '../lib/permissions';
import { isNearBottom } from '../lib/scroll';
import type { Entry, PendingReview } from '../state/types';
import { EmptyState } from './EmptyState';
import { ErrorNote, NoteRow, Turn, TurnHandlers } from './Turn';

type Props = {
  entries: Entry[];
  pending: PendingReview | null;
  caps: HostCaps;
  plugins: PluginInfo[];
  suggestions: string[];
  quota: QuotaPayload | null;
  h: TurnHandlers;
  onStarter: (text: string) => void;
};

export function Transcript({ entries, pending, caps, plugins, suggestions, quota, h, onStarter }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  useEffect(() => {
    const el = ref.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [entries, pending]);

  let lastTurnIdx = -1;
  entries.forEach((e, i) => { if (e.kind === 'turn') lastTurnIdx = i; });
  const hasTurns = lastTurnIdx >= 0;

  return (
    <div ref={ref} className="transcript" onScroll={e => { stick.current = isNearBottom(e.currentTarget); }} aria-live="off">
      {entries.map((e, i) => {
        if (e.kind === 'note') {
          return e.note.kind === 'error'
            ? <ErrorNote key={e.note.id} note={e.note} onOpenSettings={h.onOpenSettings} />
            : <NoteRow key={e.note.id} note={e.note} />;
        }
        return <Turn key={e.turn.id} turn={e.turn} isLast={i === lastTurnIdx} pending={pending} caps={caps} h={h} />;
      })}
      {!hasTurns && <EmptyState plugins={plugins} suggestions={suggestions} quota={quota} onPick={onStarter} />}
    </div>
  );
}
