using System;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimBoosted;

internal sealed class IdleServerIntegration : IDisposable
{
    private readonly IdleServerPolicy policy;
    private readonly TelemetryIntegration integration;
    private readonly FeatureStatus feature;
    private readonly double sampleInterval;
    private readonly string idleDetail;
    private readonly string waitingDetail;
    private bool failed;
    internal bool Idle => policy.Idle;
    internal double SampleInterval(double normal) => Idle ? Math.Max(normal, sampleInterval) : normal;

    internal IdleServerIntegration(ConfigFile config, TelemetryIntegration integration)
    {
        this.integration = integration;
        bool enabled = config.Bind("IdleServer", "Enabled", true, "Reduce the frame rate and telemetry frequency on empty dedicated servers. Restart required.").Value;
        double delay = config.Bind("IdleServer", "DelaySeconds", 60f,
            new ConfigDescription("Continuous seconds without any peers before idling. Saving restarts the delay.", new AcceptableValueRange<float>(10f, 600f))).Value;
        int fps = config.Bind("IdleServer", "TargetFrameRate", 10,
            new ConfigDescription("Empty-server frame cap. Existing lower caps are preserved; physics timing is unchanged.", new AcceptableValueRange<int>(5, 30))).Value;
        sampleInterval = config.Bind("IdleServer", "SampleIntervalSeconds", 5f,
            new ConfigDescription("Idle telemetry interval, never faster than the normal interval.", new AcceptableValueRange<float>(1f, 10f))).Value;
        policy = new IdleServerPolicy(delay, fps);
        idleDetail = $"Empty server: frame cap at most {fps} FPS; telemetry interval at least {sampleInterval}s. Physics timing unchanged.";
        waitingDetail = $"Waiting for {delay}s without peers or a save; normal frame cap and telemetry interval.";
        feature = new FeatureStatus { id = "IdleServer", enabled = enabled, target = "Application.targetFrameRate", status = "available" };
        integration.Features[feature.id] = feature;
    }

    // Called every frame, independently of the slower telemetry cadence.
    internal void Tick(double now)
    {
        if (failed) return;
        try
        {
            var net = ZNet.instance;
            bool eligible = feature.enabled && integration.GameSupported && net && net.IsServer() && net.IsDedicated();
            // GetPeerConnections counts only ready peers; GetPeers includes handshakes.
            bool busy = eligible && (net.GetPeers().Count != 0 || net.IsSaving());
            int current = Application.targetFrameRate;
            int next = policy.Update(net ? (object)net : null, eligible, busy, now, current);
            if (current != next) Application.targetFrameRate = next;
            feature.status = !feature.enabled ? "configured_disabled" : !integration.GameSupported ? "blocked_compatibility"
                : policy.Conflict ? "setting_changed" : Idle ? "active" : "available";
            feature.detail = policy.Conflict ? "Another component changed the frame cap; idle mode suspended until the next world."
                : !eligible ? "Idle mode applies only to supported dedicated servers."
                : Idle ? idleDetail : waitingDetail;
        }
        catch (Exception ex)
        {
            Dispose();
            feature.status = "runtime_failed";
            feature.detail = ex.GetType().Name + ": " + ex.Message;
        }
    }

    public void Dispose()
    {
        Suspend();
        failed = true;
    }

    internal void Suspend()
    {
        int current = Application.targetFrameRate;
        int restored = policy.Restore(current);
        if (current != restored) Application.targetFrameRate = restored;
        if (!failed) { feature.status = feature.enabled ? "available" : "configured_disabled"; feature.detail = waitingDetail; }
    }
}
