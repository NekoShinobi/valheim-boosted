using System;
using System.IO;

namespace ValheimBoosted;

// Compact, fixed-field binary records; never deserialize arbitrary objects or strings.
internal static class ClientTelemetryCodec
{
    internal const int Version = 1, MaxPacketBytes = 1024, MaxBatch = 5;
    internal static void Write(BinaryWriter w, ClientWindow s)
    {
        w.Write(s.sequence); w.Write(s.startMs); w.Write(s.endMs); w.Write(s.frames);
        Number(w, s.frameMeanMs); Number(w, s.frameP95Ms); Number(w, s.frameP99Ms); Number(w, s.frameMaxMs);
        w.Write(s.worstFrameEndMs ?? -1);
        w.Write(s.longFrames50 ?? -1); w.Write(s.longFrames100 ?? -1); w.Write(s.longFrames250 ?? -1);
        w.Write(s.gc0); w.Write(s.gc1); w.Write(s.gc2);
        Number(w, s.cpuPercent); Number(w, s.managedMemoryMiB); Number(w, s.networkP95Ms);
        Number(w, s.rttMs); Number(w, s.queueMs); w.Write(s.changedZdos ?? -1);
        w.Write(s.focused); w.Write(s.loading); w.Write(s.frameLimit); w.Write(s.vSyncCount); w.Write(s.markerMs ?? -1);
    }
    private static void Number(BinaryWriter w, double? v) => w.Write(v.HasValue ? (float)v.Value : float.NaN);
    private static double? Number(BinaryReader r)
    {
        float n = r.ReadSingle();
        if (float.IsNaN(n)) return null;
        if (float.IsInfinity(n) || n < 0 || n > 1e12) throw new InvalidDataException("Invalid metric");
        return n;
    }
    private static int? Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < -1) throw new InvalidDataException("Invalid count");
        return n == -1 ? (int?)null : n;
    }
    internal static ClientWindow Read(BinaryReader r)
    {
        var s = new ClientWindow { sequence = r.ReadInt64(), startMs = r.ReadDouble(), endMs = r.ReadDouble(), frames = r.ReadInt32(),
            frameMeanMs = Number(r), frameP95Ms = Number(r), frameP99Ms = Number(r), frameMaxMs = Number(r) };
        double worst = r.ReadDouble(); s.worstFrameEndMs = worst == -1 ? (double?)null : worst;
        s.longFrames50 = Count(r); s.longFrames100 = Count(r); s.longFrames250 = Count(r);
        s.gc0 = r.ReadInt32(); s.gc1 = r.ReadInt32(); s.gc2 = r.ReadInt32();
        s.cpuPercent = Number(r); s.managedMemoryMiB = Number(r); s.networkP95Ms = Number(r);
        s.rttMs = Number(r); s.queueMs = Number(r); s.changedZdos = Count(r);
        s.focused = r.ReadBoolean(); s.loading = r.ReadBoolean(); s.frameLimit = r.ReadInt32(); s.vSyncCount = r.ReadInt32();
        double marker = r.ReadDouble(); s.markerMs = marker == -1 ? (double?)null : marker;
        if (s.sequence < 1 || s.sequence > 9007199254740991L || !ClientClock.Finite(s.startMs) || !ClientClock.Finite(s.endMs)
            || s.startMs < 0 || s.endMs > 9007199254740991d || s.endMs <= s.startMs || s.endMs - s.startMs > 120000
            || s.frames < 0 || s.frames > 1000000 || s.gc0 < 0 || s.gc1 < 0 || s.gc2 < 0
            || s.frameLimit < -1 || s.frameLimit > 10000 || s.vSyncCount < 0 || s.vSyncCount > 4
            || s.worstFrameEndMs.HasValue && (!ClientClock.Finite(worst) || worst < s.startMs || worst > s.endMs)
            || s.markerMs.HasValue && (!ClientClock.Finite(marker) || marker < s.startMs || marker > s.endMs))
            throw new InvalidDataException("Invalid sample window");
        return s;
    }
}
