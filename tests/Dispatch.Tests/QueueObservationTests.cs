using System.Text.Json;
using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;
public sealed class QueueObservationTests
{
    private readonly string root=Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "queue-observation-tests"),Guid.NewGuid().ToString("N"));
    private string Source=>Path.Combine(root,"codex","sessions","rollout-thread-a.jsonl");
    private readonly Clock clock=new();
    private void Append(object value)=>File.AppendAllText(Source,JsonSerializer.Serialize(value)+"\n");
    private void Turn(string id)=>Append(new{type="turn_context",timestamp=clock.GetUtcNow(),payload=new{turn_id=id,model="test-model",effort="medium"}});
    private void Event(string type,string turn)=>Append(new{type="event_msg",payload=new{type,turn_id=turn}});
    private ResourcePermit Prepare(Scheduler s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        Append(new{type="session_meta",payload=new{id="thread-a"}});Turn("turn-a");
        s.SetRuntimeSource(Path.Combine(root,"codex"));s.SetQueueResumeConsent(new("thread-a",true));
        s.SetModules(s.GetModules() with{ModelsEnabled=false});
        return s.RequestResource(new("a","alias","app:test-editor","隔离接续验证",60,true,"thread-a","turn-a","只检查模拟报告，随后清理"));
    }
    [Fact] public void Active_source_does_not_reserve_software_and_finished_source_becomes_offer_without_models()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);
        clock.Utc=clock.Utc.AddSeconds(16);s.ObserveQueueResumptions();s.Claim();Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
        var other=s.RequestResource(new("b","other","app:test-editor","其他任务",60));Assert.Single(s.Claim());
        s.ReleaseResource(other.Job.Id,other.Token,1,true);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();
        Assert.Equal("Offered",s.GetResource(p.Job.Id,p.Token).Status);Assert.True(s.GetQueueSourceStates().Single().Finished);
    }
    [Fact] public void Incremental_cursor_survives_large_output_partial_line_and_restart()
    {
        ResourcePermit p;long before;
        using(var s=new Scheduler(new JobStore(root),clock))
        {
            p=Prepare(s);before=s.GetQueueSourceStates().Single().Offset;
            Append(new{type="response_item",payload=new{type="output",text=new string('x',3*1024*1024)}});
            File.AppendAllText(Source,"{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-a\"}}");
            s.ObserveQueueResumptions();Assert.False(s.GetQueueSourceStates().Single().CaughtUp);
            Assert.True(s.GetQueueSourceStates().Single().Offset>before);
        }
        File.AppendAllText(Source,"\n");
        using var restored=new Scheduler(new JobStore(root),clock);restored.ObserveQueueResumptions();restored.Claim();
        Assert.Equal("Offered",restored.GetResource(p.Job.Id,p.Token).Status);
    }
    [Theory]
    [InlineData("new-user")][InlineData("new-turn")][InlineData("abort")][InlineData("truncate")][InlineData("different-home")]
    public void New_work_stop_and_changed_source_block_old_automatic_resume(string change)
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");
        if(change=="new-user")Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="现在做新任务"}}}});
        if(change=="new-turn")Turn("turn-b");
        if(change=="abort")Event("task_aborted","turn-a");
        if(change=="truncate")File.WriteAllText(Source,"{}");
        if(change=="different-home"){var home=Path.Combine(root,"other");Directory.CreateDirectory(home);s.SetRuntimeSource(home);}
        s.ObserveQueueResumptions();s.Claim();Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.True(s.GetQueueSourceStates().Single().Blocked);Assert.Null(s.ClaimQueueWake());
    }
    [Fact] public void Wake_is_once_receipt_is_not_grant_and_actual_new_turn_is_required()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");
        s.ObserveQueueResumptions();s.Claim();var offer=s.GetResource(p.Job.Id,p.Token);
        var wake=s.ClaimQueueWake();Assert.NotNull(wake);Assert.Null(s.ClaimQueueWake());
        Assert.True(s.BeginQueueWakeSend(p.Job.Id,p.Token));Assert.False(s.BeginQueueWakeSend(p.Job.Id,p.Token));
        Turn("turn-wake");
        s.ObserveQueueResumptions();Assert.Equal("Offered",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.Equal("QUEUE_RECEIPT_PENDING",Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation)).Code);
        s.RecordQueueWake(p.Job.Id,"turn-wake");
        Assert.Equal("Offered",s.GetResource(p.Job.Id,p.Token).Status);
        Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text=s.QueueWakePrompt(p.Job.Id)}}}});
        var accepted=s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation);Assert.Equal("Granted",accepted.Status);
        Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="停止旧任务"}}}});
        // 已授予许可不能因新输入被回收或自动解锁，原窗口保留清理责任。
        s.ObserveQueueResumptions();Assert.Equal("Granted",s.GetResource(p.Job.Id,p.Token).Status);
        s.ReleaseResource(p.Job.Id,p.Token,accepted.Job.Generation,true);Assert.Equal("Released",s.GetResource(p.Job.Id,p.Token).Status);
    }
    [Fact] public void Unknown_delivery_is_never_retried_after_restart_or_expired_offer()
    {
        ResourcePermit p;
        using(var s=new Scheduler(new JobStore(root),clock))
        {
            p=Prepare(s);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();Assert.NotNull(s.ClaimQueueWake());
        }
        clock.Utc=clock.Utc.AddSeconds(35);
        using var restored=new Scheduler(new JobStore(root),clock);restored.ObserveQueueResumptions();restored.Claim();
        Assert.Null(restored.ClaimQueueWake());Assert.Equal("WaitingForClient",restored.GetResource(p.Job.Id,p.Token).Status);
        Assert.Equal("STOPPED_NO_RETRY",restored.GetQueueWakes().Single().State);
    }
    [Fact] public void Explicit_retry_rebinds_current_turn_and_offers_without_waking_again()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();
        Assert.NotNull(s.ClaimQueueWake());s.BeginQueueWakeSend(p.Job.Id,p.Token);s.RecordQueueWake(p.Job.Id,"turn-wake");Turn("turn-wake");
        Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text=s.QueueWakePrompt(p.Job.Id)}}}});
        Append(new{type="response_item",payload=new{type="output",text=new string('x',3*1024*1024)}});
        clock.Utc=clock.Utc.AddSeconds(35);s.ObserveQueueResumptions();s.GetResource(p.Job.Id,p.Token);
        s.RequeueResource(p.Job.Id,p.Token,"turn-wake");s.Claim();var fresh=s.GetResource(p.Job.Id,p.Token);
        Assert.Equal("Offered",fresh.Status);Assert.Null(s.ClaimQueueWake());
        Assert.Equal("Granted",s.AcceptResource(p.Job.Id,p.Token,fresh.OfferId!,fresh.Job.Generation).Status);
    }
    [Fact] public void Wake_receipt_cannot_hide_later_human_input_or_a_different_real_turn()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();
        var offer=s.GetResource(p.Job.Id,p.Token);Assert.NotNull(s.ClaimQueueWake());s.BeginQueueWakeSend(p.Job.Id,p.Token);
        Turn("turn-wake");Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text=s.QueueWakePrompt(p.Job.Id)}}}});
        Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text="不要继续这个任务"}}}});
        s.RecordQueueWake(p.Job.Id,"turn-wake");
        Assert.Equal("QUEUE_SOURCE_MISMATCH",Assert.Throws<DispatchException>(()=>s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation)).Code);
        s.ObserveQueueResumptions();Assert.Null(s.ClaimQueueWake());Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
    }
    [Theory][InlineData("module")][InlineData("consent")][InlineData("paused")]
    public void Rechecking_before_send_honors_queue_settings_without_models(string reason)
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();Assert.NotNull(s.ClaimQueueWake());
        if(reason=="module")s.SetModules(s.GetModules() with{QueueEnabled=false});
        if(reason=="consent")s.SetQueueResumeConsent(s.GetQueueResumeConsents().Single() with{Enabled=false});
        if(reason=="paused")s.SetPaused(true);
        Assert.False(s.BeginQueueWakeSend(p.Job.Id,p.Token));Assert.Equal("STOPPED_NO_RETRY",s.GetQueueWakes().Single().State);
    }
    [Fact] public void Acknowledged_retry_can_resume_after_another_long_wait_without_resending_old_attempt()
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);Event("task_complete","turn-a");s.ObserveQueueResumptions();s.Claim();
        var first=s.ClaimQueueWake();Assert.NotNull(first);s.BeginQueueWakeSend(p.Job.Id,p.Token);s.RecordQueueWake(p.Job.Id,"turn-wake");
        Turn("turn-wake");Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text=s.QueueWakePrompt(p.Job.Id)}}}});
        clock.Utc=clock.Utc.AddSeconds(35);s.ObserveQueueResumptions();s.GetResource(p.Job.Id,p.Token);
        var blocker=s.RequestResource(new("blocker","other","app:test-editor","只核验排队",60));s.Claim();
        s.RequeueResource(p.Job.Id,p.Token,"turn-wake");clock.Utc=clock.Utc.AddSeconds(16);s.ObserveQueueResumptions();
        Assert.Equal("WaitingForClient",s.GetResource(p.Job.Id,p.Token).Status);
        Event("task_complete","turn-wake");s.ObserveQueueResumptions();s.ReleaseResource(blocker.Job.Id,blocker.Token,1,true);s.Claim();
        var second=s.ClaimQueueWake();Assert.NotNull(second);Assert.NotEqual(first.OfferId,second.OfferId);Assert.Equal("turn-wake",second.SourceTurnId);
        Assert.False(s.BeginQueueWakeSend(p.Job.Id,p.Token,first.Id));s.RecordQueueWake(p.Job.Id,"forged-old-receipt",first.Id);
        Assert.Equal("DISPATCHING",s.GetQueueWakes().Single(w=>w.Id==second.Id).State);
        Assert.Equal(2,s.GetQueueWakes().Length);Assert.Null(s.ClaimQueueWake());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Active_client_keeps_existing_offer_until_offer_deadline_even_after_ready_heartbeat(bool waking)
    {
        using var s=new Scheduler(new JobStore(root),clock);var p=Prepare(s);
        if(waking){clock.Utc=clock.Utc.AddSeconds(16);Event("task_complete","turn-a");s.ObserveQueueResumptions();}
        s.Claim();var offer=s.GetResource(p.Job.Id,p.Token);Assert.Equal("Offered",offer.Status);
        if(waking)
        {
            Assert.NotNull(s.ClaimQueueWake());s.BeginQueueWakeSend(p.Job.Id,p.Token);s.RecordQueueWake(p.Job.Id,"turn-wake");Turn("turn-wake");
            Append(new{type="response_item",payload=new{type="message",role="user",content=new[]{new{type="input_text",text=s.QueueWakePrompt(p.Job.Id)}}}});
        }
        clock.Utc=clock.Utc.AddSeconds(20);s.ObserveQueueResumptions();
        Assert.Equal("Offered",s.GetResource(p.Job.Id,p.Token).Status);
        Assert.Equal("Granted",s.AcceptResource(p.Job.Id,p.Token,offer.OfferId!,offer.Job.Generation).Status);
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Utc=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Utc;
    }
}
