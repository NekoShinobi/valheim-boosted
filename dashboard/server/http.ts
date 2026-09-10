import { resolve, sep } from 'node:path';
import type { TelemetryStore } from './store';
import type { ReportRepository } from './reports';
import type { HistoryReader } from './history-service';
import { historyMetrics, type HistoryMetric, type PlayerSeriesTarget } from '../shared/history';
import { validPlayerId } from '../shared/client-telemetry';

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

export function handler(store: TelemetryStore, publicDirectory: string, reports?: ReportRepository, history?: HistoryReader) {
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
      if (history) {
        try { draft.timeline = await history.archive(Date.parse(draft.startedAtUtc!), Date.parse(draft.endedAtUtc!) + 1, draft.source.processSession, draft.source.worldSession); }
        catch { draft.timelineError = 'History could not be archived; this report contains aggregates only.'; }
      }
      try { return Response.json(await reports.save(draft), { status: 201, headers }); }
      catch (error) { console.error('Report save failed:', error); return jsonError('Could not save report. Check reports-directory permissions, free space and the 1,000-report limit.', 503); }
    }
    if (request.method !== 'GET' && request.method !== 'HEAD') return new Response('Method not allowed', { status: 405, headers: { ...headers, Allow: writable ? 'GET, HEAD, POST' : 'GET, HEAD' } });
    if (path === '/healthz') return Response.json({ service: 'valheim-boosted', status: 'ok' }, { headers });
    if (path === '/api/metrics') return Response.json(store.view(), { headers });
    if (path === '/api/history' || path === '/api/history/series' || path === '/api/history/status' || path === '/api/history/sessions') {
      if (!history) return jsonError('History storage is unavailable.', 503);
      try {
        if (path === '/api/history/status') return Response.json(await history.status(), { headers });
        const fromMs = Number(url.searchParams.get('from')), toMs = Number(url.searchParams.get('to'));
        if (!url.searchParams.has('from') || !url.searchParams.has('to') || !Number.isFinite(fromMs) || !Number.isFinite(toMs) || fromMs < 0 || toMs <= fromMs || toMs - fromMs > 366 * 86400000) return jsonError('Supply a valid from/to time range in Unix milliseconds (at most 366 days).', 400);
        if (path.endsWith('/series')) {
          const series = await history.series(fromMs, toMs,url.searchParams.get('group') === 'player');
          return Response.json({ series: series.slice(0, 256), truncated: series.length > 256 }, { headers });
        }
        if (path.endsWith('/sessions')) {
          const players=(url.searchParams.get('players') ?? '').split(',').filter(Boolean);
          if (players.length>9 || !players.every(validPlayerId)) return jsonError('Invalid player IDs.',400);
          const sessions=await history.sessions(fromMs,toMs,players);
          return Response.json({sessions:sessions.slice(0,256),truncated:sessions.length>256},{headers});
        }
        const metric = url.searchParams.get('metric') ?? 'frameP95Ms';
        const points = Number(url.searchParams.get('points') ?? 900);
        const seriesIds = (url.searchParams.get('series') ?? '').split(',').filter(Boolean);
        const playerSeries=(url.searchParams.get('players') ?? '').split(',').filter(Boolean).map(p=>{const [playerId,kind,...extra]=p.split(':');return {playerId,kind:extra.length?'invalid':kind};});
        if (!historyMetrics.some(m => m.key === metric) || !Number.isInteger(points) || points < 10 || points > 1200 || seriesIds.length+playerSeries.length > 9 || !seriesIds.every(s => /^[a-f0-9]{32}$/.test(s))
          || !playerSeries.every(p=>validPlayerId(p.playerId)&&['client','connection'].includes(p.kind))) return jsonError('Invalid metric, series IDs or point limit.', 400);
        return Response.json(await history.query({ fromMs, toMs, seriesIds,playerSeries:playerSeries as PlayerSeriesTarget[], metric: metric as HistoryMetric, points }), { headers });
      } catch (e) { console.error('History request failed:', e); return jsonError('History storage could not complete the request.', 503); }
    }
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
    const target = resolve(root, '.' + (['/', '/reports', '/history'].includes(decoded) ? '/index.html' : decoded));
    if (!target.startsWith(root + sep)) return new Response('Not found', { status: 404 });
    const file = Bun.file(target);
    if (!await file.exists()) return new Response('Not found', { status: 404 });
    return new Response(request.method === 'HEAD' ? null : file, { headers: { ...headers, 'Content-Type': file.type } });
  };
}
