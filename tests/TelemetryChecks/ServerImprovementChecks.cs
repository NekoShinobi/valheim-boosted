using System;
using System.IO;
using System.Linq;
using ValheimBoosted;

internal static class ServerImprovementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } check(rejected, name); }
        var window = new SendWindowPolicy();
        for (int i = 0; i < 30; i++) window.Observe(true, 200, 0, 0, 0, 153600, 153600, 32768);
        check(window.Bytes == 32768, "Healthy high-RTT peer reaches bounded allowance");
        window.Observe(true, 200, 300, 5000, 100000, 153600, 153600, 32768);
        check(window.Bytes == 32768, "One congestion spike does not oscillate the window");
        window.Observe(true, 200, 300, 5000, 100000, 153600, 153600, 32768);
        check(window.Bytes == 16384, "Sustained unsent backlog halves allowance");
        window.Observe(false, null, null, null, null, null, 153600, 32768);
        check(window.Bytes == 10240 && window.Reason == "metrics_unavailable", "Missing transport status restores vanilla");
        for (int i = 0; i < 100; i++) window.Observe(true, 20, 0, 0, 0, 153600, 153600, 65536);
        check(window.Bytes == 10240, "Low RTT retains adequate vanilla window");
        window.Observe(true, double.NaN, 0, 0, 0, 153600, 153600, 65536);
        check(window.Reason == "metrics_unavailable", "Invalid RTT cannot change transport policy");

        var captain = new CaptainPolicy();
        check(!captain.ShouldTransfer(1, 2, true, 0) && captain.ShouldTransfer(1, 2, true, 1), "Captain must remain eligible for a full second");
        captain.Transferred(1);
        check(!captain.ShouldTransfer(2, 3, true, 2) && !captain.ShouldTransfer(2, 3, true, 3) && captain.ShouldTransfer(2, 3, true, 6), "Five-second transfer hold");
        captain.ShouldTransfer(0, 0, false, 6);
        check(!captain.ShouldTransfer(2, 3, true, 7), "Detach/disconnect resets dwell");
        check(!captain.ShouldTransfer(0, 3, true, 8) && !captain.ShouldTransfer(3, 3, true, 9), "Unowned or already-captained ship stays vanilla");
        check(CaptainPolicy.Eligible(12, 12, 99, 99, 0.01, true), "Authenticated character, grant and helm agree");
        check(!CaptainPolicy.Eligible(12, 11, 99, 99, 0, true) && !CaptainPolicy.Eligible(12, 12, 99, 98, 0, true), "Old RPC sender and passengers cannot become captain");
        check(!CaptainPolicy.Eligible(12, 12, 99, 99, 4, true) && !CaptainPolicy.Eligible(12, 12, 99, 99, 0, false)
            && !CaptainPolicy.Eligible(12, 12, 99, 99, double.NaN, true), "Reject distant helm, unloaded area and corrupt position");

        var random = new Random(718);
        foreach (int length in new[] { 512, 10240, 32768, 65536 })
        {
            var raw = new byte[length]; for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % 127);
            var encoded = LosslessZdoCodec.Encode(raw);
            check(encoded != null && LosslessZdoCodec.Decode(encoded).SequenceEqual(raw), "Exact lossless ZDO roundtrip at " + length);
            var bad = (byte[])encoded.Clone(); bad[16] ^= 1; Reject(() => LosslessZdoCodec.Decode(bad), "Reject CRC mismatch");
            Reject(() => LosslessZdoCodec.Decode(encoded.Take(encoded.Length - 1).ToArray()), "Reject truncated frame");
            bad = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(65537), 0, bad, 8, 4); Reject(() => LosslessZdoCodec.Decode(bad), "Reject decompression allocation beyond limit");
            if (length > 512) { bad = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(512), 0, bad, 8, 4); Reject(() => LosslessZdoCodec.Decode(bad), "Reject output beyond declared size"); }
            random.NextBytes(raw); check(LosslessZdoCodec.Encode(raw) == null, "Incompressible ZDO remains vanilla");
        }
        check(LosslessZdoCodec.Encode(new byte[511]) == null && LosslessZdoCodec.Encode(new byte[65537]) == null, "Small and oversized batches bypass compression");
        var a = new CompressionSession(); var b = new CompressionSession();
        var offer = a.Offer(); var ack = b.Receive(offer);
        check(!a.SendReady && b.ReceiveReady && !b.SendReady, "One-way offer never implies agreement to send");
        a.Receive(ack); check(a.SendReady, "Sender waits for explicit acknowledgment");
        var bytes = new byte[4096]; var frame = a.Frame(LosslessZdoCodec.Encode(bytes)); var saved = (byte[])frame.Clone();
        check(b.Decode(frame).SequenceEqual(bytes) && b.Decode(frame).SequenceEqual(bytes) && frame.SequenceEqual(saved), "Queued frames are immutable and independently decodable");
        Reject(() => new CompressionSession().Decode(frame), "Unnegotiated and reconnecting receivers reject old session frames");
        var c = new CompressionSession(); var d = new CompressionSession(); c.Receive(d.Receive(c.Offer()));
        Reject(() => d.Decode(frame), "New negotiated session rejects old token");
        b.Receive(a.Stop()); check(b.Stopped && !a.SendReady && b.Decode(frame).SequenceEqual(bytes), "Stop falls back while draining frames already queued");
        var unsupported = c.Offer(); unsupported[5] = 2; Reject(() => d.Receive(unsupported), "Codec mismatch stays vanilla");
        var spam = new CompressionSession(); for (int i = 0; i < 32; i++) spam.Receive(offer);
        Reject(() => spam.Receive(offer), "Control-message budget bounded per connection");
        RateChecks(check);
    }
    private static void RateChecks(Action<bool, string> check)
    {
        int value = 153600, writes = 0; bool inherited = true, failReadback = false;
        var m = new PeerImprovementMetrics();
        SteamRateSetting Read(bool max)
        {
            if (max && failReadback) { failReadback = false; throw new InvalidOperationException("injected read failure"); }
            return new SteamRateSetting { Value = max ? value : 50000, Inherited = max && inherited };
        }
        void Write(int? maximum) { writes++; value = maximum ?? 153600; inherited = !maximum.HasValue; }
        var lease = new SteamRateLease(Read, Write, m);
        check(lease.Tick(307200) && value == 307200 && writes == 1 && m.minimumRateBytesPerSecond == 50000, "Connection max verified; minimum unchanged");
        lease.Tick(307200); check(writes == 1, "No repeated writes on a stable connection");
        lease.Restore(); check(value == 153600 && inherited && m.rateStatus == "restored", "Restore inherited setting by clearing connection override");
        value = 180000; inherited = false;
        lease = new SteamRateLease(Read, Write, new PeerImprovementMetrics()); lease.Tick(307200); lease.Restore();
        check(value == 180000 && !inherited, "Restore explicit connection override exactly");
        m = new PeerImprovementMetrics(); lease = new SteamRateLease(Read, Write, m); lease.Tick(307200); value = 400000; int before = writes;
        lease.Tick(307200); lease.Restore(); check(value == 400000 && writes == before && m.rateStatus == "external_override", "Another writer retains ownership of its setting");
        lease = new SteamRateLease(Read, Write, m); check(!lease.Tick(307200) && writes == before, "Never lower a pre-existing greater limit");
        value = 153600; inherited = true; m = new PeerImprovementMetrics();
        lease = new SteamRateLease(Read, x => { Write(x); if (x.HasValue) failReadback = true; }, m);
        check(!lease.Tick(307200) && value == 153600 && inherited && m.rateWriteFailures == 1, "Readback failure restores original after successful native write");
        value = 153600; inherited = true; m = new PeerImprovementMetrics();
        bool rejectRestore = true;
        lease = new SteamRateLease(Read, x => { if (!x.HasValue && rejectRestore) throw new InvalidOperationException("restore rejected"); Write(x); }, m);
        lease.Tick(307200); lease.Restore(); check(m.rateStatus == "restore_failed", "Restoration failure is observable");
        rejectRestore = false; lease.Tick(307200); check(value == 153600 && inherited && m.rateStatus == "restored", "Failed restoration retried without reapplying tuning");
        value = 0; before = writes; m = new PeerImprovementMetrics(); lease = new SteamRateLease(Read, Write, m);
        check(!lease.Tick(307200) && writes == before && m.rateWriteFailures == 1, "Unknown zero-limit semantics never trigger a native write");
    }
}
