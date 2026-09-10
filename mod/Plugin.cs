using System;
using System.Globalization;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Utils;
using UnityEngine;

namespace ValheimBoosted;

public enum HudPosition { TopLeft, TopRight, BottomLeft, BottomRight }

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(Jotunn.Main.ModGuid)]
[NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "valheim.boosted";
    public const string PluginName = "valheim-boosted";
    public const string PluginVersion = "0.2.0";
    private TelemetryIntegration integration;
    private TelemetryCollector collector;
    private ClientTelemetryTransport clientTelemetry;
    private ReplicationIntegration replication;
    private IdleServerIntegration idleServer;
    private CaptainIntegration captain;
    private SnapshotExporter exporter;
    private TelemetrySnapshot latest;
    private ConfigEntry<bool> hudEnabled;
    private ConfigEntry<KeyboardShortcut> hudKey;
    private ConfigEntry<HudPosition> hudPosition;
    private ConfigEntry<float> interval;
    private ConfigEntry<bool> exportServer;
    private ConfigEntry<bool> exportClient;
    private ConfigEntry<string> exportPath;
    private double nextSample;
    private double nextWarning;
    private string hudText = "Waiting for telemetry…";
    private bool stopped;
    private bool wasExporting;
    private string configuredPath;
    private GUIStyle hudStyle;

    private void Awake()
    {
        if (!Config.Bind("Telemetry", "Enabled", true, "Enable telemetry and optional scheduler integration. Changing this requires a restart.").Value)
        { Logger.LogInfo("Telemetry configured_disabled: no probes, patches, HUD, or export started."); return; }
        hudEnabled = Config.Bind("HUD", "Enabled", true, "Display local diagnostics while in a world.");
        hudKey = Config.Bind("HUD", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "Toggle diagnostics HUD.");
        hudPosition = Config.Bind("HUD", "Position", HudPosition.TopRight, "Screen corner for the diagnostics HUD.");
        interval = Config.Bind("Telemetry", "SampleIntervalSeconds", 1f,
            new ConfigDescription("Snapshot interval; percentile history is bounded to 2048 samples per window.", new AcceptableValueRange<float>(0.5f, 10f)));
        exportServer = Config.Bind("Telemetry", "ExportOnServer", true, "Write snapshots on dedicated servers and player hosts.");
        exportClient = Config.Bind("Telemetry", "ExportOnClient", false, "Also write snapshots when playing as a client.");
        exportPath = Config.Bind("Telemetry", "ExportPath", "/config/valheim-boosted/telemetry/snapshot.json",
            "Local JSON snapshot path. Restart to change. Use a unique path for each game process.");
        configuredPath = exportPath.Value;
        integration = new TelemetryIntegration(Config, message => Logger.LogInfo(message));
        collector = new TelemetryCollector(integration, message => Logger.LogWarning(message));
        TelemetryHooks.Collector = collector;
        TelemetryHooks.Integration = integration;
        integration.Install();
        replication = new ReplicationIntegration(Config, integration, message => Logger.LogInfo(message));
        clientTelemetry = new ClientTelemetryTransport(Config, integration);
        idleServer = new IdleServerIntegration(Config, integration);
        captain = new CaptainIntegration(Config, integration, message => Logger.LogInfo(message));
        nextSample = TelemetryCollector.Now + interval.Value;
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded (build {typeof(Plugin).Module.ModuleVersionId}). Telemetry ready; optional scheduler status is reported separately. F8 toggles HUD.");
    }

    private void FixedUpdate() { if (!stopped) collector?.FixedStep(); }

    private void Update()
    {
        if (collector == null || stopped) return;
        collector.Frame();
        var net = ZNet.instance;
        if (net && !net.IsDedicated() && hudKey.Value.IsDown()) hudEnabled.Value = !hudEnabled.Value;
        double now = TelemetryCollector.Now;
        bool wasIdle = idleServer.Idle;
        idleServer.Tick(now);
        // Publish the new cadence on transitions, and never delay a wake-up snapshot.
        if (wasIdle != idleServer.Idle) nextSample = now;
        try { integration.Audit(now); replication.Audit(now); replication.Advanced.Tick(now); captain.Tick(now); clientTelemetry.Tick(now); }
        catch (Exception ex) { Warn("Patch audit failed: " + ex.Message); }
        if (now < nextSample) return;
        double sampleInterval = idleServer.SampleInterval(interval.Value);
        nextSample = now + sampleInterval;
        try
        {
            latest = collector.Capture();
            latest.sampleIntervalSeconds = sampleInterval;
            clientTelemetry.Capture(latest);
            replication.Advanced.Capture(latest);
            captain.Capture(latest.serverImprovements);
            bool shouldExport = net && (net.IsServer() ? exportServer.Value : exportClient.Value);
            if (shouldExport && exporter == null) exporter = new SnapshotExporter(configuredPath);
            latest.exportError = exporter?.Error;
            // Publish one final menu snapshot when leaving a world, rather than leaving an apparently live server.
            if (shouldExport || wasExporting) exporter?.Publish(latest);
            wasExporting = shouldExport;
            if (latest.exportError != null) Warn("Telemetry export: " + latest.exportError);
            hudText = FormatHud(latest, hudKey.Value.ToString());
            if (latest.role == "client") hudText += "\nClient telemetry: " + clientTelemetry.Status + " · " + clientTelemetry.MarkerShortcut + " marks lag";
        }
        catch (Exception ex) { Warn("Telemetry collection unavailable: " + ex); hudText = "ValheimBoosted: telemetry unavailable (see log)"; }
    }

    private void Warn(string message)
    {
        double now = TelemetryCollector.Now;
        if (now < nextWarning) return;
        nextWarning = now + 30;
        Logger.LogWarning(message);
    }

    private static string Number(double? value, string format = "0.0") => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "n/a";

    private static string FormatHud(TelemetrySnapshot s, string shortcut)
    {
        var text = new StringBuilder("valheim-boosted diagnostics · ").Append(shortcut).Append('\n');
        text.Append("Compatibility: ").Append(s.compatibility?.status ?? "unknown").Append('\n');
        text.Append("Network hook: ").Append(s.networkTimingStatus).Append(" · Receive: ").Append(s.zdoReceiveStatus).Append('\n');
        text.Append("Local frame interval p95/max: ").Append(Number(s.frameIntervalMs.p95)).Append(" / ").Append(Number(s.frameIntervalMs.max)).Append(" ms\n");
        text.Append("Local ZDO update p95: ").Append(Number(s.networkUpdateDurationMs.p95)).Append(" ms\n");
        text.Append("Loaded objects: ").Append(s.loadedObjects?.ToString() ?? "n/a").Append(" · Peers: ").Append(s.readyPeers).Append('\n');
        if (s.role == "client")
        {
            var p = s.peers.Length > 0 ? s.peers[0] : null;
            if (p == null) text.Append("Server connection: unavailable\n");
            else
            {
                text.Append("Server RTT: ").Append(Number(p.rttMs)).Append(" ms · Sample variation: ").Append(Number(p.rttSampleDeltaMs)).Append(" ms\n");
                text.Append("Transport queue delay: ").Append(Number(p.estimatedTransportQueueMs)).Append(" ms\n");
                text.Append("Outstanding / sent unacked: ").Append(Number(p.outstandingBytes / 1024.0)).Append(" / ").Append(Number(p.sentUnacknowledgedReliableBytes / 1024.0)).Append(" KiB\n");
                text.Append("Traffic out/in: ").Append(Number(p.outgoingBytesPerSecond / 1024)).Append(" / ").Append(Number(p.incomingBytesPerSecond / 1024)).Append(" KiB/s\n");
                text.Append("Transport metrics: ").Append(p.measurementStatus).Append('\n');
            }
            text.Append("Remote server/owner CPU: not measured");
        }
        else text.Append("Role: ").Append(s.role).Append(" · Server metrics available in JSON");
        if (s.exportError != null) text.Append("\nSnapshot export failed (see log)");
        return text.ToString();
    }

    private void OnGUI()
    {
        if (collector == null || stopped || !hudEnabled.Value || !ZNet.instance || ZNet.instance.IsDedicated()) return;
        if (hudStyle == null) hudStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true, richText = false, padding = new RectOffset(12, 12, 10, 10) };
        float width = Math.Min(540, Math.Max(200, Screen.width - 24));
        float height = hudStyle.CalcHeight(new GUIContent(hudText), width);
        var position = hudPosition.Value;
        bool right = position == HudPosition.TopRight || position == HudPosition.BottomRight;
        bool bottom = position == HudPosition.BottomLeft || position == HudPosition.BottomRight;
        float x = right ? Math.Max(0, Screen.width - width - 12) : 12;
        float y = bottom ? Math.Max(0, Screen.height - height - 12) : 12;
        GUI.Box(new Rect(x, y, width, height), hudText, hudStyle);
    }

    private void OnApplicationQuit() => Stop();
    private void OnDisable() => idleServer?.Suspend();
    private void OnDestroy() => Stop();

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        idleServer?.Dispose();
        if (exporter != null)
        {
            // A new DTO prevents mutation of a snapshot the worker might still be serializing.
            var final = new TelemetrySnapshot
            {
                processSession = latest?.processSession, worldSession = latest?.worldSession,
                sequence = (latest?.sequence ?? 0) + 1, role = "stopped", running = false,
                capturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                peers = new PeerMetrics[0], compatibility = integration?.Compatibility.Copy(), features = integration?.Snapshot(),
            };
            exporter.Publish(final);
            exporter.Dispose();
        }
        TelemetryHooks.Collector = null;
        TelemetryHooks.Integration = null;
        replication?.Dispose();
        clientTelemetry?.Dispose();
        integration?.Dispose();
    }
}
