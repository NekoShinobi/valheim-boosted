import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import { HistoryDatabase } from './history-database';

let database: HistoryDatabase | undefined;
self.onmessage = async (event: MessageEvent) => {
  const { id, action, args } = event.data;
  try {
    let value: unknown;
    if (action === 'initialize') {
      await mkdir(dirname(args.path), { recursive: true });
      database = new HistoryDatabase(args.path, args.retentionDays);
      database.cleanup(); value = database.status();
    } else {
      if (!database) throw new Error('History database is unavailable');
      switch (action) {
        case 'ingest': database.ingest(args.snapshot, args.now); break;
        case 'query': value = database.query(args); break;
        case 'series': value = database.series(args.fromMs, args.toMs,undefined,args.groupPlayers); break;
        case 'sessions': value = database.sessions(args.fromMs,args.toMs,args.playerIds); break;
        case 'archive': value = database.archive(args.fromMs, args.toMs, args.processSession, args.worldSession); break;
        case 'status': value = database.status(); break;
        case 'cleanup': database.cleanup(); break;
        case 'close': database.close(); database = undefined; break;
        default: throw new Error('Unknown history operation');
      }
    }
    self.postMessage({ id, value });
  } catch (error) { self.postMessage({ id, error: error instanceof Error ? error.message : 'History operation failed' }); }
};
