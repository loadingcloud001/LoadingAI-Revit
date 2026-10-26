import type { ChatMode } from '../bridge';
import type { PermissionLevel } from '../state/types';

/** Host features the pane may use. Empty for today's host; P3 announces more. */
export type HostCaps = ReadonlySet<string>;

export type LevelInfo = {
  id: PermissionLevel;
  label: string;
  hint: string;
  tone: 'normal' | 'change' | 'danger';
  /** Host feature needed before the level is offered, if any. */
  needs?: string;
};

export const LEVELS: LevelInfo[] = [
  { id: 'readonly', label: 'Read only', hint: 'Answers and reads. Never changes the model.', tone: 'normal' },
  { id: 'plan', label: 'Plan', hint: 'Proposes steps. No changes until you run the plan.', tone: 'normal' },
  { id: 'confirm', label: 'Ask before changes', hint: 'Default. Every change waits for your review.', tone: 'normal' },
  { id: 'autoEdit', label: 'Auto-edit', hint: 'L2 parameter and view changes run. L3 still asks.', tone: 'change', needs: 'autoEdit' },
  { id: 'bypass', label: 'Bypass', hint: 'Nothing asks. One undo step per turn.', tone: 'danger' },
];

export const DEFAULT_LEVEL: PermissionLevel = 'confirm';

export function availableLevels(caps: HostCaps): LevelInfo[] {
  return LEVELS.filter(l => !l.needs || caps.has(l.needs));
}

export function levelInfo(id: PermissionLevel): LevelInfo {
  return LEVELS.find(l => l.id === id) ?? LEVELS[2];
}

/** The chat mode a level sends. Read only and Plan never write. */
export function modeFor(level: PermissionLevel): ChatMode {
  return level === 'readonly' ? 'ask' : level === 'plan' ? 'plan' : 'build';
}

/** Whether the host's auto-approve switch must be on for this level. */
export function autoApproveFor(level: PermissionLevel): boolean {
  return level === 'bypass';
}

/** The level to show when settings load: Bypass when the host switch is on, else the stored pick. */
export function resolveLevel(stored: string | null, autoApprove: boolean, caps: HostCaps): PermissionLevel {
  if (autoApprove) return 'bypass';
  const pick = LEVELS.find(l => l.id === stored);
  if (!pick || pick.id === 'bypass') return DEFAULT_LEVEL;
  if (pick.needs && !caps.has(pick.needs)) return DEFAULT_LEVEL;
  return pick.id;
}

/** Levels that ask once in the pane before they take effect. */
export function needsWarning(level: PermissionLevel): boolean {
  return level === 'bypass' || level === 'autoEdit';
}

export function warningText(level: PermissionLevel): string {
  return level === 'bypass'
    ? 'Bypass lets the agent change the model without asking, and dismisses Revit warning dialogs while it works. Switched-off tools stay off. You can undo each turn once with Ctrl+Z.'
    : 'Auto-edit lets L2 parameter and view changes run without asking. Delete, move and create (L3) still wait for your review.';
}

export function nextLevel(current: PermissionLevel, caps: HostCaps): PermissionLevel {
  const list = availableLevels(caps);
  const i = list.findIndex(l => l.id === current);
  return list[(i + 1) % list.length].id;
}
