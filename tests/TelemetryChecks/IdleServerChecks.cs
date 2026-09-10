using System;
using BepInEx.Configuration;
using UnityEngine;
using ValheimBoosted;

internal static class IdleServerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var world = new object();
        var policy = new IdleServerPolicy(60, 10);
        check(policy.Update(world, true, false, 0, 30) == 30 && !policy.Idle, "Empty server starts with a grace period");
        check(policy.Update(world, true, false, 59.9, 30) == 30, "Frame cap retained until the full delay elapses");
        check(policy.Update(world, true, false, 60, 30) == 10 && policy.Idle, "Empty server enters the idle cap");
        check(policy.Update(world, true, true, 60.1, 10) == 30 && !policy.Idle, "Any connection or save restores the original cap immediately");
        policy.Update(world, true, false, 61, 30);
        check(policy.Update(world, true, false, 120, 30) == 30, "A reconnect resets the continuous empty delay");
        check(policy.Update(world, true, false, 121, 30) == 10, "Server can idle again after disconnect");
        check(policy.Update(new object(), true, false, 122, 10) == 30 && !policy.Idle, "World replacement restores the cap and resets the delay");

        foreach (int original in new[] { -1, 5, 60 })
        {
            policy = new IdleServerPolicy(60, 10);
            policy.Update(world, true, false, 0, original);
            int idle = policy.Update(world, true, false, 60, original);
            check(idle == (original == 5 ? 5 : 10), "Idle cap never raises an existing lower cap");
            check(policy.Restore(idle) == original, "Shutdown restores both unlimited and explicit caps exactly");
        }
        policy = new IdleServerPolicy(60, 10);
        policy.Update(world, true, false, 0, 30);
        policy.Update(world, true, false, 60, 30);
        check(policy.Update(world, true, false, 61, 20) == 20 && policy.Conflict && !policy.Idle, "External frame-cap changes suspend idle control");
        check(policy.Update(world, true, false, 500, 20) == 20 && policy.Restore(20) == 20, "Idle mode never overwrites an external cap later");

        try
        {
            ZNet.instance = new ZNet { Server = true, Dedicated = true };
            Application.targetFrameRate = 30;
            var integration = new TelemetryIntegration();
            using (var idle = new IdleServerIntegration(new ConfigFile(), integration))
            {
                idle.Tick(0); idle.Tick(60);
                check(Application.targetFrameRate == 10 && idle.SampleInterval(1) == 5 && idle.SampleInterval(10) == 10, "Production integration caps FPS and reduces only faster telemetry intervals");
                check(integration.Features["IdleServer"].status == "active", "Idle state is visible through the existing feature telemetry");
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) idle.Tick(60);
                check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Stable idle checks allocate no managed memory per frame");
                ZNet.instance.Peers.Add(new ZNetPeer { Ready = false });
                idle.Tick(60.1);
                check(Application.targetFrameRate == 30 && idle.SampleInterval(1) == 1, "An unready joining peer wakes FPS and telemetry before completing the handshake");
                ZNet.instance.Peers.Clear(); idle.Tick(61); idle.Tick(121);
                ZNet.instance.Saving = true; idle.Tick(122);
                check(Application.targetFrameRate == 30 && !idle.Idle, "Saving exits idle mode");
                ZNet.instance.Saving = false; idle.Tick(123); idle.Tick(183);
                idle.Suspend();
                check(Application.targetFrameRate == 30 && !idle.Idle, "Disabling the component restores the frame cap");
                idle.Tick(184); idle.Tick(244);
                ZNet.instance.ThrowPeers = true; idle.Tick(245);
                check(Application.targetFrameRate == 30 && integration.Features["IdleServer"].status == "runtime_failed", "Inspection failure restores normal FPS and disables idle control");
            }
            foreach (string mode in new[] { "disabled", "unsupported", "host", "client", "menu" })
            {
                ZNet.instance = mode == "menu" ? null : new ZNet { Server = mode != "client", Dedicated = mode != "host" };
                Application.targetFrameRate = 60;
                integration = new TelemetryIntegration { GameSupported = mode != "unsupported" };
                using var idle = new IdleServerIntegration(new ConfigFile { IdleEnabled = mode != "disabled" }, integration);
                idle.Tick(0); idle.Tick(1000);
                check(Application.targetFrameRate == 60 && !idle.Idle, "Idle mode preserves FPS for " + mode);
            }
        }
        finally { ZNet.instance = null; Application.targetFrameRate = -1; }
    }
}
