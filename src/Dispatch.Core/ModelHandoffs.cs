using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
namespace Dispatch.Core;

public sealed partial class Scheduler
{
    private string[] AuthorizedThreads()
    {
        var path=Path.Combine(Root,"model-handoff-authorization.json");
        if(!File.Exists(path)) return [];
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("threads").EnumerateArray().Select(t=>t.GetString()!).ToArray();
    }
    public string[] GetAuthorizedThreads(){lock(gate)return AuthorizedThreads();}
    public ModelExclusions[] GetModelExclusions() { lock(gate) return store.Load<ModelExclusions>("model-exclusions").ToArray(); }
    public ModelExclusions SetModelExclusions(ModelExclusions settings)
    {
        lock(gate)
        {
            if(!AuthorizedThreads().Contains(settings.SessionId)) throw new DispatchException("UNAUTHORIZED_THREAD","仅可设置已授权聊天的模型排除项。");
            if(settings.Models is null || settings.Models.Length>128) throw new DispatchException("INVALID_ARGUMENT","排除列表最多128项。");
            foreach(var model in settings.Models) Validate(model,"排除模型",128);
            var saved=settings with { Models=settings.Models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
            store.Save("model-exclusions",saved.SessionId,saved);
            Log.Write("MODEL_EXCLUSIONS_SAVED","已保存执行模型排除名单，已有任务仍可返回主管。");
            return saved;
        }
    }
    private void CheckModelExclusion(string session,string model)
    {
        if(GetModelExclusions().Any(c=>c.SessionId==session && c.Models.Contains(model,StringComparer.Ordinal)))
            throw new DispatchException("MODEL_EXCLUDED","用户已将该模型排除，请由主管选择允许参与调配的执行模型。");
    }
    public SupervisorConfig? GetSupervisor(string session){lock(gate)return store.Load<SupervisorConfig>("supervisor").SingleOrDefault(c=>c.SessionId==session);}
    public SupervisorConfig SetSupervisor(SupervisorConfig config)
    {
        lock(gate)
        {
            if(!AuthorizedThreads().Contains(config.SessionId)) throw new DispatchException("UNAUTHORIZED_THREAD","仅可配置本机已登记人类授权的聊天。");
            ValidateModel(config.Model,config.Effort);
            var previous=GetSupervisor(config.SessionId);
            if(previous?.Model==config.Model && previous.Effort==config.Effort) return previous;
            var updated=config with{Version=(previous?.Version??0)+1};
            store.Save("supervisor",config.SessionId,updated);Log.Write("SUPERVISOR_CONFIGURED","用户更新主管配置");return updated;
        }
    }
    private void ValidateModel(string model,string effort)
    {
        var path=Path.Combine(Root,"diagnostics","model-access.json");
        if(!File.Exists(path))throw new DispatchException("MODEL_CATALOG_REQUIRED","请先检测模型接入。");
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        var catalog=doc.RootElement.GetProperty("standaloneCatalog");
        if(!catalog.GetProperty("reachable").GetBoolean() || !catalog.GetProperty("models").EnumerateArray().Any(m=>m.GetProperty("model").GetString()==model && m.GetProperty("efforts").EnumerateArray().Any(e=>e.GetString()==effort)))
            throw new DispatchException("INVALID_MODEL_COMBINATION","模型或强度不可用，请刷新检测。");
    }
    private static bool CycleFinished(ModelDecision d)=>d.Status is "RETURN_VERIFIED" or "CANCELLED_BEFORE_SEND" or "USER_OVERRIDE";
    private HandoffPlan? PrepareHandoff(ModelRequest request)
    {
        if(!AuthorizedThreads().Contains(request.SessionId)) return null;
        if(decisions.Values.Any(d=>d.Request.SessionId==request.SessionId && d.Execution is not null && !CycleFinished(d)))
            throw new DispatchException("HANDOFF_IN_PROGRESS","本聊天的执行/返回周期尚未结束。");
        var supervisor=GetSupervisor(request.SessionId)??throw new DispatchException("SUPERVISOR_REQUIRED","请先在界面配置主管模型和强度。");
        if(request.PlannerModel!=supervisor.Model || request.PlannerEffort!=supervisor.Effort)
            throw new DispatchException("SUPERVISOR_REQUIRED","应由当前有效主管规划任务。调用方须核对实际运行模型，不得伪报身份。");
        Validate(request.CompletionCriteria??"","任务完成或返回条件",2000);
        CheckModelExclusion(request.SessionId,request.Model);
        ValidateModel(supervisor.Model,supervisor.Effort);ValidateModel(request.Model,request.Effort);
        if(request.Model==supervisor.Model && request.Effort==supervisor.Effort)throw new DispatchException("NO_DELEGATION_NEEDED","目标即为主管，请直接执行，无需交接。");
        var observed=ObserveModelRuntime(request.SessionId);
        if(observed is null) throw new DispatchException("PLANNER_RUNTIME_UNAVAILABLE","尚无本聊天的实际主管轮次记录，不创建新委派。");
        if(observed.Model!=supervisor.Model || observed.Effort!=supervisor.Effort || observed.Timestamp>clock.GetUtcNow().AddSeconds(5))
            throw new DispatchException("PLANNER_RUNTIME_MISMATCH","最新实际轮次与已配置主管不符，不接受调用方自报主管身份。");
        var prompt="这是主管模型制定的限定任务，请只执行本任务。\n理由："+request.Reason+"\n任务："+request.Handoff+"\n完成/返回条件："+request.CompletionCriteria+
            "\n成功、失败、取消或无法继续时均须通过原周期返回主管，不得自行再次分配模型或开启新周期。先核对真实聊天和用户最新设置；用户改变设置时停止自动交接。";
        return new(request.SessionId,request.Model,request.Effort,prompt,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),supervisor.Model,supervisor.Effort,supervisor.Version);
    }
    public ModelDecision RecordHandoff(string id,string token,string outcome,string evidence,string? observedModel=null,string? observedEffort=null)
    {
        if(outcome=="runtime_verified")
        {
            var actual=VerifyModelRuntime(id,token);
            return actual.Decision ?? throw new DispatchException(actual.Status,actual.Message);
        }
        return RecordHandoffCore(id,token,outcome,evidence);
    }
    private ModelDecision RecordHandoffCore(string id,string token,string outcome,string evidence,RuntimeObservation? trustedObservation=null)
    {
        Validate(evidence,"交接证据",4000);
        lock(gate)
        {
            var d=GetDecision(id);
            if(d.Execution is not {} p || string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token),Encoding.UTF8.GetBytes(p.Token)))
                throw new DispatchException("INVALID_HANDOFF_TOKEN","交接令牌不匹配。");
            if(outcome!="user_override" && !AuthorizedThreads().Contains(p.ThreadId))
                throw new DispatchException("UNAUTHORIZED_THREAD","聊天授权已移除，停止交接并核对用户指示。");
            if(outcome!="user_override" && GetSupervisor(p.ThreadId)?.Version!=p.SupervisorVersion)
                throw new DispatchException("SUPERVISOR_CHANGED","用户已更新主管配置，旧交接暂停；核对最新设置后登记user_override，不能按旧配置返回。");
            if(d.Status=="AWAITING_CALLER_EXECUTION" && outcome=="sending") { RequireModelsEnabled(); CheckModelExclusion(p.ThreadId,p.Model); }
            if(outcome=="runtime_verified")
            {
                if(trustedObservation is null || trustedObservation.Model!=p.Model || trustedObservation.Effort!=p.Thinking)throw new DispatchException("MODEL_OBSERVATION_MISMATCH","实际模型/强度与计划不一致，不得根据目标值伪造回读。");
                evidence=$"服务实际读取运行记录：{trustedObservation.Model} / {trustedObservation.Effort}；{evidence}";
                outcome=d.Status=="SENT_AWAITING_VERIFICATION"?"user_verified":d.Status=="RETURN_SENT_AWAITING_VERIFICATION"?"supervisor_verified":"invalid";
            }
            string? next=(d.Status,outcome) switch
            {
                ("AWAITING_CALLER_EXECUTION","sending")=>"SENDING",
                ("SENDING","sent")=>"SENT_AWAITING_VERIFICATION",
                ("SENT_AWAITING_VERIFICATION","user_verified")=>"EXECUTING",
                ("EXECUTING","work_done" or "work_failed" or "work_cancelled" or "work_timeout")=>"RETURN_READY",
                ("RETURN_READY","return_sending")=>"RETURN_SENDING",
                ("RETURN_SENDING","return_sent")=>"RETURN_SENT_AWAITING_VERIFICATION",
                ("RETURN_SENT_AWAITING_VERIFICATION","supervisor_verified")=>"RETURN_VERIFIED",
                ("AWAITING_CALLER_EXECUTION","cancelled")=>"CANCELLED_BEFORE_SEND",
                ("SENDING" or "SENT_AWAITING_VERIFICATION" or "RETURN_SENDING" or "RETURN_SENT_AWAITING_VERIFICATION","failed")=>"RECOVERY_REQUIRED",
                (_,"user_override") when !CycleFinished(d)=>"USER_OVERRIDE",
                _=>null
            };
            if(next is null)throw new DispatchException("INVALID_STATE","周期状态不允许该转换；结果不明时不能自动重发。");
            if(next=="RETURN_READY") p=p with{Returning=true,Model=p.SupervisorModel,Thinking=p.SupervisorEffort,
                Prompt="限定执行任务已结束，现返回主管审查。\n原任务："+d.Request.Handoff+"\n完成条件："+d.Request.CompletionCriteria+"\n执行结果类型："+outcome+"\n执行结果及证据："+evidence+"\n周期编号："+id+"。先get_model_decision读取周期和令牌，优先调用verify_model_runtime读取实际主管轮次并登记生效，由主管决定下一步。不要仅因交接消息再次降级。"};
            var updated=d with{Status=next,Message=evidence,Execution=p,VerificationNotBefore=next is "SENDING" or "RETURN_SENDING" ? clock.GetUtcNow() : d.VerificationNotBefore};store.Save("decision",id,updated);decisions[id]=updated;Log.Write(next,evidence,id);return updated;
        }
    }
}
