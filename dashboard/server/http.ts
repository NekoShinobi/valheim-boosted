import { resolve, sep } from 'node:path';
import type { TelemetryStore } from './store';
import type { ReportRepository } from './reports';

async function jsonBody(request: Request): Promise<unknown> {
  const reader = request.body?.getReader();
  if (!reader) throw new Error('A JSON body is required.');
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.length;
      if (size > 4096) { await reader.cancel(); throw new Error('Request body exceeds 4 KiB.'); }
      chunks.push(value);
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } finally { reader.releaseLock(); }
}

export function handler(store: TelemetryStore, publicDirectory: string, reports?: ReportRepository) {
  const root = resolve(publicDirectory);
  return async (request: Request): Promise<Response> => {
    const url = new URL(request.url);
    const path = url.pathname;
    const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
    const jsonError = (message: string, status: number) => Response.json({ error: message }, { status, headers });
    const writable = path === '/api/reports' || path === '/api/recording/reset';
    if (request.method === 'POST' && writable) {
      const origin = request.headers.get('Origin');
      // Compare authority so TLS termination works when the proxy preserves Host.
      let originAllowed = !origin;
      try { if (origin) { const parsed = new URL(origin); originAllowed = ['http:', 'https:'].includes(parsed.protocol) && parsed.host === url.host; } } catch { /* Invalid origins are rejected. */ }
      if (!originAllowed || request.headers.get('Sec-Fetch-Site') === 'cross-site') return jsonError('Cross-origin changes are not allowed.', 403);
      if (request.headers.get('Content-Type')?.split(';')[0].trim() !== 'application/json') return jsonError('Use application/json.', 415);
      let body: unknown;
      try { body = await jsonBody(request); } catch { return jsonError('Expected a JSON object of at most 4 KiB.', 400); }
      if (!body || typeof body !== 'object' || Array.isArray(body)) return jsonError('Expected a JSON object.', 400);
      if (path === '/api/recording/reset') return Response.json(store.reset(), { headers });
      if (!reports) return jsonError('Report storage is unavailable.', 503);
      const name = (body as { name?: unknown }).name;
      if (typeof name !== 'string' || !name.trim() || name.trim().length > 100 || /[\u0000-\u001f\u007f]/.test(name)) return jsonError('Use a report name of 1–100 characters without control characters.', 400);
      let draft;
      try { draft = store.report(name.trim()); } catch { return jsonError('No complete, fresh telemetry windows recorded yet.', 409); }
      try { return Response.json(await reports.save(draft), { status: 201, headers }); }
      catch (error) { console.error('Report save failed:', error); return jsonError('Could not save report. Check reports-directory permissions, free space and the 1,000-report limit.', 503); }
    }
    if (request.method !== 'GET' && request.method !== 'HEAD') return new Response('Method not allowed', { status: 405, headers: { ...headers, Allow: writable ? 'GET, HEAD, POST' : 'GET, HEAD' } });
    if (path === '/healthz') return Response.json({ service: 'valheim-boosted', status: 'ok' }, { headers });
    if (path === '/api/metrics') return Response.json(store.view(), { headers });
    if (path === '/api/reports' || path.startsWith('/api/reports/')) {
      if (!reports) return jsonError('Report storage is unavailable.', 503);
      try {
        if (path === '/api/reports') return Response.json(await reports.list(), { headers });
        const match = /^\/api\/reports\/([^/]+)(\/download)?$/.exec(path);
        const report = match ? await reports.get(match[1]) : null;
        if (!report) return jsonError('Report not found.', 404);
        return new Response(request.method === 'HEAD' ? null : JSON.stringify(report, null, 2) + '\n', { headers: {
          ...headers, 'Content-Type': 'application/json',
          ...(match![2] ? { 'Content-Disposition': `attachment; filename="valheim-boosted-report-${report.id}.json"` } : {}),
        } });
      } catch (error) { console.error('Report read failed:', error); return jsonError('Could not read report storage. Check server logs.', 503); }
    }
    if (path.startsWith('/api/')) return new Response('Not found', { status: 404, headers });
    let decoded: string;
    try { decoded = decodeURIComponent(path); } catch { return new Response('Bad path', { status: 400 }); }
    const target = resolve(root, '.' + (decoded === '/' || decoded === '/reports' ? '/index.html' : decoded));
    if (!target.startsWith(root + sep)) return new Response('Not found', { status: 404 });
    const file = Bun.file(target);
    if (!await file.exists()) return new Response('Not found', { status: 404 });
    return new Response(request.method === 'HEAD' ? null : file, { headers: { ...headers, 'Content-Type': file.type } });
  };
}
