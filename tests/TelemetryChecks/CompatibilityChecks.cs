using System;
using System.Linq;
using System.Reflection;
using ValheimBoosted;

internal static class CompatibilityChecks
{
    private sealed class Target
    {
        public void Update(float delta) { GC.KeepAlive(delta); }
        public int WrongReturn(float delta) => 1;
        public static void Static(float delta) { }
        public void Other(float delta) { GC.KeepAlive(delta + 1); }
    }

    internal static void Run(Action<bool, string> check)
    {
        check(CompatibilityPolicy.Evaluate("1.0.7", 39) == "supported", "Reviewed game and protocol accepted");
        check(CompatibilityPolicy.Evaluate("1.0.8", 39) == "unsupported_game", "Unknown game blocked");
        check(CompatibilityPolicy.Evaluate("1.0.7", 40) == "unsupported_protocol", "Unknown protocol blocked");
        check(CompatibilityPolicy.Evaluate("1.0.7", null) == "unsupported_protocol", "Missing protocol blocked");
        var method = typeof(Target).GetMethod("Update");
        check(CompatibilityPolicy.Signature(method, typeof(Target), typeof(void), typeof(float)), "Exact method signature accepted");
        check(!CompatibilityPolicy.Signature(method, typeof(Target), typeof(void), typeof(int)), "Wrong parameter rejected");
        check(!CompatibilityPolicy.Signature(typeof(Target).GetMethod("WrongReturn"), typeof(Target), typeof(void), typeof(float)), "Wrong return rejected");
        check(!CompatibilityPolicy.Signature(typeof(Target).GetMethod("Static"), typeof(Target), typeof(void), typeof(float)), "Static target rejected");
        check(!CompatibilityPolicy.Signature(null, typeof(Target), typeof(void)), "Missing target rejected");
        check(CompatibilityPolicy.Fingerprint(method) != CompatibilityPolicy.Fingerprint(typeof(Target).GetMethod("Other")), "Changed IL fingerprint differs");
        check(CompatibilityPolicy.MatchesFingerprint(CompatibilityPolicy.ReceiveHash, CompatibilityPolicy.ServerReceiveHash), "Reviewed server receive variant accepted");
        check(!CompatibilityPolicy.MatchesFingerprint(CompatibilityPolicy.UpdateHash, CompatibilityPolicy.ServerReceiveHash), "Fingerprint cannot match the wrong target");
        check(CompatibilityPolicy.Calls(method).Single().Name == "KeepAlive", "Call inspection decodes operands correctly");
        foreach (var state in new[] { "configured_disabled", "blocked_compatibility", "signature_mismatch", "fingerprint_mismatch", "conflicting_patch", "installation_failed", "patch_changed", "probe_failed" })
            check(!new FeatureStatus { status = state }.Collect, "Probe must not collect in " + state);
        var feature = new FeatureStatus { id = "NetworkTiming", status = "active", invocations = 1 };
        var detached = feature.Copy(); feature.invocations++;
        check(detached.invocations == 1, "Exported feature status detached from live counter");
        var info = new CompatibilityInfo { status = "supported" };
        var copy = info.Copy(); info.status = "changed";
        check(copy.status == "supported", "Exported compatibility detached");
    }
}
