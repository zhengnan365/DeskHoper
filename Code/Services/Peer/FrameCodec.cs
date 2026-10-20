using System.Buffers.Binary;
using System.IO;

namespace MonitorSwitch.Services;

public enum FrameType : byte
{
    Identity = 1,
    Challenge = 2,
    Proof = 3,
    Paired = 4,
    Rejected = 5,
    Ping = 6,
    Pong = 7,
    Text = 10,
    FileHeader = 11,
    FileChunk = 12,
    FileDone = 13,
    FileEnd = 14
}

public readonly record struct Frame(FrameType Type, byte[] Payload);

/// 帧格式：[1 字节类型][4 字节大端负载长度][负载]。
public static class FrameCodec
{
    public const int MaxPayloadBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, FrameType type, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        if (payload.Length > 0) await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        try { await stream.ReadExactlyAsync(header, cancellationToken); }
        catch (EndOfStreamException) { return null; }
        catch (IOException) { return null; }
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayloadBytes) throw new InvalidDataException($"帧长度越界：{length}");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new Frame((FrameType)header[0], payload);
    }
}
