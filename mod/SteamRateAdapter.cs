using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Steamworks;

namespace ValheimBoosted;

// Connection scope only. Never changes Steam globals, minimum rate or send-buffer capacity.
internal sealed class SteamRateAdapter
{
    private readonly MethodInfo read, write;
    private readonly FieldInfo connection;
    internal string Backend { get; }
    internal SteamRateAdapter()
    {
        string sockets = SteamMetrics.Initialize();
        Backend = sockets.Replace("NetworkingSockets", "NetworkingUtils");
        Type utils = typeof(SteamNetworkingUtils).Assembly.GetType(Backend, true);
        read = utils.GetMethod("GetConfigValue"); write = utils.GetMethod("SetConfigValue");
        var prefix = new[] { typeof(ESteamNetworkingConfigValue), typeof(ESteamNetworkingConfigScope), typeof(IntPtr) };
        if (read == null || !read.IsStatic || read.ReturnType != typeof(ESteamNetworkingGetConfigValueResult)
            || !read.GetParameters().Select(p => p.ParameterType).SequenceEqual(prefix.Concat(new[] { typeof(ESteamNetworkingConfigDataType).MakeByRefType(), typeof(IntPtr), typeof(ulong).MakeByRefType() }))
            || write == null || !write.IsStatic || write.ReturnType != typeof(bool)
            || !write.GetParameters().Select(p => p.ParameterType).SequenceEqual(prefix.Concat(new[] { typeof(ESteamNetworkingConfigDataType), typeof(IntPtr) })))
            throw new InvalidOperationException("Steam connection config signature mismatch");
        string guard = sockets.Contains("GameServer") ? "TestIfAvailableGameServer" : "TestIfAvailableClient";
        if (!CompatibilityPolicy.Calls(read).Any(m => m.Name == guard) || !CompatibilityPolicy.Calls(write).Any(m => m.Name == guard))
            throw new InvalidOperationException("Steam config interface initialization context mismatch");
        connection = typeof(ZSteamSocket).GetField("m_con", BindingFlags.NonPublic | BindingFlags.Instance);
    }
    private IntPtr Handle(ZSteamSocket socket) => new IntPtr((long)((HSteamNetConnection)connection.GetValue(socket)).m_HSteamNetConnection);
    internal SteamRateSetting Read(ZSteamSocket socket, bool maximum)
    {
        IntPtr pointer = Marshal.AllocHGlobal(4);
        try
        {
            object[] args = { maximum ? ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax : ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, Handle(socket), default(ESteamNetworkingConfigDataType), pointer, (ulong)4 };
            var result = (ESteamNetworkingGetConfigValueResult)read.Invoke(null, args);
            if ((result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK && result != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited)
                || (ESteamNetworkingConfigDataType)args[3] != ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32 || (ulong)args[5] != 4)
                throw new InvalidOperationException("Steam config read failed: " + result);
            int value = Marshal.ReadInt32(pointer);
            if (value < 0) throw new InvalidOperationException("Invalid Steam rate readback");
            return new SteamRateSetting { Value = value, Inherited = result == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited };
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    internal void WriteMaximum(ZSteamSocket socket, int? bytesPerSecond)
    {
        IntPtr pointer = bytesPerSecond.HasValue ? Marshal.AllocHGlobal(4) : IntPtr.Zero;
        try
        {
            if (bytesPerSecond.HasValue) Marshal.WriteInt32(pointer, bytesPerSecond.Value);
            if (!(bool)write.Invoke(null, new object[] { ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, Handle(socket), ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, pointer }))
                throw new InvalidOperationException("Steam rejected connection SendRateMax write");
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); }
    }
}
