using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class ManualDispatchTests
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "manual-dispatch"), Guid.NewGuid().ToString("N"));

    [Fact] public void Selected_builtin_runs_before_older_waiter_without_changing_history_sequence()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var earlier = scheduler.Submit(new("earlier", "A", "mock-sw"));
        var selected = scheduler.Submit(new("selected", "B", "mock-sw"));
        var result = scheduler.RequestManualDispatch(new(selected.Id, 0, JobState.Queued));
        Assert.Equal(JobState.Queued, result.Job.State);
        Assert.True(result.Job.UserPriority);
        Assert.Equal(selected.Sequence, result.Job.Sequence);
        var running = Assert.Single(scheduler.Claim());
        Assert.Equal(selected.Id, running.Id);
        Assert.False(running.UserPriority);
        scheduler.Complete(running.Id, running.Generation, JobState.Passed, true, "模拟完成");
        Assert.Equal(earlier.Id, Assert.Single(scheduler.Claim()).Id);
    }

    [Fact] public void External_requires_real_acceptance_and_repeated_click_is_idempotent()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var permit = scheduler.RequestResource(new("p", "A", "solidworks", "虚拟流程", RequireAcceptance: true));
        var request = new ManualDispatchRequest(permit.Job.Id, 0, JobState.Queued);
        var first = scheduler.RequestManualDispatch(request);
        Assert.Equal(first.Job.Id, scheduler.RequestManualDispatch(request).Job.Id);
        scheduler.Claim();
        var offered = scheduler.GetResource(permit.Job.Id, permit.Token);
        Assert.Equal("Offered", offered.Status);
        Assert.False(offered.Job.UserPriority);
        Assert.Throws<DispatchException>(() => scheduler.RequestManualDispatch(request));
        var accepted = scheduler.AcceptResource(permit.Job.Id, permit.Token, offered.OfferId!, offered.Job.Generation);
        Assert.Equal("Granted", accepted.Status);
        scheduler.ReleaseResource(permit.Job.Id, permit.Token, accepted.Job.Generation, true);
    }

    [Fact] public void Busy_or_recovery_holder_blocks_promotion_and_other_software_is_independent()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var holder = scheduler.RequestResource(new("h", "A", "solidworks", "原占用"));
        scheduler.Claim();
        var waiting = scheduler.RequestResource(new("w", "B", "solidworks", "等待"));
        var request = new ManualDispatchRequest(waiting.Job.Id, 0, JobState.Queued);
        Assert.Equal("RESOURCE_BUSY", Assert.Throws<DispatchException>(() => scheduler.RequestManualDispatch(request)).Code);
        var current = scheduler.GetResource(holder.Job.Id, holder.Token);
        scheduler.ReleaseResource(holder.Job.Id, holder.Token, current.Job.Generation, false);
        Assert.Equal("RESOURCE_BUSY", Assert.Throws<DispatchException>(() => scheduler.RequestManualDispatch(request)).Code);
        var independent = scheduler.RequestResource(new("i", "C", "app:viewer", "另一软件"));
        Assert.True(scheduler.RequestManualDispatch(new(independent.Job.Id, 0, JobState.Queued)).Job.UserPriority);
        Assert.Equal(JobState.RecoveryRequired, scheduler.Get(holder.Job.Id).State);
    }

    [Fact] public void Human_priority_survives_restart_and_respects_disabled_or_paused_queue()
    {
        string selected;
        using (var scheduler = new Scheduler(new JobStore(root)))
        {
            scheduler.Submit(new("a", "A", "mock-sw"));
            selected = scheduler.Submit(new("b", "B", "mock-sw")).Id;
            scheduler.SetPaused(true);
            Assert.Throws<DispatchException>(() => scheduler.RequestManualDispatch(new(selected, 0, JobState.Queued)));
            scheduler.SetPaused(false);
            scheduler.RequestManualDispatch(new(selected, 0, JobState.Queued));
        }
        using var reopened = new Scheduler(new JobStore(root));
        Assert.True(reopened.Get(selected).UserPriority);
        Assert.Equal(selected, Assert.Single(reopened.Claim()).Id);
    }
}
