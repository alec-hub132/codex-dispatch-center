using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core;

public sealed record ModelContinuation(string Id, string DecisionId, bool Returning, string SourceTurnId, string State, int? ProcessId = null);
public sealed partial class Scheduler
{
    public ModelContinuation PrepareModelContinuation(string id, string token)
    {
        lock(gate)
        {
            var d=GetDecision(id);var p=d.Execution;
            if(p is null || string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token),Encoding.UTF8.GetBytes(p.Token)))
                throw new DispatchException("INVALID_HANDOFF_TOKEN","交接令牌不匹配。");
            if(!AuthorizedThreads().Contains(p.ThreadId))throw new DispatchException("UNAUTHORIZED_THREAD","聊天交接授权已撤销。");
            if(GetSupervisor(p.ThreadId)?.Version!=p.SupervisorVersion)throw new DispatchException("SUPERVISOR_CHANGED","用户主管设置已改变，停止旧续跑。");
            if(!p.Returning)RequireModelsEnabled();
            if(d.Status!=(p.Returning?"RETURN_SENT_AWAITING_VERIFICATION":"SENT_AWAITING_VERIFICATION"))
                throw new DispatchException("INVALID_STATE","仅允许已发送待核验的方向登记续跑，不重复执行任务。");
            var key=id+(p.Returning?"-return":"-execute");
            var previous=store.Load<ModelContinuation>("model-continuation").SingleOrDefault(c=>c.Id==key);
            if(previous is not null)return previous;
            var observed=ObserveModelRuntime(p.ThreadId)??throw new DispatchException("SOURCE_UNAVAILABLE","无法取得当前实际轮次，未登记续跑。");
            var sourceModel=p.Returning?d.Request.Model:p.SupervisorModel;
            var sourceEffort=p.Returning?d.Request.Effort:p.SupervisorEffort;
            if(observed.Model!=sourceModel || observed.Effort!=sourceEffort)
                throw new DispatchException("SOURCE_MODEL_CHANGED","实际源轮模型/强度已改变，不启动旧续跑计划。");
            var result=new ModelContinuation(key,id,p.Returning,observed.TurnId,"REGISTERED");
            store.Save("model-continuation",key,result);Log.Write("MODEL_CONTINUATION_REGISTERED","同周期单次续跑已登记，尚未启动下一轮。",id);return result;
        }
    }
    public bool ClaimModelContinuation(string id)
    {
        lock(gate)
        {
            var c=GetModelContinuation(id);
            if(c.State!="REGISTERED")return false;
            store.Save("model-continuation",id,c with{State="LAUNCHING"});return true;
        }
    }
    public ModelContinuation GetModelContinuation(string id)
    {lock(gate)return store.Load<ModelContinuation>("model-continuation").SingleOrDefault(c=>c.Id==id)??throw new DispatchException("NOT_FOUND","续跑登记不存在。");}
    public ModelContinuation RecordModelContinuation(string id,string state,int? pid=null)
    {
        lock(gate)
        {
            if(state is not ("WAITING" or "STOPPED_NO_RETRY" or "STARTED_AWAITING_RUNTIME"))throw new DispatchException("INVALID_STATE","续跑状态无效。");
            var c=GetModelContinuation(id);
            if(c.State is not ("LAUNCHING" or "WAITING"))return c;
            var next=c with{State=state,ProcessId=pid??c.ProcessId};store.Save("model-continuation",id,next);
            Log.Write("MODEL_CONTINUATION_"+state,"续跑状态更新；启动回执不等于模型生效。",c.DecisionId);return next;
        }
    }
    // 只供本地固定执行器序列化到私有数据目录，不返回MCP或写入日志。
    public object BuildModelContinuationPlan(ModelContinuation c,string instance="default")
    {
        lock(gate)
        {
            var d=GetDecision(c.DecisionId);var p=d.Execution!;
            PrepareModelContinuation(d.Id,p.Token);
            var observed=ObserveModelRuntime(p.ThreadId);
            if(observed?.TurnId!=c.SourceTurnId)throw new DispatchException("SOURCE_TURN_CHANGED","源轮次已改变，未启动续跑。");
            var home=store.Load<RuntimeSource>("runtime-source").Single().Home;
            var files=Directory.EnumerateFiles(Path.Combine(home,"sessions"),"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint})
                .Where(f=>Path.GetFileName(f)==observed.SourceFile).Take(2).ToArray();
            if(files.Length!=1)throw new DispatchException("SOURCE_UNAVAILABLE","源记录不唯一。");
            return new {instance,threadId=p.ThreadId,sourceTurnId=c.SourceTurnId,sourceFile=files[0],home,dataRoot=Root,
                expectedModel=p.Model,expectedEffort=p.Thinking,cycleId=d.Id,cycleToken=p.Token,supervisorVersion=p.SupervisorVersion,
                armedAfter=clock.GetUtcNow(),maxPolls=1200,
                prompt=$"这是调度工具登记的同周期续跑提醒。周期编号：{d.Id}。先get_model_decision读取原任务与令牌，再verify_model_runtime核对实际模型；VERIFIED才执行。不要新建委派，不重做已完成操作，不重发交接。当前方向：{(p.Returning?"返回主管，审查执行结果并继续":"限定执行任务，结束后沿同周期返回主管")}。本聊天已授权自动交接；发送并登记sent/return_sent后调用arm_model_continuation登记一次下一轮，无需用户输入继续。令牌不得公开。"};
        }
    }
}
