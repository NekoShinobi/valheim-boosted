using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ValheimBoosted;

internal static class ReferenceChecks
{
    internal static int Run(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        int matched = 0;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Name) != "ZDOMan") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                string name = metadata.GetString(method.Name);
                string expected = name == "Update" ? CompatibilityPolicy.UpdateHash : name == "RPC_ZDOData" ? CompatibilityPolicy.ReceiveHash : null;
                if (expected == null) continue;
                string actual = Convert.ToHexString(SHA256.HashData(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes())).ToLowerInvariant();
                if (!CompatibilityPolicy.MatchesFingerprint(expected, actual)) throw new InvalidOperationException($"Unreviewed reference contract: ZDOMan.{name} IL={actual}. Inspect game changes before changing the allowlist.");
                matched++;
            }
        }
        if (matched != 2) throw new InvalidOperationException("Expected exactly two reviewed ZDOMan targets");
        Console.WriteLine("PASS: both game reference patch fingerprints match the reviewed baseline.");
        return 0;
    }
}
