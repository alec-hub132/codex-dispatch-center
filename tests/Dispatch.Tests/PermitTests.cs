using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class PermitTests
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "permit-tests"), Guid.NewGuid().ToString("N"));
    private static ResourceRequest Request(string id) => new(id, "窗口A", "solidworks", "验证本窗口功能", 30);

    [Fact] public void Mcp_solidworks_and_other_apps_run_together_but_same_software_waits()
    {
        using var s=new Scheduler(new JobStore(root));
        var sw=s.RequestResource(Request("sw"));
        var desktop=s.RequestResource(Request("desktop") with{Resource="desktop"});
        var a=s.RequestResource(Request("app-a") with{Resource="app:editor"});
        var b=s.RequestResource(Request("app-b") with{Resource="app:viewer"});
        var same=s.RequestResource(Request("same") with{Resource="app:editor"});
        var sw2=s.RequestResource(Request("sw2") with{Resource="app:solidworks"});
        Assert.Equal(4,s.Claim().Count);
        Assert.Equal("Queued",s.GetResource(same.Job.Id,same.Token).Status);
        Assert.Equal("Queued",s.GetResource(sw2.Job.Id,sw2.Token).Status);
    }
    [Fact] public void Mouse_based_sw_requests_both_locks_and_readonly_suite_only_locks_sw()
    {
        using var s=new Scheduler(new JobStore(root));
        var ui=s.RequestResource(Request("ui") with{Resource="solidworks-ui"});
        Assert.Equal(new[]{"sw-main","desktop-ui"},ui.Job.Suite.Resources);
        Assert.Equal(new[]{"sw-main"},SuiteCatalog.Get("sw-readonly").Resources);
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request("bad") with{Resource="app:../bad"}));
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request("newline") with{Resource="app:editor\n"}));
    }
    [Fact]
    public void Permit_and_builtin_test_share_fifo_without_starting_external_runner()
    {
        using var s = new Scheduler(new JobStore(root));
        var permit = s.RequestResource(Request("permit-a"));
        var test = s.Submit(new("test-a", "窗口B", "mock-sw"));
        var granted = Assert.Single(s.Claim());
        Assert.Equal(permit.Job.Id, granted.Id);
        Assert.Equal("external-agent", granted.Suite.Runner);
        Assert.Empty(s.Claim());
        var state = s.GetResource(permit.Job.Id, permit.Token);
        Assert.Equal("Granted", state.Status);
        s.ReleaseResource(granted.Id, permit.Token, granted.Generation, true);
        Assert.Equal(JobState.Released, s.Get(granted.Id).State);
        Assert.Equal(test.Id, Assert.Single(s.Claim()).Id);
    }
    [Fact]
    public void Earlier_builtin_runs_first_and_generic_completion_cannot_release_permit()
    {
        using var s = new Scheduler(new JobStore(root));
        var test = s.Submit(new("first-test", "窗口B", "mock-sw"));
        var permit = s.RequestResource(Request("permit-a"));
        var first = Assert.Single(s.Claim());
        Assert.Equal(test.Id, first.Id);
        Assert.Empty(s.Claim());
        s.Complete(first.Id, first.Generation, JobState.Passed, true, "模拟完成");
        var granted = Assert.Single(s.Claim());
        Assert.Equal(permit.Job.Id, granted.Id);
        Assert.Throws<DispatchException>(() => s.Complete(granted.Id, granted.Generation, JobState.Passed, true, "不能冒充许可释放"));
        Assert.Equal("Granted", s.GetResource(granted.Id, permit.Token).Status);
        Assert.DoesNotContain(permit.Token, System.Text.Json.JsonSerializer.Serialize(s.List(), Json.Options));
    }
    [Fact]
    public void Wrong_token_stale_generation_and_missing_cleanup_never_unlock()
    {
        using var s = new Scheduler(new JobStore(root));
        var permit = s.RequestResource(Request("permit-a")); var job = Assert.Single(s.Claim());
        Assert.Throws<DispatchException>(() => s.GetResource(job.Id, "wrong-token"));
        Assert.Throws<DispatchException>(() => s.ReleaseResource(job.Id, permit.Token, job.Generation - 1, true));
        s.Submit(new("test-a", "窗口B", "mock-sw"));
        s.ReleaseResource(job.Id, permit.Token, job.Generation, false);
        Assert.Equal(JobState.RecoveryRequired, s.Get(job.Id).State);
        Assert.Empty(s.Claim());
        Assert.Throws<DispatchException>(() => s.Cancel(job.Id));
        s.ReleaseResource(job.Id, permit.Token, job.Generation, true);
        Assert.Single(s.Claim());
    }
    [Fact]
    public void Expiry_quarantines_instead_of_reassigning_and_owner_can_cleanup()
    {
        var clock = new Clock();
        using var s = new Scheduler(new JobStore(root), clock);
        var permit = s.RequestResource(Request("permit-a"));var job=Assert.Single(s.Claim());
        clock.Utc = clock.Utc.AddSeconds(31);
        s.Submit(new("test-a", "窗口B", "mock-sw"));
        Assert.Empty(s.Claim());
        Assert.Equal("RecoveryRequired", s.GetResource(job.Id, permit.Token).Status);
        s.ReleaseResource(job.Id, permit.Token, job.Generation, true);
        Assert.Single(s.Claim());
    }
    [Fact]
    public void Restart_keeps_owner_token_and_idempotency_but_quarantines_active_permit()
    {
        ResourcePermit permit;
        using(var s=new Scheduler(new JobStore(root))){permit=s.RequestResource(Request("permit-a"));s.Claim();}
        using var restored = new Scheduler(new JobStore(root));
        Assert.Equal("RecoveryRequired",restored.GetResource(permit.Job.Id,permit.Token).Status);
        Assert.Equal(permit.Token,restored.RequestResource(Request("permit-a")).Token);
        Assert.Throws<DispatchException>(()=>restored.RequestResource(Request("permit-a") with{Purpose="changed"}));
        Assert.Throws<DispatchException>(()=>restored.Submit(new("permit-a", "窗口A", "resource-solidworks")));
        Assert.Empty(restored.Claim());
    }
    [Fact]
    public void Queued_permit_is_cancellable_and_invalid_resource_is_rejected()
    {
        using var s=new Scheduler(new JobStore(root));
        var permit=s.RequestResource(Request("permit-a"));s.Cancel(permit.Job.Id);
        Assert.Equal("Cancelled",s.GetResource(permit.Job.Id,permit.Token).Status);
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request("permit-b") with{Resource="arbitrary-shell"}));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026,10,28,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Utc;
    }
}
