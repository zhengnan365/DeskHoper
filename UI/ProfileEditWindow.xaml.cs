using System.Windows;

namespace MonitorSwitch.Views;

public partial class ProfileEditWindow : Window
{
    public ProfileEditWindow() => InitializeComponent();
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
