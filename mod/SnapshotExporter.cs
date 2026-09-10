using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace ValheimBoosted;

internal sealed class SnapshotExporter : IDisposable
{
    private readonly string path;
    private readonly object gate = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly Thread worker;
    private TelemetrySnapshot pending;
    private bool stopping;
    private string error;
    public string Error { get { lock (gate) return error; } }

    public SnapshotExporter(string path)
    {
        this.path = Path.GetFullPath(path);
        worker = new Thread(Work) { IsBackground = true, Name = "ValheimBoosted telemetry export" };
        worker.Start();
    }

    public void Publish(TelemetrySnapshot snapshot)
    {
        lock (gate)
        {
            if (stopping) return;
            pending = snapshot; // Latest wins: a slow filesystem cannot build a backlog.
            wake.Set();
        }
    }

    private void Work()
    {
        try
        {
            while (true)
            {
                wake.WaitOne();
                TelemetrySnapshot snapshot;
                bool stop;
                lock (gate) { snapshot = pending; pending = null; stop = stopping; }
                if (snapshot != null)
                {
                    try
                    {
                        WriteAtomic(path, snapshot);
                        lock (gate) error = null;
                    }
                    catch (Exception ex)
                    {
                        lock (gate) error = ex.GetType().Name + ": " + ex.Message;
                    }
                }
                if (stop) return;
            }
        }
        finally { wake.Dispose(); }
    }

    internal static void WriteAtomic(string path, TelemetrySnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(TelemetrySnapshot)).WriteObject(stream, snapshot);
            // Same-directory replacement preserves complete reads. Never delete the old snapshot first.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            wake.Set();
        }
        worker.Join(500); // A blocked disk must not hold up game shutdown.
    }
}
