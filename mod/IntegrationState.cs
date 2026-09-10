using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ValheimBoosted;

public sealed class CompatibilityInfo
{
    public string gameVersion;
    public uint? networkVersion;
    public string gameModuleId;
    public string status = "unavailable";
    public string steamInterface;
    public CompatibilityInfo Copy() => (CompatibilityInfo)MemberwiseClone();
}

public sealed class FeatureStatus
{
    public string id;
    public bool enabled;
    public string status;
    public string detail;
    public string target;
    public long invocations;
    public FeatureStatus Copy() => (FeatureStatus)MemberwiseClone();
    public bool Collect => status == "available" || status == "installed_waiting" || status == "active";
}

internal static class CompatibilityPolicy
{
    internal const string GameVersion = "1.0.7";
    internal const uint NetworkVersion = 39;
    internal const string UpdateHash = "6abb99226c81efa7feae749ccdb6744751e4dbef9df639ee4fbe2a623c8488a2";
    internal const string ReceiveHash = "8cce5e4fde99f56bd1c6a719a8a5788f5b465a887f89f72eb9693a9a3aaf20a3";
    internal const string ServerReceiveHash = "79287d6cb3029b2c5f15d45b857ca105e5296ae1bbf3049d0ff0102c2a0e7792";
    internal static bool MatchesFingerprint(string expected, string actual) => actual == expected
        || (expected == ReceiveHash && actual == ServerReceiveHash);
    internal static string Evaluate(string game, uint? protocol) =>
        game != GameVersion ? "unsupported_game" : protocol != NetworkVersion ? "unsupported_protocol" : "supported";

    internal static string Fingerprint(MethodInfo method)
    {
        var bytes = method?.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) return null;
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static bool Signature(MethodInfo method, Type declaring, Type result, params Type[] parameters) =>
        method != null && method.DeclaringType == declaring && !method.IsStatic && !method.IsGenericMethod
        && method.ReturnType == result && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters);

    // Decode instructions instead of mistaking operand bytes for call opcodes.
    internal static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
            .ToDictionary(c => unchecked((ushort)c.Value));
        var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? throw new InvalidOperationException("Method has no IL");
        for (int i = 0; i < bytes.Length;)
        {
            ushort value = bytes[i++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[i++]);
            var code = codes[value];
            int size;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch: size = checked(4 + 4 * BitConverter.ToInt32(bytes, i)); break;
                default: size = 4; break;
            }
            if (size < 0 || i + size > bytes.Length) throw new InvalidOperationException("Malformed IL");
            if (code == OpCodes.Call || code == OpCodes.Callvirt)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, i));
            i += size;
        }
    }
}
