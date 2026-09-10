using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class CaptainContracts
{
    internal readonly MethodInfo[] Methods;
    internal CaptainContracts()
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Methods = new[] {
            typeof(ShipControlls).GetMethod("RPC_RequestControl", flags), typeof(ShipControlls).GetMethod("RPC_ReleaseControl", flags),
            typeof(Player).GetMethod("GetRelativePosition", flags), typeof(ZSyncTransform).GetMethod("OwnerSync", flags), typeof(ZDO).GetMethod("SetOwner", flags),
            typeof(Ship).GetMethod("UpdateOwner", flags)
        };
        string[] client = {
            "754df4012c5b8c0f46dbd375c85ff662dc6b32c9968c5ca1ae75c0d914a618c3", "3380bd2c2a9cbf7a8c858b74658dd6943f6e34b421551f759ecfe675fcfc9cdf",
            "94cce6e2800ac869f5ce2cb8a50f86a1ec50ea85a393b4c61b3ab463e2aeacf4", "af40dcf689b3fa4127d79f1ea4f21a3a6e4f3344012ea9a536c2b0e612628a7f",
            "5b9c2f98daffe7231d86b4b6d117c19646fafb5e7e53fcc1175d95ca31a2eb4f", "926c2740c872faf02e1d8934ca84f86b9a7e1280b4db9728c196f35807e4780e"
        };
        string[] server = { "3cc225d26ed72841cd2fc0c3093a8de3049e0df51e3241dae85ff41e7bd315a0", "f426d0d10ad7603158ceeec81fd37409d02cdfab87d1203cc5355c71bb26fc2e",
            client[2], "6d95dbf81f603a99634299620c35dfb55c4424826988891c3b2a5ca6e2a28cff", client[4], "9e16286854ba4710495caca59514f315dc627e33d206ce0fece7193ca438611a" };
        var actual = Methods.Select(CompatibilityPolicy.Fingerprint).ToArray();
        if (!actual.SequenceEqual(client) && !actual.SequenceEqual(server)) throw new InvalidOperationException("Unreviewed captain ownership contracts: " + string.Join("/", actual));
        if (!CompatibilityPolicy.Signature(Methods[0], typeof(ShipControlls), typeof(void), typeof(long), typeof(long))
            || !CompatibilityPolicy.Signature(Methods[1], typeof(ShipControlls), typeof(void), typeof(long), typeof(long))
            || !CompatibilityPolicy.Signature(Methods[4], typeof(ZDO), typeof(void), typeof(long))
            || !CompatibilityPolicy.Signature(Methods[5], typeof(Ship), typeof(void)))
            throw new InvalidOperationException("Captain ownership signature mismatch");
        Audit();
    }
    internal void Audit()
    {
        if (Methods.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any() == true)) throw new InvalidOperationException("Another mod patches captain grants, attachment sync or ownership");
    }
}
