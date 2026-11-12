using System.Windows;
using Dispatch.Core;

namespace Dispatch.Desktop;

public partial class ManualReleaseDialog : Window
{
    private readonly bool waiting;
    public bool CanConfirm=>waiting || Stopped.IsChecked==true && Cleaned.IsChecked==true;
    public ManualReleaseDialog(JobRow row)
    {
        InitializeComponent();DataContext=row;
        waiting=row.Raw.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient;
        CleanupChecks.Visibility=waiting?Visibility.Collapsed:Visibility.Visible;
        Explanation.Text=waiting?"这次提交尚未获准，确认后撤回等待或接单预留。":
            "请先核对原测试确已停止并完成清理。此按钮不会停止测试或代做清理；确认后才释放本次许可，允许下一窗口使用资源。";
        ConfirmButton.IsEnabled=CanConfirm;
    }
    private void ConfirmationChanged(object sender,RoutedEventArgs e){if(ConfirmButton is not null)ConfirmButton.IsEnabled=CanConfirm;}
    private void Confirm(object sender,RoutedEventArgs e){if(CanConfirm)DialogResult=true;}
}
