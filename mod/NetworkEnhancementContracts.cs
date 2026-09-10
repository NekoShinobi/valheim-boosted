using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoosted;

internal static class NetworkEnhancementContracts
{
    // These pairs were reviewed against the client and dedicated 1.0.7 / protocol 39 assemblies.
    private static readonly Dictionary<string, string[]> hashes = new Dictionary<string, string[]> {
        ["CreateSyncList"] = new[] { "435ee8b873f8d8a3d9a7593af1c33488363e945578d187e87017f218af06e7ee", "7b0dd80e9fd6aae1113694388148863ba9368a8282cbc60ff9a299481f98721f" },
        ["ServerSortSendZDOS"] = new[] { "ecaa08738b6f1e2f4620fed276ba86242ba3f5f753ce241235f8b1f4d2deeac2", "cdbf7f3d6081db6c925ed7e22e5a9f37d751dd7d02825d99b4fc312dd26adad0" },
        ["ServerSendCompare"] = new[] { "d965e8a8fb608d063fe295381c8c1798826207469bd37578d38061753d442f77", "79fec23a9cb134c3e92e2e97c3aeff14c337b18781efd883f3cb63c7ad881fc4" },
        ["AddForceSendZdos"] = new[] { "48dd7bc12a1621b9cb5d377934c52d42c1172b1ce7211f83e8840d61ea17219a", "5e8a0acc62921c8783c3b9d96eb91ae437e57d65c24a9ec49760df0c37271880" },
        ["GetRefPos"] = new[] { "7fc85f008c69612c1a103fce5028eb23f8100d56c58b9bb50f834e5314844125" },
        ["OnNewConnection"] = new[] { "2d412e32e640107d1ad083c8dc5a6be914512e00f1bbe1402634704c5ed19c2c", "cfb847e5cff212e365400fd8094b880c7bd83e8d6f75ff2772356b37037c203a" },
        ["AddPeer"] = new[] { "a5ad42c32a86d75d45f71305ac2c0032965080900a101c0b1e2d8218e4ab8cef", "f02d509081d0521af11793ad7625e247c98c903f30ff0d0d060e6b46ce8176d3" },
        ["RPC_ZDOData"] = new[] { CompatibilityPolicy.ReceiveHash, CompatibilityPolicy.ServerReceiveHash },
        ["UpdatePlayerList"] = new[] { "765e89d2505496aa63c45d24d0d7217e7b6dca8dd3065b17fdf8a5f0d8f227f6", "1b26b1419d4b3c9aa588ecc2c836d5ace0995d2d761d0a304e242653477f3e62" },
        ["GetOtherPublicPlayers"] = new[] { "d5fdd8c5afac5f0536c20fd857b9c019480c9f4f81d84fbe96bfa6972e4bc7cb", "9caa382435946a5fe6c0bd15d68c961ca86835a2339bee499259a778d81a0af0" },
        ["UpdatePlayerPins"] = new[] { "07caf57b8699d76cce3b4914fc90ca540c6667e71839b9f1f5081d31a86643c5", "ca50a2fa76eb4e4cd164683c1427ab98a9ac12f19811ab7e27fd6ac9dcca95e2" },
    };
    internal static MethodInfo Method(Type type, string name, Type result, params Type[] args)
    {
        var method = AccessTools.DeclaredMethod(type, name, args);
        bool signature = method != null && method.DeclaringType == type && method.ReturnType == result && !method.IsGenericMethod && method.IsStatic == (name == "ServerSendCompare")
            && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(args);
        if (!signature || !hashes[name].Contains(CompatibilityPolicy.Fingerprint(method)))
            throw new InvalidOperationException("Unreviewed network contract: " + type.Name + "." + name);
        return method;
    }
    internal static Type PeerType => typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing ZDOPeer contract");
    internal static MethodInfo SyncList() => Method(typeof(ZDOMan), "CreateSyncList", typeof(void), PeerType, typeof(List<ZDO>));
    internal static MethodInfo Sort() => Method(typeof(ZDOMan), "ServerSortSendZDOS", typeof(void), typeof(List<ZDO>), typeof(Vector3), PeerType);
    internal static MethodInfo Receive() => Method(typeof(ZDOMan), "RPC_ZDOData", typeof(void), typeof(ZRpc), typeof(ZPackage));
}

internal static class NetworkEnhancementTransforms
{
    internal static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> input, MethodInfo original, MethodInfo replacement)
        => Replace(input, i => i.Calls(original), replacement);
    internal static IEnumerable<CodeInstruction> ForceSharing(IEnumerable<CodeInstruction> input, MethodInfo replacement)
        => Replace(input, i => i.opcode == OpCodes.Ldfld && Equals(i.operand, AccessTools.Field(typeof(ZNetPeer), "m_publicRefPos")), replacement);
    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> input, Func<CodeInstruction, bool> match, MethodInfo replacement)
    {
        int count = 0;
        var result = input.Select(i => new CodeInstruction(i)).ToList();
        foreach (var i in result) if (match(i)) { count++; i.opcode = OpCodes.Call; i.operand = replacement; }
        if (count != 1) throw new InvalidOperationException("Expected exactly one reviewed network call site, found " + count);
        return result;
    }
}
