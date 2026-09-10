import { historyMetrics, type HistoryArchive, type HistoryMetric, type HistoryResult, type HistoryRow, type HistorySeries, type HistorySummary } from './history';

export function archiveSeries(a:HistoryArchive,groupPlayers:boolean,from=a.fromMs,to=a.toMs):HistorySeries[] {
  const matching = a.series.filter(s => s.firstMs <= to && s.lastMs >= from);
  if (!groupPlayers) return matching;
  const groups=new Map<string,HistorySeries>(),sessions=new Map<string,Set<string>>();
  for(const s of matching) {
    if (!s.playerId) { groups.set(s.id,s);continue; }
    const key=`${s.playerId}:${s.kind}`,prior=groups.get(key);
    const members=sessions.get(key) ?? new Set<string>();if(s.sessionId)members.add(s.sessionId);sessions.set(key,members);
    groups.set(key,{...s,id:prior?.id ?? s.id,grouped:true,sessionId:null,sessionCount:members.size,processSession:'multiple',worldSession:null,modBuildId:null,
      name:prior && prior.lastMs>s.lastMs?prior.name:s.name,firstMs:Math.min(prior?.firstMs ?? s.firstMs,s.firstMs),lastMs:Math.max(prior?.lastMs ?? s.lastMs,s.lastMs)});
  }
  return [...groups.values()].sort((a,b)=>b.lastMs-a.lastMs);
}

export function archiveHistory(a:HistoryArchive,chosen:HistorySeries[],key:HistoryMetric,start:number,end:number):HistoryResult {
  const index=historyMetrics.findIndex(m=>m.key===key),metric=historyMetrics[index];
  const combine=(rows:HistoryRow[]):HistorySummary=>{
    let value:number|null=null,total=0,weight=0;
    for(const r of rows) {
      const v=r.values[index];if(v==null)continue;
      const w=r.weights?.[index] ?? (metric.rollup==='frames'?r.values[historyMetrics.findIndex(m=>m.key==='frames')] ?? 0:metric.rollup==='fps'?r.observedMs ?? r.endMs-r.startMs:r.samples ?? 1);
      if(['mean','frames','fps'].includes(metric.rollup)){total+=v*w;weight+=w;value=weight?total/weight:null;}
      else value=value==null?v:metric.rollup==='sum'?value+v:metric.rollup==='max'?Math.max(value,v):Math.min(value,v);
    }
    const errors=rows.flatMap(r=>r.clockErrorMs==null?[]:[r.clockErrorMs]);
    return {value,samples:rows.reduce((n,r)=>n+(r.samples??1),0),observedMs:rows.reduce((n,r)=>n+(r.observedMs??r.endMs-r.startMs),0),clockErrorMs:errors.length?Math.max(...errors):null};
  };
  const result:HistoryResult={fromMs:start,toMs:end,stepMs:a.stepMs,metric:key,retentionDays:0,series:[],markers:[],markersTruncated:false};
  for(const info of chosen) {
    const ids=new Set(a.series.filter(s=>info.grouped?s.playerId===info.playerId&&s.kind===info.kind:s.id===info.id).map(s=>s.id));
    const rows=a.rows.filter(r=>ids.has(r.seriesId)&&r.endMs>=start&&r.endMs<=end),buckets=new Map<number,HistoryRow[]>();
    for(const r of rows){const at=Math.floor(r.endMs/a.stepMs)*a.stepMs;const list=buckets.get(at)??[];list.push(r);buckets.set(at,list);}
    result.series.push({info,summary:rows.every(r=>r.weights)||metric.rollup!=='mean'?combine(rows):undefined,
      points:[...buckets].map(([at,rows])=>({at,...combine(rows)})).sort((a,b)=>a.at-b.at)});
    result.markers.push(...rows.filter(r=>r.markerMs!=null&&r.markerMs>=start&&r.markerMs<=end).map(r=>({seriesId:info.id,at:r.markerMs!,clockErrorMs:r.clockErrorMs})));
  }
  return result;
}
