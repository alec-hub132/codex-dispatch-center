using Dispatch.Core;
using System.Text.Json;
using Xunit;
namespace Dispatch.Tests;
public sealed class PermitEntryTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "permit-entry-tests"),Guid.NewGuid().ToString("N"));
    [Fact] public async Task Mcp_request_without_acceptance_option_never_grants_to_an_absent_window()
    {
        using var s=new Scheduler(new JobStore(root));var api=new ServiceApi(s);
        var args=JsonSerializer.SerializeToElement(new{requestId="a",sessionId="finished-window",resource="app:fixture",purpose="结束窗口不能被直接授予"},Json.Options);
        var p=Assert.IsType<ResourcePermit>(await api.Execute("request_resource",args,CancellationToken.None));
        s.Claim();var offer=s.GetResource(p.Job.Id,p.Token);
        Assert.Equal("Offered",offer.Status);Assert.Null(offer.ExpiresAt);Assert.Null(offer.Job.StartedAt);
        Assert.Equal(p.Job.Id,Assert.IsType<ResourcePermit>(await api.Execute("request_resource",args,CancellationToken.None)).Job.Id);
    }
    [Fact] public void Rejected_cleanup_receipt_records_reason_without_token_and_does_not_unlock()
    {
        using var s=new Scheduler(new JobStore(root));var p=s.RequestResource(new("a","owner","app:fixture","释放拒绝诊断",60));s.Claim();
        Assert.Equal("STALE_RESULT",Assert.Throws<DispatchException>(()=>s.ReleaseResource(p.Job.Id,p.Token,0,true)).Code);
        Assert.Equal("Granted",s.GetResource(p.Job.Id,p.Token).Status);
        var log=File.ReadAllText(Path.Combine(root,"logs","bug.jsonl"));
        Assert.Contains("RESOURCE_RELEASE_REJECTED",log);Assert.Contains("STALE_RESULT",log);Assert.DoesNotContain(p.Token,log);
    }
    [Fact] public void Upgrade_converts_only_ungranted_legacy_queue_and_preserves_owner_cleanup()
    {
        ResourcePermit held,queued;
        using(var s=new Scheduler(new JobStore(root)))
        {
            held=s.RequestResource(new("held","owner","app:fixture","持有许可",60));s.Claim();
            queued=s.RequestResource(new("queued","stopped-window","app:fixture","停止窗口的旧申请",60));
        }
        using var next=new Scheduler(new JobStore(root));
        Assert.Equal(1,next.UpgradeQueuedResourcePermits());Assert.Equal(0,next.UpgradeQueuedResourcePermits());
        var isolated=next.GetResource(held.Job.Id,held.Token);
        Assert.Equal("RecoveryRequired",isolated.Status);Assert.Equal(1,isolated.Job.Generation);Assert.Equal("UNCERTAIN",isolated.Job.CleanupStatus);
        Assert.Empty(next.Claim());Assert.Equal("Queued",next.GetResource(queued.Job.Id,queued.Token).Status);
        next.ReleaseResource(held.Job.Id,held.Token,1,true);next.Claim();
        var offer=next.GetResource(queued.Job.Id,queued.Token);Assert.Equal("Offered",offer.Status);Assert.Null(offer.ExpiresAt);
        Assert.Equal(queued.Token,offer.Token);Assert.Equal(queued.Job.Id,offer.Job.Id);
    }
}
