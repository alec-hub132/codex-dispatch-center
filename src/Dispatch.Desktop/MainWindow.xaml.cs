using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dispatch.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); Closed += (_, _) => (DataContext as MainViewModel)?.Dispose(); }
}
public sealed class PageVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int index && index.ToString(CultureInfo.InvariantCulture) == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
