namespace Dispatch.Core;

public sealed record ResourceOffer(string LeaseId,string Id,int Generation,DateTimeOffset ExpiresAt);
public sealed partial class Scheduler
{
    private ResourceOffer? FindResourceOffer(string lease)=>store.Load<ResourceOffer>("resource-offer").SingleOrDefault(o=>o.LeaseId==lease);
    private bool TryCreateResourceOffer(Job job)
    {
        if(job.Suite.Runner!="external-agent" || !permits[job.Id].Request.RequireAcceptance)return false;
        if(!QueueBindingAllowed(job.Id)){Save(job with{State=JobState.WaitingForClient,Summary="原聊天的接续授权已改变，等待重新确认。"});return true;}
        if(!QueueSourceAllowsOffer(job.Id)){Save(job with{State=JobState.WaitingForClient,Summary="原聊天尚未准备接单或已收到新任务；未占用软件。"});return true;}
        var generation=job.Generation+1;
        var offer=new ResourceOffer(job.Id,Guid.NewGuid().ToString("N"),generation,clock.GetUtcNow().AddSeconds(30));
        var next=job with{State=JobState.Offered,Generation=generation,StartedAt=null,CleanupStatus="NOT_REQUIRED",Summary="资源可接单；尚无操作许可，请原窗口核对并accept_resource。"};
        store.SaveBatch(new("job",job.Id,next),new("resource-offer",job.Id,offer));jobs[job.Id]=next;
        Log.Write("RESOURCE_OFFERED","短期接单预留已建立，尚未授予操作许可。",job.Id);return true;
    }
    private void CheckResourceOffers(bool restarting=false)
    {
        foreach(var job in jobs.Values.Where(j=>j.State==JobState.Offered).ToArray())
        {
            var offer=FindResourceOffer(job.Id);
            if(restarting || offer is null || offer.ExpiresAt<=clock.GetUtcNow() || !QueueBindingAllowed(job.Id))
            {
                Save(job with{State=JobState.WaitingForClient,Summary=restarting?"服务重启，未接单预留已撤回；等待原窗口重新接续。":"未在期限内接单，预留已结束；原任务保留，不占用软件。"});
                Log.Write("RESOURCE_OFFER_ENDED","未曾授予操作许可的短期预留已结束，未解锁任何已授予许可。",job.Id);
            }
        }
    }
    public ResourcePermit AcceptResource(string id,string token,string offerId,int generation)
    {
        lock(gate)
        {
            var permit=AuthorizePermit(id,token);CheckResourceOffers();CheckPermitExpiry();
            var job=Find(id);var offer=FindResourceOffer(id);
            if(offer is null || offer.Id!=offerId || offer.Generation!=generation || job.Generation!=generation)
                throw new DispatchException("STALE_OFFER","接单编号或代次已失效，不可操作软件。");
            if(job.State==JobState.Running)return PermitView(permit);
            if(job.State!=JobState.Offered)throw new DispatchException("OFFER_NOT_ACTIVE","已无有效接单预留；不能据旧通知开始测试。");
            RequireQueueBindingAllowed(id);
            RequireQueueAcceptanceRuntime(id);
            RequireQueueEnabled();
            if(paused)throw new DispatchException("QUEUE_PAUSED","派发已暂停，暂不授予新的操作许可。");
            var next=permit with{ExpiresAt=clock.GetUtcNow().AddSeconds(permit.Request.DurationSeconds)};
            SavePermit(job with{State=JobState.Running,StartedAt=clock.GetUtcNow(),CleanupStatus="UNCERTAIN",Summary="原窗口已确认接单，现已获得操作许可。"},next);
            NotifyResourceGranted(id);return PermitView(next);
        }
    }
}
