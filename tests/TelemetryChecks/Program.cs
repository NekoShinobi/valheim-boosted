using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValheimBoosted;

namespace ValheimBoosted
{
    // Pure managed checks do not load Unity or BepInEx.
    public static class Plugin { public const string PluginVersion = "test"; }
}

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }

    private static TelemetrySnapshot Snapshot(long sequence) => new TelemetrySnapshot
    {
        sequence = sequence, processSession = "session", role = "dedicated_server",
        capturedAtUtc = DateTime.UtcNow.ToString("O"),
        peers = new[] { new PeerMetrics { peerSessionId = "123", measurementStatus = "unavailable" } },
    };

    private static long ReadSequence(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("sequence").GetInt64();
    }

    public static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "valheim-boosted-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var window = new SampleWindow();
            Check(window.Take().p95 == null, "Empty windows must not imply zero latency");
            for (int i = 1; i <= 100; i++) window.Add(i);
            window.Add(double.NaN); window.Add(double.PositiveInfinity); window.Add(-1);
            var stats = window.Take();
            Check(stats.samples == 100 && stats.mean == 50.5 && stats.p95 == 95 && stats.max == 100, "Distribution accuracy");
            Check(window.Take().samples == 0, "Sample windows reset");
            window.Add(100000);
            for (int i = 0; i < 3000; i++) window.Add(1);
            stats = window.Take();
            Check(stats.samples == 3001 && stats.percentileSamples == 2048 && stats.p95 == 1 && stats.max == 100000, "Bounded percentile window retains full-window max/count");

            string path = Path.Combine(directory, "snapshot.json");
            SnapshotExporter.WriteAtomic(path, Snapshot(0));
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Check(json.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Schema version");
                Check(json.RootElement.GetProperty("peers")[0].GetProperty("rttMs").ValueKind == JsonValueKind.Null, "Unknown measurements serialize as null");
            }

            // Readers must only see complete JSON while the writer replaces the snapshot.
            int reads = 0;
            using (var stop = new CancellationTokenSource())
            {
                var reader = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        ReadSequence(path);
                        Interlocked.Increment(ref reads);
                    }
                });
                for (int i = 1; i <= 100; i++) SnapshotExporter.WriteAtomic(path, Snapshot(i));
                stop.Cancel();
                reader.GetAwaiter().GetResult();
            }
            Check(reads > 0 && ReadSequence(path) == 100, "Atomic replacement under concurrent reads");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary files cleaned");

            using (var exporter = new SnapshotExporter(path))
            {
                for (int i = 101; i <= 300; i++) exporter.Publish(Snapshot(i));
                Check(SpinWait.SpinUntil(() => ReadSequence(path) == 300, 5000), "Latest snapshot is eventually exported");
                var final = Snapshot(301); final.running = false; final.role = "stopped";
                exporter.Publish(final);
            }
            Check(ReadSequence(path) == 301, "Shutdown flushes final snapshot");

            string blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "not a directory");
            using (var exporter = new SnapshotExporter(Path.Combine(blocked, "snapshot.json")))
            {
                exporter.Publish(Snapshot(1));
                Check(SpinWait.SpinUntil(() => exporter.Error != null, 5000), "Export failures are observable");
                File.Delete(blocked); Directory.CreateDirectory(blocked);
                exporter.Publish(Snapshot(2));
                Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(blocked, "snapshot.json")) && exporter.Error == null, 5000), "Exporter recovers after filesystem failure");
            }
            Console.WriteLine($"PASS: {checks} telemetry checks (including {reads} concurrent snapshot reads).");
            return 0;
        }
        finally { Directory.Delete(directory, true); }
    }
}
