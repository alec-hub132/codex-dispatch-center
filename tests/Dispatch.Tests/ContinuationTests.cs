using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class ContinuationTests
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "continuation-tests"), Guid.NewGuid().ToString("N"));
    public static WorkspaceScene Scene(int stamp = 10) => new(123, new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), "29.5.1",
        [new("D:/temporary/A.SLDPRT", "A.SLDPRT", 1, stamp, false, "默认", null,
            [1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0], [0,0,0], 1, [])], "D:/temporary/A.SLDPRT");
    private static ResourceRequest Request(string id, string session = "窗口A") => new(id, session, "solidworks", "分段验证", 600);
    private static ResourceCheckpoint Capture(Scheduler s, ResourcePermit p, string request = "capture-a")
    {
        s.BeginWorkspaceCapture(p.Job.Id, p.Token, 1, request, "已完成第一段", "继续第二段", true);
        return s.FinishWorkspaceCapture(p.Job.Id, request, Scene());
    }

    [Fact]
    public void Yield_allows_B_while_A_thinks_and_resume_keeps_existing_fifo()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim();
        var checkpoint = Capture(s, a);
        var b = s.RequestResource(Request("b", "窗口B"));
        Assert.Empty(s.Claim());
        s.YieldResource(a.Job.Id, a.Token, 1, checkpoint.Id, true);
        Assert.Equal(b.Job.Id, Assert.Single(s.Claim()).Id);
        var c = s.RequestResource(Request("c", "窗口C"));
        var resume = s.ResumeResource(a.Job.Id, a.Token, checkpoint.Id, "resume-a");
        Assert.Equal("Queued", resume.Status);
        s.ReleaseResource(b.Job.Id, b.Token, 1, true);
        Assert.Equal(c.Job.Id, Assert.Single(s.Claim()).Id);
        s.ReleaseResource(c.Job.Id, c.Token, 1, true);
        Assert.Equal(resume.Job.Id, Assert.Single(s.Claim()).Id);
        Assert.True(s.GetResource(resume.Job.Id, resume.Token).RestoreRequired);
        Assert.Throws<DispatchException>(() => s.BeginWorkspaceCapture(resume.Job.Id, resume.Token, 1, "skip-restore", "绕过恢复", "不得继续", true));
        Assert.Contains(s.GetResourceEvents(a.Job.Id, a.Token, 0), e => e.Kind == "RESTORE_REQUIRED");
        s.BeginWorkspaceRestore(resume.Job.Id, resume.Token, 1, checkpoint.Id);
        s.FinishWorkspaceRestore(resume.Job.Id, new(true, true, "已回读核验"));
        Assert.False(s.GetResource(resume.Job.Id, resume.Token).RestoreRequired);
    }

    [Fact]
    public void Missing_cleanup_bad_token_stale_generation_and_active_capture_never_yield()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim();
        s.BeginWorkspaceCapture(a.Job.Id, a.Token, 1, "cap", "已完成", "剩余", true);
        Assert.Throws<DispatchException>(() => s.ReleaseResource(a.Job.Id, a.Token, 1, true));
        var checkpoint = s.FinishWorkspaceCapture(a.Job.Id, "cap", Scene());
        Assert.Throws<DispatchException>(() => s.YieldResource(a.Job.Id, "bad", 1, checkpoint.Id, true));
        Assert.Throws<DispatchException>(() => s.YieldResource(a.Job.Id, a.Token, 0, checkpoint.Id, true));
        Assert.Throws<DispatchException>(() => s.YieldResource(a.Job.Id, a.Token, 1, checkpoint.Id, false));
        Assert.Equal("Granted", s.GetResource(a.Job.Id, a.Token).Status);
    }

    [Fact]
    public void Checkpoint_and_resume_are_persistent_idempotent_and_query_needs_owner_token()
    {
        ResourcePermit a; ResourceCheckpoint checkpoint; ResourcePermit resume;
        using (var s = new Scheduler(new JobStore(root)))
        {
            a = s.RequestResource(Request("a")); s.Claim(); checkpoint = Capture(s, a);
            s.YieldResource(a.Job.Id, a.Token, 1, checkpoint.Id, true);
            resume = s.ResumeResource(a.Job.Id, a.Token, checkpoint.Id, "resume");
            Assert.Equal(resume.Job.Id, s.ResumeResource(a.Job.Id, a.Token, checkpoint.Id, "resume").Job.Id);
        }
        using var reopened = new Scheduler(new JobStore(root));
        Assert.Equal(checkpoint.Id, reopened.GetResourceCheckpoint(a.Job.Id, a.Token, checkpoint.Id).Id);
        Assert.Throws<DispatchException>(() => reopened.GetResourceEvents(a.Job.Id, "bad", 0));
        Assert.Equal(resume.Job.Id, reopened.ResumeResource(a.Job.Id, a.Token, checkpoint.Id, "resume").Job.Id);
        Assert.DoesNotContain(a.Token, System.Text.Json.JsonSerializer.Serialize(reopened.GetResourceEvents(a.Job.Id, a.Token, 0)));
    }

    [Fact]
    public async Task Notification_wait_returns_when_yield_commits_and_cursor_prevents_reprocessing()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim(); var cp = Capture(s, a);
        var api = new ServiceApi(s);
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { leaseId = a.Job.Id, token = a.Token, afterSequence = 0, maxWaitSeconds = 2 });
        var pending = api.Execute("get_resource_events", args, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        s.YieldResource(a.Job.Id, a.Token, 1, cp.Id, true);
        var notice = Assert.Single((ResourceNotice[])await pending);
        Assert.Equal("YIELDED", notice.Kind);
        Assert.Empty(s.GetResourceEvents(a.Job.Id, a.Token, notice.Sequence));
    }
    [Fact]
    public void New_capture_supersedes_older_scene_and_secret_progress_is_rejected()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim();
        Assert.Throws<DispatchException>(() => s.BeginWorkspaceCapture(a.Job.Id, a.Token, 1, "secret", a.Token, "无", true));
        var first = Capture(s, a, "first"); var second = Capture(s, a, "second");
        Assert.Throws<DispatchException>(() => s.YieldResource(a.Job.Id, a.Token, 1, first.Id, true));
        Assert.Equal("Granted", s.GetResource(a.Job.Id, a.Token).Status);
        s.YieldResource(a.Job.Id, a.Token, 1, second.Id, true);
        Assert.Equal("Released", s.GetResource(a.Job.Id, a.Token).Status);
    }
    [Fact]
    public void Resume_transaction_failure_never_leaves_an_orphan_queued_permit()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim(); var cp = Capture(s, a);
        s.YieldResource(a.Job.Id, a.Token, 1, cp.Id, true);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "dispatch.db"));
        db.Open(); using var trigger = db.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER reject_resume BEFORE UPDATE ON records WHEN NEW.kind='resource-checkpoint' BEGIN SELECT RAISE(ABORT,'injected persistence failure'); END";
        trigger.ExecuteNonQuery();
        Assert.ThrowsAny<Exception>(() => s.ResumeResource(a.Job.Id, a.Token, cp.Id, "resume"));
        Assert.Single(s.List());
        Assert.Empty(s.Claim());
    }
    [Fact]
    public void Disabled_queue_still_allows_yield_but_not_new_resume_and_restart_keeps_capture_uncertainty()
    {
        ResourcePermit a;
        using (var s = new Scheduler(new JobStore(root)))
        {
            a = s.RequestResource(Request("a")); s.Claim(); var cp = Capture(s, a);
            s.SetModules(s.GetModules() with { QueueEnabled = false });
            s.YieldResource(a.Job.Id, a.Token, 1, cp.Id, true);
            Assert.Throws<DispatchException>(() => s.ResumeResource(a.Job.Id, a.Token, cp.Id, "resume"));
            s.SetModules(s.GetModules() with { QueueEnabled = true });
            var r = s.ResumeResource(a.Job.Id, a.Token, cp.Id, "resume"); s.Claim();
            s.BeginWorkspaceRestore(r.Job.Id, r.Token, 1, cp.Id);
            a = r;
        }
        using var reopened = new Scheduler(new JobStore(root));
        Assert.Equal("RecoveryRequired", reopened.GetResource(a.Job.Id, a.Token).Status);
        Assert.Throws<DispatchException>(() => reopened.BeginWorkspaceRestore(a.Job.Id, a.Token, 1, reopened.GetResource(a.Job.Id, a.Token).CheckpointId!));
    }
    [Fact]
    public void Restore_failure_does_not_claim_success_and_keeps_exclusive_resource()
    {
        using var s = new Scheduler(new JobStore(root));
        var a = s.RequestResource(Request("a")); s.Claim(); var cp = Capture(s, a);
        s.YieldResource(a.Job.Id, a.Token, 1, cp.Id, true);
        var r = s.ResumeResource(a.Job.Id, a.Token, cp.Id, "resume"); s.Claim();
        s.BeginWorkspaceRestore(r.Job.Id, r.Token, 1, cp.Id);
        s.FinishWorkspaceRestore(r.Job.Id, new(false, true, "回读失败"));
        Assert.Equal("RecoveryRequired", s.GetResource(r.Job.Id, r.Token).Status);
        s.RequestResource(Request("b", "窗口B")); Assert.Empty(s.Claim());
        Assert.True(s.GetResource(r.Job.Id, r.Token).RestoreRequired);
    }
}
