import test from 'node:test';
import assert from 'node:assert/strict';

import worker from '../src/index.js';

// A script can invent any number of device ids, so the per-device limit alone cannot bound spend.
// These cover the two ceilings that do not depend on the device id.

const CLIENT = 'loadingai-addin-v1';

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
    TICKET_SECRET: 'ticket-signing-secret',
    DAILY_LIMIT: '100',
    ...overrides,
  };
}

function send(device, ip) {
  const headers = { 'x-loadingai-client': CLIENT, 'x-loadingai-device': device };
  if (ip) headers['cf-connecting-ip'] = ip;
  return worker.fetch(new Request('https://gateway.test/v1/quota', { method: 'POST', headers }), makeEnv.env);
}

async function sendWith(env, device, ip) {
  makeEnv.env = env;
  return send(device, ip);
}

test('a script minting fresh device ids is stopped by the global ceiling', async () => {
  const env = makeEnv({ GLOBAL_DAILY_LIMIT: '3' });
  for (let i = 0; i < 3; i++) assert.equal((await sendWith(env, `device-mint-000${i}`, `203.0.113.${i}`)).status, 200);

  const refused = await sendWith(env, 'device-mint-0009', '198.51.100.7');
  assert.equal(refused.status, 503);
  const body = await refused.json();
  assert.equal(body.error.code, 'capacity_reached');
  assert.match(body.error.message, /at capacity/);
  assert.ok(body.resetUtc);
});

test('one address is capped even when every device id is new', async () => {
  const env = makeEnv({ IP_DAILY_LIMIT: '2' });
  for (let i = 0; i < 2; i++) assert.equal((await sendWith(env, `device-ip-0000${i}`, '203.0.113.9')).status, 200);

  const refused = await sendWith(env, 'device-ip-00009', '203.0.113.9');
  assert.equal(refused.status, 503);
  assert.equal((await refused.json()).error.code, 'ip_limit');

  // A different address is unaffected.
  assert.equal((await sendWith(env, 'device-ip-00010', '203.0.113.10')).status, 200);
});

test('a refused send does not spend the device allowance', async () => {
  const env = makeEnv({ GLOBAL_DAILY_LIMIT: '1' });
  await sendWith(env, 'device-first-001', '203.0.113.1');
  assert.equal((await sendWith(env, 'device-spare-01', '203.0.113.2')).status, 503);

  const peek = await worker.fetch(
    new Request('https://gateway.test/v1/quota', {
      headers: { 'x-loadingai-client': CLIENT, 'x-loadingai-device': 'device-spare-01' },
    }),
    env,
  );
  assert.equal((await peek.json()).used, 0);
});

test('the add-in is not told its own allowance ran out: the ceilings never answer 429', async () => {
  const env = makeEnv({ GLOBAL_DAILY_LIMIT: '1', IP_DAILY_LIMIT: '1' });
  await sendWith(env, 'device-first-002', '203.0.113.1');
  assert.notEqual((await sendWith(env, 'device-second-02', '203.0.113.1')).status, 429);
});

test('without a client address (a local run) only the global ceiling applies', async () => {
  const env = makeEnv({ IP_DAILY_LIMIT: '1', GLOBAL_DAILY_LIMIT: '5' });
  for (let i = 0; i < 3; i++) assert.equal((await sendWith(env, `device-local-0${i}`, '')).status, 200);
});

test('the address is stored only as a short hash', async () => {
  const env = makeEnv();
  await sendWith(env, 'device-hash-0001', '203.0.113.77');
  const keys = [...env.QUOTA.map.keys()];
  assert.ok(keys.some((k) => k.startsWith('i:')));
  assert.ok(keys.every((k) => !k.includes('203.0.113.77')));
});

test('the ceilings have sensible defaults and a bad value falls back to them', async () => {
  const env = makeEnv({ GLOBAL_DAILY_LIMIT: 'lots', IP_DAILY_LIMIT: '-4' });
  assert.equal((await sendWith(env, 'device-default-01', '203.0.113.5')).status, 200);
});

test('a failed ceiling check never records a send', async () => {
  const env = makeEnv({ GLOBAL_DAILY_LIMIT: '1' });
  await sendWith(env, 'device-one-000001', '203.0.113.1');
  await sendWith(env, 'device-two-000002', '203.0.113.2');
  const global = [...env.QUOTA.map.entries()].find(([k]) => k.startsWith('g:'));
  assert.equal(global[1], '1');
});
