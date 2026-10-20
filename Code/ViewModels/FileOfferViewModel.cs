using MonitorSwitch.Services;

namespace MonitorSwitch.ViewModels;

public sealed class FileOfferViewModel
{
    public FileOfferViewModel(FileOffer offer, string receiveDirectory, bool autoAcceptEnabled)
    {
        Lines = offer.Files.Select(file => $"{DescribeSize(file.Size)}    {file.Name}").ToList();
        Total = DescribeSize(offer.TotalBytes);
        CountLine = $"共 {offer.Files.Count} 个文件，合计";
        ReceiveDirectory = receiveDirectory;
        AutoAcceptEnabled = autoAcceptEnabled;
    }

    public IReadOnlyList<string> Lines { get; }
    public string Total { get; }
    public string CountLine { get; }
    public string ReceiveDirectory { get; }
    /// 已经勾过“以后自动接收”时不再显示这个勾选框。
    public bool AutoAcceptEnabled { get; }
    public bool AutoAcceptChosen { get; set; }

    private static string DescribeSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
