using System;
using System.IO;

namespace ValheimBoosted;

internal sealed class CompressionSession
{
    internal readonly Guid LocalToken = Guid.NewGuid();
    internal Guid RemoteToken { get; private set; }
    internal bool SendReady { get; private set; }
    internal bool ReceiveReady { get; private set; }
    internal bool Stopped { get; private set; }
    private bool offered;
    internal int Attempts { get; private set; }
    private int received;
    internal byte[] Offer() { offered = true; Attempts++; return Message(1, LocalToken); }
    internal byte[] Stop() { Stopped = true; SendReady = false; return Message(3, LocalToken); }
    internal byte[] Receive(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 29 || ++received > 32) throw new InvalidDataException("Invalid compression negotiation size or budget");
        using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
        {
            if (reader.ReadUInt32() != 0x31434256 || reader.ReadByte() != LosslessZdoCodec.Version || reader.ReadByte() != LosslessZdoCodec.Algorithm
                || reader.ReadUInt16() != LosslessZdoCodec.Dictionary || reader.ReadInt32() != LosslessZdoCodec.MaximumRawBytes)
                throw new InvalidDataException("Compression capabilities do not match");
            byte kind = reader.ReadByte(); var token = new Guid(reader.ReadBytes(16));
            if (token == Guid.Empty) throw new InvalidDataException("Invalid compression session");
            if (kind == 1 && !Stopped)
            {
                if (ReceiveReady && token != RemoteToken) throw new InvalidDataException("Compression session changed on an existing connection");
                RemoteToken = token; ReceiveReady = true; return Message(2, token);
            }
            if (kind == 2 && offered && token == LocalToken && !Stopped) { SendReady = true; return null; }
            if (kind == 3 && ReceiveReady && token == RemoteToken) { Stopped = true; SendReady = false; return null; }
            throw new InvalidDataException("Unexpected compression negotiation message");
        }
    }
    internal byte[] Frame(byte[] encoded)
    {
        if (!SendReady || Stopped) throw new InvalidOperationException("Compression send direction is not negotiated");
        var frame = new byte[16 + encoded.Length]; Buffer.BlockCopy(LocalToken.ToByteArray(), 0, frame, 0, 16); Buffer.BlockCopy(encoded, 0, frame, 16, encoded.Length); return frame;
    }
    internal byte[] Decode(byte[] frame)
    {
        if (!ReceiveReady || frame == null || frame.Length < 16 + LosslessZdoCodec.HeaderBytes || frame.Length > 16 + LosslessZdoCodec.MaximumRawBytes)
            throw new InvalidDataException("Unnegotiated or oversized compressed payload");
        var token = new byte[16]; Buffer.BlockCopy(frame, 0, token, 0, 16);
        if (new Guid(token) != RemoteToken) throw new InvalidDataException("Compressed payload belongs to another connection session");
        var encoded = new byte[frame.Length - 16]; Buffer.BlockCopy(frame, 16, encoded, 0, encoded.Length);
        return LosslessZdoCodec.Decode(encoded);
    }
    private static byte[] Message(byte kind, Guid token)
    {
        using (var output = new MemoryStream(29))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write(0x31434256U); writer.Write((byte)LosslessZdoCodec.Version); writer.Write((byte)LosslessZdoCodec.Algorithm);
            writer.Write((ushort)LosslessZdoCodec.Dictionary); writer.Write(LosslessZdoCodec.MaximumRawBytes); writer.Write(kind); writer.Write(token.ToByteArray());
            return output.ToArray();
        }
    }
}
