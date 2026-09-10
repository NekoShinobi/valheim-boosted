<script lang="ts">
  import { onMount } from 'svelte';
  import { init, use, type ECharts } from 'echarts/core';
  import { LineChart } from 'echarts/charts';
  import { GridComponent, TooltipComponent, LegendComponent } from 'echarts/components';
  import { CanvasRenderer } from 'echarts/renderers';

  use([LineChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer]);
  let { title, unit, series }: { title: string; unit: string; series: { name: string; data: [number, number | null][] }[] } = $props();
  let element: HTMLDivElement;
  let chart: ECharts | undefined;
  const option = $derived({
    animation: false,
    color: ['#9ddeae', '#dcb271', '#86bcea', '#d28dae'],
    tooltip: { trigger: 'axis' as const },
    legend: { bottom: 0, textStyle: { color: '#a7b4ac' }, type: 'scroll' as const },
    grid: { left: 54, right: 20, top: 26, bottom: 65 },
    xAxis: { type: 'time' as const, axisLabel: { color: '#8f9e96' }, axisLine: { lineStyle: { color: '#34433b' } } },
    yAxis: { type: 'value' as const, name: unit, min: 0, nameTextStyle: { color: '#8f9e96' }, axisLabel: { color: '#8f9e96' }, splitLine: { lineStyle: { color: '#26342c' } } },
    series: series.map(s => ({ ...s, type: 'line' as const, showSymbol: false, connectNulls: false, lineStyle: { width: 2 } })),
  });
  $effect(() => { chart?.setOption(option, { notMerge: true }); });
  onMount(() => {
    chart = init(element);
    chart.setOption(option);
    const resize = new ResizeObserver(() => chart?.resize());
    resize.observe(element);
    return () => { resize.disconnect(); chart?.dispose(); };
  });
</script>

<section class="panel chart-panel">
  <div class="panel-heading"><h2>{title}</h2><span class="eyebrow">{unit}</span></div>
  <div class="chart" bind:this={element} role="img" aria-label={`${title} history in ${unit}. Current values are available in the connections table.`}></div>
  {#if !series.some(s => s.data.some(([, value]) => value !== null))}<p class="chart-empty">Waiting for measured samples</p>{/if}
</section>
