/**
 * Node entry point for the LoadingAI free-tier gateway.
 *
 * Routes, quota rules and send tickets all live in `./index.js` — the same
 * module the Cloudflare Worker entry point uses. This file is only transport:
 * it adapts a Node HTTP request into a web `Request`, runs that handler, and
 * streams the result back out.
 *
 * Production binds to loopback and is published by `cloudflared` (see
 * `deploy/`), so the VM needs no inbound ports and the upstream key never
 * leaves the box.
 */

import { createServer } from 'node:http';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { realpathSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import worker from './index.js';
import { openQuotaStore } from './sqlite-kv.js';

const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_PORT = 8787;
const DEFAULT_QUOTA_DB = 'quota.db';

/** Cloudflare caps request bodies well below this; the check is a backstop. */
const DEFAULT_MAX_BODY_BYTES = 16 * 1024 * 1024;

/** How long an in-flight response may finish after a shutdown signal. */
const GRACE_MS = 5_000;

/** Headers that describe this hop only, plus the ones the adapter rewrites. */
const STRIP_HEADERS = new Set([
  'connection',
  'keep-alive',
  'transfer-encoding',
  'upgrade',
  'te',
  'trailer',
  'proxy-connection',
  'proxy-authorization',
  'host',
  'content-length',
]);

const log = (message) => console.log(`[loadingai-gateway] ${message}`);

function toWebHeaders(nodeHeaders) {
  const headers = new Headers();
  for (const [name, value] of Object.entries(nodeHeaders)) {
    if (STRIP_HEADERS.has(name)) continue;
    for (const entry of Array.isArray(value) ? value : [value]) {
      if (entry !== undefined) headers.append(name, entry);
    }
  }
  return headers;
}

async function toWebRequest(request, origin, maxBodyBytes) {
  const method = (request.method || 'GET').toUpperCase();
  const headers = toWebHeaders(request.headers);

  const declared = Number(request.headers['content-length']);
  if (Number.isFinite(declared) && declared > maxBodyBytes) {
    return { tooLarge: true };
  }

  const init = { method, headers };
  if (method !== 'GET' && method !== 'HEAD') {
    // `duplex: half` is required whenever a request carries a stream body.
    init.body = Readable.toWeb(request);
    init.duplex = 'half';
  }

  return { request: new Request(new URL(request.url || '/', origin), init) };
}

async function sendWebResponse(nodeResponse, response) {
  nodeResponse.writeHead(response.status, Object.fromEntries(response.headers));
  if (!response.body) {
    nodeResponse.end();
    return;
  }

  const body = Readable.fromWeb(response.body);
  // A client that hangs up (or a Cloudflare retry) must not leak the stream.
  nodeResponse.on('close', () => body.destroy());
  try {
    await pipeline(body, nodeResponse);
  } catch {
    // Mid-stream disconnect: nothing to report and nowhere to report it.
  }
}

/** Builds the Node request listener. Exported so tests can drive it directly. */
export function createRequestListener(env, options = {}) {
  const maxBodyBytes = options.maxBodyBytes ?? DEFAULT_MAX_BODY_BYTES;

  return async function handle(request, nodeResponse) {
    const origin = `http://${request.headers.host || `${DEFAULT_HOST}:${DEFAULT_PORT}`}`;
    try {
      const adapted = await toWebRequest(request, origin, maxBodyBytes);
      if (adapted.tooLarge) {
        await sendWebResponse(
          nodeResponse,
          new Response(JSON.stringify({ error: { message: 'Request body is too large.', code: 'too_large' } }), {
            status: 413,
            headers: { 'content-type': 'application/json; charset=utf-8' },
          }),
        );
        return;
      }
      await sendWebResponse(nodeResponse, await worker.fetch(adapted.request, env));
    } catch (error) {
      log(`request failed: ${error?.message ?? error}`);
      if (nodeResponse.headersSent) {
        nodeResponse.destroy();
        return;
      }
      await sendWebResponse(
        nodeResponse,
        new Response(JSON.stringify({ error: { message: 'Gateway failed to handle the request.', code: 'gateway_error' } }), {
          status: 500,
          headers: { 'content-type': 'application/json; charset=utf-8' },
        }),
      );
    }
  };
}

/**
 * Starts the gateway. Returns the `http.Server`, the counter store, and the
 * address it actually bound (useful when the port is 0 in tests).
 */
export function startGateway(options = {}) {
  const env = options.env ?? process.env;
  const host = options.host ?? env.HOST ?? DEFAULT_HOST;
  const port = options.port ?? (Number.parseInt(env.PORT ?? '', 10) || DEFAULT_PORT);
  const store = options.store ?? openQuotaStore(env.QUOTA_DB || DEFAULT_QUOTA_DB);

  const server = createServer(createRequestListener({ ...env, QUOTA: store }, options));
  // Long agent runs stream for a while; the default socket timeout would cut them.
  server.timeout = 0;
  server.headersTimeout = 20_000;
  server.requestTimeout = 60_000;
  server.keepAliveTimeout = 70_000;

  return {
    server,
    store,
    async listen() {
      await new Promise((resolve_, reject) => {
        server.once('error', reject);
        server.listen(port, host, resolve_);
      });
      return server.address();
    },
    async close() {
      if (this._closing) return this._closing;
      this._closing = (async () => {
        await new Promise((resolve_) => {
          server.close(resolve_);
          // Idle keep-alive sockets would otherwise hold the shutdown open.
          server.closeIdleConnections();
          const force = setTimeout(() => server.closeAllConnections(), GRACE_MS);
          force.unref();
          server.once('close', () => clearTimeout(force));
        });
        store.close();
      })();
      return this._closing;
    },
  };
}

function announced(env) {
  const problems = [];
  if (!env.MINIMAX_API_KEY) {
    problems.push('MINIMAX_API_KEY is not set: chat and quota calls will answer 500 gateway_misconfigured.');
  }
  if (!env.TICKET_SECRET) {
    problems.push('TICKET_SECRET is not set: quota and chat will refuse with 500 gateway_misconfigured. Generate one with: npm run secret');
  }
  if (!env.LOADINGAI_CLIENT_TOKEN) {
    problems.push('LOADINGAI_CLIENT_TOKEN is not set: /v1/* will require the compiled default add-in token.');
  }
  return problems;
}

async function main() {
  const gateway = startGateway();
  const address = await gateway.listen();
  const shown = typeof address === 'object' && address ? `${address.address}:${address.port}` : address;

  log(`listening on ${shown} (quota db: ${process.env.QUOTA_DB || DEFAULT_QUOTA_DB})`);
  log(`daily limit: ${process.env.DAILY_LIMIT || '100'} per device per UTC day`);
  for (const problem of announced(process.env)) log(`WARNING: ${problem}`);

  const stop = async (signal) => {
    log(`${signal} received, shutting down`);
    await gateway.close();
    process.exit(0);
  };
  process.on('SIGTERM', () => void stop('SIGTERM'));
  process.on('SIGINT', () => void stop('SIGINT'));
}

function isMain() {
  const entry = process.argv[1];
  if (!entry) return false;
  try {
    return realpathSync(entry) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return resolve(entry) === fileURLToPath(import.meta.url);
  }
}

if (isMain()) {
  await main();
}
