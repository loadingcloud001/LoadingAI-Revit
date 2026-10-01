import test from 'node:test';
import assert from 'node:assert/strict';
import { rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { startGateway } from '../src/server.mjs';
import { openQuotaStore } from '../src/sqlite-kv.js';

const DEVICE = 'device-abc12345';
const UPSTREAM = 'https://upstream.test/v1';

function testEnv(overrides = {}) {
  return {
    MINIMAX_API_KEY: 'sk-server-side-secret',
    TICKET_SECRET: 'ticket-signing-secret',
    MINIMAX_BASE_URL: UPSTREAM,
    MINIMAX_MODEL: 'MiniMax-M3.1-Flash-Preview',
    DAILY_LIMIT: '100',
    ...overrides,
  };
}

async function startTestGateway(t, options = {}) {
  const env = testEnv(options.env);
  const gateway = startGateway({
    env,
    host: '127.0.0.1',
    port: 0,
    store: options.store ?? openQuotaStore(':memory:'),
    ...(options.maxBodyBytes ? { maxBodyBytes: options.maxBodyBytes } : {}),
  });
  const address = await gateway.listen();
  t.after(() => gateway.close());
  return { env, gateway, base: `http://127.0.0.1:${address.port}` };
}

/**
 * Replaces the upstream host only, so the test's own HTTP calls to the local
 * gateway still reach it through the real fetch.
 */
function stubUpstream(t, handler) {
  const calls = [];
  const original = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = original;
  });
  globalThis.fetch = async (url, init) => {
    const href = String(url);
    if (!href.startsWith(`${UPSTREAM}/`)) return original(url, init);
    calls.push({ url: href, init, body: init?.body ? JSON.parse(init.body) : null });
    return handler(href, init);
  };
  return calls;
}

const jsonUpstream = (body = { choices: [{ message: { content: 'ok' } }] }) =>
  () => new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } });

const quotaPath = '/v1/quota';
const CLIENT = 'loadingai-addin-v1';
const deviceHeaders = (extra = {}) => ({
  'x-loadingai-client': CLIENT,
  'x-loadingai-device': DEVICE,
  ...extra,
});

async function issueTicket(base) {
  const res = await fetch(`${base}${quotaPath}`, { method: 'POST', headers: deviceHeaders() });
  assert.equal(res.status, 200);
  return (await res.json()).ticket;
}

test('the tunnel target answers /health', async (t) => {
  const { base } = await startTestGateway(t);
  const res = await fetch(`${base}/health`);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { ok: true, service: 'loadingai-gateway' });
});

test('GET /v1/quota reports the full allowance without consuming', async (t) => {
  const { base } = await startTestGateway(t);
  const res = await fetch(`${base}${quotaPath}`, { headers: deviceHeaders() });
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.equal(body.limit, 100);
  assert.equal(body.remaining, 100);
  assert.equal(body.ticket, undefined);
});

test('POST /v1/quota consumes one send and returns a ticket', async (t) => {
  const { base } = await startTestGateway(t);
  const body = await (await fetch(`${base}${quotaPath}`, { method: 'POST', headers: deviceHeaders() })).json();
  assert.equal(body.used, 1);
  assert.equal(body.remaining, 99);
  assert.equal(typeof body.ticket, 'string');
});

test('a malformed device header is rejected', async (t) => {
  const { base } = await startTestGateway(t);
  const res = await fetch(`${base}${quotaPath}`, { headers: { 'x-loadingai-client': CLIENT, 'x-loadingai-device': 'no' } });
  assert.equal(res.status, 400);
  assert.equal((await res.json()).error.code, 'bad_device');
});

test('chat without a ticket is rejected before reaching upstream', async (t) => {
  const { base } = await startTestGateway(t);
  const calls = stubUpstream(t, jsonUpstream());
  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: deviceHeaders({ 'content-type': 'application/json' }),
    body: JSON.stringify({ messages: [{ role: 'user', content: 'hi' }] }),
  });
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'no_ticket');
  assert.equal(calls.length, 0);
});

test('chat with a ticket reaches upstream under the server key only', async (t) => {
  const { base } = await startTestGateway(t);
  const calls = stubUpstream(t, jsonUpstream());
  const ticket = await issueTicket(base);

  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: deviceHeaders({
      'content-type': 'application/json',
      'x-loadingai-ticket': ticket,
      authorization: 'Bearer client-supplied-key',
    }),
    body: JSON.stringify({ model: 'something-else', messages: [{ role: 'user', content: 'hi' }] }),
  });

  assert.equal(res.status, 200);
  assert.equal((await res.json()).choices[0].message.content, 'ok');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, `${UPSTREAM}/chat/completions`);
  assert.equal(new Headers(calls[0].init.headers).get('authorization'), 'Bearer sk-server-side-secret');
  assert.equal(calls[0].body.model, 'MiniMax-M3.1-Flash-Preview');
});

test('a ticket minted for another device is refused', async (t) => {
  const { base } = await startTestGateway(t);
  stubUpstream(t, jsonUpstream());
  const ticket = await issueTicket(base);

  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-loadingai-client': CLIENT, 'x-loadingai-device': 'device-other9999', 'x-loadingai-ticket': ticket },
    body: JSON.stringify({ messages: [{ role: 'user', content: 'hi' }] }),
  });
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'bad_ticket');
});

test('the daily limit answers 429 once the allowance is spent', async (t) => {
  const { base } = await startTestGateway(t, { env: { DAILY_LIMIT: '2' } });

  assert.equal((await fetch(`${base}${quotaPath}`, { method: 'POST', headers: deviceHeaders() })).status, 200);
  assert.equal((await fetch(`${base}${quotaPath}`, { method: 'POST', headers: deviceHeaders() })).status, 200);

  const denied = await fetch(`${base}${quotaPath}`, { method: 'POST', headers: deviceHeaders() });
  assert.equal(denied.status, 429);
  const body = await denied.json();
  assert.equal(body.error.code, 'daily_limit');
  assert.equal(body.remaining, 0);
  assert.match(body.error.message, /2 requests\/day/);
});

test('streaming responses are relayed chunk by chunk', async (t) => {
  const { base } = await startTestGateway(t);
  const encoder = new TextEncoder();
  let release;
  stubUpstream(
    t,
    () =>
      new Response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(encoder.encode('data: first\n\n'));
            release = () => {
              controller.enqueue(encoder.encode('data: second\n\n'));
              controller.close();
            };
          },
        }),
        { headers: { 'content-type': 'text/event-stream' } },
      ),
  );
  const ticket = await issueTicket(base);

  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: deviceHeaders({ 'content-type': 'application/json', 'x-loadingai-ticket': ticket }),
    body: JSON.stringify({ stream: true, messages: [{ role: 'user', content: 'hi' }] }),
  });
  assert.equal(res.headers.get('content-type'), 'text/event-stream');

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  const first = await reader.read();
  assert.match(decoder.decode(first.value), /data: first/);

  release();
  let rest = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    rest += decoder.decode(value);
  }
  assert.match(rest, /data: second/);
});

test('GET /v1/models without the add-in client header is refused', async (t) => {
  const { base } = await startTestGateway(t);
  const res = await fetch(`${base}/v1/models`);
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'bad_client');
});

test('GET /v1/models is answered locally', async (t) => {
  const { base } = await startTestGateway(t);
  const calls = stubUpstream(t, jsonUpstream());
  const res = await fetch(`${base}/v1/models`, { headers: deviceHeaders() });
  assert.equal(res.status, 200);
  assert.equal((await res.json()).data[0].id, 'MiniMax-M3.1-Flash-Preview');
  assert.equal(calls.length, 0);
});

test('unknown routes answer 404', async (t) => {
  const { base } = await startTestGateway(t);
  const res = await fetch(`${base}/v1/nope`);
  assert.equal(res.status, 404);
  assert.equal((await res.json()).error.code, 'not_found');
});

test('an oversized body is refused without reaching the handler', async (t) => {
  const { base } = await startTestGateway(t, { maxBodyBytes: 32 });
  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: deviceHeaders({ 'content-type': 'application/json' }),
    body: JSON.stringify({ messages: [{ role: 'user', content: 'x'.repeat(200) }] }),
  });
  assert.equal(res.status, 413);
});

test('counters live in SQLite, so a restart keeps the day count', async () => {
  const file = join(tmpdir(), `loadingai-gateway-${process.pid}-${Date.now()}.db`);
  const env = testEnv();
  const device = { 'x-loadingai-client': CLIENT, 'x-loadingai-device': DEVICE };
  let first;
  let second;

  try {
    first = startGateway({ env, host: '127.0.0.1', port: 0, store: openQuotaStore(file) });
    const before = await first.listen();
    await fetch(`http://127.0.0.1:${before.port}${quotaPath}`, { method: 'POST', headers: device });
    await first.close();

    second = startGateway({ env, host: '127.0.0.1', port: 0, store: openQuotaStore(file) });
    const after = await second.listen();
    const body = await (await fetch(`http://127.0.0.1:${after.port}${quotaPath}`, { headers: device })).json();

    assert.equal(body.used, 1);
    assert.equal(body.remaining, 99);
  } finally {
    await first?.close();
    await second?.close();
    // Windows keeps the SQLite file locked until every handle is closed.
    for (const suffix of ['', '-wal', '-shm']) rmSync(file + suffix, { force: true });
  }
});

test('a missing upstream key is reported, not guessed', async (t) => {
  const { base } = await startTestGateway(t, { env: { MINIMAX_API_KEY: '' } });
  const res = await fetch(`${base}/v1/chat/completions`, {
    method: 'POST',
    headers: deviceHeaders({ 'content-type': 'application/json' }),
    body: JSON.stringify({ messages: [{ role: 'user', content: 'hi' }] }),
  });
  assert.equal(res.status, 500);
  assert.equal((await res.json()).error.code, 'gateway_misconfigured');
});
