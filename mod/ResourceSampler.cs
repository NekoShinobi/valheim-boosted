using System;
using System.Diagnostics;

namespace ValheimBoosted;

internal sealed class ResourceSampler
{
    private double? previousCpu, previousTime;
    internal ResourceMetrics Capture(double now)
    {
        var result = new ResourceMetrics { status = "available", processorCount = Environment.ProcessorCount };
        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh();
                double cpu = process.TotalProcessorTime.TotalSeconds;
                if (previousTime.HasValue && now > previousTime.Value && cpu >= previousCpu.Value)
                    result.cpuPercentOneCore = (cpu - previousCpu.Value) / (now - previousTime.Value) * 100;
                previousCpu = cpu; previousTime = now;
                result.residentBytes = process.WorkingSet64;
                result.threads = process.Threads.Count;
            }
        }
        catch (Exception ex) { result.status = "unavailable:" + ex.GetType().Name; }
        return result;
    }
}
