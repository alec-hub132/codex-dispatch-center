using System.Windows;
using Dispatch.Core;

namespace Dispatch.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = Transport.Argument(e.Args, "--data", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDispatch"));
        var log = new Diagnostics(System.IO.Path.Combine(data, "logs"));
        DispatcherUnhandledException += (_, error) => { log.Write("DESKTOP_ERROR", "桌面程序发生异常", error: error.Exception); error.Handled = true; MessageBox.Show("操作失败，详情已记录到 bug.jsonl。", "Codex 调度工具"); };
        try
        {
            var backend = new DesktopBackend(Transport.Argument(e.Args, "--instance", "default"), data,
                Transport.Argument(e.Args, "--home", ""));
            if (e.Args.Contains("--self-test"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await DesktopSmoke.Run(backend, Transport.Argument(e.Args, "--output", System.IO.Path.Combine(data, "smoke")));
                Shutdown(0);
                return;
            }
            var model = new MainViewModel(backend);
            var window = new MainWindow { DataContext = model };
            MainWindow = window;
            window.Show();
            await model.Initialize();
        }
        catch (Exception ex) { log.Write("STARTUP_FAILED", "启动失败", error: ex); if (!e.Args.Contains("--self-test")) MessageBox.Show(ex.Message, "启动失败"); Shutdown(1); }
    }
}
