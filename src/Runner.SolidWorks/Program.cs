using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Runner.SolidWorks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        object? application = null;
        var passed = false;
        var blocked = true;
        var cleanupOk = true;
        var summary = "请先手动打开 SolidWorks，再重新提交只读检查。";
        object? details = null;
        var stage = "进程环境检查";
        using var exclusive = new Mutex(false, "Global\\CodexDispatch-SolidWorks-Readonly");
        var acquired = false;
        try
        {
            if (args.Length != 0 && !(args.Length == 1 && args[0] == "--environment-only")) return 2;
            var processes = Process.GetProcessesByName("SLDWORKS");
            try
            {
                if (processes.Length == 0) { /* 不创建宿主，不启动应用。 */ }
                else if (processes.Length != 1 || processes[0].SessionId != Process.GetCurrentProcess().SessionId)
                    summary = "检测到多个或不同登录会话中的 SolidWorks，无法确定目标，请人工确认。";
                else if (args.Contains("--environment-only"))
                    summary = "仅完成进程环境检查，未连接 COM；需要在人工验收中执行只读连接。";
                else
                {
                    try { acquired = exclusive.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; summary = "发现上一个只读执行器异常退出，请先确认宿主状态。"; }
                    if (!acquired) summary = "其他只读执行器正在占用 SolidWorks，请稍后重试。";
                    else if (!summary.StartsWith("发现上一个", StringComparison.Ordinal))
                    {
                        var clsid = Guid.Empty;
                        stage = "解析宿主CLSID";
                        Marshal.ThrowExceptionForHR(CLSIDFromProgID("SldWorks.Application", out clsid));
                        stage = "取得运行中宿主";
                        Marshal.ThrowExceptionForHR(GetActiveObject(ref clsid, IntPtr.Zero, out application));
                        stage = "读取版本";
                        string revision = DispatchReader.Revision(application);
                        stage = "读取文档数量";
                        int documentCount = DispatchReader.DocumentCount(application);
                        if (string.IsNullOrWhiteSpace(revision) || documentCount < 0) throw new InvalidOperationException("宿主返回无效的版本或文档数量。");
                        details = new { revision, documentCount, solidWorksPid = processes[0].Id, readOnly = true, apartment = Thread.CurrentThread.GetApartmentState().ToString() };
                        passed = true; blocked = false;
                        summary = $"只读连接成功：版本 {revision}，已打开文档 {documentCount} 个。未验证插件功能。";
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception ex)
        {
            summary = "只读连接暂不可用：" + ex.Message;
            details = new { error = ex.GetType().Name, hresult = ex.HResult, readOnly = true, stage, diagnostic = ex.ToString() };
        }
        finally
        {
            try { if (application is not null && Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application); }
            catch (Exception ex) { cleanupOk = false; summary += " COM 引用释放失败：" + ex.Message; }
            if (acquired) exclusive.ReleaseMutex();
        }
        Console.WriteLine(JsonSerializer.Serialize(new { passed, blocked, cleanupOk, summary, details }));
        return 0;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object application);
}
