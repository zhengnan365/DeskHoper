using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonitorSwitch.Services;

internal readonly record struct LocalSegment(IPAddress Address, IPAddress? Mask, bool HasGateway, bool Wired)
{
    public IPAddress? Broadcast => Mask is null ? null : SubnetHelper.Broadcast(Address, Mask);
}

/// 只认物理网卡。Tailscale、Hyper-V/VMware 这类虚拟网卡各自编了一个内网段，对面拿那个地址回不来，
/// 所以本机地址和发现层的收发都限定在有线、无线这两类网卡上。
internal static class PhysicalNetwork
{
    /// 驱动描述是驱动自带的英文，中文版 Windows 也一样；网卡名会被用户和系统改，所以两个都匹配。
    private static readonly string[] VirtualAdapterHints =
        ["Hyper-V", "VEthernet", "VMware", "VMnet", "VirtualBox", "VBox", "Tailscale", "Wintun", "Tap Adapter", "Bluetooth", "Loopback", "Local Area Connection*"];

    public static List<LocalSegment> Segments()
    {
        var segments = new List<LocalSegment>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            if (VirtualAdapterHints.Any(hint => nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)
                                                || nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))) continue;
            var properties = nic.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork);
            var wired = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (unicast.Address.GetAddressBytes()[0] == 169) continue;   // 没拿到 DHCP 的临时地址，报出去对面也连不回来
                segments.Add(new LocalSegment(unicast.Address, unicast.IPv4Mask, hasGateway, wired));
            }
        }
        return segments;
    }

    /// 对端真能走到的本机 IPv4。填了或发现了地址就先问系统"走哪块网卡到它"，答案必须落在物理网卡上才认——
    /// 到 Tailscale 地址的路由会探出 Tailscale 的网卡，那种结果要丢掉。剩下按有网关优先、有线优先于无线排名。
    public static string? BestAddress(string? probeTarget)
    {
        var segments = Segments();
        if (segments.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(probeTarget)
            && RouteProbe(probeTarget) is { } probed
            && segments.Any(segment => segment.Address.Equals(probed))) return probed.ToString();
        return segments
            .OrderBy(segment => (segment.HasGateway ? 0 : 100) + (segment.Wired ? 0 : 5))
            .ThenBy(segment => segment.Address.ToString(), StringComparer.Ordinal)
            .First().Address.ToString();
    }

    /// 这个地址是不是和我某块物理网卡同网段。发现层用它挡掉从虚拟网卡上冒出来的"对端"。
    public static bool IsOnLocalSegment(IPAddress address) =>
        Segments().Any(segment => segment.Mask is not null && SubnetHelper.SameSegment(address, segment.Address, segment.Mask));

    private static IPAddress? RouteProbe(string target)
    {
        try
        {
            using var probe = new UdpClient();
            probe.Connect(target, 1);   // 只让系统选路，一个包都不发
            return (probe.Client.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
    }
}

internal static class SubnetHelper
{
    public static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var bytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) bytes[i] = (byte)(bytes[i] | ~maskBytes[i]);
        return new IPAddress(bytes);
    }

    public static bool SameSegment(IPAddress address, IPAddress other, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var b = other.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
    }
}
