using Dispatch.Core;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Dispatch.Tests;
public sealed class IdleCleanupTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "idle-cleanup-tests"),Guid.NewGuid().ToString("N"));
    private const string Thread="11111111-1111-4111-8111-111111111111";
    private string Source=>Path.Combine(root,"codex","sessions","rollout-"+Thread+".jsonl");
    private readonly Clock clock=new();
    private void Record(string type,object payload)=>File.AppendAllText(Source,JsonSerializer.Serialize(new{type,timestamp=clock.Utc,payload})+"\n");
    private ResourcePermit Setup(Scheduler s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);Record("session_meta",new{id=Thread});
        Record("turn_context",new{turn_id="turn-a",model="fixture",effort="high"});s.SetRuntimeSource(Path.Combine(root,"codex"));
        var p=s.RequestResource(new("a",Thread,"app:fixture","纯虚拟清理",1800));s.Claim();
        s.SetIdleCleanupSettings(new(true,10));return p;
    }
    [Fact] public void Idle_cleanup_uses_real_finished_turn_not_silence_and_claim_is_not_release()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);
        clock.Utc=clock.Utc.AddMinutes(15);Assert.Null(s.ClaimIdleCleanup());
        Record("event_msg",new{type="task_complete",turn_id="turn-a"});Assert.Null(s.ClaimIdleCleanup());
        clock.Utc=clock.Utc.AddMinutes(11);var ticket=s.ClaimIdleCleanup();Assert.NotNull(ticket);Assert.Equal(p.Job.Id,ticket.LeaseId);
        Assert.Null(s.ClaimIdleCleanup());Assert.Equal("Granted",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.True(s.BeginIdleCleanupSend(p.Job.Id,p.Token,ticket.Id));s.RecordIdleCleanupSend(p.Job.Id,ticket.Id,"turn-cleanup");
        Assert.Equal("Granted",s.GetResource(p.Job.Id,p.Token).Status);
        s.ReleaseResource(p.Job.Id,p.Token,1,true);Assert.Equal("CONFIRMED",s.GetIdleCleanupTickets().Single().State);
    }
    [Fact] public void New_work_half_line_and_source_replacement_prevent_cleanup()
    {
        using var s=new Scheduler(new JobStore(root),clock);Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);File.AppendAllText(Source,"{\"type\":\"event_msg\"");Assert.Null(s.ClaimIdleCleanup());
        File.AppendAllText(Source,",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-b\"},\"timestamp\":\""+clock.Utc.ToString("O")+"\"}\n");
        Assert.Null(s.ClaimIdleCleanup());clock.Utc=clock.Utc.AddMinutes(20);Assert.Null(s.ClaimIdleCleanup());
        File.WriteAllText(Source,"{}\n");Assert.Null(s.ClaimIdleCleanup());
    }
    [Fact] public void Settings_change_and_new_user_input_are_checked_again_before_send()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);var ticket=s.ClaimIdleCleanup();Assert.NotNull(ticket);
        Record("response_item",new{type="message",role="user",content=new[]{new{type="input_text",text="执行新任务"}}});
        Assert.False(s.BeginIdleCleanupSend(p.Job.Id,p.Token,ticket.Id));Assert.Equal("STOPPED_NO_RETRY",s.GetIdleCleanupTickets().Single().State);
        var settings=s.GetIdleCleanupSettings();Assert.Throws<DispatchException>(()=>s.SetIdleCleanupSettings(settings with{Version=0,Enabled=false}));
    }
    [Fact] public void Pending_tool_and_registered_test_process_are_activity()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);
        Record("response_item",new{type="function_call",call_id="pending-call",name="fixture"});Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);Assert.Null(s.ClaimIdleCleanup());
        Record("response_item",new{type="function_call_output",call_id="pending-call",output="不保存正文"});clock.Utc=clock.Utc.AddMinutes(11);
        using var current=System.Diagnostics.Process.GetCurrentProcess();s.AttachRunner(p.Job.Id,1,current.Id,new DateTimeOffset(current.StartTime));
        Assert.Null(s.ClaimIdleCleanup());
    }
    [Fact] public void Restart_never_resends_unknown_cleanup_and_models_disabled_does_not_disable_queue_cleanup()
    {
        ResourcePermit p;
        using(var s=new Scheduler(new JobStore(root),clock))
        {
            p=Setup(s);s.SetModules(s.GetModules() with{ModelsEnabled=false});Record("event_msg",new{type="task_complete",turn_id="turn-a"});
            clock.Utc=clock.Utc.AddMinutes(11);Assert.NotNull(s.ClaimIdleCleanup());
        }
        using var restored=new Scheduler(new JobStore(root),clock);Assert.Null(restored.ClaimIdleCleanup());
        Assert.Equal("STOPPED_NO_RETRY",restored.GetIdleCleanupTickets().Single().State);
        Assert.Equal("RecoveryRequired",restored.GetResource(p.Job.Id,p.Token).Status);
    }
    [Fact] public void Cleanup_confirmation_and_resource_release_are_one_transaction()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);Assert.NotNull(s.ClaimIdleCleanup());
        using var db=new SqliteConnection("Data Source="+Path.Combine(root,"dispatch.db"));db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_cleanup BEFORE UPDATE ON records WHEN NEW.kind='idle-cleanup-ticket' BEGIN SELECT RAISE(ABORT,'injected'); END;";cmd.ExecuteNonQuery();
        Assert.Throws<SqliteException>(()=>s.ReleaseResource(p.Job.Id,p.Token,1,true));
        Assert.Equal("Granted",s.GetResource(p.Job.Id,p.Token).Status);
    }
    [Fact] public void Queue_disable_revoked_settings_and_unknown_alias_do_not_dispatch()
    {
        using var s=new Scheduler(new JobStore(root),clock);Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});clock.Utc=clock.Utc.AddMinutes(11);
        s.SetModules(s.GetModules() with{QueueEnabled=false});Assert.Null(s.ClaimIdleCleanup());
        s.SetModules(s.GetModules() with{QueueEnabled=true});var t=s.ClaimIdleCleanup();Assert.NotNull(t);
        s.SetIdleCleanupSettings(s.GetIdleCleanupSettings() with{Enabled=false});
        var p=s.GetIdleCleanupTickets().Single();Assert.NotNull(p);
        // 不改原令牌/代次，也不因配置关闭释放。
        Assert.Equal(JobState.Running,s.List().Single().State);
        var other=s.RequestResource(new("b","free-alias","app:other","未知原聊天",1800));s.Claim();
        s.SetIdleCleanupSettings(s.GetIdleCleanupSettings() with{Enabled=true});clock.Utc=clock.Utc.AddMinutes(11);Assert.Null(s.ClaimIdleCleanup());
        Assert.Equal("Granted",s.GetResource(other.Job.Id,other.Token).Status);
    }
    [Fact] public async Task Cleanup_plan_is_private_and_settings_never_inherit_queue_resume_consent()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);
        Record("event_msg",new{type="task_complete",turn_id="turn-a"});clock.Utc=clock.Utc.AddMinutes(11);
        var ticket=s.ClaimIdleCleanup();Assert.NotNull(ticket);
        var plan=JsonSerializer.SerializeToElement(s.BuildIdleCleanupPlan(p.Job.Id,"fixture-instance"),Json.Options);
        Assert.Equal("idle-cleanup",plan.GetProperty("kind").GetString());Assert.Equal(ticket.Id,plan.GetProperty("wakeId").GetString());
        Assert.Equal(p.Token,plan.GetProperty("token").GetString());Assert.DoesNotContain("accept_resource",plan.GetProperty("prompt").GetString()!);
        Assert.False(s.BeginIdleCleanupSend(p.Job.Id,p.Token,"another-ticket"));
        var api=new ServiceApi(s);var view=JsonSerializer.Serialize(await api.Execute("get_idle_cleanup_settings",JsonSerializer.SerializeToElement(new{}),CancellationToken.None),Json.Options);
        Assert.DoesNotContain(p.Token,view);Assert.Contains("DISPATCHING",view);
        s.SetIdleCleanupSettings(s.GetIdleCleanupSettings() with{Enabled=false});Assert.False(s.BeginIdleCleanupSend(p.Job.Id,p.Token,ticket.Id));
    }
    [Fact] public void Same_chat_cannot_receive_test_resumption_while_cleanup_is_pending()
    {
        using var s=new Scheduler(new JobStore(root),clock);Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);Assert.NotNull(s.ClaimIdleCleanup());
        s.SetQueueResumeConsent(new(Thread,true));
        s.RequestResource(new("b",Thread,"app:other","接续互斥",60,true,Thread,"turn-a","仅读报告"));s.Claim();
        Assert.Null(s.ClaimQueueWake());
    }
    [Fact] public void Unknown_alias_can_be_explicitly_bound_without_changing_lease_or_authorizing_wakeup()
    {
        using var s=new Scheduler(new JobStore(root),clock);Setup(s);
        var other=s.RequestResource(new("alias","old-free-alias","app:other","补充绑定",600));s.Claim();
        var bound=s.SetIdleCleanupOwner(other.Job.Id,Thread);Assert.Equal(Thread,bound.ThreadId);
        Assert.Equal("Granted",s.GetResource(other.Job.Id,other.Token).Status);
        Assert.Throws<DispatchException>(()=>s.SetIdleCleanupOwner(other.Job.Id,"another-alias"));
        Assert.Equal("WINDOW_ACTIVE",s.GetIdleCleanupStatuses().Single(x=>x.LeaseId==other.Job.Id).Status);
    }
    [Fact] public void Owner_can_report_uncertain_cleanup_then_confirm_later_without_repeat_wakeup()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});
        clock.Utc=clock.Utc.AddMinutes(11);Assert.NotNull(s.ClaimIdleCleanup());
        s.ReleaseResource(p.Job.Id,p.Token,1,false);Assert.Equal("CLEANUP_UNCONFIRMED",s.GetIdleCleanupTickets().Single().State);
        Assert.Null(s.ClaimIdleCleanup());s.ReleaseResource(p.Job.Id,p.Token,1,true);
        Assert.Equal("CONFIRMED",s.GetIdleCleanupTickets().Single().State);
    }
    [Fact] public void Late_real_release_after_unknown_delivery_is_recorded_without_retry()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Setup(s);Record("event_msg",new{type="task_complete",turn_id="turn-a"});clock.Utc=clock.Utc.AddMinutes(11);
        var t=s.ClaimIdleCleanup();Assert.NotNull(t);s.RecordIdleCleanupSend(p.Job.Id,t.Id,null);
        Assert.Equal("STOPPED_NO_RETRY",s.GetIdleCleanupTickets().Single().State);
        s.ReleaseResource(p.Job.Id,p.Token,1,true);Assert.Equal("CONFIRMED",s.GetIdleCleanupTickets().Single().State);Assert.Null(s.ClaimIdleCleanup());
    }
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Utc=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Utc;}
}
