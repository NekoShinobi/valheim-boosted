using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ValheimBoosted;

internal static class TransformChecks
{
    private static int allowance = 32768, dispatches;
    private static object payload = new object();
    private static int Window(object peer, bool flush) => flush ? 10240 : allowance;
    private static void Dispatch(ZRpc rpc, string method, object[] args)
    {
        if (method != "ZDOData" || args.Length != 1 || !ReferenceEquals(args[0], payload)) throw new Exception("Dispatch changed RPC contents");
        dispatches++;
    }
    private static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> instructions) => SendZdoTransform.Apply(instructions,
        AccessTools.Method(typeof(TransformChecks), nameof(Window)), AccessTools.Method(typeof(TransformChecks), nameof(Dispatch)));
    private sealed class Fixture
    {
        internal int Queue;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool Send(object peer, bool flush)
        {
            if (!flush && Queue > 10240) return false;
            int room = 10240 - Queue;
            if (room < 2048) return false;
            ZRpc rpc = null;
            rpc.Invoke("ZDOData", payload);
            return true;
        }
    }
    internal static void Run(MethodInfo actualSend)
    {
        var harmony = new Harmony("valheim.boosted.contract-transform");
        try
        {
            // Compile and install the real game's full IL under Mono for both reference sets.
            harmony.Patch(actualSend, transpiler: new HarmonyMethod(typeof(TransformChecks), nameof(Transform)));
            if (Harmony.GetPatchInfo(actualSend).Transpilers.Count(p => p.owner == harmony.Id) != 1) throw new Exception("Send transform registration");
            harmony.Patch(AccessTools.Method(typeof(Fixture), "Send"), transpiler: new HarmonyMethod(typeof(TransformChecks), nameof(Transform)));
            var f = new Fixture { Queue = 16000 };
            if (!f.Send(new object(), false) || dispatches != 1) throw new Exception("Both window sites must change; dispatch exactly once");
            if (f.Send(new object(), true) || dispatches != 1) throw new Exception("Forced flush must retain vanilla allowance");
            allowance = 10240;
            if (f.Send(new object(), false) || dispatches != 1) throw new Exception("Disabled/stale allowance must defer as vanilla");
            f.Queue = 0;
            if (!f.Send(new object(), false) || dispatches != 2) throw new Exception("Vanilla fallback must deliver exactly once");
            bool rejected = false;
            try { Transform(Array.Empty<CodeInstruction>()).ToList(); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Unexpected IL must fail closed");
        }
        finally { harmony.UnpatchSelf(); }
    }
}
