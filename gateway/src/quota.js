/**
 * Per-device daily counters plus per-send tickets signed with a server-only
 * secret.
 *
 * Tickets are stateless: they carry the device and an expiry signed with HMAC,
 * so they need no storage. One consume is one counter write, and a ticket stays
 * valid for a whole long agent run.
 *
 * The store is KV-shaped (`get`/`put`) so tests can use an in-memory map and
 * production uses SQLite (`sqlite-kv.js`). A device is a single add-in install
 * and sends serially, so overlapping POSTs are not a practical concern.
 */

export const DEFAULT_LIMIT = 100;

/**
 * Ceilings that do not depend on the device id, which the client invents for
 * itself. A script can mint any number of device ids, so the per-device limit
 * alone cannot bound spend; these two can.
 *
 * Both count chat sends per UTC day. They are approximate under concurrent
 * requests (a read and a write, not one atomic step), which is fine for a
 * ceiling that exists to stop runaway spend rather than to meter exactly.
 */
export const DEFAULT_GLOBAL_LIMIT = 3000;
export const DEFAULT_IP_LIMIT = 500;

/** Counters outlive the UTC day boundary they belong to. */
const COUNTER_TTL_SECONDS = 60 * 60 * 48;

/** Long enough that a multi-round agent run never loses its ticket mid-flight. */
export const DEFAULT_TICKET_TTL_SECONDS = 60 * 60 * 2;

const encoder = new TextEncoder();

export function utcDay(now = new Date()) {
  return now.toISOString().slice(0, 10);
}

export function nextUtcReset(now = new Date()) {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1)).toISOString();
}

export function isValidDevice(device) {
  return typeof device === 'string' && /^[A-Za-z0-9_-]{8,64}$/.test(device);
}

function counterKey(device, day) {
  return `q:${device}:${day}`;
}

/** A ticket lets one chat send make many upstream calls without extra quota. */
export async function issueTicket(secret, device, ttlSeconds, now = Date.now()) {
  const expiresAt = Math.floor(now / 1000) + ttlSeconds;
  const payload = `${device}.${expiresAt}`;
  const signature = await hmac(secret, payload);
  return `${toBase64Url(encoder.encode(payload))}.${toBase64Url(signature)}`;
}

/**
 * Returns the device a ticket was issued for, or null when the signature does
 * not verify, the ticket is malformed, or it has expired.
 */
export async function ticketDevice(secret, ticket, now = Date.now()) {
  if (typeof ticket !== 'string' || ticket.length > 512) return null;
  const separator = ticket.lastIndexOf('.');
  if (separator <= 0) return null;

  const payload = decodeBase64Url(ticket.slice(0, separator));
  const provided = decodeBase64Url(ticket.slice(separator + 1));
  if (!payload || !provided) return null;

  const expected = await hmac(secret, new TextDecoder().decode(payload));
  if (expected.length !== provided.length || !timingSafeEqual(expected, provided)) return null;

  const text = new TextDecoder().decode(payload);
  const dot = text.lastIndexOf('.');
  const device = text.slice(0, dot);
  const expiresAt = Number.parseInt(text.slice(dot + 1), 10);
  if (!isValidDevice(device)) return null;
  if (!Number.isFinite(expiresAt) || expiresAt * 1000 <= now) return null;
  return device;
}

async function hmac(secret, message) {
  const key = await crypto.subtle.importKey('raw', encoder.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, [
    'sign',
  ]);
  return new Uint8Array(await crypto.subtle.sign('HMAC', key, encoder.encode(message)));
}

function toBase64Url(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeBase64Url(text) {
  if (typeof text !== 'string' || !/^[A-Za-z0-9_-]+$/.test(text)) return null;
  const padded = text.replace(/-/g, '+').replace(/_/g, '/');
  try {
    const binary = atob(padded.padEnd(Math.ceil(padded.length / 4) * 4, '='));
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  } catch {
    return null;
  }
}

function timingSafeEqual(a, b) {
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

async function readUsed(kv, device, day) {
  const raw = await kv.get(counterKey(device, day));
  const used = Number.parseInt(raw ?? '0', 10);
  return Number.isFinite(used) && used > 0 ? used : 0;
}

/** Does not consume. Used by Settings to show how many requests are left today. */
export async function remaining(kv, device, limit, now = new Date()) {
  const used = await readUsed(kv, device, utcDay(now));
  return { limit, used, remaining: Math.max(0, limit - used), resetUtc: nextUtcReset(now) };
}

/**
 * Consumes one unit for this device. Returns allowed=false once the device has
 * used `limit` units in the current UTC day.
 */
export async function consume(kv, device, limit, now = new Date()) {
  const used = await readUsed(kv, device, utcDay(now));
  if (used >= limit) {
    return { allowed: false, limit, used, remaining: 0, resetUtc: nextUtcReset(now) };
  }
  const next = used + 1;
  await kv.put(counterKey(device, utcDay(now)), String(next), { expirationTtl: COUNTER_TTL_SECONDS });
  return { allowed: true, limit, used: next, remaining: Math.max(0, limit - next), resetUtc: nextUtcReset(now) };
}

function globalKey(day) {
  return `g:${day}`;
}

/** The address is stored only as a short hash, and only for the counter's lifetime. */
async function ipKey(ip, day) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(`ip:${ip}`)));
  let hex = '';
  for (let i = 0; i < 8; i++) hex += digest[i].toString(16).padStart(2, '0');
  return `i:${hex}:${day}`;
}

async function readCount(kv, key) {
  const used = Number.parseInt((await kv.get(key)) ?? '0', 10);
  return Number.isFinite(used) && used > 0 ? used : 0;
}

/**
 * Checks the whole-service and per-address ceilings without spending anything.
 * `ip` may be empty (a local run with no proxy in front): then only the global
 * ceiling applies. Returns { allowed: true } or { allowed: false, reason, resetUtc }
 * where reason is 'capacity' or 'ip'.
 */
export async function checkCeilings(kv, ip, limits, now = new Date()) {
  const day = utcDay(now);
  if ((await readCount(kv, globalKey(day))) >= limits.global) {
    return { allowed: false, reason: 'capacity', resetUtc: nextUtcReset(now) };
  }
  if (ip && (await readCount(kv, await ipKey(ip, day))) >= limits.ip) {
    return { allowed: false, reason: 'ip', resetUtc: nextUtcReset(now) };
  }
  return { allowed: true };
}

/** Records one send against the whole-service and per-address counters. */
export async function recordSend(kv, ip, now = new Date()) {
  const day = utcDay(now);
  const options = { expirationTtl: COUNTER_TTL_SECONDS };
  await kv.put(globalKey(day), String((await readCount(kv, globalKey(day))) + 1), options);
  if (ip) {
    const key = await ipKey(ip, day);
    await kv.put(key, String((await readCount(kv, key)) + 1), options);
  }
}
