namespace Dispatch.Core;

public sealed record HostRecoverySettings(bool Enabled = false, int Version = 0, string Executable = "");
public sealed record HostRecoveryIncident(string Id, string State, int OldPid, DateTimeOffset OldStart, string Executable,
    int SettingsVersion, DateTimeOffset At, string Message, int? NewPid = null);

public sealed partial class Scheduler
{
    private bool hostControlBusy;
    public bool HostControlBusy{get{lock(gate)return hostControlBusy;}}
    public void BeginHostControl()
    {
        lock(gate)
        {
            if(hostControlBusy || HostRecoveryBlocking || jobs.Values.Any(j=>j.Suite.Resources.Contains("sw-main") && HoldsResources(j)))throw new DispatchException("RESOURCE_BUSY","SolidWorks仍被占用或正在处理，请清理释放后操作。");
            store.Save("host-control","busy",true);hostControlBusy=true;
        }
    }
    public void EndHostControl(){lock(gate){store.Save("host-control","busy",false);hostControlBusy=false;}}
    public HostRecoverySettings GetHostRecoverySettings(){lock(gate)return store.LoadOne<HostRecoverySettings>("host-recovery-settings","global")??new();}
    public HostRecoverySettings SetHostRecoverySettings(HostRecoverySettings value)
    {
        lock(gate)
        {
            var current=GetHostRecoverySettings();
            value=value with{Executable=value.Executable??""};
            if(value.Version!=current.Version)throw new DispatchException("SETTINGS_CHANGED","崩溃重启设置已改变，请重新载入。");
            if(value.Executable.Length>1024 || value.Executable.Length>0 && (!Path.IsPathFullyQualified(value.Executable) || !Path.GetFileName(value.Executable).Equals("SLDWORKS.exe",StringComparison.OrdinalIgnoreCase)))throw new DispatchException("INVALID_ARGUMENT","请填写SLDWORKS.exe的完整路径。");
            if(value.Enabled==current.Enabled && value.Executable==current.Executable)return current;
            var next=value with{Version=checked(current.Version+1)};store.Save("host-recovery-settings","global",next);
            Log.Write("HOST_RECOVERY_SETTINGS",next.Enabled?"已启用SolidWorks崩溃单次重启与跳过恢复文件。":"已关闭后续自动崩溃处理，已启动的宿主保留。");return next;
        }
    }
    public HostRecoveryIncident? GetHostRecovery(){lock(gate)return store.LoadOne<HostRecoveryIncident>("host-recovery","current");}
    public bool HostRecoveryBlocking=>GetHostRecovery()?.State is "Recovering" or "Blocked";
    private void SaveHostRecovery(HostRecoveryIncident value)
    {store.SaveBatch(new("host-recovery","current",value),new("host-recovery-history",value.Id,value));Log.Write("HOST_RECOVERY_"+value.State.ToUpperInvariant(),value.Message);}
    private void InterruptHostRecovery()
    {
        if(GetHostRecovery() is {State:"Recovering"} current)
            SaveHostRecovery(current with{State="Blocked",Message="服务曾中断，自动启动结果不明，不重发启动；请检查宿主。"});
    }
    public HostRecoveryIncident? BeginHostRecovery(int pid, DateTimeOffset start, string executable)
    {
        lock(gate)
        {
            var settings=GetHostRecoverySettings();
            if(!settings.Enabled || paused || hostControlBusy || !GetModules().QueueEnabled || HostRecoveryBlocking)return null;
            if(pid<=0 || !Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("SLDWORKS.exe",StringComparison.OrdinalIgnoreCase))
                throw new DispatchException("INVALID_HOST","宿主身份无效。");
            var history=store.Load<HostRecoveryIncident>("host-recovery-history");
            if(history.Any(i=>i.OldPid==pid && i.OldStart==start))return null;
            var limited=history.Count(i=>i.At>clock.GetUtcNow().AddMinutes(-10))>=3;
            var incident=new HostRecoveryIncident(Guid.NewGuid().ToString("N"),limited?"Blocked":"Recovering",pid,start,executable,settings.Version,clock.GetUtcNow(),
                limited?"10分钟内已处理3次异常，停止连续重启，请检查崩溃原因。":"已确认宿主异常，暂缓SolidWorks派发，原许可需原窗口确认停止清理。");
            var affected=jobs.Values.Where(j=>j.Suite.Runner=="external-agent" && j.Suite.Resources.Contains("sw-main") && j.State is JobState.Running or JobState.Cleanup or JobState.Offered).Select(j=>
                j.State==JobState.Offered?j with{State=JobState.WaitingForClient,UserPriority=false,Summary="SolidWorks崩溃，接单预留已撤回，原任务未执行。"}:
                j with{State=JobState.RecoveryRequired,CleanupStatus="UNCERTAIN",Summary="SolidWorks崩溃，请原窗口停止旧操作并确认清理；未自动重跑测试。"}).ToArray();
            store.SaveBatch([new("host-recovery","current",incident),new("host-recovery-history",incident.Id,incident),..affected.Select(j=>new StoreWrite("job",j.Id,j))]);
            foreach(var job in affected)RememberJob(job);
            Log.Write("HOST_CRASH_DETECTED",incident.Message);return incident;
        }
    }
    public bool HostRecoveryActionAllowed(string id)
    {
        lock(gate){var current=GetHostRecovery();var settings=GetHostRecoverySettings();return current is {State:"Recovering"} && current.Id==id && settings.Enabled && settings.Version==current.SettingsVersion && !paused && GetModules().QueueEnabled;}
    }
    public HostRecoveryIncident FinishHostRecovery(string id, int? newPid, bool ready, string message, bool manualCheck=false)
    {
        lock(gate)
        {
            var current=GetHostRecovery();if(current?.Id!=id)throw new DispatchException("STALE_RESULT","宿主恢复事故编号已改变。");
            if(current.State=="Ready")return current;
            ready=ready && newPid is >0 && newPid!=current.OldPid && (HostRecoveryActionAllowed(id) || manualCheck && current.State=="Blocked");
            var next=current with{State=ready?"Ready":"Blocked",NewPid=newPid,Message=message};SaveHostRecovery(next);return next;
        }
    }
}
