using Dispatch.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dispatch.Tests;

public sealed class QueueChangeTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dispatch-changes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unchanged_history_is_not_resent_and_committed_states_are_synchronized()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var permit = scheduler.RequestResource(new("one", "A", "app:fixture", "纯虚拟增量状态验证", RequireAcceptance: true));
        var initial = scheduler.ListChanges();
        Assert.True(initial.Reset);
        Assert.Single(initial.Items);
        Assert.Empty(scheduler.ListChanges(initial.Epoch, initial.UpperRevision).Items);
        scheduler.Claim();
        var changes = scheduler.ListChanges(initial.Epoch, initial.UpperRevision);
        Assert.False(changes.Reset);
        Assert.Equal(JobState.Offered, Assert.Single(changes.Items).State);
        var offer = scheduler.GetResource(permit.Job.Id, permit.Token);
        var accepted = scheduler.AcceptResource(permit.Job.Id, permit.Token, offer.OfferId!, offer.Job.Generation);
        Assert.Equal(JobState.Running, Assert.Single(scheduler.ListChanges(changes.Epoch, changes.UpperRevision).Items).State);
        var beforeRelease = scheduler.ListChanges().UpperRevision;
        scheduler.ReleaseResource(permit.Job.Id, permit.Token, accepted.Job.Generation, true);
        var released = Assert.Single(scheduler.ListChanges(changes.Epoch, beforeRelease).Items);
        Assert.Equal(JobState.Released, released.State);
        Assert.Equal("OK", released.CleanupStatus);
    }

    [Fact]
    public void Paged_changes_do_not_lose_updates_arriving_during_a_refresh()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        for (var i = 0; i < 80; i++) scheduler.RequestResource(new("r" + i, "fixture", "app:fixture", new string('测', 1000)));
        var page = scheduler.ListChanges();
        var epoch = page.Epoch;
        var upper = page.UpperRevision;
        Assert.NotNull(page.NextRevision);
        var earlierId = page.Items[0].Id;
        scheduler.Cancel(earlierId);
        var later = scheduler.Submit(new("later", "fixture", "mock-sw"));
        var seen = new List<Job>(page.Items);
        while (page.NextRevision is {} cursor)
        {
            page = scheduler.ListChanges(epoch, cursor, upper);
            Assert.False(page.Reset);
            seen.AddRange(page.Items);
        }
        Assert.Equal(80, seen.Select(j => j.Id).Distinct().Count());
        var next = scheduler.ListChanges(epoch, upper);
        Assert.Equal(2, next.Items.Length);
        Assert.Equal(JobState.Cancelled, next.Items.Single(j => j.Id == earlierId).State);
        Assert.Contains(next.Items, j => j.Id == later.Id);
        Assert.Empty(scheduler.ListChanges(epoch, next.UpperRevision).Items);
    }

    [Fact]
    public void Restart_changes_epoch_and_invalid_cursors_do_not_silently_drop_history()
    {
        string epoch;
        long revision;
        using (var scheduler = new Scheduler(new JobStore(root)))
        {
            scheduler.Submit(new("one", "fixture", "mock-sw"));
            var first = scheduler.ListChanges();
            epoch = first.Epoch;
            revision = first.UpperRevision;
            Assert.Throws<DispatchException>(() => scheduler.ListChanges(epoch, -1));
            Assert.Throws<DispatchException>(() => scheduler.ListChanges(epoch, revision + 1));
        }
        using var reopened = new Scheduler(new JobStore(root));
        var reset = reopened.ListChanges(epoch, revision);
        Assert.True(reset.Reset);
        Assert.NotEqual(epoch, reset.Epoch);
        Assert.Single(reset.Items);
    }

    [Fact]
    public void Manual_priority_and_withdrawal_are_visible_without_changing_sequence()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var job = scheduler.Submit(new("one", "fixture", "mock-sw"));
        var before = scheduler.ListChanges();
        scheduler.RequestManualDispatch(new(job.Id, job.Generation, job.State));
        var prioritized = scheduler.ListChanges(before.Epoch, before.UpperRevision);
        Assert.True(Assert.Single(prioritized.Items).UserPriority);
        Assert.Equal(job.Sequence, prioritized.Items[0].Sequence);
        scheduler.ManualReleaseResource(new(job.Id, job.Generation, JobState.Queued, false, false, "人工撤回纯模拟排队"));
        Assert.Equal(JobState.Cancelled, Assert.Single(scheduler.ListChanges(prioritized.Epoch, prioritized.UpperRevision).Items).State);
    }

    [Fact]
    public void Failed_database_write_does_not_publish_a_queue_change()
    {
        using var scheduler = new Scheduler(new JobStore(root));
        var job = scheduler.Submit(new("one", "fixture", "mock-sw"));
        var before = scheduler.ListChanges();
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "dispatch.db"));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_job BEFORE UPDATE ON records WHEN NEW.kind='job' BEGIN SELECT RAISE(ABORT,'injected'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => scheduler.Cancel(job.Id));
        Assert.Equal(JobState.Queued, scheduler.Get(job.Id).State);
        Assert.Empty(scheduler.ListChanges(before.Epoch, before.UpperRevision).Items);
    }
}
