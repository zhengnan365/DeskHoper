using System.Buffers.Binary;
using System.IO;

namespace MonitorSwitch.Services;

public readonly record struct KeyboardInput(bool IsDown, bool IsExtended, ushort VirtualKey, ushort ScanCode);

/// 鼠标一律转成相对位移：两端分辨率和缩放不一样，绝对坐标映射只会带来对不上的光标。
public readonly record struct MouseInput(int DeltaX, int DeltaY, uint EventFlags, int WheelDelta);

/// 一次按键 / 一次鼠标变化的线上格式。定长小端——鼠标移动一秒能上百次，这里再走 JSON 就纯属浪费。
public static class InputPayload
{
    public const int KeyboardSize = 6;
    public const int MouseSize = 16;

    public static byte[] Write(KeyboardInput input)
    {
        var buffer = new byte[KeyboardSize];
        buffer[0] = (byte)(input.IsDown ? 1 : 0);
        buffer[1] = (byte)(input.IsExtended ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), input.VirtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), input.ScanCode);
        return buffer;
    }

    public static KeyboardInput ReadKeyboard(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < KeyboardSize) throw new InvalidDataException($"键盘帧长度不对：{payload.Length}");
        return new KeyboardInput(payload[0] != 0, payload[1] != 0, BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]), BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]));
    }

    public static byte[] Write(MouseInput input)
    {
        var buffer = new byte[MouseSize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), input.DeltaX);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), input.DeltaY);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), input.EventFlags);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), input.WheelDelta);
        return buffer;
    }

    public static MouseInput ReadMouse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MouseSize) throw new InvalidDataException($"鼠标帧长度不对：{payload.Length}");
        return new MouseInput(BinaryPrimitives.ReadInt32LittleEndian(payload[0..]), BinaryPrimitives.ReadInt32LittleEndian(payload[4..]), BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]), BinaryPrimitives.ReadInt32LittleEndian(payload[12..]));
    }
}
