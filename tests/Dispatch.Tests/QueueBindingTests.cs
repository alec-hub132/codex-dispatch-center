using Dispatch.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;
namespace Dispatch.Tests;
public sealed class QueueBindingTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "queue-binding-tests"),Guid.NewGuid().ToString("N"));
    private ResourceRequest Request()=>new("request-a","alias-only","app:test-editor","验证绑定不操作软件",60,false,"thread-a","turn-a","读取原测试报告并清理");
    private void Runtime(Scheduler s,string turn="turn-a")
    {
        var home=Path.Combine(root,"codex");var folder=Path.Combine(home,"sessions");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"rollout-thread-a.jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id="thread-a"}})+"\n"+
            JsonSerializer.Serialize(new{type="turn_context",timestamp=DateTimeOffset.UtcNow,payload=new{turn_id=turn,model="test-model",effort="medium"}})+"\n");
        s.SetRuntimeSource(home);
    }
    [Fact] public void Queue_consent_is_separate_from_models_and_binding_is_atomic_and_idempotent()
    {
        using var s=new Scheduler(new JobStore(root));Runtime(s);
        File.WriteAllText(Path.Combine(root,"model-handoff-authorization.json"),"""{"threads":["thread-a"]}""");
        Assert.Equal("QUEUE_RESUME_NOT_AUTHORIZED",Assert.Throws<DispatchException>(()=>s.RequestResource(Request())).Code);
        Assert.Empty(s.List());
        s.SetQueueResumeConsent(new("thread-a",true));s.SetModules(s.GetModules() with{ModelsEnabled=false});
        var p=s.RequestResource(Request());Assert.Single(s.GetQueueBindings());
        Assert.True(s.GetQueueBindings()[0].ConsentVersion>0);Assert.Empty(s.Claim());Assert.Equal("Offered",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.Equal(p.Job.Id,s.RequestResource(Request()).Job.Id);
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request() with{RemainingWork="不同任务"}));
    }
    [Fact] public void Wrong_thread_turn_and_partial_binding_never_create_jobs()
    {
        using var s=new Scheduler(new JobStore(root));Runtime(s);s.SetQueueResumeConsent(new("thread-a",true));
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request() with{SourceTurnId="wrong"}));
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request() with{ResumeThreadId="alias-only"}));
        Assert.Throws<DispatchException>(()=>s.RequestResource(Request() with{SourceTurnId=null}));
        Assert.Empty(s.List());
    }
    [Fact] public void Revocation_survives_restart_and_blocks_offer_and_acceptance()
    {
        ResourcePermit p;
        using(var s=new Scheduler(new JobStore(root)))
        {
            Runtime(s);var c=s.SetQueueResumeConsent(new("thread-a",true));p=s.RequestResource(Request());s.Claim();var offer=s.GetResource(p.Job.Id,p.Token);
            s.SetQueueResumeConsent(c with{Enabled=false});
            Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation));
        }
        using var restored=new Scheduler(new JobStore(root));Assert.False(restored.GetQueueResumeConsents().Single().Enabled);
        Assert.Equal("WaitingForClient",restored.GetResource(p.Job.Id,p.Token).Status);
    }
    [Fact] public void Database_failure_cannot_leave_unbound_permit_or_job()
    {
        using var s=new Scheduler(new JobStore(root));Runtime(s);s.SetQueueResumeConsent(new("thread-a",true));
        using var db=new SqliteConnection("Data Source="+Path.Combine(root,"dispatch.db"));db.Open();
        using(var cmd=db.CreateCommand()){cmd.CommandText="CREATE TRIGGER fail_binding BEFORE INSERT ON records WHEN NEW.kind='queue-binding' BEGIN SELECT RAISE(ABORT,'injected'); END;";cmd.ExecuteNonQuery();}
        Assert.Throws<SqliteException>(()=>s.RequestResource(Request()));Assert.Empty(s.List());Assert.Empty(s.GetQueueBindings());
    }
    [Fact] public void Consent_changes_require_fresh_version_and_binding_reads_require_owner_token()
    {
        using var s=new Scheduler(new JobStore(root));Runtime(s);var c=s.SetQueueResumeConsent(new("thread-a",true));
        Assert.Equal("VERSION_CONFLICT",Assert.Throws<DispatchException>(()=>s.SetQueueResumeConsent(c with{Version=0,Enabled=false})).Code);
        var p=s.RequestResource(Request());
        Assert.Throws<DispatchException>(()=>s.GetQueueBindingState(p.Job.Id,"wrong"));
        var view=JsonSerializer.Serialize(s.GetQueueBindingState(p.Job.Id,p.Token),Json.Options);
        Assert.DoesNotContain(p.Token,view);Assert.Contains("thread-a",view);
    }
    [Fact] public async Task Unaccepted_resource_is_never_reported_as_finished_test()
    {
        using var s=new Scheduler(new JobStore(root));Runtime(s);s.SetQueueResumeConsent(new("thread-a",true));
        var p=s.RequestResource(Request());s.Claim();var api=new ServiceApi(s);
        var args=JsonSerializer.SerializeToElement(new{jobId=p.Job.Id},Json.Options);
        var report=JsonSerializer.SerializeToElement(await api.Execute("get_test_report",args,CancellationToken.None),Json.Options);
        Assert.False(report.GetProperty("reportReady").GetBoolean());
    }
}
