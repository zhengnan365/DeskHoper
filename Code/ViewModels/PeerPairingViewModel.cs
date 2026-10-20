namespace MonitorSwitch.ViewModels;

public sealed class PeerPairingViewModel : ObservableObject
{
    private string _host = string.Empty;
    private string _code = string.Empty;

    public PeerPairingViewModel(bool isConnectMode, string? localPairingCode = null)
    {
        IsConnectMode = isConnectMode;
        LocalPairingCode = localPairingCode;
    }

    /// true = 我要连别人（填对端地址和配对码）；false = 我把配对码给对方看。
    public bool IsConnectMode { get; }
    public string? LocalPairingCode { get; }
    public string Title => IsConnectMode ? "连接对端" : "本机配对码";
    public string Host { get => _host; set { if (SetProperty(ref _host, value)) OnPropertyChanged(nameof(CanConnect)); } }
    public string Code { get => _code; set { if (SetProperty(ref _code, value)) OnPropertyChanged(nameof(CanConnect)); } }
    public bool CanConnect => !string.IsNullOrWhiteSpace(Host) && Code.Trim().Length == 6 && Code.Trim().All(char.IsDigit);
}
