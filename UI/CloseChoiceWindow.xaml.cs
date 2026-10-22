using System.Windows;

namespace MonitorSwitch.Views;

/// <summary>
/// 第一次点关闭时问一句。用 ShowDialog 走，DialogResult 为 null 表示既没选托盘也没选退出
/// （比如按了 Alt+F4），这种情况调用方要保持窗口原样，不能替他决定退出。
/// </summary>
public partial class CloseChoiceWindow : Window
{
    public CloseChoiceWindow() => InitializeComponent();

    public bool Answered => DialogResult.HasValue;
    public bool ChoseTray => DialogResult == true;
    public bool Remember => RememberCheck.IsChecked == true;

    private void Tray_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
