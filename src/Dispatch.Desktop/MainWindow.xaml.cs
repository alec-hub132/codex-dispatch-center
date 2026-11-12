using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dispatch.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged+=(_,e)=>{if(e.NewValue is MainViewModel vm)vm.ConfirmManualRelease=row=>Task.FromResult(new ManualReleaseDialog(row){Owner=this}.ShowDialog()==true);};
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }
    private void QueueSelectionChanged(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if(DataContext is MainViewModel {IsRefreshing:false} vm)vm.SelectedJob=e.NewValue as JobRow;
    }
}
public sealed class PageVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int index && index.ToString(CultureInfo.InvariantCulture) == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
