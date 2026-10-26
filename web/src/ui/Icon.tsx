/** Small stroke icon set drawn inline, so the pane needs no font or network in WebView2. */
const PATHS: Record<string, string> = {
  settings: 'M10.3 4.3a1.7 1.7 0 0 1 3.4 0l.2 1a1.7 1.7 0 0 0 2.5 1l.9-.5a1.7 1.7 0 0 1 2.3 2.4l-.5.9a1.7 1.7 0 0 0 1 2.5l1 .2a1.7 1.7 0 0 1 0 3.4l-1 .2a1.7 1.7 0 0 0-1 2.5l.5.9a1.7 1.7 0 0 1-2.3 2.3l-.9-.5a1.7 1.7 0 0 0-2.5 1l-.2 1a1.7 1.7 0 0 1-3.4 0l-.2-1a1.7 1.7 0 0 0-2.5-1l-.9.5a1.7 1.7 0 0 1-2.3-2.3l.5-.9a1.7 1.7 0 0 0-1-2.5l-1-.2a1.7 1.7 0 0 1 0-3.4l1-.2a1.7 1.7 0 0 0 1-2.5l-.5-.9a1.7 1.7 0 0 1 2.3-2.4l.9.5a1.7 1.7 0 0 0 2.5-1zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
  history: 'M12 8v4l2 2M3.05 11a9 9 0 1 1 .5 4M3 4v4h4',
  chevronRight: 'M9 6l6 6-6 6',
  chevronDown: 'M6 9l6 6 6-6',
  arrowLeft: 'M5 12h14M5 12l6 6M5 12l6-6',
  arrowUp: 'M12 19V5M6 11l6-6 6 6',
  check: 'M5 12l5 5L20 7',
  x: 'M18 6L6 18M6 6l12 12',
  alert: 'M12 9v4M12 17h.01M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z',
  bolt: 'M13 3L4 14h7l-1 7 9-11h-7l1-7z',
  shield: 'M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6l8-3zM9 12l2 2 4-4',
  eye: 'M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
  list: 'M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01',
  pencil: 'M4 20h4L19 9l-4-4L4 16v4zM14 6l4 4',
  image: 'M4 5h16v14H4zM4 16l5-5 4 4 3-3 4 4M15 9h.01',
  stop: 'M7 7h10v10H7z',
  copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
  plus: 'M12 5v14M5 12h14',
  refresh: 'M20 11a8 8 0 0 0-14.8-4M4 4v4h4M4 13a8 8 0 0 0 14.8 4M20 20v-4h-4',
  spark: 'M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5L18 18M6 18l2.5-2.5M15.5 8.5L18 6',
  ruler: 'M4 15L15 4l5 5L9 20zM8 11l2 2M11 8l2 2M14 5l1 1',
  palette: 'M12 3a9 9 0 1 0 0 18c1 0 1.5-.8 1.5-1.5 0-1.2-1-1.5-1-2.5s.7-1.5 1.8-1.5H17a4 4 0 0 0 4-4c0-4.7-4-8.5-9-8.5zM7.5 11.5h.01M10 7.5h.01M15 7.5h.01',
  scissors: 'M6 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM8.1 7.9L20 20M8.1 16.1L20 4',
  box: 'M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3zM12 12l8-4.5M12 12v9M12 12L4 7.5',
  tag: 'M3 12V4h8l9 9-8 8-9-9zM7.5 7.5h.01',
  export: 'M12 4v11M8 8l4-4 4 4M5 15v4h14v-4',
  sheet: 'M6 3h9l4 4v14H6zM14 3v5h5M9 13h6M9 17h6',
  circle: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z',
  dot: 'M12 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2z',
  loader: 'M12 3a9 9 0 1 0 9 9',
  undo: 'M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-3',
  send: 'M5 12h14M13 6l6 6-6 6',
};

export type IconName = keyof typeof PATHS;

export function Icon({ name, size = 14, className, title }: { name: IconName; size?: number; className?: string; title?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden={title ? undefined : true}
      role={title ? 'img' : undefined}
      style={{ flexShrink: 0 }}
    >
      {title ? <title>{title}</title> : null}
      <path d={PATHS[name]} />
    </svg>
  );
}
