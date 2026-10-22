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
    FileEnd = 14,
    InputArm = 20,
    InputAck = 21,
    InputReject = 22,
    InputStop = 23,
    Keyboard = 24,
    Mouse = 25,
    InputMode = 26
}

public readonly record struct Frame(FrameType Type, byte[] Payload);

/// 帧格式：[1 字节类型][4 字节大端负载长度][负载]。
public static class FrameCodec
{
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>
    /// 超过这个量的载荷不合成一次写：文件块有 256KB，为了省一个头而整体多拷一份不值。
    /// </summary>
    private const int CombinedWriteMaxPayload = 8 * 1024;

    public static async Task WriteAsync(Stream stream, FrameType type, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        // 键鼠、心跳、配对这类小帧合成一次写：SslStream 每次 Write 就是一个 TLS 记录，
        // 头和体分开写会让一个 21 字节的鼠标帧变成两个包，交互手感就散成一段一段的。
        if (payload.Length <= CombinedWriteMaxPayload)
        {
            var frame = new byte[header.Length + payload.Length];
            header.CopyTo(frame, 0);
            payload.CopyTo(frame, header.Length);
            await stream.WriteAsync(frame, cancellationToken);
        }
        else
        {
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
        }
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
