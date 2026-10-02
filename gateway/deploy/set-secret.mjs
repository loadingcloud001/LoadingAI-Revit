/**
 * Stores the upstream MiniMax key (and the add-in client token) in
 * /etc/loadingai-gateway.env, then restarts the gateway and proves the whole
 * path end to end.
 *
 *   sudo node /opt/loadingai-gateway/deploy/set-secret.mjs            # set/rotate
 *   sudo node /opt/loadingai-gateway/deploy/set-secret.mjs --check    # status only
 *
 * The key is typed at a hidden prompt on the operator's own terminal, so it
 * never reaches a shell history, a log, or a chat transcript. This script
 * prints lengths and status codes, never secret material.
 */
import { readFileSync, writeFileSync, statSync, chmodSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const ENV_FILE = process.env.LOADINGAI_ENV_FILE || '/etc/loadingai-gateway.env';
const UNIT = process.env.LOADINGAI_UNIT || 'loadingai-gateway';
const ORIGIN = process.env.LOADINGAI_ORIGIN || 'http://127.0.0.1:8787';

/** The compiled default in src/index.js and BuiltinQuota.ClientToken. */
const DEFAULT_CLIENT_TOKEN = 'loadingai-addin-v1';

const say = (message) => console.log(message);

function die(message) {
  console.error(message);
  process.exit(1);
}

function readEnv() {
  try {
    return readFileSync(ENV_FILE, 'utf8');
  } catch (error) {
    die(`cannot read ${ENV_FILE}: ${error.message}`);
  }
}

function parseEnv(text) {
  const values = {};
  for (const line of text.split('\n')) {
    const match = /^([A-Z0-9_]+)=(.*)$/.exec(line.trim());
    if (match) values[match[1]] = match[2];
  }
  return values;
}

function setEnvValue(text, name, value) {
  const line = `${name}=${value}`;
  const existing = new RegExp(`^${name}=.*$`, 'm');
  if (existing.test(text)) return text.replace(existing, line);
  const padded = text.endsWith('\n') ? text : `${text}\n`;
  return `${padded}${line}\n`;
}

function writeEnv(text) {
  writeFileSync(ENV_FILE, text, { mode: 0o600 });
  chmodSync(ENV_FILE, 0o600);
}

/** Reads one line without echoing it back to the terminal. */
async function promptHidden(label) {
  const stdin = process.stdin;
  if (!stdin.isTTY) {
    die('stdin is not a terminal: run this over `ssh -t` so the key is not echoed.');
  }

  return await new Promise((settle, reject) => {
    const wasRaw = stdin.isRaw;
    stdin.setRawMode(true);
    stdin.resume();
    process.stdout.write(label);

    let value = '';
    const done = (fn, arg) => {
      stdin.setRawMode(wasRaw);
      stdin.pause();
      stdin.removeListener('data', onData);
      process.stdout.write('\n');
      fn(arg);
    };

    const onData = (chunk) => {
      for (const ch of chunk.toString('utf8')) {
        if (ch === '\r' || ch === '\n') return done(settle, value);
        if (ch === '\u0003') return done(reject, new Error('cancelled'));
        if (ch === '\u007f' || ch === '\b') value = value.slice(0, -1);
        else value += ch;
      }
    };

    stdin.on('data', onData);
  });
}

/** Rejects the shapes that mean a paste went wrong, and nothing more. */
function validateKey(key) {
  if (!key) return 'nothing was entered';
  if (/\s/.test(key)) return 'the value contains whitespace or a newline';
  if (!/^[\x21-\x7e]+$/.test(key)) return 'the value has non-printable characters';
  if (key.length < 16) return `only ${key.length} characters, which is too short to be a MiniMax key`;
  if (key.length > 512) return `${key.length} characters, which is longer than any MiniMax key`;
  if (/^(your|paste|changeme|xxx)/i.test(key)) return 'that looks like a placeholder';
  return null;
}

function systemctl(...args) {
  return execFileSync('systemctl', args, { encoding: 'utf8' });
}

async function health() {
  try {
    const response = await fetch(`${ORIGIN}/health`, { signal: AbortSignal.timeout(5_000) });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

/** Uses a throwaway device so the operator's real allowance is untouched. */
async function proveChat(clientToken) {
  const device = randomUUID();
  const base = { 'x-loadingai-device': device, 'x-loadingai-client': clientToken };

  let ticket;
  try {
    const quota = await fetch(`${ORIGIN}/v1/quota`, {
      method: 'POST',
      headers: base,
      signal: AbortSignal.timeout(15_000),
    });
    const body = await quota.json().catch(() => ({}));
    if (!quota.ok) return `quota answered HTTP ${quota.status}: ${body?.error?.message ?? ''}`;
    ticket = body.ticket;
    if (!ticket) return 'quota answered without a ticket';
  } catch (error) {
    return `quota request failed: ${error.message}`;
  }

  try {
    const chat = await fetch(`${ORIGIN}/v1/chat/completions`, {
      method: 'POST',
      headers: { ...base, 'x-loadingai-ticket': ticket, 'content-type': 'application/json' },
      body: JSON.stringify({
        messages: [{ role: 'user', content: 'Reply with the single word: ok' }],
        // A reasoning model spends part of the budget thinking before it writes any
        // visible content, so a ceiling of 8 came back with content:"" and made a
        // healthy upstream look broken. The add-in sets no max_tokens at all, so the
        // probe only needs a ceiling loose enough to let it think and then answer.
        max_tokens: 1024,
        stream: false,
      }),
      signal: AbortSignal.timeout(60_000),
    });
    const text = await chat.text();
    if (chat.ok) {
      const parsed = JSON.parse(text);
      const reply = parsed?.choices?.[0]?.message?.content?.trim();
      // Explicit ok flag, never a prefix match on the message: an empty reply must not
      // be able to pass as success by matching the same leading words as a good one.
      return reply
        ? { ok: true, message: `upstream answered: "${reply.slice(0, 60)}"` }
        : { ok: false, message: 'upstream answered without content (no visible reply to show)' };
    }
    return { ok: false, message: `chat answered HTTP ${chat.status}: ${text.slice(0, 300)}` };
  } catch (error) {
    return { ok: false, message: `chat request failed: ${error.message}` };
  }
}

async function check() {
  const text = readEnv();
  const env = parseEnv(text);
  const mode = (statSync(ENV_FILE).mode & 0o777).toString(8);

  say(`env file    ${ENV_FILE} (mode ${mode})`);
  say(`unit        ${systemctl('is-active', UNIT).trim()}`);
  say(`key         ${env.MINIMAX_API_KEY ? `set (${env.MINIMAX_API_KEY.length} chars)` : 'MISSING'}`);
  say(`ticket      ${env.TICKET_SECRET ? `set (${env.TICKET_SECRET.length} chars)` : 'MISSING'}`);
  say(`client gate ${env.LOADINGAI_CLIENT_TOKEN ? 'explicit' : 'compiled default'}`);
  say(`model       ${env.MINIMAX_MODEL || '(default)'} via ${env.MINIMAX_BASE_URL || '(default)'}`);

  const up = await health();
  say(`/health     ${up ? JSON.stringify(up) : 'no response'}`);

  const token = env.LOADINGAI_CLIENT_TOKEN || DEFAULT_CLIENT_TOKEN;
  const probe = await proveChat(token);
  say(`chat probe  ${probe.message}`);
  return env.MINIMAX_API_KEY && probe.ok ? 0 : 1;
}

async function setSecret() {
  if (process.getuid && process.getuid() !== 0) die('run me with sudo: the env file is root-owned.');

  let text = readEnv();
  const before = parseEnv(text);

  const key = (await promptHidden('MiniMax API key (hidden, not echoed): ')).trim();
  const problem = validateKey(key);
  if (problem) die(`refusing to store that: ${problem}`);
  if (key === before.MINIMAX_API_KEY) {
    say('that is the key already stored; nothing changed.');
  } else {
    text = setEnvValue(text, 'MINIMAX_API_KEY', key);
    say(`key replaced (${key.length} chars, value never printed)`);
  }

  // Pin the add-in gate explicitly so the file is self-describing.
  if (!before.LOADINGAI_CLIENT_TOKEN) {
    text = setEnvValue(text, 'LOADINGAI_CLIENT_TOKEN', DEFAULT_CLIENT_TOKEN);
    say(`client token written as the compiled default (${DEFAULT_CLIENT_TOKEN.length} chars)`);
  }
  writeEnv(text);

  say(`restarting ${UNIT}`);
  systemctl('restart', UNIT);

  let up = null;
  for (let attempt = 0; attempt < 20 && !up; attempt += 1) {
    await new Promise((r) => setTimeout(r, 500));
    up = await health();
  }
  if (!up) {
    execFileSync('journalctl', ['-u', UNIT, '-n', '30', '--no-pager'], { stdio: 'inherit' });
    die('the gateway did not come back up after the restart.');
  }
  say(`/health     ${JSON.stringify(up)}`);

  const token = parseEnv(readEnv()).LOADINGAI_CLIENT_TOKEN || DEFAULT_CLIENT_TOKEN;
  const probe = await proveChat(token);
  say(`chat probe  ${probe.message}`);
  say(probe.ok ? 'end to end OK' : 'still failing: see the probe above');
  process.exit(probe.ok ? 0 : 1);
}

if (process.argv.includes('--check')) {
  process.exit(await check());
} else {
  await setSecret();
}
