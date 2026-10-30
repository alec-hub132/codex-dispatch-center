using System.Text.RegularExpressions;
namespace Dispatch.Core;

public sealed record QueueResumeConsent(string ThreadId,bool Enabled,int Version=0);
public sealed record QueueResumeBinding(string LeaseId,string ThreadId,string SourceTurnId,string RemainingWork,int ConsentVersion,DateTimeOffset RegisteredAt,string SourceFile);
public sealed partial class Scheduler
{
    public QueueResumeConsent[] GetQueueResumeConsents(){lock(gate)return store.Load<QueueResumeConsent>("queue-resume-consent").ToArray();}
    public QueueResumeBinding[] GetQueueBindings(){lock(gate)return store.Load<QueueResumeBinding>("queue-binding").ToArray();}
    public QueueResumeConsent SetQueueResumeConsent(QueueResumeConsent desired)
    {
        var thread=desired.ThreadId??"";
        if(!Regex.IsMatch(thread,"^[a-zA-Z0-9-]{1,128}\\z"))throw new DispatchException("INVALID_ARGUMENT","请输入真实聊天编号，不使用会话别名。");
        lock(gate)
        {
            var previous=GetQueueResumeConsents().SingleOrDefault(c=>c.ThreadId==thread);
            if(desired.Version!=(previous?.Version??0))throw new DispatchException("VERSION_CONFLICT","授权已被其他界面修改，请刷新后再保存。");
            if(desired.Enabled && ReadQueueRuntime(thread) is null)throw new DispatchException("SOURCE_UNAVAILABLE","找不到该真实聊天的有效运行记录，请先连接并核对聊天编号。");
            if(previous?.Enabled==desired.Enabled)return previous;
            var result=desired with{ThreadId=thread,Version=(previous?.Version??0)+1};
            store.Save("queue-resume-consent",thread,result);
            Log.Write("QUEUE_RESUME_CONSENT_SAVED",desired.Enabled?"用户允许该聊天的排队原任务接续。":"用户关闭该聊天的排队自动接续。");return result;
        }
    }
    private RuntimeObservation? ReadQueueRuntime(string thread)
    {
        var source=store.Load<RuntimeSource>("runtime-source").SingleOrDefault();
        try{return source is null?null:ReadObservation(source.Home,thread);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {Log.Write("QUEUE_RUNTIME_UNAVAILABLE","排队接续运行记录暂不可读取，未自动选择其他聊天。");return null;}
    }
    private QueueResumeBinding? PrepareQueueBinding(ResourceRequest request,string lease)
    {
        if(request.ResumeThreadId is null && request.SourceTurnId is null && request.RemainingWork is null)return null;
        Validate(request.ResumeThreadId??"","真实聊天编号",128);Validate(request.SourceTurnId??"","源轮次编号",128);Validate(request.RemainingWork??"","剩余步骤",4000);
        var consent=GetQueueResumeConsents().SingleOrDefault(c=>c.ThreadId==request.ResumeThreadId && c.Enabled)
            ??throw new DispatchException("QUEUE_RESUME_NOT_AUTHORIZED","该聊天尚未由用户开启排队接续授权；模型授权不能替代。");
        var observed=ReadQueueRuntime(consent.ThreadId);
        if(observed is null || observed.TurnId!=request.SourceTurnId || observed.Timestamp>clock.GetUtcNow().AddSeconds(5))
            throw new DispatchException("QUEUE_SOURCE_MISMATCH","真实聊天/当前源轮次无法匹配，未创建任务或绑定。");
        var work=request.RemainingWork!;
        if(permits.Values.Any(p=>work.Contains(p.Token,StringComparison.Ordinal)) || decisions.Values.Any(d=>d.Execution is {} p && work.Contains(p.Token,StringComparison.Ordinal)))
            throw new DispatchException("SECRET_IN_CHECKPOINT","剩余步骤不能含令牌，请仅记录任务编号。");
        return new(lease,consent.ThreadId,observed.TurnId,work,consent.Version,clock.GetUtcNow(),observed.SourceFile);
    }
    private bool QueueBindingAllowed(string lease)
    {
        var binding=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);
        return binding is null || GetQueueResumeConsents().Any(c=>c.ThreadId==binding.ThreadId && c.Enabled && c.Version==binding.ConsentVersion);
    }
    private void RequireQueueBindingAllowed(string lease)
    {if(!QueueBindingAllowed(lease))throw new DispatchException("QUEUE_RESUME_NOT_AUTHORIZED","原聊天的排队接续授权已撤销或改变，请重新确认原任务。");}
    public object GetQueueBindingState(string lease,string token)
    {
        lock(gate)
        {
            AuthorizePermit(lease,token);CheckResourceOffers();CheckPermitExpiry();
            var binding=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);
            var job=Find(lease);var offer=FindResourceOffer(lease);
            return new{binding,wake=FindQueueWake(lease),authorized=QueueBindingAllowed(lease) && binding is not null,queueEnabled=GetModules().QueueEnabled,
                state=job.State,generation=job.Generation,offerId=offer?.Id,offerExpiresAt=offer?.ExpiresAt};
        }
    }
    public ResourcePermit RequeueResource(string lease,string token,string? sourceTurnId=null)
    {
        lock(gate)
        {
            var permit=AuthorizePermit(lease,token);CheckResourceOffers();var job=Find(lease);
            if(job.State!=JobState.WaitingForClient || !permit.Request.RequireAcceptance || permit.ExpiresAt is not null)
                throw new DispatchException("INVALID_STATE","仅能重新排队尚未获得操作许可的等待接续任务。");
            RequireQueueEnabled();
            if(job.Generation>=3)throw new DispatchException("RESUME_LIMIT","三次接单机会均已结束，原任务保留，请人工确认后处理。");
            var oldOffer=FindResourceOffer(lease);
            if(oldOffer is not null && clock.GetUtcNow()<oldOffer.ExpiresAt.AddSeconds(2))throw new DispatchException("RESUME_BACKOFF","请稍后重新接续，不反复抢占资源。");
            var binding=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);
            QueueSourceCursor? current=null;
            if(binding is not null)
            {
                var c=GetQueueResumeConsents().SingleOrDefault(c=>c.ThreadId==binding.ThreadId && c.Enabled)
                    ??throw new DispatchException("QUEUE_RESUME_NOT_AUTHORIZED","原聊天未允许排队接续。");
                current=FindQueueSource(lease);
                if(current is not null)current=RefreshQueueSource(binding,current);
                if(current is not {Blocked:false,CaughtUp:true,Finished:false} || current.LatestTurnId!=sourceTurnId)
                    throw new DispatchException("QUEUE_SOURCE_MISMATCH","重排必须核对原聊天当前实际轮次，不能覆盖新的用户任务。");
                current=current with{ReadyUntil=clock.GetUtcNow().AddSeconds(15)};
                binding=binding with{SourceTurnId=current.LatestTurnId,ConsentVersion=c.Version,RegisteredAt=clock.GetUtcNow()};
            }
            SavePermit(job with{State=JobState.Queued,Sequence=++sequence,Summary="原任务已重新排队接单，未新建或重做测试。"},permit,binding,current);
            return PermitView(permit);
        }
    }
}
