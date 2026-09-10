using System;
using System.IO;
using System.IO.Compression;

namespace ValheimBoosted;

// Independent frames: no shared dictionary, mutable baseline or retry-time re-encoding.
internal static class LosslessZdoCodec
{
    internal const int Version = 1, Algorithm = 1, Dictionary = 0, MaximumRawBytes = 65536;
    internal const int HeaderBytes = 20;
    private const uint Magic = 0x31425a56; // VZB1
    private static readonly uint[] CrcTable = MakeTable();

    internal static byte[] Encode(byte[] raw)
    {
        if (raw == null || raw.Length < 512 || raw.Length > MaximumRawBytes) return null;
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(raw, 0, raw.Length);
            compressed = output.ToArray();
        }
        if (compressed.Length + HeaderBytes + 16 + 64 >= raw.Length || (compressed.Length + HeaderBytes + 16) * 100L > raw.Length * 95L) return null;
        using (var output = new MemoryStream(HeaderBytes + compressed.Length))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write(Magic); writer.Write((byte)Version); writer.Write((byte)Algorithm); writer.Write((ushort)Dictionary);
            writer.Write(raw.Length); writer.Write(compressed.Length); writer.Write(Crc(raw)); writer.Write(compressed);
            return output.ToArray();
        }
    }

    internal static byte[] Decode(byte[] frame)
    {
        if (frame == null || frame.Length < HeaderBytes || frame.Length > MaximumRawBytes) throw new InvalidDataException("Invalid compressed frame size");
        using (var input = new MemoryStream(frame, false))
        using (var reader = new BinaryReader(input))
        {
            if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version || reader.ReadByte() != Algorithm || reader.ReadUInt16() != Dictionary)
                throw new InvalidDataException("Unsupported compressed frame format");
            int rawSize = reader.ReadInt32(), compressedSize = reader.ReadInt32(); uint checksum = reader.ReadUInt32();
            if (rawSize < 512 || rawSize > MaximumRawBytes || compressedSize <= 0 || compressedSize != frame.Length - HeaderBytes)
                throw new InvalidDataException("Invalid compressed frame lengths");
            var raw = new byte[rawSize];
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress, true))
            {
                int position = 0;
                while (position < raw.Length)
                {
                    int count = deflate.Read(raw, position, raw.Length - position);
                    if (count == 0) throw new InvalidDataException("Truncated compressed frame");
                    position += count;
                }
                if (deflate.ReadByte() != -1) throw new InvalidDataException("Decompression output exceeds declared size");
            }
            if (Crc(raw) != checksum) throw new InvalidDataException("Compressed frame checksum mismatch");
            return raw;
        }
    }
    private static uint Crc(byte[] bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes) value = (value >> 8) ^ CrcTable[(value ^ b) & 255];
        return ~value;
    }
    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320U);
            table[i] = value;
        }
        return table;
    }
}
