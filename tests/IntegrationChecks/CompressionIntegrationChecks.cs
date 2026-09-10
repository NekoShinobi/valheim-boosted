using System;
using System.Linq;
using BepInEx.Configuration;
using ValheimBoosted;

namespace UnityEngine { public static class Time { public static int frameCount; } }

internal static class CompressionIntegrationChecks
{
    private const string Control = "valheim.boosted.compression.v1", Data = "valheim.boosted.zdo.v1";
    internal static void Run(Action<bool, string> check)
    {
        using (var telemetry = new TelemetryIntegration(new ConfigFile(), _ => { }))
        using (var advanced = new AdvancedReplication(new ConfigFile(), telemetry, _ => { }))
            check(!advanced.Windows.enabled && !advanced.Rates.enabled && !advanced.Compression.enabled && !advanced.NeedsPatch,
                "New generated stage 3/5 switches are off independently of default-on scheduling");
        WindowChecks(check);
        // The fixture receiver is intentionally unreviewed. Production must reject it.
        bool rejected = false;
        try { new ZdoCompression(new FeatureStatus { enabled = true, status = "available" }, 1, _ => { }); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Compression rejects unreviewed receiver before negotiation");
        // Activate only after constructing while disabled, to exercise real handlers against the fixture receiver.
        var feature = new FeatureStatus { enabled = true, status = "configured_disabled" };
        ZNet.instance = new ZNet(); ZDOMan.instance = new ZDOMan(); TelemetryCollector.Now = 0; TelemetryCollector.ClockStep = 0;
        ZNetPeer modded = new ZNetPeer(), vanilla = new ZNetPeer { m_uid = 2 }, otherTransport = new ZNetPeer { m_uid = 3, m_socket = new TestSocket() };
        ZNet.instance.Peers.AddRange(new[] { modded, vanilla, otherTransport });
        using (var compression = new ZdoCompression(feature, 1, _ => { }))
        {
            feature.status = "available"; compression.Tick(0);
            check(otherTransport.m_rpc.Handlers.Count == 0, "Non-Steam peer gets no compression handlers or offers");
            var remote = new CompressionSession();
            var offer = ((ZPackage)modded.m_rpc.Sent.Single(p => p.Item1 == Control).Item2[0]).GetArray();
            byte[] raw = new byte[4096]; for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 100);
            compression.Invoke(modded.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            check(modded.m_rpc.Sent.Last().Item1 == "ZDOData", "Offer without acknowledgment uses vanilla dispatch");
            modded.m_rpc.Deliver(Control, remote.Receive(offer));
            modded.m_rpc.Deliver(Control, remote.Offer());
            remote.Receive(((ZPackage)modded.m_rpc.Sent.Last().Item2[0]).GetArray());
            int before = modded.m_rpc.Sent.Count;
            compression.Invoke(modded.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            var output = modded.m_rpc.Sent.Last();
            check(output.Item1 == Data && modded.m_rpc.Sent.Count == before + 1 && remote.Decode(((ZPackage)output.Item2[0]).GetArray()).SequenceEqual(raw), "Negotiated send dispatches exactly one lossless frame");
            var incoming = remote.Frame(LosslessZdoCodec.Encode(raw));
            modded.m_rpc.Deliver(Data, incoming);
            check(ZDOMan.instance.ReceiveCalls == 1 && ZDOMan.instance.Received.SequenceEqual(raw), "Compressed receiver invokes vanilla parser once with exact raw bytes");
            compression.Invoke(vanilla.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            check(vanilla.m_rpc.Sent.Last().Item1 == "ZDOData", "Mixed unmodded peer retains vanilla traffic");
            UnityEngine.Time.frameCount++; TelemetryCollector.ClockStep = 0.002;
            compression.Invoke(modded.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            compression.Invoke(modded.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            check(modded.m_rpc.Sent.Last().Item1 == "ZDOData", "Soft encode budget defers later work to vanilla in the same frame");
            TelemetryCollector.ClockStep = 0;
            compression.StopSending("test fallback");
            compression.Invoke(modded.m_rpc, "ZDOData", new object[] { new ZPackage(raw) });
            modded.m_rpc.Deliver(Data, incoming);
            check(modded.m_rpc.Sent.Last().Item1 == "ZDOData" && ZDOMan.instance.ReceiveCalls == 2, "Runtime fallback drains previously queued frames while sending vanilla");
            var corrupt = (byte[])incoming.Clone(); corrupt[0] ^= 1;
            modded.m_rpc.Deliver(Data, corrupt);
            check(!modded.m_socket.IsConnected() && vanilla.m_socket.IsConnected() && ZDOMan.instance.ReceiveCalls == 2, "Invalid framed payload closes only offending connection and never calls parser");
            var metrics = new ServerImprovementMetrics(); compression.Capture(metrics);
            check(metrics.compressedSent == 2 && metrics.compressedReceived == 2 && metrics.compressionRejected == 1
                && metrics.rawPayloadBytes > metrics.framedPayloadBytes && metrics.compressionEncodeMs.samples == 2, "Actual handler work, saved bytes and rejection counters exported");
            compression.Capture(metrics); check(metrics.compressedSent == 0 && metrics.compressionEncodeMs.samples == 0, "Window metrics drain exactly once");
            compression.Tick(5); check(modded.m_rpc.Handlers.Count == 0, "Disconnect removes old token handlers");
        }
        check(vanilla.m_rpc.Handlers.Count == 0, "Disposal removes unmodded peer handlers too");
        ZNet.instance = null; ZDOMan.instance = null;
    }
    private static void WindowChecks(Action<bool, string> check)
    {
        ZNet.instance = new ZNet(); var peer = new ZNetPeer(); ZNet.instance.Peers.Add(peer);
        using (var telemetry = new TelemetryIntegration(new ConfigFile(), _ => { }))
        using (var advanced = new AdvancedReplication(new ConfigFile(), telemetry, _ => { }))
        {
            advanced.Windows.enabled = true; advanced.Windows.status = "available"; advanced.Initialize(); advanced.MarkPatched();
            for (int i = 0; i < 20; i++) { TelemetryCollector.Now = i; advanced.Tick(i); }
            check(advanced.Window(peer, false) == 32768 && advanced.Window(peer, true) == 10240, "Live measured window expands; forced flush remains vanilla");
            TelemetryCollector.Now = 23;
            check(advanced.Window(peer, false) == 10240, "Three-second stale measurement guard uses vanilla allowance");
            ZNet.instance.Dedicated = false;
            check(advanced.Window(peer, false) == 10240, "Client/host cannot use server window tuning");
            ZNet.instance.Dedicated = true;
            ZNet.instance.Peers.Clear(); var reconnected = new ZNetPeer { m_uid = peer.m_uid }; ZNet.instance.Peers.Add(reconnected);
            advanced.Tick(23);
            check(advanced.Window(reconnected, false) == 10240 && advanced.Window(peer, false) == 10240, "Reused peer ID does not reuse allowance or old connection state");
            advanced.Disable("test conflict");
            check(!advanced.Patched && !advanced.Windows.Collect, "Integration fallback disables window tuning");
        }
    }
}
