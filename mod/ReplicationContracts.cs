using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimBoosted;

internal sealed class ReplicationContracts
{
    internal const string ClientScheduleHash = "94e2e1fde2a1814725f7a221eacd34bc9d14ae83aa12f7f0f0199c72a39aa874";
    internal const string ServerScheduleHash = "3f07a7733c48a5e6dededb1869bd4f5009ac66848aa4c5c9214142b71ffab401";
    internal const string ClientSendHash = "c5b10b489a5f5448da36708199e5955e3e3033de801c2853e4291a55a6ecbfb8";
    internal const string ServerSendHash = "ee39ad2d80aaa2fbf761a61e7ad89d1dfb03653efc8c6f149138638b092a69be";
    internal readonly MethodInfo Schedule, Send;
    internal readonly FieldInfo Peers, Peer, Sent, Timer, Cursor;
    internal ReplicationContracts()
    {
        var type = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic);
        if (type == null) throw new InvalidOperationException("Missing ZDOPeer type");
        Schedule = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOToPeers2", new[] { typeof(float) });
        Send = AccessTools.DeclaredMethod(typeof(ZDOMan), "SendZDOs", new[] { type, typeof(bool) });
        if (!CompatibilityPolicy.Signature(Schedule, typeof(ZDOMan), typeof(void), typeof(float))
            || !CompatibilityPolicy.Signature(Send, typeof(ZDOMan), typeof(bool), type, typeof(bool)))
            throw new InvalidOperationException("ZDO scheduler/send signature mismatch");
        string schedule = CompatibilityPolicy.Fingerprint(Schedule), send = CompatibilityPolicy.Fingerprint(Send);
        // Only reviewed pairs: never mix unrelated client/server method versions.
        if (!((schedule == ClientScheduleHash && send == ClientSendHash) || (schedule == ServerScheduleHash && send == ServerSendHash)))
            throw new InvalidOperationException("Unreviewed ZDO scheduler/send fingerprints: " + schedule + "/" + send);
        Peers = Field(typeof(ZDOMan), "m_peers", typeof(List<>).MakeGenericType(type));
        Peer = Field(type, "m_peer", typeof(ZNetPeer));
        Sent = Field(typeof(ZDOMan), "m_zdosSent", typeof(int));
        Timer = Field(typeof(ZDOMan), "m_sendTimer", typeof(float));
        Cursor = Field(typeof(ZDOMan), "m_nextSendPeer", typeof(int));
    }
    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = AccessTools.DeclaredField(type, name);
        if (field == null || field.IsStatic || field.FieldType != expected) throw new InvalidOperationException("Field contract mismatch: " + name);
        return field;
    }
}
