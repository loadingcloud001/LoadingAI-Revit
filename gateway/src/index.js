/**
 * LoadingAI free-tier gateway.
 *
 * Routes
 *   GET  /health                 liveness probe (no add-in header)
 *   GET  /v1/quota               remaining requests today (no consumption)
 *   POST /v1/quota               consume one request, returns a short-lived ticket
 *   GET  /v1/models              model list (no upstream call, no quota)
 *   POST /v1/chat/completions    requires a valid ticket, proxies to the upstream model
 *
 * /v1/* requires X-LoadingAI-Client matching LOADINGAI_CLIENT_TOKEN so the
 * public hostname is not an anonymous API. The upstream API key lives only on
 * the server (MINIMAX_API_KEY). The add-in ships no key: it asks for a ticket,
 * then sends that ticket instead. This same handler runs as a Node process in
 * production and as a fetch handler in tests.
 */

import {
  DEFAULT_LIMIT,
  DEFAULT_TICKET_TTL_SECONDS,
  consume,
  isValidDevice,
  issueTicket,
  remaining,
  ticketDevice,
} from './quota.js';

const DEVICE_HEADER = 'x-loadingai-device';
const TICKET_HEADER = 'x-loadingai-ticket';
const CLIENT_HEADER = 'x-loadingai-client';
const DEFAULT_CLIENT_TOKEN = 'loadingai-addin-v1';

/** The only request fields the add-in sends; anything else never reaches the paid upstream. */
const FORWARDED_FIELDS = ['messages', 'tools', 'tool_choice', 'temperature', 'stream'];
const DEFAULT_MAX_OUTPUT_TOKENS = 8192;
// Below the add-in's own 120 s non-stream timeout, so the gateway's clear 504 arrives before the client gives up.
const DEFAULT_UPSTREAM_TIMEOUT_MS = 100_000;
const MAX_UPSTREAM_TIMEOUT_MS = 600_000;
const MAX_OUTPUT_TOKENS_LIMIT = 1_000_000;

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' },
  });
}

function fail(status, message, code, extra = {}) {
  return json({ error: { message, code, type: status === 429 ? 'rate_limit_error' : 'invalid_request_error' }, ...extra }, status);
}

function dailyLimit(env) {
  const parsed = Number.parseInt(env.DAILY_LIMIT ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_LIMIT;
}

/** Whole decimal digits only (no '1.5', '1e9' or '120000ms'), clamped to `max` so a timer delay cannot overflow. */
function positiveInt(raw, fallback, max = Number.MAX_SAFE_INTEGER) {
  const text = String(raw ?? '').trim();
  if (!/^\d+$/.test(text)) return fallback;
  const parsed = Number(text);
  return parsed > 0 ? Math.min(parsed, max) : fallback;
}

export function upstreamBody(payload, env) {
  const body = {};
  for (const key of FORWARDED_FIELDS) {
    if (payload[key] !== undefined) body[key] = payload[key];
  }
  const asked = payload.max_tokens;
  if (typeof asked === 'number' && Number.isFinite(asked) && asked >= 1) {
    body.max_tokens = Math.min(Math.floor(asked), positiveInt(env.MAX_OUTPUT_TOKENS, DEFAULT_MAX_OUTPUT_TOKENS, MAX_OUTPUT_TOKENS_LIMIT));
  }
  body.model = env.MINIMAX_MODEL || 'MiniMax-M3.1-Flash-Preview';
  return body;
}

function ticketTtl(env) {
  const parsed = Number.parseInt(env.TICKET_TTL_SECONDS ?? '', 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_TICKET_TTL_SECONDS;
}

/**
 * Signs tickets. Never leaves the server, so clients cannot mint one.
 *
 * TICKET_SECRET is required and is never derived from MINIMAX_API_KEY. Falling
 * back to the upstream key coupled two independent roles: rotating either one
 * invalidated the other, and any path that leaked the HMAC key leaked the
 * credential that spends money. A missing secret fails closed.
 */
function ticketSecret(env) {
  return env.TICKET_SECRET || '';
}

function misconfigured() {
  return fail(500, 'Gateway is not configured: MINIMAX_API_KEY is missing.', 'gateway_misconfigured');
}

function limitMessage(limit, resetUtc) {
  return `LoadingAI daily limit reached (${limit} requests/day). Try again after ${resetUtc}, or add your own API key in Settings.`;
}

function clientToken(env) {
  const configured = env.LOADINGAI_CLIENT_TOKEN;
  return configured == null || configured === '' ? DEFAULT_CLIENT_TOKEN : configured;
}

function requireAddin(request, env) {
  const presented = request.headers.get(CLIENT_HEADER);
  if (!presented || presented !== clientToken(env)) {
    return fail(401, 'LoadingAI add-in identity missing. Use the LoadingAI add-in.', 'bad_client');
  }
  return null;
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname.replace(/\/+$/, '') || '/';
    switch (path) {
      case '/':
      case '/health':
        return json({ ok: true, service: 'loadingai-gateway' });
      case '/v1/quota':
      case '/v1/models':
      case '/v1/chat/completions': {
        const denied = requireAddin(request, env);
        if (denied) return denied;
        if (path === '/v1/quota') return handleQuota(request, env);
        if (path === '/v1/models') return handleModels(request, env);
        return handleChat(request, env);
      }
      default:
        return fail(404, `No route for ${path}.`, 'not_found');
    }
  },
};

async function handleQuota(request, env) {
  if (request.method !== 'GET' && request.method !== 'POST') {
    return fail(405, 'Use GET or POST.', 'method_not_allowed');
  }

  const device = request.headers.get(DEVICE_HEADER);
  if (!isValidDevice(device)) {
    return fail(400, `Missing or malformed ${DEVICE_HEADER}.`, 'bad_device');
  }

  const limit = dailyLimit(env);

  if (request.method === 'GET') {
    const status = await remaining(env.QUOTA, device, limit);
    return json(status);
  }

  // Both halves of the configuration are required before a single request is
  // consumed. Issuing a ticket on a gateway that cannot serve the chat would
  // burn the user's daily allowance for nothing.
  if (!env.MINIMAX_API_KEY) return misconfigured();
  const secret = ticketSecret(env);
  if (!secret) return misconfigured();

  const status = await consume(env.QUOTA, device, limit);
  if (!status.allowed) {
    return fail(429, limitMessage(limit, status.resetUtc), 'daily_limit', {
      limit: status.limit,
      used: status.used,
      remaining: 0,
      resetUtc: status.resetUtc,
    });
  }

  const ttl = ticketTtl(env);
  const ticket = await issueTicket(secret, device, ttl);
  return json({ ...status, ticket, ticketTtlSeconds: ttl });
}

async function handleModels(request, env) {
  if (request.method !== 'GET') return fail(405, 'Use GET.', 'method_not_allowed');
  // Answered locally so a model probe never spends upstream credit.
  return json({
    object: 'list',
    data: [{ id: env.MINIMAX_MODEL || 'MiniMax-M3.1-Flash-Preview', object: 'model', owned_by: 'LoadingAI' }],
  });
}

async function handleChat(request, env) {
  if (request.method !== 'POST') return fail(405, 'Use POST.', 'method_not_allowed');

  const device = request.headers.get(DEVICE_HEADER);
  if (!isValidDevice(device)) {
    return fail(400, `Missing or malformed ${DEVICE_HEADER}.`, 'bad_device');
  }

  // Checked before the ticket so a keyless deployment fails everywhere the same way.
  if (!env.MINIMAX_API_KEY) return misconfigured();
  const apiKey = env.MINIMAX_API_KEY;

  // No ticket secret means every ticket would verify against an empty HMAC key.
  // Fail closed rather than accepting it.
  const secret = ticketSecret(env);
  if (!secret) return misconfigured();

  const owner = await ticketDevice(secret, request.headers.get(TICKET_HEADER));
  if (!owner) {
    return fail(401, 'LoadingAI ticket missing or expired. Send your message again.', 'no_ticket');
  }
  if (owner !== device) {
    return fail(401, 'This ticket belongs to a different device.', 'bad_ticket');
  }

  let payload;
  try {
    payload = await request.json();
  } catch {
    return fail(400, 'Request body must be JSON.', 'bad_json');
  }
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
    return fail(400, 'Request body must be a JSON object.', 'bad_json');
  }

  // The callers' Authorization header, if any, is deliberately dropped: the
  // upstream key is the only credential that reaches the model.
  const base = (env.MINIMAX_BASE_URL || 'https://api.minimaxi.com/v1').replace(/\/+$/, '');
  // Bounds the wait for response headers only; a long stream that is flowing is never cut.
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), positiveInt(env.UPSTREAM_TIMEOUT_MS, DEFAULT_UPSTREAM_TIMEOUT_MS, MAX_UPSTREAM_TIMEOUT_MS));
  let upstream;
  try {
    upstream = await fetch(`${base}/chat/completions`, {
      method: 'POST',
      headers: {
        authorization: `Bearer ${apiKey}`,
        'content-type': 'application/json',
        accept: request.headers.get('accept') || 'application/json',
      },
      body: JSON.stringify(upstreamBody(payload, env)),
      signal: controller.signal,
    });
  } catch (error) {
    if (controller.signal.aborted) {
      return fail(504, 'The model did not answer in time. Send your message again.', 'upstream_timeout');
    }
    throw error;
  } finally {
    clearTimeout(timer);
  }

  return new Response(upstream.body, {
    status: upstream.status,
    headers: {
      'content-type': upstream.headers.get('content-type') || 'application/json',
      'cache-control': 'no-store',
    },
  });
}
