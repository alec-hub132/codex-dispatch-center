using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatch.Core;
using Microsoft.Win32;

namespace Dispatch.Daemon;

internal static class SolidWorksRecoveryWindows
{
    private delegate bool EnumWindow(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,EnumWindow callback,IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    private static string Title(IntPtr h){var b=new StringBuilder(1024);GetWindowText(h,b,b.Capacity);return b.ToString();}
    private static string ClassName(IntPtr h){var b=new StringBuilder(128);GetClassName(h,b,b.Capacity);return b.ToString();}
    public static Process[] Hosts()=>Process.GetProcessesByName("SLDWORKS");
    public static string RecoveryDirectory(string executable)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var version=FileVersionInfo.GetVersionInfo(executable).FileMajorPart+1992;
        using var key=Registry.CurrentUser.OpenSubKey($"Software\\SolidWorks\\SOLIDWORKS {version}\\General");
        return key?.GetValue("AutoRecover Directory") as string??throw new InvalidOperationException("无法核对当前版本的自动恢复目录。");
    }
    private static bool Reporter(int candidate,int original,string executable,DateTimeOffset started)
    {
        try
        {
            using var process=Process.GetProcessById(candidate);
            if(process.SessionId!=Process.GetCurrentProcess().SessionId || new DateTimeOffset(process.StartTime)<started)return false;
            var path=process.MainModule?.FileName;if(path is null)return false;
            var name=Path.GetFileName(path);
            var isVendor=name.Equals("SLDEXITAPP.exe",StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(path)==Path.GetDirectoryName(executable);
            var isWer=name.Equals("WerFault.exe",StringComparison.OrdinalIgnoreCase) &&
                new[]{Environment.SystemDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"SysWOW64")}.Contains(Path.GetDirectoryName(path),StringComparer.OrdinalIgnoreCase);
            if(!isVendor && !isWer)return false;
            var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add("-NoProfile");info.ArgumentList.Add("-NonInteractive");info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"Get-CimInstance Win32_Process -Filter 'ProcessId = {candidate}' | Select-Object ParentProcessId,CommandLine | ConvertTo-Json -Compress");
            using var query=Process.Start(info)??throw new InvalidOperationException();
            var output=query.StandardOutput.ReadToEndAsync();var error=query.StandardError.ReadToEndAsync();
            if(!query.WaitForExit(3000)){query.Kill(true);return false;}
            Task.WhenAll(output,error).GetAwaiter().GetResult();
            using var json=JsonDocument.Parse(output.Result);var r=json.RootElement;
            return isVendor?r.GetProperty("ParentProcessId").GetInt32()==original:
                Regex.IsMatch(r.GetProperty("CommandLine").GetString()??"",@"(?:^|\s)-p\s+"+original+@"(?:\s|$)",RegexOptions.IgnoreCase);
        }
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or ArgumentException){return false;}
    }
    public static IntPtr[] CrashWindows(int pid,DateTimeOffset started,string executable)
    {
        var windows=new List<IntPtr>();var reporters=new Dictionary<int,bool>();
        EnumWindows((h,_)=>
        {
            if(!IsWindowVisible(h))return true;
            var title = Title(h);
            if (!RecoveryFiles.IsCrashCaption(title))
            {
                if (!title.Contains("SOLIDWORKS",StringComparison.OrdinalIgnoreCase) && !title.Contains("SLDWORKS",StringComparison.OrdinalIgnoreCase)) return true;
                if (ClassName(h) != "#32770") return true;
                var controls = new List<string>();
                EnumChildWindows(h, (child, _) => { controls.Add(Title(child)); return true; }, IntPtr.Zero);
                if (!RecoveryFiles.IsCrashDialog(title, controls)) return true;
            }
            GetWindowThreadProcessId(h,out var owner);var id=(int)owner;
            if(id==pid)
            {
                try{using var original=Process.GetProcessById(pid);if(new DateTimeOffset(original.StartTime)!=started)return true;}
                catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return true;}
            }
            if(id==pid || (reporters.TryGetValue(id,out var trusted)?trusted:reporters[id]=Reporter(id,pid,executable,started)))windows.Add(h);
            return true;
        },IntPtr.Zero);return windows.ToArray();
    }
    public static void CloseCrashWindows(int pid,DateTimeOffset started,string executable,Func<bool> allowed)
    {
        foreach(var h in CrashWindows(pid,started,executable))
        {
            if(!allowed())throw new OperationCanceledException();
            // 回读标题和归属，避免按过期句柄操作另一个窗口。只选择完成/关闭，绝不点重启。
            if(!CrashWindows(pid,started,executable).Contains(h))continue;
            var clicked=false;
            EnumChildWindows(h,(child,_)=>
            {
                var text=Title(child).Replace("&","").Trim();
                if(!clicked && ClassName(child)=="Button" && new[]{"完成","关闭程序","Done","Close program"}.Contains(text,StringComparer.OrdinalIgnoreCase))
                {if(allowed()){PostMessage(child,0x00F5,IntPtr.Zero,IntPtr.Zero);clicked=true;}}
                return true;
            },IntPtr.Zero);
            if(!clicked && allowed())PostMessage(h,0x0010,IntPtr.Zero,IntPtr.Zero);
        }
    }
    public static bool HasBlockingDialog(int pid)
    {
        var blocked=false;
        EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out var owner);if(owner==pid && IsWindowVisible(h) && ClassName(h)=="#32770")blocked=true;return true;},IntPtr.Zero);
        return blocked;
    }
    public static int CloseStartupNotices(int pid, Func<bool> allowed)
    {
        try
        {
            using var host = Process.GetProcessById(pid);
            if (host.HasExited || host.SessionId != Process.GetCurrentProcess().SessionId) return 0;
            var mainWindow = host.MainWindowHandle;
            var requests = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != pid || window == mainWindow || !IsWindowVisible(window)) return true;
                var controls = new List<string>();
                EnumChildWindows(window, (child, _) => { controls.Add(Title(child)); return true; }, IntPtr.Zero);
                if (RecoveryFiles.IsAddinPerformanceNotice(controls) && allowed()
                    && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) requests++;
                return true;
            }, IntPtr.Zero);
            return requests;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 主观察器持有原进程句柄，下一轮仍能核对退出码并处理崩溃。
            return 0;
        }
    }
    public static void SkipRecoveryPrompts(int pid,Func<bool> allowed)
    {
        EnumWindows((h,_)=>
        {
            GetWindowThreadProcessId(h,out var owner);
            if(owner!=pid || !IsWindowVisible(h) || ClassName(h)!="#32770")return true;
            var title=Title(h);
            var welcome=title.Equals("欢迎使用SOLIDWORKS",StringComparison.OrdinalIgnoreCase) || title.Equals("Welcome to SOLIDWORKS",StringComparison.OrdinalIgnoreCase);
            var recovery=new[]{"文档恢复","自动恢复","Document Recovery","Auto-Recover"}.Any(t=>title.Contains(t,StringComparison.OrdinalIgnoreCase));
            if(!welcome && !recovery)return true;
            var clicked=false;
            EnumChildWindows(h,(child,_)=>
            {
                var text=Title(child).Replace("&","").Trim();
                if(!clicked && ClassName(child)=="Button" && new[]{"不恢复","全部放弃","不打开","Discard All","Don't recover","Do not recover","Skip"}.Contains(text,StringComparer.OrdinalIgnoreCase) && allowed())
                {PostMessage(child,0x00F5,IntPtr.Zero,IntPtr.Zero);clicked=true;}
                return true;
            },IntPtr.Zero);
            if(!clicked && allowed())PostMessage(h,0x0010,IntPtr.Zero,IntPtr.Zero);
            return true;
        },IntPtr.Zero);
    }
}
