using System;
using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using ValheimBoosted;

namespace ValheimBoosted { public static class Plugin { public const string PluginVersion = "contract-check"; } }

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the game Managed directory");
        string directory = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            // Keep Mono's own framework libraries; resolve only game dependencies here.
            if (name == "mscorlib" || name == "netstandard" || name == "System" || name.StartsWith("System.")) return null;
            string path = Path.Combine(directory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return Inspect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Inspect()
    {
        var version = typeof(ZNet).Assembly.GetType("Version", true);
        string game = version.GetProperty("CurrentVersion").GetValue(null).ToString();
        uint protocol = Convert.ToUInt32(version.GetField("c_networkVersion").GetRawConstantValue());
        if (CompatibilityPolicy.Evaluate(game, protocol) != "supported") throw new Exception("Unsupported game/protocol references");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var entry in new[] {
            new { Name = "Update", Types = new[] { typeof(float) }, Hash = CompatibilityPolicy.UpdateHash },
            new { Name = "RPC_ZDOData", Types = new[] { typeof(ZRpc), typeof(ZPackage) }, Hash = CompatibilityPolicy.ReceiveHash }
        })
        {
            var method = typeof(ZDOMan).GetMethod(entry.Name, flags, null, entry.Types, null);
            if (!CompatibilityPolicy.Signature(method, typeof(ZDOMan), typeof(void), entry.Types)
                || !CompatibilityPolicy.MatchesFingerprint(entry.Hash, CompatibilityPolicy.Fingerprint(method)))
                throw new Exception("Unreviewed target: " + entry.Name);
        }
        var replication = new ReplicationContracts();
        int checks = 0;
        ServerImprovementChecks.Run((ok, message) => { if (!ok) throw new Exception(message); checks++; });
        System.Console.WriteLine("PASS: " + checks + " server improvement checks under Mono");
        new CaptainContracts().Audit();
        var adapter = new SteamRateAdapter();
        TransformChecks.Run(replication.Send);
        System.Console.WriteLine("PASS: captain contracts, Steam connection config interface " + adapter.Backend + ", real Harmony send transforms");
        System.Console.WriteLine("PASS: scheduler/send fingerprint pair, signatures and peer fields");
        // Initialize inspects managed contracts; it never calls Steam or starts Unity.
        string transport = SteamMetrics.Initialize();
        var send = typeof(ZSteamSocket).GetMethod("SendQueuedPackages", flags);
        var sendInterface = CompatibilityPolicy.Calls(send).OfType<MethodInfo>()
            .Single(m => m.Name == "SendMessageToConnection").DeclaringType;
        if (transport != sendInterface.FullName)
            throw new Exception("Steam telemetry must query the same interface that sends game traffic");
        // Dedicated references reproduce the bug: quality uses the client API while
        // actual game traffic uses the server API. Inspect without initializing Steam.
        string expectedGuard = transport == "Steamworks.SteamGameServerNetworkingSockets"
            ? "TestIfAvailableGameServer" : "TestIfAvailableClient";
        var query = sendInterface.GetMethod("GetConnectionRealTimeStatus");
        if (!CompatibilityPolicy.Calls(query).Any(m => m.Name == expectedGuard))
            throw new Exception("Steam query uses an unexpected initialization context");
        System.Console.WriteLine($"PASS: game {game}, protocol {protocol}, transport {transport}, guard {expectedGuard}");
        return 0;
    }
}
