using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class SchedulerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "dispatch-tests"), Guid.NewGuid().ToString("N"));
    private Scheduler Open() => new(new JobStore(root));
    private static Submission Request(string id, string suite = "mock-sw", string session = "窗口A") => new(id, session, suite);

    [Fact]
    public void Conflicting_jobs_are_fifo_but_headless_can_pass()
    {
        using var s = Open();
        var a = s.Submit(Request("request-a"));
        var b = s.Submit(Request("request-b", session: "窗口B"));
        var c = s.Submit(Request("request-c", "mock-headless"));
        var first = s.Claim();
        Assert.Equal(new[] { a.Id, c.Id }, first.Select(j => j.Id));
        Assert.Empty(s.Claim());
        s.Complete(a.Id, a.Generation + 1, JobState.Passed, true, "模拟完成");
        Assert.Equal(b.Id, Assert.Single(s.Claim()).Id);
    }

    [Fact]
    public void Idempotency_is_scoped_to_session_and_rejects_changed_payload()
    {
        using var s = Open();
        var a = s.Submit(Request("request-a"));
        Assert.Equal(a.Id, s.Submit(Request("request-a")).Id);
        Assert.Throws<DispatchException>(() => s.Submit(Request("request-a", "mock-headless")));
        Assert.NotEqual(a.Id, s.Submit(Request("request-a", session: "窗口B")).Id);
    }

    [Fact]
    public void Restart_keeps_uncertain_resources_locked_and_queued_jobs_persist()
    {
        string aId;
        using (var s = Open()) { aId = s.Submit(Request("request-a")).Id; s.Claim(); s.Submit(Request("request-b")); }
        using var restored = Open();
        Assert.Equal(JobState.RecoveryRequired, restored.Get(aId).State);
        Assert.Empty(restored.Claim());
        Assert.Equal(2, restored.List().Count);
    }

    [Fact]
    public void Failed_cleanup_and_stale_completion_never_release_resource()
    {
        using var s = Open();
        s.Submit(Request("request-a"));
        var a = Assert.Single(s.Claim());
        s.Submit(Request("request-b"));
        Assert.Throws<DispatchException>(() => s.Complete(a.Id, a.Generation - 1, JobState.Passed, true, "过期回执"));
        s.Complete(a.Id, a.Generation, JobState.Failed, false, "清理不确定");
        Assert.Empty(s.Claim());
        Assert.Equal(JobState.RecoveryRequired, s.Get(a.Id).State);
    }

    [Fact]
    public void Pause_cancel_and_terminal_cancel_preserve_state()
    {
        using var s = Open();
        var a = s.Submit(Request("request-a"));
        s.SetPaused(true);
        Assert.Empty(s.Claim());
        s.Cancel(a.Id);
        s.SetPaused(false);
        Assert.Empty(s.Claim());
        Assert.Equal(JobState.Cancelled, s.Get(a.Id).State);
        s.Cancel(a.Id);
        Assert.Equal(JobState.Cancelled, s.Get(a.Id).State);
    }

    [Fact]
    public void Model_decision_is_persisted_without_claiming_switch_success()
    {
        string id;
        using (var s = Open()) { id = s.RequestModel(new("decision-a", "窗口A", "用户选择的模型", "high", "进入审查", "检查资源竞争")).Id; }
        using var restored = Open();
        Assert.Equal("UNSUPPORTED", restored.GetDecision(id).Status);
        Assert.Equal("用户选择的模型", restored.GetDecision(id).Request.Model);
    }

    [Fact]
    public void Concurrent_retries_create_one_job_and_exposed_snapshot_cannot_change_locks()
    {
        using var s = Open();
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, _ => ids.Add(s.Submit(Request("same-request")).Id));
        Assert.Single(ids.Distinct());
        var snapshot = s.List()[0];
        snapshot.Suite.Resources[0] = "伪造资源";
        Assert.Contains("sw-main", s.Get(snapshot.Id).Suite.Resources);
        Assert.DoesNotContain("伪造资源", s.Get(snapshot.Id).Suite.Resources);
    }

    [Fact]
    public void Pause_survives_restart_and_store_has_exclusive_owner()
    {
        using (var s = Open())
        {
            s.SetPaused(true);
            s.Submit(Request("request-a"));
            Assert.Throws<IOException>(() => new JobStore(root));
        }
        using var restored = Open();
        Assert.True(restored.Paused);
        Assert.Empty(restored.Claim());
        restored.SetPaused(false);
        Assert.Single(restored.Claim());
    }

    [Fact]
    public void Active_cancel_does_not_release_lease_before_completion()
    {
        using var s = Open();
        s.Submit(Request("request-a"));
        var a = Assert.Single(s.Claim());
        s.Submit(Request("request-b"));
        s.Cancel(a.Id);
        Assert.Empty(s.Claim());
        Assert.Equal(JobState.Running, s.Get(a.Id).State);
        s.Complete(a.Id, a.Generation, JobState.Cancelled, true, "进程退出已验证");
        Assert.Single(s.Claim());
    }

    [Fact]
    public void Unknown_suite_and_invalid_identity_are_rejected()
    {
        using var s = Open();
        Assert.Throws<DispatchException>(() => s.Submit(Request("request-a", "powershell.exe")));
        Assert.Throws<DispatchException>(() => s.Submit(Request("request-a", session: "")));
        Assert.Empty(s.List());
    }

    public void Dispose() { /* 保留本轮测试证据，不自动删除。 */ }
}
