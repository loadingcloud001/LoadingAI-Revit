import { describe, expect, it } from 'vitest';
import type { PluginInfo, PluginLoadIssue } from '../bridge';
import { canTrust, diagnostics, groupAriaLabel, issueLines, sourceBadge, splitIssues, toolAriaLabel, verifiedLabel } from './pluginView';

const plugin = (over: Partial<PluginInfo> = {}): PluginInfo => ({
  id: 'hello', name: 'Hello', version: '1.0.0', license: 'MIT', author: 'a', verified: true,
  folderPath: 'C:\\p\\hello', source: 'shipped',
  tools: [{ name: 'hello', tier: 'L1', description: 'Says hi', enabledByDefault: true, enabled: true }],
  ...over
});

const issue = (over: Partial<PluginLoadIssue> = {}): PluginLoadIssue => ({
  folder: 'C:\\p\\x', pluginId: 'x', assembly: 'x.dll', summary: 'DLL not on allowlist', detail: 'line one\nline two', hint: 'Trust it',
  ...over
});

describe('plugin shelf helpers', () => {
  it('labels the source in words', () => {
    expect(sourceBadge({ source: 'shipped' })).toEqual({ label: 'Shipped', tone: 'ok' });
    expect(sourceBadge({ source: 'trusted' }).label).toBe('Trusted by you');
    expect(sourceBadge({ source: 'dev' }).label).toBe('Dev folder');
    expect(sourceBadge({}).label).toBe('Unknown source');
  });

  it('only a shipped plugin earns Verified, whatever its manifest claims', () => {
    expect(verifiedLabel({ source: 'shipped', verified: true })).toBe('Verified');
    expect(verifiedLabel({ source: 'dev', verified: true })).toBe('Unverified');
    expect(verifiedLabel({ source: 'trusted', verified: true })).toBe('Unverified');
    expect(verifiedLabel({ source: 'shipped', verified: false })).toBe('Unverified');
  });

  it('names the tool and the plugin for a screen reader', () => {
    expect(toolAriaLabel({ name: 'delete_elements', tier: 'L3' })).toBe('Enable delete_elements (L3)');
    expect(groupAriaLabel({ name: 'Hello' })).toBe('Enable every tool in Hello');
  });

  it('separates failures from notices', () => {
    const { failures, notices } = splitIssues([issue(), issue({ code: 'shadowed', notice: true })]);
    expect(failures).toHaveLength(1);
    expect(notices).toHaveLength(1);
  });

  it('offers Trust only for an allowlist refusal code', () => {
    expect(canTrust({ code: 'allowlist-refusal' })).toBe(true);
    expect(canTrust({ code: 'allowlist-unreadable' })).toBe(false);
    expect(canTrust({})).toBe(false);
  });

  it('lists the folder, assembly, hash and detail lines', () => {
    expect(issueLines(issue({ sha256: 'ab'.repeat(32) }))).toEqual([
      'Folder: C:\\p\\x', 'Assembly: x.dll', `SHA-256: ${'ab'.repeat(32)}`, 'line one', 'line two'
    ]);
  });

  it('writes a diagnostics block a user can paste into a bug report', () => {
    const text = diagnostics([plugin()], [issue()]);
    expect(text).toContain('- hello 1.0.0 [Shipped, Verified] 1/1 tools on');
    expect(text).toContain('Load issues');
    expect(text).toContain('- x: DLL not on allowlist (Trust it)');
  });
});
