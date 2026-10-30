using Dispatch.Core;
using Xunit;
namespace Dispatch.Tests;
public sealed class OfferTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "offer-tests"),Guid.NewGuid().ToString("N"));
    private static ResourceRequest Request(string id,string resource="solidworks")=>new(id,"window",resource,"接单状态验证，不操作软件",60,true);
    [Fact] public void Offer_is_not_permission_and_timeout_starts_only_after_acceptance()
    {
        var clock=new Clock();using var s=new Scheduler(new JobStore(root),clock);
        var p=s.RequestResource(Request("a"));Assert.Empty(s.Claim());
        var offer=s.GetResource(p.Job.Id,p.Token);Assert.Equal("Offered",offer.Status);Assert.Null(offer.ExpiresAt);
        clock.Utc=clock.Utc.AddSeconds(10);
        var accepted=s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation);
        Assert.Equal("Granted",accepted.Status);Assert.Equal(clock.Utc.AddSeconds(60),accepted.ExpiresAt);
        var repeated=s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation);
        Assert.Equal(accepted.Job.Id,repeated.Job.Id);Assert.Equal(accepted.ExpiresAt,repeated.ExpiresAt);
        Assert.Equal(accepted.Job.Generation,repeated.Job.Generation);
    }
    [Fact] public void Unaccepted_offer_expires_without_isolating_or_blocking_next_waiter()
    {
        var clock=new Clock();using var s=new Scheduler(new JobStore(root),clock);
        var p=s.RequestResource(Request("a"));var other=s.RequestResource(Request("b"));
        s.Claim();var old=s.GetResource(p.Job.Id,p.Token);clock.Utc=clock.Utc.AddSeconds(31);s.Claim();
        Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.Equal("Offered",s.GetResource(other.Job.Id,other.Token).Status);
        Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,old.OfferId!,old.Job.Generation));
    }
    [Fact] public void Wrong_receipt_disabled_module_and_restart_never_grant_automatically()
    {
        ResourcePermit p;
        using(var s=new Scheduler(new JobStore(root)))
        {
            p=s.RequestResource(Request("a"));s.Claim();var offer=s.GetResource(p.Job.Id,p.Token);
            Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,"wrong",offer.OfferId!,offer.Job.Generation));
            Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,"wrong",offer.Job.Generation));
            s.SetModules(s.GetModules() with{QueueEnabled=false});
            Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation));
        }
        using var restored=new Scheduler(new JobStore(root));Assert.Equal("WaitingForClient",restored.GetResource(p.Job.Id,p.Token).Status);
        Assert.DoesNotContain(restored.List(),j=>j.State==JobState.RecoveryRequired);
        Assert.Equal("Cancelled",restored.ReleaseResource(p.Job.Id,p.Token,1,false).Status);
    }
    [Fact] public void Other_software_runs_during_offer_but_granted_expiry_stays_isolated()
    {
        var clock=new Clock();using var s=new Scheduler(new JobStore(root),clock);
        var p=s.RequestResource(Request("a"));var app=s.RequestResource(new("b","other","app:editor","独立软件",60));
        Assert.Single(s.Claim());var offer=s.GetResource(p.Job.Id,p.Token);
        s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation);
        clock.Utc=clock.Utc.AddSeconds(61);s.Claim();Assert.Equal("RecoveryRequired",s.GetResource(p.Job.Id,p.Token).Status);
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Utc=new(2026,10,29,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Utc;
    }
    [Fact] public void Retry_preserves_task_identity_and_joins_tail_without_reusing_old_offer()
    {
        var clock=new Clock();using var s=new Scheduler(new JobStore(root),clock);
        var p=s.RequestResource(Request("first"));s.Claim();var old=s.GetResource(p.Job.Id,p.Token);
        clock.Utc=clock.Utc.AddSeconds(34);var second=s.RequestResource(Request("second"));s.Claim();
        var retry=s.RequeueResource(p.Job.Id,p.Token);Assert.Equal(p.Job.Id,retry.Job.Id);
        Assert.True(retry.Job.Sequence>second.Job.Sequence);
        Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,old.OfferId!,old.Job.Generation));
        var other=s.GetResource(second.Job.Id,second.Token);s.ReleaseResource(second.Job.Id,second.Token,other.Job.Generation,false);
        s.Claim();var fresh=s.GetResource(p.Job.Id,p.Token);Assert.NotEqual(old.OfferId,fresh.OfferId);
        Assert.True(fresh.Job.Generation>old.Job.Generation);
    }
    [Fact] public void Three_expired_offers_stop_retrying_and_never_become_granted_or_recovery()
    {
        var clock=new Clock();using var s=new Scheduler(new JobStore(root),clock);var p=s.RequestResource(Request("bounded"));
        for(var i=1;i<=3;i++)
        {
            s.Claim();Assert.Equal(i,s.GetResource(p.Job.Id,p.Token).Job.Generation);clock.Utc=clock.Utc.AddSeconds(34);
            Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
            if(i<3)s.RequeueResource(p.Job.Id,p.Token);
        }
        Assert.Equal("RESUME_LIMIT",Assert.Throws<DispatchException>(()=>s.RequeueResource(p.Job.Id,p.Token)).Code);
        Assert.Null(s.GetResource(p.Job.Id,p.Token).ExpiresAt);Assert.DoesNotContain(s.List(),j=>j.State==JobState.RecoveryRequired);
    }
}
