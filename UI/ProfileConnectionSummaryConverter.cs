using System.Globalization;
using System.Windows.Data;
using MonitorSwitch.Models;
using MonitorSwitch.ViewModels;

namespace MonitorSwitch.Views;

public sealed class ProfileConnectionSummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProfileViewModel profile) return string.Empty;
        if (!string.IsNullOrWhiteSpace(profile.ConnectionSummary)) return profile.ConnectionSummary;
        return string.Join(" + ", profile.Profile.Monitors.Select(item => item.InputKind switch
        {
            InputSourceKind.DisplayPort => "DP",
            InputSourceKind.UsbC => "Type-C",
            InputSourceKind.Hdmi => "HDMI",
            _ => item.Input switch { 15 => "DP", 16 => "Type-C", 17 => "HDMI", _ => "未知" }
        }));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
