/**
 * KV-shaped counter store backed by SQLite.
 *
 * `quota.js` is written against the Workers KV interface (`get`, `put` with
 * `expirationTtl`), so the only thing the VM host has to supply is this adapter.
 * SQLite has no per-day write cap, which is why production runs here rather than
 * on a Worker with a KV binding.
 *
 * Uses `node:sqlite`, so the gateway needs no native build tools: Node >= 22.5
 * is the only requirement.
 */

import { DatabaseSync } from 'node:sqlite';

/** KV has no expiry unless asked, which is a very long time from now. */
const NEVER = Number.MAX_SAFE_INTEGER;

/** Expired rows are pruned every this many writes; keys are per device per day. */
const PURGE_EVERY = 100;

export function openQuotaStore(file, options = {}) {
  const now = options.now ?? (() => Date.now());
  const db = new DatabaseSync(file);
  db.exec('PRAGMA journal_mode = WAL');
  db.exec(`CREATE TABLE IF NOT EXISTS kv (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    expires_at INTEGER NOT NULL
  )`);

  const select = db.prepare('SELECT value FROM kv WHERE key = ? AND expires_at > ?');
  const upsert = db.prepare(`INSERT INTO kv (key, value, expires_at) VALUES (?, ?, ?)
    ON CONFLICT(key) DO UPDATE SET value = excluded.value, expires_at = excluded.expires_at`);
  const prune = db.prepare('DELETE FROM kv WHERE expires_at <= ?');

  let writes = 0;
  let closed = false;

  return {
    /** Mirrors KV: an expired or unknown key reads as null. */
    async get(key) {
      const row = select.get(String(key), now());
      return row ? String(row.value) : null;
    },

    async put(key, value, putOptions = {}) {
      const ttl = Number(putOptions?.expirationTtl);
      const expiresAt = Number.isFinite(ttl) && ttl > 0 ? now() + ttl * 1000 : NEVER;
      upsert.run(String(key), String(value), expiresAt);
      if (++writes % PURGE_EVERY === 0) prune.run(now());
    },

    /** Test/maintenance hook; the quota code never deletes a counter itself. */
    purgeExpired() {
      return prune.run(now()).changes;
    },

    close() {
      if (closed) return;
      closed = true;
      db.close();
    },
  };
}
