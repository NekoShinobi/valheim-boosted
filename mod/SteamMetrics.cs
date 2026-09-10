using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace ValheimBoosted;

internal static class SteamMetrics
{
    private static readonly FieldInfo Connection = AccessTools.Field(typeof(ZSteamSocket), "m_con");
    private static readonly FieldInfo SendQueue = AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue");

    public static void Read(ZSteamSocket socket, PeerMetrics metrics)
    {
        if (Connection == null)
        {
            metrics.measurementStatus = "steam_connection_field_unavailable";
            return;
        }
        var connection = (HSteamNetConnection)Connection.GetValue(socket);
        var status = default(SteamNetConnectionRealTimeStatus_t);
        var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
        var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lane);
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
        if (SendQueue?.GetValue(socket) is Queue<byte[]> queue)
        {
            long bytes = 0;
            foreach (var packet in queue) bytes += packet.Length;
            metrics.applicationQueuedBytes = bytes;
            metrics.outstandingBytes = bytes + status.m_cbPendingReliable + status.m_cbPendingUnreliable + status.m_cbSentUnackedReliable;
        }
    }

    private static double? Quality(float value) => value >= 0 && value <= 1 ? (double?)value : null;
    private static double? Nonnegative(float value) => value >= 0 && !float.IsInfinity(value) ? (double?)value : null;
}
