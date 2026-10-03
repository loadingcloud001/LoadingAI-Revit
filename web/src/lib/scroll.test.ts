import { describe, expect, it } from 'vitest';
import { isNearBottom } from './scroll';

describe('isNearBottom', () => {
  it('is true at the bottom', () => {
    expect(isNearBottom({ scrollTop: 600, scrollHeight: 1000, clientHeight: 400 })).toBe(true);
  });
  it('is true within the threshold', () => {
    expect(isNearBottom({ scrollTop: 570, scrollHeight: 1000, clientHeight: 400 })).toBe(true);
  });
  it('is false when the reader scrolled up', () => {
    expect(isNearBottom({ scrollTop: 100, scrollHeight: 1000, clientHeight: 400 })).toBe(false);
  });
});
