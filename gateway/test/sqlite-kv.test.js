import test from 'node:test';
import assert from 'node:assert/strict';
import { rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { openQuotaStore } from '../src/sqlite-kv.js';

/** A store whose clock we control, so expiry is tested without sleeping. */
function storeWithClock() {
  let current = Date.UTC(2026, 0, 1);
  const store = openQuotaStore(':memory:', { now: () => current });
  return { store, advance: (seconds) => (current += seconds * 1000) };
}

test('unknown keys read as null, like KV', async () => {
  const { store } = storeWithClock();
  assert.equal(await store.get('q:device-abc12345:2026-01-01'), null);
  store.close();
});

test('a value survives a read/write round trip as a string', async () => {
  const { store } = storeWithClock();
  await store.put('q:device-abc12345:2026-01-01', 7);
  assert.equal(await store.get('q:device-abc12345:2026-01-01'), '7');
  store.close();
});

test('a later write replaces the counter instead of duplicating the key', async () => {
  const { store } = storeWithClock();
  await store.put('k', '1');
  await store.put('k', '2');
  assert.equal(await store.get('k'), '2');
  store.close();
});

test('expirationTtl hides the value once it lapses', async () => {
  const { store, advance } = storeWithClock();
  await store.put('k', '1', { expirationTtl: 60 });

  advance(59);
  assert.equal(await store.get('k'), '1');
  advance(2);
  assert.equal(await store.get('k'), null);
  store.close();
});

test('a value written without a TTL never expires', async () => {
  const { store, advance } = storeWithClock();
  await store.put('k', '1');
  advance(60 * 60 * 24 * 365 * 10);
  assert.equal(await store.get('k'), '1');
  store.close();
});

test('pruning removes lapsed rows and leaves live ones alone', async () => {
  const { store, advance } = storeWithClock();
  await store.put('stale', '1', { expirationTtl: 1 });
  advance(10);
  await store.put('fresh', '1');

  assert.equal(store.purgeExpired(), 1);
  assert.equal(await store.get('fresh'), '1');
  store.close();
});

test('the write interval prunes expired rows on its own', async () => {
  const { store, advance } = storeWithClock();
  await store.put('stale', '1', { expirationTtl: 1 });
  advance(10);

  for (let i = 0; i < 100; i++) await store.put(`fresh-${i}`, '1');

  assert.equal(store.purgeExpired(), 0);
  assert.equal(await store.get('fresh-99'), '1');
  store.close();
});

test('counters persist across store instances on the same file', async (t) => {
  const file = join(tmpdir(), `loadingai-quota-${process.pid}-${Date.now()}.db`);
  t.after(() => {
    for (const suffix of ['', '-wal', '-shm']) rmSync(file + suffix, { force: true });
  });

  const first = openQuotaStore(file);
  await first.put('q:device-abc12345:2026-01-01', '12');
  first.close();

  const second = openQuotaStore(file);
  assert.equal(await second.get('q:device-abc12345:2026-01-01'), '12');
  second.close();
});
