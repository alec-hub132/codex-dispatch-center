using System.Diagnostics;
namespace Dispatch.Core;
public sealed record IdleCleanupSettings(bool Enabled=false,int IdleMinutes=10,int Version=0);
public sealed record IdleCleanupTicket(string Id,string LeaseId,string ThreadId,string SourceTurnId,int SettingsVersion,string State,string? NewTurnId=null);
public sealed record IdleCleanupObservation(string LeaseId,IdleActivity Activity);
public sealed record IdleCleanupOwnerBinding(string LeaseId,string ThreadId);
public sealed record IdleCleanupStatus(string LeaseId,string? ThreadId,string Status,string Reason,DateTimeOffset? LastActivity=null);
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
        var owner=store.LoadOne<IdleCleanupOwnerBinding>("idle-cleanup-owner",lease)?.ThreadId??permits[lease].Request.OwnerThreadId;
        if(owner is not null)return owner;
        var binding=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);if(binding is not null)return binding.ThreadId;
        var label=permits[lease].Request.SessionId;
        if(label.StartsWith("Codex-",StringComparison.Ordinal))label=label[6..];
        return Guid.TryParseExact(label,"D",out _)?label:null; // 自由别名不猜聊天，后续可在界面显式绑定。
    }
    private IdleActivity VerifyIdleCleanupOwner(string thread)
    {
        var home=store.Load<RuntimeSource>("runtime-source").SingleOrDefault()?.Home;
        if(!Guid.TryParseExact(thread,"D",out _) || home is null)
            throw new DispatchException("SOURCE_UNAVAILABLE","找不到该真实聊天的唯一运行记录，请核对聊天编号。");
        var a=IdleActivityReader.Read(home,thread,null,clock.GetUtcNow());if(a.Unknown)throw new DispatchException("SOURCE_UNAVAILABLE","该聊天的运行记录无法核对。");return a;
    }
    public IdleCleanupOwnerBinding SetIdleCleanupOwner(string lease,string thread)
    {
        lock(gate)
        {
            if(!permits.ContainsKey(lease))throw new DispatchException("NOT_FOUND","该任务不是窗口资源许可。");
            var existing=IdleCleanupOwner(lease);
            if(existing is not null && existing!=thread)throw new DispatchException("OWNER_CONFLICT","原聊天已明确绑定，不能改成另一个窗口。");
            if(GetIdleCleanupTickets().Any(t=>t.LeaseId==lease))throw new DispatchException("INVALID_STATE","已发起过清理的原任务不能重新绑定或重发。");
            var a=VerifyIdleCleanupOwner(thread);var value=new IdleCleanupOwnerBinding(lease,thread);
            store.SaveBatch(new("idle-cleanup-owner",lease,value),new("idle-cleanup-source",lease,new IdleCleanupObservation(lease,a)));
            Log.Write("IDLE_CLEANUP_OWNER_BOUND","已核对真实聊天记录并补充原窗口关联；未解锁或发送清理消息。",lease);return value;
        }
    }
    public IdleCleanupStatus[] GetIdleCleanupStatuses()
    {
        lock(gate)
        {
            return jobs.Values.Where(j=>j.Suite.Runner=="external-agent" && j.State is JobState.Running or JobState.RecoveryRequired).Select(job=>
            {
                var thread=IdleCleanupOwner(job.Id);if(thread is null)return new IdleCleanupStatus(job.Id,null,"UNBOUND","会话别名不能识别原窗口，请补充真实聊天编号。");
                var ticket=GetIdleCleanupTickets().SingleOrDefault(t=>t.LeaseId==job.Id);
                if(ticket is not null)return new IdleCleanupStatus(job.Id,thread,ticket.State,ticket.State=="CLEANUP_UNCONFIRMED"?"原窗口无法确认清理完成，继续隔离，未重复唤醒。":ticket.State=="SENT"?"已发送清理请求，等待原窗口确认；尚未释放。":ticket.State=="STOPPED_NO_RETRY"?"发送未确认或设置改变，未重复发送；原许可仍需清理。":"单次清理登记处理中，尚未释放。");
                var a=store.LoadOne<IdleCleanupObservation>("idle-cleanup-source",job.Id)?.Activity;
                if(a is null)return new IdleCleanupStatus(job.Id,thread,"WAITING_OBSERVATION","等待活动检测；功能关闭时不发起清理。");
                if(a.Unknown)return new IdleCleanupStatus(job.Id,thread,"SOURCE_UNAVAILABLE","运行记录身份、边界或时间无法核对，未自动清理。");
                if(!a.CaughtUp)return new IdleCleanupStatus(job.Id,thread,"READING_LOG","运行记录尚未读完整，未判定空闲。",a.LastActivity);
                if(a.Active||a.PendingCalls.Length>0)return new IdleCleanupStatus(job.Id,thread,"WINDOW_ACTIVE","窗口轮次或工具调用仍在活动。",a.LastActivity);
                if(HasRegisteredRunningProcess(job))return new IdleCleanupStatus(job.Id,thread,"TEST_RUNNING","已登记的测试进程尚未结束。",a.LastActivity);
                return new IdleCleanupStatus(job.Id,thread,"WAITING_IDLE_LIMIT","窗口已结束本轮，等待连续无活动达到设定时间。",a.LastActivity);
            }).ToArray();
        }
    }
    private IdleActivity? RefreshIdleActivity(string lease,string thread)
    {
        var home=store.Load<RuntimeSource>("runtime-source").SingleOrDefault()?.Home;if(home is null)return null;
        var old=store.LoadOne<IdleCleanupObservation>("idle-cleanup-source",lease);
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
                if(GetQueueWakes().Any(w=>w.ThreadId==thread && (w.State is "DISPATCHING" or "SENDING" || w.State=="SENT" && Find(w.LeaseId).State is JobState.Offered or JobState.Queued or JobState.WaitingForClient)))continue;
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
        foreach(var t in GetIdleCleanupTickets().Where(t=>t.LeaseId==lease && t.State is "DISPATCHING" or "SENDING" or "SENT" or "CLEANUP_UNCONFIRMED" or "STOPPED_NO_RETRY"))
            yield return new("idle-cleanup-ticket",t.Id,t with{State="CONFIRMED"});
    }
    private IEnumerable<StoreWrite> IdleCleanupUnconfirmedWrites(string lease)
    {
        foreach(var t in GetIdleCleanupTickets().Where(t=>t.LeaseId==lease && t.State is "DISPATCHING" or "SENDING" or "SENT"))
            yield return new("idle-cleanup-ticket",t.Id,t with{State="CLEANUP_UNCONFIRMED"});
    }
    // 只供服务固定发送器写入私有计划；不返回MCP，不写日志或验收证据。
    public object BuildIdleCleanupPlan(string lease,string instance)
    {
        lock(gate)
        {
            var t=GetIdleCleanupTickets().SingleOrDefault(t=>t.LeaseId==lease)??throw new DispatchException("NOT_FOUND","清理登记不存在。");
            if(t.State!="DISPATCHING")throw new DispatchException("INVALID_STATE","该许可的清理通知已处理，不重复启动。");
            var planName="idle-cleanup-plan-"+t.Id+".json";
            return new{kind="idle-cleanup",leaseId=lease,wakeId=t.Id,threadId=t.ThreadId,sourceTurnId=t.SourceTurnId,token=permits[lease].Token,instance,dataRoot=Root,
                prompt=$"这是用户已启用的无活动窗口单次清理请求。仅处理原许可{lease}，保留本Codex聊天、用户原有SolidWorks及原有文档。"+
                "先读取get_module_settings，再从本聊天原记录取原token；若上下文缺失，仅可从本机私有计划"+Path.Combine(Root,"diagnostics",planName)+"读取该许可token，不得输出或写入公开证据。"+
                "先get_resource核对原许可/当前generation。已Released/Cancelled仅回读，不重做。Granted或RecoveryRequired时，核对是否有自建测试进程、异步操作或对话框；先停止自建操作，再清理自己创建的测试资源，确认清理后release_resource(cleanupConfirmed=true)并回读Released/cleanupStatus=OK。"+
                "若无法确认停止或清理，保持隔离并说明原因，可用release_resource(cleanupConfirmed=false)记录不确定状态。不得把窗口结束或超时当清理证明，不强关用户宿主/文档。"+
                "本轮只做原任务的停止与清理，不继续原测试、不创建新资源申请、不切模型、不新委派、不发送其他聊天消息，也不关闭/归档聊天。若用户已安排新任务，尊重新指令并先明确原许可清理状态，不以清理名义重跑业务。"};
        }
    }
}
