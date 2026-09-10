using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ValheimBoosted;

// Server-scoped pseudonyms: the persisted key and raw platform ID never enter telemetry.
internal sealed class PlayerIdentity
{
    private readonly byte[] key;
    internal bool Available => key != null;
    internal PlayerIdentity(string hex)
    {
        if (hex == null || hex.Length != 64) return;
        var bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return;
        key = bytes;
    }
    internal static string NewKey()
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return Hex(bytes);
    }
    internal string Steam(string platformId)
    {
        if (key == null || !ulong.TryParse(platformId, NumberStyles.None, CultureInfo.InvariantCulture, out var account) || account == 0) return null;
        using (var hmac = new HMACSHA256(key))
            return Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes("valheim-boosted:steam:" + account.ToString(CultureInfo.InvariantCulture))));
    }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
