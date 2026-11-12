using Dispatch.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace Dispatch.Tests;

public sealed class QueueManagementTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "queue-management"),Guid.NewGuid().ToString("N"));
    private string CreateHome()
    {
        var home=Path.Combine(root,"codex");Directory.CreateDirectory(home);
        using var db=new SqliteConnection($"Data Source={Path.Combine(home,"state_5.sqlite")};Pooling=False");db.Open();
        using var c=db.CreateCommand();c.CommandText="CREATE TABLE threads(id TEXT PRIMARY KEY,name TEXT,title TEXT); INSERT INTO threads VALUES('thread-a','真实窗口','首条消息'),('thread-b','真实窗口','另一条消息');";c.ExecuteNonQuery();
        return home;
    }
    [Fact] public void Names_follow_native_rename_and_never_merge_same_named_threads_or_aliases()
    {
        using var s=new Scheduler(new JobStore(root));var home=CreateHome();s.SetRuntimeSource(home);
        var alias=s.RequestResource(new("c","真实窗口","solidworks","c"));
        // 元数据名称相同，真实编号仍不同；不按自由标签猜测聊天。
        using(var metadata=new SqliteConnection($"Data Source={Path.Combine(home,"state_5.sqlite")};Pooling=False"))
        {metadata.Open();using var c=metadata.CreateCommand();c.CommandText="UPDATE threads SET id='11111111-1111-1111-1111-111111111111' WHERE id='thread-a';UPDATE threads SET id='22222222-2222-2222-2222-222222222222' WHERE id='thread-b'";c.ExecuteNonQuery();}
        var one=s.RequestResource(new("one","11111111-1111-1111-1111-111111111111","solidworks","one"));
        var two=s.RequestResource(new("two","22222222-2222-2222-2222-222222222222","solidworks","two"));
        var owners=s.GetQueueOwners(new[]{one.Job.Id,two.Job.Id,alias.Job.Id});
        Assert.Equal("真实窗口",owners[0].Name);Assert.Equal("真实窗口",owners[1].Name);
        Assert.NotEqual(owners[0].GroupKey,owners[1].GroupKey);Assert.Null(owners[2].ThreadId);
        using(var db=new SqliteConnection($"Data Source={Path.Combine(home,"state_5.sqlite")};Pooling=False"))
        {db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE threads SET name='已改名' WHERE id LIKE '11111111%'";c.ExecuteNonQuery();}
        Assert.Equal("已改名",s.GetQueueOwners(new[]{one.Job.Id})[0].Name);
        Assert.DoesNotContain(one.Token,JsonSerializer.Serialize(owners,Json.Options));
    }
    [Fact] public void Missing_native_metadata_degrades_without_losing_queue_or_creating_database()
    {
        using var s=new Scheduler(new JobStore(root));var home=Path.Combine(root,"empty");Directory.CreateDirectory(home);s.SetRuntimeSource(home);
        var a=s.RequestResource(new("a","11111111-1111-1111-1111-111111111111","solidworks","a"));
        var owner=Assert.Single(s.GetQueueOwners(new[]{a.Job.Id}));Assert.NotNull(owner.ThreadId);Assert.Equal("UNAVAILABLE",owner.NameStatus);
        Assert.Empty(Directory.GetFiles(home));Assert.Single(s.List());
    }
    [Fact] public void Manual_release_requires_both_human_confirmations_and_current_snapshot_and_is_audited()
    {
        using var s=new Scheduler(new JobStore(root));var a=s.RequestResource(new("a","窗口A","solidworks","a"));var job=Assert.Single(s.Claim());
        var b=s.RequestResource(new("b","窗口B","solidworks","b"));
        var request=new QueueManualRelease(a.Job.Id,job.Generation,JobState.Running,true,false,"用户确认测试已结束");
        Assert.Throws<DispatchException>(()=>s.ManualReleaseResource(request));Assert.Empty(s.Claim());
        Assert.Throws<DispatchException>(()=>s.ManualReleaseResource(request with{OwnedResourcesCleaned=true,Generation=0}));
        var released=s.ManualReleaseResource(request with{OwnedResourcesCleaned=true});
        Assert.Equal(JobState.Released,released.State);Assert.Equal("OK",released.CleanupStatus);Assert.Equal(b.Job.Id,Assert.Single(s.Claim()).Id);
        Assert.DoesNotContain(a.Token,JsonSerializer.Serialize(released,Json.Options));
        Assert.Single(new JobStoreReader(root).ReadAudits());
    }
    [Fact] public void Stale_queued_snapshot_cannot_release_a_newly_granted_or_offered_job()
    {
        using var s=new Scheduler(new JobStore(root));var a=s.RequestResource(new("a","A","solidworks","a",RequireAcceptance:true));s.Claim();
        Assert.Throws<DispatchException>(()=>s.ManualReleaseResource(new(a.Job.Id,0,JobState.Queued,false,false,"取消等待")));
        var offered=s.Get(a.Job.Id);var cancelled=s.ManualReleaseResource(new(a.Job.Id,offered.Generation,offered.State,false,false,"撤回预留"));
        Assert.Equal(JobState.Cancelled,cancelled.State);Assert.Equal("NOT_REQUIRED",cancelled.CleanupStatus);
        Assert.Equal("Cancelled",s.GetResource(a.Job.Id,a.Token).Status);
    }
    [Fact] public void Running_registered_process_blocks_manual_cleanup_confirmation()
    {
        using var s=new Scheduler(new JobStore(root));var a=s.RequestResource(new("a","A","solidworks","a"));var job=Assert.Single(s.Claim());
        using var process=System.Diagnostics.Process.GetCurrentProcess();s.AttachRunner(job.Id,job.Generation,process.Id,process.StartTime);
        var ex=Assert.Throws<DispatchException>(()=>s.ManualReleaseResource(new(job.Id,job.Generation,job.State,true,true,"用户已清理")));
        Assert.Equal("PROCESS_STILL_RUNNING",ex.Code);Assert.Equal(JobState.Running,s.Get(job.Id).State);
    }
    [Fact] public void Corrupt_native_database_does_not_hide_jobs_and_native_reads_do_not_modify_it()
    {
        using var s=new Scheduler(new JobStore(root));var home=CreateHome();s.SetRuntimeSource(home);
        var a=s.RequestResource(new("a","11111111-1111-1111-1111-111111111111","solidworks","a"));
        var db=Path.Combine(home,"state_5.sqlite");var before=File.ReadAllBytes(db);
        s.GetQueueOwners(new[]{a.Job.Id});Assert.Equal(before,File.ReadAllBytes(db));
        File.WriteAllText(Path.Combine(home,"state_99.sqlite"),"invalid database");
        Assert.Equal("UNAVAILABLE",s.GetQueueOwners(new[]{a.Job.Id})[0].NameStatus);Assert.Single(s.List());
    }
    [Fact] public void Recovery_can_be_manually_released_only_after_cleanup_even_when_queue_is_disabled()
    {
        ResourcePermit p;
        using(var s=new Scheduler(new JobStore(root))){p=s.RequestResource(new("a","A","solidworks","a"));s.Claim();}
        using var reopened=new Scheduler(new JobStore(root));var job=reopened.Get(p.Job.Id);Assert.Equal(JobState.RecoveryRequired,job.State);
        reopened.SetModules(reopened.GetModules() with{QueueEnabled=false});
        var req=new QueueManualRelease(job.Id,job.Generation,job.State,false,true,"用户确认已清理");
        Assert.Throws<DispatchException>(()=>reopened.ManualReleaseResource(req));
        Assert.Equal(JobState.Released,reopened.ManualReleaseResource(req with{OperationsStopped=true}).State);
        Assert.Equal("OK",reopened.GetResource(p.Job.Id,p.Token).Job.CleanupStatus);
    }
    private sealed class JobStoreReader(string folder)
    {
        public string[] ReadAudits()
        {
            using var db=new SqliteConnection($"Data Source={Path.Combine(folder,"dispatch.db")};Mode=ReadOnly");db.Open();
            using var c=db.CreateCommand();c.CommandText="SELECT body FROM records WHERE kind='queue-manual-release'";using var r=c.ExecuteReader();
            var rows=new List<string>();while(r.Read())rows.Add(r.GetString(0));return rows.ToArray();
        }
    }
}
