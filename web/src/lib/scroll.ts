export type ScrollBox = { scrollTop: number; scrollHeight: number; clientHeight: number };

/** True when the reader is at (or within `threshold` px of) the bottom, so new output may follow. */
export function isNearBottom(el: ScrollBox, threshold = 48): boolean {
  return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
}
