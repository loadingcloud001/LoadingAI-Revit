/**
 * LoadingAI free-tier gateway.
 *
 * Routes
 *   GET  /health                 liveness probe
 *   GET  /v1/quota               remaining requests today (no consumption)
 *   POST /v1/quota               consume one request, returns a short-lived ticket
 *   GET  /v1/models              model list (no upstream call, no quota)
 *   POST /v1/chat/completions    requires a valid ticket, proxies to the upstream model
 *
 * The upstream API key lives only here (Worker secret MINIMAX_API_KEY). The
 * add-in ships no key: it asks for a ticket, then sends that ticket instead.
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

function ticketTtl(env) {
  const parsed = Number.parseInt(env.TICKET_TTL_SECONDS ?? '', 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_TICKET_TTL_SECONDS;
}

/** Signs tickets. Never leaves the Worker, so clients cannot mint one. */
function ticketSecret(env) {
  return env.TICKET_SECRET || env.MINIMAX_API_KEY || '';
}

function misconfigured() {
  return fail(500, 'Gateway is not configured: MINIMAX_API_KEY is missing.', 'gateway_misconfigured');
}

function limitMessage(limit, resetUtc) {
  return `LoadingAI daily limit reached (${limit} requests/day). Try again after ${resetUtc}, or add your own API key in Settings.`;
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname.replace(/\/+$/, '') || '/';
    switch (path) {
      case '/':
      case '/health':
        return json({ ok: true, service: 'loadingai-gateway' });
      case '/v1/quota':
        return handleQuota(request, env);
      case '/v1/models':
        return handleModels(request, env);
      case '/v1/chat/completions':
        return handleChat(request, env);
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
    data: [{ id: env.MINIMAX_MODEL || 'MiniMax-M3', object: 'model', owned_by: 'LoadingAI' }],
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

  const owner = await ticketDevice(ticketSecret(env), request.headers.get(TICKET_HEADER));
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
  const upstream = await fetch(`${base}/chat/completions`, {
    method: 'POST',
    headers: {
      authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
      accept: request.headers.get('accept') || 'application/json',
    },
    body: JSON.stringify({ ...payload, model: env.MINIMAX_MODEL || 'MiniMax-M3' }),
  });

  return new Response(upstream.body, {
    status: upstream.status,
    headers: {
      'content-type': upstream.headers.get('content-type') || 'application/json',
      'cache-control': 'no-store',
    },
  });
}
