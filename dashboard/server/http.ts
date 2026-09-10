import { resolve, sep } from 'node:path';
import type { TelemetryStore } from './store';

export function handler(store: TelemetryStore, publicDirectory: string) {
  const root = resolve(publicDirectory);
  return async (request: Request): Promise<Response> => {
    const path = new URL(request.url).pathname;
    if (request.method !== 'GET' && request.method !== 'HEAD') return new Response('Method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } });
    const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
    if (path === '/healthz') return Response.json({ service: 'valheim-boosted', status: 'ok' }, { headers });
    if (path === '/api/metrics') return Response.json(store.view(), { headers });
    if (path.startsWith('/api/')) return new Response('Not found', { status: 404, headers });
    let decoded: string;
    try { decoded = decodeURIComponent(path); } catch { return new Response('Bad path', { status: 400 }); }
    const target = resolve(root, '.' + (decoded === '/' ? '/index.html' : decoded));
    if (!target.startsWith(root + sep)) return new Response('Not found', { status: 404 });
    const file = Bun.file(target);
    if (!await file.exists()) return new Response('Not found', { status: 404 });
    return new Response(request.method === 'HEAD' ? null : file, { headers: { ...headers, 'Content-Type': file.type } });
  };
}
