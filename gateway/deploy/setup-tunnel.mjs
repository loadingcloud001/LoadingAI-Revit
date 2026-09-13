#!/usr/bin/env node
// Points gateway.loadingtechnology.app at the local gateway through a Cloudflare
// tunnel, so the VM never needs an inbound port or a public IP.
//
//   sudo node gateway/deploy/setup-tunnel.mjs
//   sudo node gateway/deploy/setup-tunnel.mjs --hostname gateway.example.com --zone example.com
//
// The API token comes from CF_API_TOKEN, from --token-file, or from a hidden
// prompt. It is never echoed, logged, or written to disk.
//
// Token scopes: Zone:Read, DNS:Edit, Account:Cloudflare Tunnel:Edit.
//
// Idempotent: reuses the tunnel and the DNS record when they already exist, so
// re-running it after a credential rotation is safe.

import { readFileSync, writeFileSync, mkdirSync, chmodSync, existsSync, copyFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const API = 'https://api.cloudflare.com/client/v4';
const TOKEN_FILE = '/etc/cloudflared/loadingai-tunnel.env';
const UNIT_PATH = '/etc/systemd/system/cloudflared.service';
const UNIT_SOURCE = '/opt/loadingai-gateway/deploy/cloudflared.service';

const args = parseArgs(process.argv.slice(2));
const zoneName = args.zone ?? 'loadingtechnology.app';
const hostname = args.hostname ?? 'gateway.loadingtechnology.app';
const service = args.service ?? 'http://127.0.0.1:8787';
const tunnelName = args.tunnel ?? 'loadingai-gateway';

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i];
    if (!key.startsWith('--')) continue;
    const name = key.slice(2);
    if (name === 'help') {
      console.log(
        'usage: sudo node setup-tunnel.mjs [--zone loadingtechnology.app] ' +
          '[--hostname gateway.loadingtechnology.app] [--service http://127.0.0.1:8787] ' +
          '[--tunnel loadingai-gateway] [--token-file /path/to/token]',
      );
      process.exit(0);
    }
    out[name] = argv[i + 1];
    i += 1;
  }
  return out;
}

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

async function cf(path, init = {}) {
  // Cloudflare returns 6003 "Invalid request headers" if a GET carries
  // Content-Type, so only send it when there is a body.
  const headers = { Authorization: `Bearer ${token}`, ...(init.headers ?? {}) };
  if (init.body) headers['Content-Type'] = 'application/json';
  const res = await fetch(`${API}${path}`, { ...init, headers });
  const body = await res.json().catch(() => null);
  if (!body?.success) {
    const detail = (body?.errors ?? []).map((e) => `${e.code} ${e.message}`).join('; ');
    let hint = '';
    if (res.status === 403 && path.includes('/cfd_tunnel')) {
      hint =
        '\n  token is missing Account-level tunnel write. Recreate it with:\n' +
        '    Account resources  →  this account  →  Cloudflare Tunnel:Edit\n' +
        '    Zone resources     →  loadingtechnology.app  →  Zone:Read, DNS:Edit\n' +
        '  (dashboard names it Cloudflare Tunnel Edit / Cloudflare One Connector: cloudflared Write)';
    }
    fail(`${init.method ?? 'GET'} ${path} -> ${res.status} ${detail || 'no response body'}${hint}`);
  }
  return body.result;
}

// A prompt with echo disabled: the token only ever exists in memory.
async function promptSecret(label) {
  if (!process.stdin.isTTY) fail('no TTY for a prompt; pass --token-file or CF_API_TOKEN instead');
  process.stdout.write(label);
  return new Promise((resolve) => {
    const stdin = process.stdin;
    stdin.setRawMode(true);
    stdin.resume();
    stdin.setEncoding('utf8');
    let buf = '';
    const finish = (value, code) => {
      stdin.setRawMode(false);
      stdin.pause();
      stdin.off('data', onData);
      process.stdout.write('\n');
      if (code) process.exit(code);
      resolve(value);
    };
    const onData = (chunk) => {
      for (const ch of chunk) {
        if (ch === '\r' || ch === '\n') return finish(buf.trim());
        if (ch === '\u0003') return finish('', 130);
        if (ch === '\u007f' || ch === '\b') {
          buf = buf.slice(0, -1);
          continue;
        }
        buf += ch;
      }
    };
    stdin.on('data', onData);
  });
}

async function readToken() {
  if (process.env.CF_API_TOKEN) return process.env.CF_API_TOKEN.trim();
  if (args['token-file']) return readFileSync(args['token-file'], 'utf8').trim();
  return promptSecret('Cloudflare API token (hidden): ');
}

function systemctl(...argv) {
  try {
    return execFileSync('systemctl', argv, { encoding: 'utf8' }).trim();
  } catch (err) {
    return `${err.stdout ?? ''}`.trim();
  }
}

function systemctlStrict(...argv) {
  return execFileSync('systemctl', argv, { encoding: 'utf8' }).trim();
}

async function main() {
  if (process.getuid?.() !== 0) fail('run me as root: I write /etc/cloudflared and a systemd unit');
  if (!existsSync('/usr/bin/cloudflared')) fail('cloudflared is not installed at /usr/bin/cloudflared');

  token = await readToken();
  if (!token) fail('no API token given');

  const verify = await cf('/user/tokens/verify');
  console.log(`token ok: ${verify.status}`);

  const zones = await cf(`/zones?name=${encodeURIComponent(zoneName)}`);
  const zone = zones[0];
  if (!zone) fail(`zone ${zoneName} is not visible to this token (needs Zone:Read)`);
  const accountId = zone.account.id;
  console.log(`zone ${zone.name} ${zone.id} (account ${accountId})`);

  const existing = await cf(`/accounts/${accountId}/cfd_tunnel?name=${encodeURIComponent(tunnelName)}&is_deleted=false`);
  let tunnel = existing[0];
  if (tunnel) {
    console.log(`tunnel ${tunnel.name} ${tunnel.id} already exists, reusing it`);
  } else {
    tunnel = await cf(`/accounts/${accountId}/cfd_tunnel`, {
      method: 'POST',
      body: JSON.stringify({ name: tunnelName, config_src: 'cloudflare' }),
    });
    console.log(`created tunnel ${tunnel.name} ${tunnel.id}`);
  }

  // Remote-managed ingress: same rules as deploy/tunnel-config.json.
  await cf(`/accounts/${accountId}/cfd_tunnel/${tunnel.id}/configurations`, {
    method: 'PUT',
    body: JSON.stringify({
      config: {
        ingress: [
          { hostname, service, originRequest: { connectTimeout: 10 } },
          { service: 'http_status:404' },
        ],
      },
    }),
  });
  console.log(`ingress: ${hostname} -> ${service}, everything else 404`);

  const cname = `${tunnel.id}.cfargotunnel.com`;
  const records = await cf(`/zones/${zone.id}/dns_records?type=CNAME&name=${encodeURIComponent(hostname)}`);
  const record = records[0];
  if (!record) {
    const created = await cf(`/zones/${zone.id}/dns_records`, {
      method: 'POST',
      body: JSON.stringify({ type: 'CNAME', name: hostname, content: cname, proxied: true, ttl: 1 }),
    });
    console.log(`created DNS ${created.name} -> ${cname} (proxied)`);
  } else if (record.content !== cname || !record.proxied) {
    await cf(`/zones/${zone.id}/dns_records/${record.id}`, {
      method: 'PUT',
      body: JSON.stringify({ type: 'CNAME', name: hostname, content: cname, proxied: true, ttl: 1 }),
    });
    console.log(`updated DNS ${hostname} -> ${cname} (proxied)`);
  } else {
    console.log(`DNS ${hostname} -> ${cname} (proxied) already correct`);
  }

  const connectorToken = await cf(`/accounts/${accountId}/cfd_tunnel/${tunnel.id}/token`);

  mkdirSync('/etc/cloudflared', { recursive: true, mode: 0o755 });
  writeFileSync(TOKEN_FILE, `TUNNEL_TOKEN=${connectorToken}\n`, { mode: 0o600 });
  chmodSync(TOKEN_FILE, 0o600);
  console.log(`wrote ${TOKEN_FILE} (mode 600, connector token not printed)`);

  if (!existsSync(UNIT_PATH)) {
    if (!existsSync(UNIT_SOURCE)) fail(`no ${UNIT_PATH} and no ${UNIT_SOURCE} to install from`);
    copyFileSync(UNIT_SOURCE, UNIT_PATH);
    chmodSync(UNIT_PATH, 0o644);
    console.log(`installed ${UNIT_PATH}`);
  }
  execFileSync('systemctl', ['daemon-reload']);
  systemctlStrict('enable', '--now', 'cloudflared');

  for (let i = 0; i < 10; i += 1) {
    await new Promise((r) => setTimeout(r, 1000));
    if (systemctl('is-active', 'cloudflared') === 'active') break;
  }
  const state = systemctl('is-active', 'cloudflared');
  console.log(`cloudflared: ${state}`);
  if (state !== 'active') {
    execFileSync('journalctl', ['-u', 'cloudflared', '-n', '20', '--no-pager'], { stdio: 'inherit' });
    fail('cloudflared did not stay up');
  }

  console.log(`\ntunnel up. Verify from anywhere:\n  curl -s https://${hostname}/health`);
}

let token = '';
await main();
