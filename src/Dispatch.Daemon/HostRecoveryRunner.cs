using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;

// 只有用户启用后才观察当前唯一实例。每次故障持久化登记后仅启动一次。
public sealed class HostRecoveryRunner(Scheduler scheduler)
{
    private readonly SemaphoreSlim action=new(1,1);
    private Process? tracked;
    private string executable="";
    private DateTimeOffset started;
    private string monitorStatus = "宿主监测正在初始化。";
    public string MonitorStatus => Volatile.Read(ref monitorStatus);
    public async Task Run(CancellationToken stop)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                try { await Tick(stop); }
                catch(Exception ex) when(ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {scheduler.Log.Write("HOST_RECOVERY_OBSERVATION_FAILED","宿主进程观察暂不可核对，不猜测崩溃或启动新实例。",error:ex);ResetTracked();}
                await Task.Delay(1500,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally{ResetTracked();}
    }
    private void ResetTracked(){tracked?.Dispose();tracked=null;}
    private async Task Tick(CancellationToken stop)
    {
        if (action.CurrentCount == 0) return;
        if(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("DISPATCH_TEST_NO_HOST_RECOVERY")=="1" || !scheduler.GetHostRecoverySettings().Enabled || !scheduler.GetModules().QueueEnabled){ResetTracked();monitorStatus="自动宿主监测已暂停。";return;}
        if(scheduler.HostRecoveryBlocking)return;
        if(tracked is null)
        {
            var hosts=SolidWorksRecoveryWindows.Hosts();
            try
            {
                if(hosts.Length!=1 || hosts[0].SessionId!=Process.GetCurrentProcess().SessionId){monitorStatus="等待当前登录会话中的唯一SolidWorks实例。";return;}
                executable=hosts[0].MainModule?.FileName??throw new InvalidOperationException("宿主路径无法读取。");
                if(!Path.GetFileName(executable).Equals("SLDWORKS.exe",StringComparison.OrdinalIgnoreCase))return;
                started=new(hosts[0].StartTime);tracked=hosts[0];_ = tracked.Handle;
                monitorStatus=$"已接入SolidWorks进程（PID {tracked.Id}），自动监测正在运行。";
                scheduler.Log.Write("HOST_MONITOR_ATTACHED",monitorStatus);
            }
            finally{foreach(var p in hosts)if(!ReferenceEquals(p,tracked))p.Dispose();}
            return;
        }
        var exited=tracked.HasExited;
        var dialogs=SolidWorksRecoveryWindows.CrashWindows(tracked.Id,started,executable);
        if(!exited && dialogs.Length==0)
        {
            monitorStatus=$"已接入SolidWorks进程（PID {tracked.Id}），自动监测正在运行。";
            var ownsNoticeControl=false;
            try
            {
                bool NoticeAllowed()
                {
                    if(!scheduler.GetHostRecoverySettings().Enabled || scheduler.Paused)return false;
                    if(!ownsNoticeControl)
                    {
                        try{scheduler.BeginHostControl();ownsNoticeControl=true;}
                        catch(DispatchException ex) when(ex.Code=="RESOURCE_BUSY"){return false;}
                    }
                    return true;
                }
                var closed=SolidWorksRecoveryWindows.CloseStartupNotices(tracked.Id,NoticeAllowed);
                if(closed>0)scheduler.Log.Write("HOST_STARTUP_NOTICE_CLOSE","已请求关闭已识别的插件加载性能通知。");
            }
            finally{if(ownsNoticeControl)scheduler.EndHostControl();}
            return;
        }
        if(exited && tracked.ExitCode==0 && dialogs.Length==0){ResetTracked();return;}
        var incident=scheduler.BeginHostRecovery(tracked.Id,started,executable);
        if(incident is null || incident.State!="Recovering")return;
        await Recover(incident,stop);ResetTracked();
    }
    private async Task Recover(HostRecoveryIncident incident,CancellationToken stop)
    {
        await action.WaitAsync(stop);int? newPid=null;
        try
        {
            var lockRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexDispatch","locks");Directory.CreateDirectory(lockRoot);
            using var exclusive=new FileStream(Path.Combine(lockRoot,"solidworks-recovery-"+Process.GetCurrentProcess().SessionId+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            bool Allowed()=>!stop.IsCancellationRequested && scheduler.HostRecoveryActionAllowed(incident.Id);
            void Require(){if(!Allowed())throw new OperationCanceledException("自动处理已停用或派发暂停，未继续后续动作。");}
            Require();SolidWorksRecoveryWindows.CloseCrashWindows(incident.OldPid,incident.OldStart,incident.Executable,Allowed);
            var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
            while(tracked is {HasExited:false} && DateTimeOffset.UtcNow<deadline){Require();await Task.Delay(500,stop);}
            if(tracked is {HasExited:false})throw new InvalidOperationException("原SolidWorks进程尚未退出，未强制结束或重复启动。");
            var hosts=SolidWorksRecoveryWindows.Hosts();try{if(hosts.Length!=0)throw new InvalidOperationException("已存在其他SolidWorks实例，停止自动创建新实例。");}finally{foreach(var p in hosts)p.Dispose();}
            Require();SolidWorksRecoveryWindows.CloseCrashWindows(incident.OldPid,incident.OldStart,incident.Executable,Allowed);
            deadline=DateTimeOffset.UtcNow.AddSeconds(5);
            while(SolidWorksRecoveryWindows.CrashWindows(incident.OldPid,incident.OldStart,incident.Executable).Length!=0 && DateTimeOffset.UtcNow<deadline)
            {
                Require();await Task.Delay(250,stop);
            }
            if(SolidWorksRecoveryWindows.CrashWindows(incident.OldPid,incident.OldStart,incident.Executable).Length!=0)throw new InvalidOperationException("原错误报告仍在显示，停止重新启动。");
            Require();var count=RecoveryFiles.Isolate(SolidWorksRecoveryWindows.RecoveryDirectory(incident.Executable),scheduler.Root,incident.Id,Allowed);
            Require();hosts=SolidWorksRecoveryWindows.Hosts();try{if(hosts.Length!=0)throw new InvalidOperationException("启动前出现其他宿主，未重复打开。");}finally{foreach(var p in hosts)p.Dispose();}
            using var launched=Process.Start(new ProcessStartInfo(incident.Executable){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal})??throw new InvalidOperationException("启动宿主失败。");newPid=launched.Id;
            deadline=DateTimeOffset.UtcNow.AddMinutes(2);var stable=0;
            while(DateTimeOffset.UtcNow<deadline && !launched.HasExited)
            {
                Require();await Task.Delay(2000,stop);
                SolidWorksRecoveryWindows.SkipRecoveryPrompts(launched.Id,Allowed);
                SolidWorksRecoveryWindows.CloseStartupNotices(launched.Id,Allowed);
                if(await ProbeBlank(launched.Id,stop)){if(++stable>=2)break;}else stable=0;
            }
            if(stable<2)throw new InvalidOperationException("新实例未通过唯一进程、无恢复文档和无模态提示核验，请人工检查。未自动关闭未知文档。");
            Require();scheduler.FinishHostRecovery(incident.Id,newPid,true,$"新SolidWorks实例已就绪，已隔离{count}个恢复文件，未打开恢复零件。原许可仍由原窗口清理释放。");
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        {scheduler.FinishHostRecovery(incident.Id,newPid,false,"自动恢复停止，保留SolidWorks派发阻塞。"+ex.Message);scheduler.Log.Write("HOST_RECOVERY_FAILED","崩溃自动处理未完成，不重发启动，不解锁原许可。",error:ex);}
        finally{action.Release();}
    }
    private async Task<bool> ProbeBlank(int pid,CancellationToken stop)
    {
        var hosts=SolidWorksRecoveryWindows.Hosts();try{if(hosts.Length!=1 || hosts[0].Id!=pid || hosts[0].SessionId!=Process.GetCurrentProcess().SessionId || SolidWorksRecoveryWindows.HasBlockingDialog(pid))return false;}finally{foreach(var p in hosts)p.Dispose();}
        var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"solidworks","Runner.SolidWorks.dll"));
        using var probe=Process.Start(info)??throw new InvalidOperationException();
        var stdout=probe.StandardOutput.ReadToEndAsync();var stderr=probe.StandardError.ReadToEndAsync();
        try{await probe.WaitForExitAsync(stop).WaitAsync(TimeSpan.FromSeconds(7),stop);await Task.WhenAll(stdout,stderr);}
        catch(Exception ex) when(ex is TimeoutException or OperationCanceledException){if(!probe.HasExited){probe.Kill(true);await probe.WaitForExitAsync();}return false;}
        try{using var json=JsonDocument.Parse(await stdout);var r=json.RootElement;return r.GetProperty("passed").GetBoolean() && r.GetProperty("cleanupOk").GetBoolean() && r.GetProperty("details").GetProperty("solidWorksPid").GetInt32()==pid && r.GetProperty("details").GetProperty("documentCount").GetInt32()==0;}
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException){return false;}
    }
    public async Task<object> Recheck(CancellationToken stop)
    {
        if(Environment.GetEnvironmentVariable("DISPATCH_TEST_NO_HOST_RECOVERY")=="1")throw new DispatchException("HOST_TEST_MODE","隔离检查未获准进入真实宿主。");
        if(!await action.WaitAsync(0,stop))throw new DispatchException("RECOVERY_BUSY","自动处理仍在进行，请稍后查看状态。");
        try
        {
            var incident=scheduler.GetHostRecovery();
            if(scheduler.HostControlBusy)
            {
                var existing=SolidWorksRecoveryWindows.Hosts();
                try
                {
                    if(existing.Length>1 || existing.Length==1 && !await ProbeBlank(existing[0].Id,stop))throw new DispatchException("HOST_NOT_READY","请处理宿主提示并关闭文档后重新检查。");
                    scheduler.EndHostControl();return new{message="宿主管理状态已核对，操作守卫已结束。"};
                }
                finally{foreach(var p in existing)p.Dispose();}
            }
            if(incident is null)throw new DispatchException("NOT_FOUND","当前没有待检查的恢复事故。");
            if(incident.State!="Blocked")return incident;
            if(SolidWorksRecoveryWindows.CrashWindows(incident.OldPid,incident.OldStart,incident.Executable).Length!=0)throw new DispatchException("HOST_NOT_READY","原错误报告仍在显示，请处理后再核验。");
            var hosts=SolidWorksRecoveryWindows.Hosts();int pid;
            try{if(hosts.Length!=1)throw new DispatchException("HOST_NOT_READY","请先确认唯一的SolidWorks空白实例。");pid=hosts[0].Id;}finally{foreach(var p in hosts)p.Dispose();}
            if(!await ProbeBlank(pid,stop))throw new DispatchException("HOST_NOT_READY","宿主未通过空白检查，请处理提示或恢复文档后再检查。");
            return scheduler.FinishHostRecovery(incident.Id,pid,true,"用户发起只读复核，新实例为空白。原许可仍需原窗口清理释放。",manualCheck:true);
        }
        finally{action.Release();}
    }
    public async Task<object> Control(string method,CancellationToken stop)
    {
        if(Environment.GetEnvironmentVariable("DISPATCH_TEST_NO_HOST_RECOVERY")=="1")throw new DispatchException("HOST_TEST_MODE","隔离检查已屏蔽真实宿主管理。");
        if(!await action.WaitAsync(0,stop))throw new DispatchException("RECOVERY_BUSY","宿主操作正在进行。");
        var ownsResource=false;var uncertain=false;
        try
        {
            var hosts=SolidWorksRecoveryWindows.Hosts();
            try
            {
                if(hosts.Length>1 || hosts.Length==1 && hosts[0].SessionId!=Process.GetCurrentProcess().SessionId)throw new DispatchException("HOST_AMBIGUOUS","请确认当前登录会话只有一个SolidWorks实例。");
                if(method=="detect_solidworks_path")
                {
                    if(hosts.Length==0)throw new DispatchException("HOST_NOT_FOUND","请先打开SolidWorks，再获取路径。");
                    var path=hosts[0].MainModule?.FileName??throw new InvalidOperationException("程序路径不可读取。");
                    return new{executable=path,message="已从运行中的SolidWorks获取实际路径。"};
                }
                if(method=="detect_solidworks_errors")
                {
                    var incident=scheduler.GetHostRecovery();
                    var pid=hosts.Length==1?hosts[0].Id:incident?.OldPid;
                    if(pid is null)return new{message="当前没有可关联的SolidWorks实例或错误记录。"};
                    var start=hosts.Length==1?new DateTimeOffset(hosts[0].StartTime):incident!.OldStart;
                    var exe=hosts.Length==1?hosts[0].MainModule!.FileName:incident!.Executable;
                    var count=SolidWorksRecoveryWindows.CrashWindows(pid.Value,start,exe).Length;
                    return new{message=$"检测到{count}个已识别错误报告窗口。自动关闭由崩溃自动处理开关控制。"};
                }
                if(method=="open_solidworks" && hosts.Length==1)return new{message="SolidWorks已经运行。"};
                scheduler.BeginHostControl();ownsResource=true;
                if(method=="open_solidworks")
                {
                    var path=scheduler.GetHostRecoverySettings().Executable;
                    if(!File.Exists(path))throw new DispatchException("HOST_PATH_REQUIRED","请先获取或填写并保存程序路径。");
                    var launched=Process.Start(new ProcessStartInfo(path){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal})??throw new InvalidOperationException("宿主启动失败。");
                    if (scheduler.GetHostRecoverySettings().Enabled)
                    {
                        ResetTracked();
                        tracked=launched;
                        executable=path;
                        started=new DateTimeOffset(launched.StartTime);
                        _ = launched.Handle;
                        monitorStatus=$"SolidWorks启动中（PID {launched.Id}），已接入闪退监测。";
                    }
                    else launched.Dispose();
                    return new{message="已请求打开SolidWorks，请等待启动完成。"};
                }
                if(hosts.Length==0)return new{message="SolidWorks已经关闭。"};
                if(!await ProbeBlank(hosts[0].Id,stop))throw new DispatchException("HOST_DOCUMENTS_OPEN","请先保存并关闭文档、处理提示，再关闭SolidWorks。");
                uncertain=true;
                if(!hosts[0].CloseMainWindow()){uncertain=false;throw new DispatchException("HOST_CLOSE_FAILED","宿主未接受正常关闭请求。");}
                try{await hosts[0].WaitForExitAsync(stop).WaitAsync(TimeSpan.FromSeconds(7),stop);}
                catch(TimeoutException){throw new DispatchException("HOST_CLOSE_PENDING","关闭仍在等待，请检查宿主提示。");}
                uncertain=false;ResetTracked();return new{message="SolidWorks已正常关闭。"};
            }
            finally{foreach(var p in hosts)p.Dispose();}
        }
        catch(Exception ex){scheduler.Log.Write("HOST_CONTROL_FAILED","宿主管理操作未完成。",error:ex);throw;}
        finally{if(ownsResource && !uncertain)scheduler.EndHostControl();action.Release();}
    }
}
