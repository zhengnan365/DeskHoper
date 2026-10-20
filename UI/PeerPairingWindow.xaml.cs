using System.Windows;

namespace MonitorSwitch.Views;

public partial class PeerPairingWindow : Window
{
    public PeerPairingWindow() => InitializeComponent();
    public bool CodeCancelled { get; private set; }
    private void Connect_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelCode_Click(object sender, RoutedEventArgs e) { CodeCancelled = true; DialogResult = false; }
}
