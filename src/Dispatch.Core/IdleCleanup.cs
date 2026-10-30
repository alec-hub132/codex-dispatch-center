using System.Diagnostics;
namespace Dispatch.Core;
public sealed record IdleCleanupSettings(bool Enabled=false,int IdleMinutes=10,int Version=0);
public sealed record IdleCleanupTicket(string Id,string LeaseId,string ThreadId,string SourceTurnId,int SettingsVersion,string State,string? NewTurnId=null);
public sealed record IdleCleanupObservation(string LeaseId,IdleActivity Activity);
public sealed partial class Scheduler
{
    public IdleCleanupSettings GetIdleCleanupSettings(){lock(gate)return store.Load<IdleCleanupSettings>("idle-cleanup-settings").SingleOrDefault()??new();}
    public IdleCleanupSettings SetIdleCleanupSettings(IdleCleanupSettings requested)
    {
        lock(gate)
        {
            var current=GetIdleCleanupSettings();if(requested.Version!=current.Version)throw new DispatchException("SETTINGS_CHANGED","无活动清理设置已改变，请刷新后保存。");
            if(requested.IdleMinutes is <1 or >120)throw new DispatchException("INVALID_ARGUMENT","无活动时间须在1至120分钟之间。");
            if(requested.Enabled==current.Enabled && requested.IdleMinutes==current.IdleMinutes)return current;
            var next=requested with{Version=checked(current.Version+1)};store.Save("idle-cleanup-settings","global",next);
            Log.Write("IDLE_CLEANUP_SETTINGS_CHANGED",next.Enabled?"已授权无活动原窗口单次清理；清理成功才释放，保留聊天与用户宿主。":"无活动清理已关闭，既有许可清理责任保持。");return next;
        }
    }
    public IdleCleanupTicket[] GetIdleCleanupTickets(){lock(gate)return store.Load<IdleCleanupTicket>("idle-cleanup-ticket").ToArray();}
    public IdleCleanupObservation[] GetIdleCleanupObservations(){lock(gate)return store.Load<IdleCleanupObservation>("idle-cleanup-source").ToArray();}
    private string? IdleCleanupOwner(string lease)
    {
        var binding=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);if(binding is not null)return binding.ThreadId;
        var label=permits[lease].Request.SessionId;
        if(label.StartsWith("Codex-",StringComparison.Ordinal))label=label[6..];
        return Guid.TryParseExact(label,"D",out _)?label:null; // 自由别名不猜聊天，后续可在界面显式绑定。
    }
    private IdleActivity? RefreshIdleActivity(string lease,string thread)
    {
        var home=store.Load<RuntimeSource>("runtime-source").SingleOrDefault()?.Home;if(home is null)return null;
        var old=GetIdleCleanupObservations().SingleOrDefault(o=>o.LeaseId==lease);
        var next=IdleActivityReader.Read(home,thread,old?.Activity,clock.GetUtcNow());
        if(old is null || next!=old.Activity)store.Save("idle-cleanup-source",lease,new IdleCleanupObservation(lease,next));return next;
    }
    private static bool HasRegisteredRunningProcess(Job job)
    {
        if(job.RunnerPid is not {} pid)return false;
        try
        {
            using var process=Process.GetProcessById(pid);
            // 身份不明也不据此证明进程已退出。
            if(job.RunnerStartedAt is null || new DateTimeOffset(process.StartTime)!=job.RunnerStartedAt)return true;
            return !process.HasExited;
        }
        catch(ArgumentException){return false;}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){return true;}
    }
    private bool IdleCleanupEligible(Job job,IdleActivity? activity,IdleCleanupSettings settings)
    {
        if(job.Suite.Runner!="external-agent" || job.State is not (JobState.Running or JobState.RecoveryRequired))return false;
        if(activity is not {Unknown:false,CaughtUp:true,Active:false,TurnId:not null,LastActivity:not null} || activity.PendingCalls.Length>0 || HasRegisteredRunningProcess(job))return false;
        var since=activity.LastActivity.Value;var granted=job.StartedAt??job.CreatedAt;if(granted>since)since=granted;
        return clock.GetUtcNow()>=since.AddMinutes(settings.IdleMinutes);
    }
    public IdleCleanupTicket? ClaimIdleCleanup()
    {
        lock(gate)
        {
            var settings=GetIdleCleanupSettings();if(!settings.Enabled || !GetModules().QueueEnabled || paused)return null;
            foreach(var job in jobs.Values.Where(j=>j.Suite.Runner=="external-agent" && j.State is JobState.Running or JobState.RecoveryRequired).OrderBy(j=>j.Sequence))
            {
                if(GetIdleCleanupTickets().Any(t=>t.LeaseId==job.Id))continue;
                var thread=IdleCleanupOwner(job.Id);if(thread is null)continue;
                var activity=RefreshIdleActivity(job.Id,thread);if(!IdleCleanupEligible(job,activity,settings))continue;
                if(GetIdleCleanupTickets().Any(t=>t.ThreadId==thread && t.State is "DISPATCHING" or "SENDING" or "SENT"))continue;
                var ticket=new IdleCleanupTicket("cleanup-"+job.Id,job.Id,thread,activity!.TurnId!,settings.Version,"DISPATCHING");
                store.Save("idle-cleanup-ticket",ticket.Id,ticket);Log.Write("IDLE_CLEANUP_DISPATCHING","真实窗口连续无活动，登记单次原窗口清理；尚未解除许可。",job.Id);return ticket;
            }
            return null;
        }
    }
    public bool BeginIdleCleanupSend(string lease,string token,string ticketId)
    {
        lock(gate)
        {
            AuthorizePermit(lease,token);var ticket=GetIdleCleanupTickets().SingleOrDefault(t=>t.Id==ticketId && t.LeaseId==lease);
            if(ticket?.State!="DISPATCHING")return false;
            var settings=GetIdleCleanupSettings();var activity=RefreshIdleActivity(lease,ticket.ThreadId);
            var allowed=settings.Enabled && settings.Version==ticket.SettingsVersion && GetModules().QueueEnabled && !paused &&
                activity?.TurnId==ticket.SourceTurnId && IdleCleanupEligible(Find(lease),activity,settings);
            store.Save("idle-cleanup-ticket",ticket.Id,ticket with{State=allowed?"SENDING":"STOPPED_NO_RETRY"});return allowed;
        }
    }
    public void RecordIdleCleanupSend(string lease,string ticketId,string? newTurnId)
    {
        lock(gate)
        {
            var ticket=GetIdleCleanupTickets().SingleOrDefault(t=>t.Id==ticketId && t.LeaseId==lease)??throw new DispatchException("NOT_FOUND","清理登记不存在。");
            if(ticket.State is not ("DISPATCHING" or "SENDING"))return;
            var valid=ticket.State=="SENDING" && newTurnId!=ticket.SourceTurnId && System.Text.RegularExpressions.Regex.IsMatch(newTurnId??"","^[a-zA-Z0-9-]{1,128}\\z");
            store.Save("idle-cleanup-ticket",ticket.Id,ticket with{State=valid?"SENT":"STOPPED_NO_RETRY",NewTurnId=valid?newTurnId:null});
            Log.Write(valid?"IDLE_CLEANUP_SENT":"IDLE_CLEANUP_UNCONFIRMED","单次清理发送结果已登记，不按回执解除许可。",lease);
        }
    }
    private void InterruptIdleCleanup()
    {
        foreach(var t in GetIdleCleanupTickets().Where(t=>t.State is "DISPATCHING" or "SENDING"))
            store.Save("idle-cleanup-ticket",t.Id,t with{State="STOPPED_NO_RETRY"});
    }
    private IEnumerable<StoreWrite> IdleCleanupConfirmWrites(string lease)
    {
        foreach(var t in GetIdleCleanupTickets().Where(t=>t.LeaseId==lease && t.State is "DISPATCHING" or "SENDING" or "SENT"))
            yield return new("idle-cleanup-ticket",t.Id,t with{State="CONFIRMED"});
    }
}
