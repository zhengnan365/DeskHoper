namespace MonitorSwitch.Models;

public sealed class MonitorInputMapping
{
    public uint DisplayPort { get; init; } = 15;
    public uint UsbC { get; init; } = 16;
    public uint? Hdmi { get; init; } = 17;
    public uint DisplayPortValue { get => DisplayPort; init => DisplayPort = value; }
    public uint UsbCValue { get => UsbC; init => UsbC = value; }
    public uint? HdmiValue { get => Hdmi; init => Hdmi = value; }

    public MonitorInputSource ToSource(uint value) => value switch
    {
        var v when v == DisplayPort => MonitorInputSource.DisplayPort,
        var v when v == UsbC => MonitorInputSource.UsbC,
        var v when Hdmi is { } hdmi && v == hdmi => MonitorInputSource.Hdmi,
        _ => MonitorInputSource.Unknown
    };
}
