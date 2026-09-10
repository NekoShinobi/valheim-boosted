<script lang="ts">
  import { LineChart, Points } from 'layerchart/svg';
  import { scaleTime } from 'd3-scale';
  import { curveLinear } from 'd3-shape';
  let { title, unit, series, onselect }: { title: string; unit: string; series: { key?: string; name: string; data: [number, number | null][] }[]; onselect?: (at: number) => void } = $props();
  const palette = ['#b8edc6','#efc58b','#91c9ef','#e8a7ce','#bfb1f2','#80d8cd','#e8a296','#c5d886','#b4c8d5'];
  let hidden = $state<string[]>([]);
  // Filter controlled visibility so restoring the last hidden series also restores its domain.
  const lines = $derived(series.map((s,i) => ({key:s.key??s.name,label:s.name,value:'value',color:palette[i%palette.length],
    data:s.data.map(([at,value])=>({at:new Date(at),value})).sort((a,b)=>+a.at-+b.at)})).filter(s=>!hidden.includes(s.key)));
  const hasData = $derived(series.some(s=>s.data.some(([,v])=>v!=null)));
  const negative = $derived(series.some(s=>s.data.some(([,v])=>v!=null&&v<0)));
  const format = (v:number|null|undefined) => v==null?'—':v.toLocaleString(undefined,{maximumFractionDigits:2});
  function toggle(name:string) { hidden=hidden.includes(name)?hidden.filter(s=>s!==name):[...hidden,name]; }
</script>

<section class="panel chart-panel layerchart-panel">
  <div class="panel-heading"><h2>{title}</h2><span class="chart-unit">{unit}</span></div>
  <div class="chart" role="group" aria-label={`${title} chart in ${unit}`}>
    {#if hasData && lines.length}
      <LineChart series={lines} x="at" y="value" xScale={scaleTime()} seriesLayout="overlap" yDomain={negative ? undefined : [0,null]} yNice
        padding={{left:48,right:22,top:18,bottom:36}} motion="none" legend={false}
        tooltipContext={{mode:'bisect-x',findTooltipData:'closest'}}
        onTooltipClick={(_e,{data})=>{const at=Number(data?.at);if(Number.isFinite(at))onselect?.(at);}}
        props={{spline:{curve:curveLinear,defined:(d)=>d.value!=null,strokeWidth:2.1},
          xAxis:{ticks:4},yAxis:{ticks:4},tooltip:{hideTotal:true,item:{format:(v)=>`${format(v as number)} ${unit}`}}}}>
        {#snippet points()}
          {#each lines as s (s.key)}
            <Points seriesKey={s.key} data={s.data.filter((d,i)=>d.value!=null && (s.data.length<=180 || s.data[i-1]?.value==null && s.data[i+1]?.value==null))} r={2} strokeWidth={0} />
          {/each}
        {/snippet}
      </LineChart>
    {:else}<div class="plot-empty"><span class="plot-empty-icon">∿</span><strong>{hasData?'All series hidden':'Waiting for measurements'}</strong><span>{hasData?'Select a legend item to show its measurements.':'Available samples will appear here.'}</span></div>{/if}
  </div>
  <div class="chart-legend" aria-label={`${title} series`}>
    {#each series as s,i (s.key??s.name)}{@const latest=[...s.data].reverse().find(([,v])=>v!=null)?.[1]}{@const key=s.key??s.name}
      <button class:muted-series={hidden.includes(key)} aria-pressed={!hidden.includes(key)} onclick={()=>toggle(key)} title={`${s.name} · last observed: ${format(latest)} ${unit}`}>
        <span class="legend-dot" style:background={palette[i%palette.length]}></span><span class="legend-name">{s.name}</span><span class="legend-value">{format(latest)}<small>{unit}</small></span>
      </button>
    {/each}
  </div>
</section>
