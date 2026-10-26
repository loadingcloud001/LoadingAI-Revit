import { describe, expect, it } from 'vitest';
import { callInfo, classifyResult, resultBody } from './legacyText';

describe('classifyResult', () => {
  it('reads a plain result', () => {
    expect(classifyResult('split_beams result: ok')).toEqual({ kind: 'ran', name: 'split_beams' });
  });

  it('reads a multi-line result', () => {
    expect(classifyResult("get_wall_metrics result: Walls: 14\nTotal length: 78400 mm")).toEqual({ kind: 'ran', name: 'get_wall_metrics' });
  });

  it('reads a denial as cancelled', () => {
    expect(classifyResult('delete_elements denied by user')).toEqual({ kind: 'outcome', name: 'delete_elements', outcome: 'cancelled' });
  });

  it('reads an approval timeout', () => {
    expect(classifyResult('x timed out waiting for approval (180s)')).toEqual({ kind: 'outcome', name: 'x', outcome: 'timeout' });
  });

  it('reads a preset timeout as timeout, not cancel', () => {
    expect(classifyResult('color_by_parameter Cancelled: preset timeout.')).toEqual({ kind: 'outcome', name: 'color_by_parameter', outcome: 'timeout' });
  });

  it('leaves an argument error without a tool name', () => {
    expect(classifyResult('Tool argument error: bad')).toEqual({ kind: 'other', name: '' });
  });
});

describe('callInfo', () => {
  it('strips the awaiting marker and keeps its id', () => {
    const info = callInfo('a: {} (awaiting approval ab12)');
    expect(info).toMatchObject({ name: 'a', awaiting: 'approval', awaitingId: 'ab12', display: 'a: {}', args: '{}' });
  });

  it('reads a plain call', () => {
    expect(callInfo('get_beams: {"level":"Level 2"}')).toMatchObject({ name: 'get_beams', awaiting: null, args: '{"level":"Level 2"}' });
  });
});

describe('resultBody', () => {
  it('returns the text after result:', () => {
    expect(resultBody('t result: Split 8 beams.')).toBe('Split 8 beams.');
  });
});
