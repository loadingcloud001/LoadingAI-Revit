import test from 'node:test';
import assert from 'node:assert/strict';

import worker from '../src/index.js';
import { issueTicket as signTicket } from '../src/quota.js';

const DEVICE = 'device-abc12345';
const OTHER_DEVICE = 'device-xyz98765';

function memoryKv() {
  const map = new Map();
  return {
    map,
    async get(key) {
      return map.has(key) ? map.get(key) : null;
    },
    async put(key, value) {
      map.set(key, String(value));
    },
  };
}

function makeEnv(overrides = {}) {
  return {
    QUOTA: memoryKv(),
    MINIMAX_API_KEY: 'sk-server-side-secret',
    MINIMAX_BASE_URL: 'https://upstream.test/v1',
    MINIMAX_MODEL: 'MiniMax-M3',
    DAILY_LIMIT: '100',
    ...overrides,
  };
}

function quotaRequest(method, device = DEVICE) {
  return new Request('https://gateway.test/v1/quota', {
    method,
    headers: { 'x-loadingai-device': device },
  });
}

function chatRequest(headers = {}, body = { messages: [{ role: 'user', content: 'hi' }] }) {
  return new Request('https://gateway.test/v1/chat/completions', {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...headers },
    body: JSON.stringify(body),
  });
}

async function issueTicket(env) {
  const res = await worker.fetch(quotaRequest('POST'), env);
  assert.equal(res.status, 200);
  return (await res.json()).ticket;
}

function ticketPayload(ticket) {
  return Buffer.from(ticket.split('.')[0], 'base64url').toString('utf8');
}

function stubUpstream(t, body = { choices: [{ message: { content: 'ok' } }] }) {
  const calls = [];
  const original = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = original;
  });
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  };
  return calls;
}

test('health answers without configuration', async () => {
  const res = await worker.fetch(new Request('https://gateway.test/health'), {});
  assert.equal(res.status, 200);
  assert.equal((await res.json()).ok, true);
});

test('GET /v1/quota reports the full allowance and consumes nothing', async () => {
  const env = makeEnv();
  const res = await worker.fetch(quotaRequest('GET'), env);
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.equal(body.limit, 100);
  assert.equal(body.used, 0);
  assert.equal(body.remaining, 100);
  assert.equal(body.ticket, undefined);
  assert.equal(env.QUOTA.map.size, 0);
});

test('POST /v1/quota consumes one request and returns a ticket', async () => {
  const env = makeEnv();
  const res = await worker.fetch(quotaRequest('POST'), env);
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.equal(body.used, 1);
  assert.equal(body.remaining, 99);
  assert.equal(body.ticketTtlSeconds, 1800);
  assert.match(ticketPayload(body.ticket), new RegExp(`^${DEVICE}\\.\\d+$`));

  const peek = await (await worker.fetch(quotaRequest('GET'), env)).json();
  assert.equal(peek.used, 1);
  assert.equal(peek.remaining, 99);
});

test('quota is tracked per device', async () => {
  const env = makeEnv();
  await worker.fetch(quotaRequest('POST', DEVICE), env);
  await worker.fetch(quotaRequest('POST', DEVICE), env);
  const other = await (await worker.fetch(quotaRequest('GET', OTHER_DEVICE), env)).json();
  assert.equal(other.used, 0);
  assert.equal(other.remaining, 100);
});

test('quota rejects an unknown device header', async () => {
  const env = makeEnv();
  const res = await worker.fetch(quotaRequest('GET', 'short'), env);
  assert.equal(res.status, 400);
  assert.equal((await res.json()).error.code, 'bad_device');
});

test('the 101st send is refused with the daily limit code', async () => {
  const env = makeEnv({ DAILY_LIMIT: '3' });
  for (let i = 0; i < 3; i++) {
    assert.equal((await worker.fetch(quotaRequest('POST'), env)).status, 200);
  }
  const res = await worker.fetch(quotaRequest('POST'), env);
  assert.equal(res.status, 429);
  const body = await res.json();
  assert.equal(body.error.code, 'daily_limit');
  assert.match(body.error.message, /daily limit reached \(3 requests\/day\)/);
  assert.equal(body.remaining, 0);
});

test('GET /v1/models answers locally so a model probe never spends credit', async () => {
  const env = makeEnv();
  const res = await worker.fetch(new Request('https://gateway.test/v1/models', { method: 'GET' }), env);
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.equal(body.data[0].id, 'MiniMax-M3');
  assert.equal(body.data[0].owned_by, 'LoadingAI');
});

test('chat without a ticket is refused', async () => {
  const env = makeEnv();
  const res = await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE }), env);
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'no_ticket');
});

test('a ticket cannot be used from another device', async () => {
  const env = makeEnv();
  const ticket = await issueTicket(env);
  const res = await worker.fetch(
    chatRequest({ 'x-loadingai-device': OTHER_DEVICE, 'x-loadingai-ticket': ticket }),
    env,
  );
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'bad_ticket');
});

test('a ticket signed with a different secret is refused', async () => {
  const env = makeEnv();
  const forged = await signTicket('not-the-server-secret', DEVICE, 600);
  const res = await worker.fetch(
    chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': forged }),
    env,
  );
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'no_ticket');
});

test('a ticket whose payload was edited is refused', async () => {
  const env = makeEnv();
  const signature = (await issueTicket(env)).split('.')[1];
  const edited = `${Buffer.from(`${OTHER_DEVICE}.9999999999`).toString('base64url')}.${signature}`;

  const res = await worker.fetch(
    chatRequest({ 'x-loadingai-device': OTHER_DEVICE, 'x-loadingai-ticket': edited }),
    env,
  );
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'no_ticket');
});

test('an expired ticket is refused', async () => {
  const env = makeEnv({ TICKET_TTL_SECONDS: '0' });
  const ticket = await issueTicket(env);
  const res = await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket }), env);
  assert.equal(res.status, 401);
  assert.equal((await res.json()).error.code, 'no_ticket');
});

test('a ticket keeps working with no server-side storage', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);
  env.QUOTA.map.clear();
  stubUpstream(t);

  const res = await worker.fetch(
    chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket }),
    env,
  );
  assert.equal(res.status, 200);
});

test('chat forwards to the upstream model with the server-side key', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);

  const calls = [];
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return new Response(JSON.stringify({ choices: [{ message: { content: 'ok' } }] }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  };

  const res = await worker.fetch(
    chatRequest({
      'x-loadingai-device': DEVICE,
      'x-loadingai-ticket': ticket,
      authorization: 'Bearer sk-stolen-from-a-client',
    }),
    env,
  );

  assert.equal(res.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'https://upstream.test/v1/chat/completions');
  assert.equal(calls[0].init.headers.authorization, 'Bearer sk-server-side-secret');
  assert.equal(calls[0].init.headers.authorization.includes('sk-stolen-from-a-client'), false);

  const forwarded = JSON.parse(calls[0].init.body);
  assert.equal(forwarded.model, 'MiniMax-M3');
  assert.equal(forwarded.messages[0].content, 'hi');
});

test('chat forces the configured model and keeps streaming responses intact', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);

  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });
  let sentBody;
  globalThis.fetch = async (_url, init) => {
    sentBody = JSON.parse(init.body);
    return new Response('data: {"choices":[{"delta":{"content":"h"}}]}\n\ndata: [DONE]\n\n', {
      status: 200,
      headers: { 'content-type': 'text/event-stream' },
    });
  };

  const res = await worker.fetch(
    chatRequest(
      { 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket, accept: 'text/event-stream' },
      { model: 'some-cheaper-model', stream: true, messages: [] },
    ),
    env,
  );

  assert.equal(res.status, 200);
  assert.equal(res.headers.get('content-type'), 'text/event-stream');
  assert.equal(sentBody.model, 'MiniMax-M3');
  assert.match(await res.text(), /data: \[DONE\]/);
});

test('chat reports an upstream failure status without inventing a body', async (t) => {
  const env = makeEnv();
  const ticket = await issueTicket(env);

  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });
  globalThis.fetch = async () =>
    new Response(JSON.stringify({ error: { message: 'upstream said no' } }), {
      status: 402,
      headers: { 'content-type': 'application/json' },
    });

  const res = await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket }), env);
  assert.equal(res.status, 402);
  assert.match(await res.text(), /upstream said no/);
});

test('an unconfigured gateway refuses quota and chat alike', async () => {
  const env = makeEnv({ MINIMAX_API_KEY: '' });

  const quota = await worker.fetch(quotaRequest('POST'), env);
  assert.equal(quota.status, 500);
  assert.equal((await quota.json()).error.code, 'gateway_misconfigured');

  const res = await worker.fetch(chatRequest({ 'x-loadingai-device': DEVICE }), env);
  assert.equal(res.status, 500);
  assert.equal((await res.json()).error.code, 'gateway_misconfigured');
});

test('a rejected non-JSON body does not consume the ticket', async () => {
  const env = makeEnv();
  const ticket = await issueTicket(env);
  const res = await worker.fetch(
    new Request('https://gateway.test/v1/chat/completions', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-loadingai-device': DEVICE, 'x-loadingai-ticket': ticket },
      body: 'not json',
    }),
    env,
  );
  assert.equal(res.status, 400);
  assert.equal((await res.json()).error.code, 'bad_json');
});

test('unknown routes are rejected', async () => {
  const res = await worker.fetch(new Request('https://gateway.test/v1/anything', { method: 'POST' }), {});
  assert.equal(res.status, 404);
});

test('a send costs exactly one KV write', async () => {
  const env = makeEnv();
  const puts = [];
  env.QUOTA.put = async (key, value, options) => {
    puts.push({ key, value, options });
  };
  await worker.fetch(quotaRequest('POST'), env);

  assert.equal(puts.length, 1);
  assert.match(puts[0].key, /^q:device-abc12345:\d{4}-\d{2}-\d{2}$/);
  assert.equal(puts[0].value, '1');
  assert.equal(puts[0].options.expirationTtl, 60 * 60 * 48);
});
