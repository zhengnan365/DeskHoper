using System.Windows;

namespace MonitorSwitch.Views;

public partial class FileReceiveWindow : Window
{
    public FileReceiveWindow() => InitializeComponent();
    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
