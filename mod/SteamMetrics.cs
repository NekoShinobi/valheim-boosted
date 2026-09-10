using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Steamworks;

namespace ValheimBoosted;

internal static class SteamMetrics
{
    private static readonly FieldInfo Connection = AccessTools.Field(typeof(ZSteamSocket), "m_con");
    private static readonly FieldInfo SendQueue = AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue");

    private static MethodInfo readStatus;

    internal static string Initialize()
    {
        if (Connection == null || Connection.IsStatic || Connection.FieldType != typeof(HSteamNetConnection)
            || SendQueue == null || SendQueue.IsStatic || SendQueue.FieldType != typeof(Queue<byte[]>))
            throw new InvalidOperationException("Steam connection/queue field contract changed");
        // GetConnectionQuality uses the client API even in the dedicated-server build.
        // The queue query uses the interface that owns the game's transport connections.
        var queueSize = AccessTools.DeclaredMethod(typeof(ZSteamSocket), "GetSendQueueSize", Type.EmptyTypes);
        if (!CompatibilityPolicy.Signature(queueSize, typeof(ZSteamSocket), typeof(int), Type.EmptyTypes))
            throw new InvalidOperationException("Game transport queue method contract changed");
        var calls = CompatibilityPolicy.Calls(queueSize).OfType<MethodInfo>().Where(m => m.Name == "GetConnectionRealTimeStatus"
            && (m.DeclaringType.FullName == "Steamworks.SteamNetworkingSockets" || m.DeclaringType.FullName == "Steamworks.SteamGameServerNetworkingSockets"))
            .Distinct().ToArray();
        var parameters = new[] { typeof(HSteamNetConnection), typeof(SteamNetConnectionRealTimeStatus_t).MakeByRefType(), typeof(int), typeof(SteamNetConnectionRealTimeLaneStatus_t).MakeByRefType() };
        if (calls.Length != 1 || !calls[0].IsStatic || calls[0].ReturnType != typeof(EResult)
            || !calls[0].GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
            throw new InvalidOperationException("Cannot identify the game's exact Steam status interface");
        var send = AccessTools.DeclaredMethod(typeof(ZSteamSocket), "SendQueuedPackages", Type.EmptyTypes);
        if (!CompatibilityPolicy.Signature(send, typeof(ZSteamSocket), typeof(void), Type.EmptyTypes))
            throw new InvalidOperationException("Game transport send method contract changed");
        var sendCalls = CompatibilityPolicy.Calls(send).OfType<MethodInfo>()
            .Where(m => m.Name == "SendMessageToConnection").Distinct().ToArray();
        if (sendCalls.Length != 1 || sendCalls[0].DeclaringType != calls[0].DeclaringType)
            throw new InvalidOperationException("Steam status interface does not match the game's send interface");
        readStatus = calls[0];
        return readStatus.DeclaringType.FullName;
    }

    public static void Read(ZSteamSocket socket, PeerMetrics metrics)
    {
        if (Connection == null)
        {
            metrics.measurementStatus = "steam_connection_field_unavailable";
            return;
        }
        var connection = (HSteamNetConnection)Connection.GetValue(socket);
        if (readStatus == null) throw new InvalidOperationException("Steam probe not initialized");
        object[] args = { connection, default(SteamNetConnectionRealTimeStatus_t), 0, default(SteamNetConnectionRealTimeLaneStatus_t) };
        var result = (EResult)readStatus.Invoke(null, args);
        var status = (SteamNetConnectionRealTimeStatus_t)args[1];
        if (result != EResult.k_EResultOK)
        {
            metrics.measurementStatus = "steam_status_unavailable:" + result;
            return;
        }
        metrics.measurementStatus = "available";
        metrics.rttMs = status.m_nPing >= 0 ? (double?)status.m_nPing : null;
        metrics.localDeliveryQuality = Quality(status.m_flConnectionQualityLocal);
        metrics.remoteDeliveryQuality = Quality(status.m_flConnectionQualityRemote);
        metrics.outgoingBytesPerSecond = Nonnegative(status.m_flOutBytesPerSec);
        metrics.incomingBytesPerSecond = Nonnegative(status.m_flInBytesPerSec);
        metrics.pendingReliableBytes = status.m_cbPendingReliable;
        metrics.pendingUnreliableBytes = status.m_cbPendingUnreliable;
        metrics.sentUnacknowledgedReliableBytes = status.m_cbSentUnackedReliable;
        long queueMicroseconds = (long)status.m_usecQueueTime;
        metrics.estimatedTransportQueueMs = queueMicroseconds >= 0 ? (double?)(queueMicroseconds / 1000.0) : null;
        metrics.estimatedSendRateBytesPerSecond = status.m_nSendRateBytesPerSecond;
        if (metrics.applicationQueuedBytes.HasValue)
            metrics.outstandingBytes = metrics.applicationQueuedBytes.Value + status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;
    }

    internal static void ReadManagedQueue(ZSteamSocket socket, PeerMetrics metrics)
    {
        if (SendQueue == null || SendQueue.IsStatic || SendQueue.FieldType != typeof(Queue<byte[]>))
            throw new InvalidOperationException("Steam managed queue field contract changed");
        var queue = (Queue<byte[]>)SendQueue.GetValue(socket);
        metrics.applicationQueuedPackets = queue.Count;
        long bytes = 0;
        // Bound inspection work during severe backlogs; never report a partial byte total as complete.
        if (queue.Count > 4096) { metrics.connectionHealthStatus = "queue_scan_limit"; return; }
        foreach (var packet in queue) bytes += packet.Length;
        metrics.applicationQueuedBytes = bytes;
    }

    private static double? Quality(float value) => value >= 0 && value <= 1 ? (double?)value : null;
    private static double? Nonnegative(float value) => value >= 0 && !float.IsInfinity(value) ? (double?)value : null;
}
