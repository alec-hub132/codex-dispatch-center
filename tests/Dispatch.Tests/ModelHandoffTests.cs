using Dispatch.Core;
using Xunit;
namespace Dispatch.Tests;
public sealed class ModelHandoffTests
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "handoff-tests"), Guid.NewGuid().ToString("N"));
    private static ModelRequest Request(string id="a", string session="bound-thread") => new(id,session,"worker","medium","测试限定任务","只读取测试报告","supervisor","high","取得报告，或遇到失败立即返回主管");
    private Scheduler Open(bool configure=true)
    {
        Directory.CreateDirectory(Path.Combine(root,"diagnostics"));
        File.WriteAllText(Path.Combine(root,"model-handoff-authorization.json"), """{"threads":["bound-thread"]}""");
        File.WriteAllText(Path.Combine(root,"diagnostics","model-access.json"), """{"standaloneCatalog":{"reachable":true,"models":[{"model":"worker","efforts":["medium"]},{"model":"supervisor","efforts":["high","medium"]}]}}""");
        var s=new Scheduler(new JobStore(root));
        if(configure && s.GetSupervisor("bound-thread") is null) s.SetSupervisor(new("bound-thread","supervisor","high"));
        Runtime(s);
        return s;
    }
    private void Runtime(Scheduler s, string model="supervisor", string effort="high")
    {
        var home=Path.Combine(root,"runtime-fixture"); var sessions=Path.Combine(home,"sessions"); Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions,"rollout-bound-thread.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new{type="session_meta",payload=new{id="bound-thread"}})+"\n"+
            System.Text.Json.JsonSerializer.Serialize(new{type="turn_context",timestamp=DateTimeOffset.UtcNow,payload=new{turn_id="fixture-turn",model,effort}})+"\n");
        s.SetRuntimeSource(home);
    }
    [Fact] public void Planner_cannot_impersonate_supervisor_or_plan_without_runtime_source()
    {
        using var s=Open(); Runtime(s,"worker","medium");
        Assert.Equal("PLANNER_RUNTIME_MISMATCH",Assert.Throws<DispatchException>(()=>s.RequestModel(Request())).Code);
        Assert.Empty(s.ListDecisions());
        var empty=Path.Combine(root,"empty-runtime");Directory.CreateDirectory(empty);s.SetRuntimeSource(empty);
        Assert.Equal("PLANNER_RUNTIME_UNAVAILABLE",Assert.Throws<DispatchException>(()=>s.RequestModel(Request())).Code);
        Assert.Empty(s.ListDecisions());
    }
    [Fact] public void Claimed_runtime_values_cannot_bypass_service_observation()
    {
        using var s=Open();Runtime(s);var d=s.RequestModel(Request());var t=d.Execution!.Token;
        s.RecordHandoff(d.Id,t,"sending","测试发送");s.RecordHandoff(d.Id,t,"sent","测试回执");
        Assert.Equal("AWAITING_MATCH",Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"runtime_verified","自报目标值","worker","medium")).Code);
        Assert.Equal("SENT_AWAITING_VERIFICATION",s.GetDecision(d.Id).Status);
        Runtime(s,"worker","medium");
        Assert.Equal("EXECUTING",s.RecordHandoff(d.Id,t,"runtime_verified","调用方填错也须以真实记录为准","fake","fake").Status);
    }
    private static ModelDecision Begin(Scheduler s, string id="a")
    {
        var d=s.RequestModel(Request(id));var t=d.Execution!.Token;
        s.RecordHandoff(d.Id,t,"sending","发送前确认身份与授权");
        s.RecordHandoff(d.Id,t,"sent","发送回执");
        return s.RecordHandoff(d.Id,t,"user_verified","用户确认执行模型生效");
    }
    [Fact] public void Continuation_registration_requires_sent_cycle_and_claims_launch_only_once()
    {
        using var s=Open();var d=s.RequestModel(Request());var t=d.Execution!.Token;
        Assert.Throws<DispatchException>(()=>s.PrepareModelContinuation(d.Id,t));
        s.RecordHandoff(d.Id,t,"sending","测试");s.RecordHandoff(d.Id,t,"sent","测试");
        var c=s.PrepareModelContinuation(d.Id,t);
        Assert.DoesNotContain(t,System.Text.Json.JsonSerializer.Serialize(c));
        Assert.Equal(c,s.PrepareModelContinuation(d.Id,t));
        Assert.True(s.ClaimModelContinuation(c.Id));Assert.False(s.ClaimModelContinuation(c.Id));
        Assert.Throws<DispatchException>(()=>s.PrepareModelContinuation(d.Id,"wrong"));
    }
    [Fact] public void Continuation_return_survives_module_off_but_user_override_blocks_new_arm()
    {
        using var s=Open();var d=Begin(s);var t=d.Execution!.Token;
        s.RecordHandoff(d.Id,t,"work_done","测试完成");
        s.RecordHandoff(d.Id,t,"return_sending","返回");s.RecordHandoff(d.Id,t,"return_sent","返回");
        s.SetModules(s.GetModules() with {ModelsEnabled=false});Runtime(s,"worker","medium");
        Assert.True(s.PrepareModelContinuation(d.Id,t).Returning);
    }
    [Fact] public void Continuation_launch_claim_persists_across_restart()
    {
        string id;
        using(var s=Open()) {var d=s.RequestModel(Request());var t=d.Execution!.Token;s.RecordHandoff(d.Id,t,"sending","测试");s.RecordHandoff(d.Id,t,"sent","测试");id=s.PrepareModelContinuation(d.Id,t).Id;Assert.True(s.ClaimModelContinuation(id));}
        using var restored=Open();Assert.False(restored.ClaimModelContinuation(id));
    }
    [Fact] public void Disabled_models_block_new_send_but_preserve_queue_and_supervisor_return()
    {
        using var s=Open(); var d=s.RequestModel(Request()); var t=d.Execution!.Token;
        s.SetModules(s.GetModules() with { ModelsEnabled=false });
        Assert.NotNull(s.Submit(new("queue", "window", "mock-sw")));
        Assert.Equal("MODELS_DISABLED",Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"sending","关闭后不能发送新委派")).Code);
        s.RecordHandoff(d.Id,t,"cancelled","撤销未发送委派");
        s.SetModules(s.GetModules() with { ModelsEnabled=true });
        d=Begin(s,"after-disable"); t=d.Execution!.Token;
        s.SetModules(s.GetModules() with { QueueEnabled=false,ModelsEnabled=false });
        s.RecordHandoff(d.Id,t,"work_done","仍可结束和返回");
        s.RecordHandoff(d.Id,t,"return_sending","返回不受模块开关影响");
        s.RecordHandoff(d.Id,t,"return_sent","返回已发送");
        Assert.Equal("RETURN_VERIFIED",s.RecordHandoff(d.Id,t,"supervisor_verified","主管已核验").Status);
    }

    [Fact] public void Mid_execution_takeover_keeps_checkpoint_and_resource_ownership()
    {
        using var s=Open(); var d=Begin(s); var t=d.Execution!.Token;
        var permit=s.RequestResource(new("permit","bound-thread","desktop","仅验证许可，不操作桌面"));
        s.Claim(); permit=s.GetResource(permit.Job.Id,permit.Token);
        s.SetModules(s.GetModules() with { ModelsEnabled=false });
        var request=new TakeoverRequest(d.Id,t,"需要主管分析异常","已构建","分析失败原因并继续","无运行操作", "许可编号："+permit.Job.Id+"，仍由原窗口持有");
        var takeover=s.RequestTakeover(request);
        Assert.Equal("RETURN_READY",takeover.Status);
        Assert.Equal("supervisor",takeover.Execution!.Model);
        Assert.Equal(request.RemainingWork,takeover.Checkpoint!.RemainingWork);
        Assert.Contains("尚未完成",takeover.Execution.Prompt);
        Assert.DoesNotContain(t,takeover.Execution.Prompt);
        Assert.Equal(takeover,s.RequestTakeover(request));
        Assert.Equal("Granted",s.GetResource(permit.Job.Id,permit.Token).Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT",Assert.Throws<DispatchException>(()=>s.RequestTakeover(request with{RemainingWork="不同的任务"})).Code);
        Assert.Equal("INVALID_HANDOFF_TOKEN",Assert.Throws<DispatchException>(()=>s.RequestTakeover(request with{Token="wrong"})).Code);
        s.ReleaseResource(permit.Job.Id,permit.Token,permit.Job.Generation,true);
    }

    [Fact] public void Exclusions_persist_block_new_work_and_can_be_cleared()
    {
        using(var s=Open())
        {
            Assert.Throws<DispatchException>(()=>s.SetModelExclusions(new("foreign",["worker"])));
            s.SetModelExclusions(new("bound-thread",["worker","worker"]));
            Assert.Single(s.GetModelExclusions().Single().Models);
            Assert.Equal("MODEL_EXCLUDED",Assert.Throws<DispatchException>(()=>s.RequestModel(Request())).Code);
        }
        using var restored=Open();
        Assert.Equal("MODEL_EXCLUDED",Assert.Throws<DispatchException>(()=>restored.RequestModel(Request())).Code);
        restored.SetModelExclusions(new("bound-thread",[]));
        Assert.NotNull(restored.RequestModel(Request()).Execution);
    }

    [Fact] public void Runtime_verifier_reads_bound_session_and_rejects_stale_or_foreign_evidence()
    {
        using var s=Open();
        var home=Path.Combine(root,"codex"); var sessions=Path.Combine(home,"sessions","2026","10","29"); Directory.CreateDirectory(sessions);
        var d=s.RequestModel(Request()); var token=d.Execution!.Token;
        s.SetRuntimeSource(home);
        s.RecordHandoff(d.Id,token,"sending","测试夹具登记发送"); s.RecordHandoff(d.Id,token,"sent","测试夹具发送回执");
        var file=Path.Combine(sessions,"rollout-bound-thread.jsonl");
        void Evidence(string identity,string model,string effort,DateTimeOffset time) => File.WriteAllText(file,
            System.Text.Json.JsonSerializer.Serialize(new{type="session_meta",payload=new{id=identity}})+"\n"+
            System.Text.Json.JsonSerializer.Serialize(new{type="turn_context",timestamp=time,payload=new{turn_id="fixture-turn",model,effort}})+"\n");
        Assert.Equal("SOURCE_UNAVAILABLE",s.VerifyModelRuntime(d.Id,token).Status);
        Evidence("foreign","worker","medium",DateTimeOffset.UtcNow);
        Assert.Equal("SOURCE_UNAVAILABLE",s.VerifyModelRuntime(d.Id,token).Status);
        Evidence("bound-thread","worker","medium",d.CreatedAt.AddMinutes(-1));
        Assert.Equal("AWAITING_MATCH",s.VerifyModelRuntime(d.Id,token).Status);
        Evidence("bound-thread","supervisor","high",DateTimeOffset.UtcNow);
        Assert.Equal("AWAITING_MATCH",s.VerifyModelRuntime(d.Id,token).Status);
        Evidence("bound-thread","worker","medium",DateTimeOffset.UtcNow);
        Assert.Equal("VERIFIED",s.VerifyModelRuntime(d.Id,token).Status);
        Assert.Equal("EXECUTING",s.GetDecision(d.Id).Status);
        Assert.Equal("INVALID_HANDOFF_TOKEN",Assert.Throws<DispatchException>(()=>s.VerifyModelRuntime(d.Id,"wrong")).Code);
    }
    [Fact] public void Policy_change_blocks_unsent_plan_but_never_blocks_existing_return()
    {
        using var s=Open();var d=s.RequestModel(Request());var t=d.Execution!.Token;
        s.SetModelExclusions(new("bound-thread",["worker"]));
        Assert.Equal(d.Id,s.RequestModel(Request()).Id);
        Assert.Equal("MODEL_EXCLUDED",Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"sending","尚未发送时策略改变")).Code);
        s.RecordHandoff(d.Id,t,"cancelled","未发送的旧计划撤销");
        s.SetModelExclusions(new("bound-thread",[]));
        d=s.RequestModel(Request("b"));t=d.Execution!.Token;
        s.RecordHandoff(d.Id,t,"sending","发送前已核对");s.RecordHandoff(d.Id,t,"sent","发送成功");s.RecordHandoff(d.Id,t,"user_verified","用户确认");
        s.SetModelExclusions(new("bound-thread",["worker","supervisor"]));
        Assert.Equal("RETURN_READY",s.RecordHandoff(d.Id,t,"work_done","结束后即使主管在排除名单，也要返回主管").Status);
        s.RecordHandoff(d.Id,t,"return_sending","固定返回");s.RecordHandoff(d.Id,t,"return_sent","已返回");
        Assert.Equal("RETURN_VERIFIED",s.RecordHandoff(d.Id,t,"supervisor_verified","主管确认").Status);
        Assert.Equal("MODEL_EXCLUDED",Assert.Throws<DispatchException>(()=>s.RequestModel(Request("c"))).Code);
    }
    [Fact] public void Requires_supervisor_configuration_planner_and_task_boundary()
    {
        using var s=Open(false);
        Assert.Equal("UNSUPPORTED",s.RequestModel(Request(session:"foreign")).Status);
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request()));
        s.SetSupervisor(new("bound-thread","supervisor","high"));
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request() with{PlannerModel="worker"}));
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request() with{CompletionCriteria=null}));
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request() with{Effort="invalid"}));
        Assert.NotNull(s.RequestModel(Request()).Execution);
    }
    [Fact] public void Full_cycle_returns_to_saved_supervisor_before_allowing_next_work()
    {
        using var s=Open();var d=Begin(s);var t=d.Execution!.Token;
        Assert.Equal("EXECUTING",d.Status);
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request("b")));
        var ret=s.RecordHandoff(d.Id,t,"work_done","取得限定任务报告");
        Assert.True(ret.Execution!.Returning);Assert.Equal("supervisor",ret.Execution.Model);Assert.Equal("high",ret.Execution.Thinking);
        s.RecordHandoff(d.Id,t,"return_sending","只执行一次返回");
        s.RecordHandoff(d.Id,t,"return_sent","返回消息已发送，未当作生效");
        Assert.Throws<DispatchException>(()=>s.RequestModel(Request("b")));
        Assert.Equal("RETURN_VERIFIED",s.RecordHandoff(d.Id,t,"supervisor_verified","用户确认已返回主管").Status);
        Assert.NotNull(s.RequestModel(Request("b")).Execution);
    }
    [Theory] [InlineData("work_failed")] [InlineData("work_cancelled")] [InlineData("work_timeout")]
    public void Abnormal_work_still_returns_to_supervisor(string outcome)
    {
        using var s=Open();var d=Begin(s);
        Assert.Equal("RETURN_READY",s.RecordHandoff(d.Id,d.Execution!.Token,outcome,"限定任务未完成，返回主管判断").Status);
    }
    [Fact] public void Restart_keeps_uncertain_send_blocked_and_rejects_wrong_token()
    {
        ModelDecision d;using(var s=Open()){d=s.RequestModel(Request());s.RecordHandoff(d.Id,d.Execution!.Token,"sending","开始发送");}
        using var restored=Open();Assert.Equal("SENDING",restored.GetDecision(d.Id).Status);
        Assert.Throws<DispatchException>(()=>restored.RecordHandoff(d.Id,"wrong","sent","伪回执"));
        Assert.Equal("RECOVERY_REQUIRED",restored.RecordHandoff(d.Id,d.Execution!.Token,"failed","结果不明不重发").Status);
        Assert.Throws<DispatchException>(()=>restored.RequestModel(Request("b")));
    }
    [Fact] public void Changed_supervisor_or_user_override_never_uses_old_return_target()
    {
        using var s=Open();var d=Begin(s);s.SetSupervisor(new("bound-thread","supervisor","medium"));
        Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,d.Execution!.Token,"work_done","完成"));
        Assert.Equal("USER_OVERRIDE",s.RecordHandoff(d.Id,d.Execution!.Token,"user_override","用户改变设置，停止自动交接").Status);
    }
    [Fact] public void Runtime_readback_must_match_current_leg_and_cannot_skip_send_or_replay()
    {
        using var s=Open();var d=s.RequestModel(Request());var t=d.Execution!.Token;
        Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"runtime_verified","还未发送","worker","medium"));
        s.RecordHandoff(d.Id,t,"sending","登记发送");
        Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"sending","重复发送必须阻止"));
        s.RecordHandoff(d.Id,t,"sent","发送回执");
        Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"runtime_verified","实际仍为主管","supervisor","high"));
        Runtime(s,"worker","medium");
        Assert.Equal("EXECUTING",s.RecordHandoff(d.Id,t,"runtime_verified","使用测试夹具运行文件核验","worker","medium").Status);
        s.RecordHandoff(d.Id,t,"work_failed","限定任务失败");
        s.RecordHandoff(d.Id,t,"return_sending","登记返回");s.RecordHandoff(d.Id,t,"return_sent","返回回执");
        Assert.Throws<DispatchException>(()=>s.RecordHandoff(d.Id,t,"runtime_verified","仍为执行模型","worker","medium"));
        Runtime(s);
        Assert.Equal("RETURN_VERIFIED",s.RecordHandoff(d.Id,t,"runtime_verified","使用测试夹具主管运行文件核验","supervisor","high").Status);
    }
}
