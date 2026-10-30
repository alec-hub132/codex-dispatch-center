using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core;

public sealed record HandoffCheckpoint(string Reason, string CompletedWork, string RemainingWork, string RunningOperations, string ResourceState);
public sealed record TakeoverRequest(string DecisionId, string Token, string Reason, string CompletedWork, string RemainingWork, string RunningOperations, string ResourceState);

public sealed partial class Scheduler
{
    public ModelDecision RequestTakeover(TakeoverRequest request)
    {
        Validate(request.Reason, "接管原因", 2000);
        Validate(request.CompletedWork, "已完成工作", 4000);
        Validate(request.RemainingWork, "剩余工作", 4000);
        Validate(request.RunningOperations, "运行中操作（没有则填无）", 2000);
        Validate(request.ResourceState, "资源占用说明（没有则填无）", 2000);
        lock (gate)
        {
            var decision = GetDecision(request.DecisionId);
            var plan = decision.Execution;
            if (plan is null || string.IsNullOrEmpty(request.Token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Token), Encoding.UTF8.GetBytes(plan.Token)))
                throw new DispatchException("INVALID_HANDOFF_TOKEN", "交接令牌不匹配。");
            if (!AuthorizedThreads().Contains(plan.ThreadId)) throw new DispatchException("UNAUTHORIZED_THREAD", "聊天已撤销交接授权。");
            if (GetSupervisor(plan.ThreadId)?.Version != plan.SupervisorVersion) throw new DispatchException("SUPERVISOR_CHANGED", "主管配置已改变，请核对用户新设置，不使用旧返回目标。");
            var checkpoint = new HandoffCheckpoint(request.Reason, request.CompletedWork, request.RemainingWork, request.RunningOperations, request.ResourceState);
            if (decision.Checkpoint is not null)
            {
                if (decision.Checkpoint != checkpoint) throw new DispatchException("IDEMPOTENCY_CONFLICT", "本周期已保存不同的接管进度，不能覆盖。");
                return decision;
            }
            if (decision.Status != "EXECUTING") throw new DispatchException("INVALID_STATE", "仅在执行模型已核验生效后申请执行中接管。");
            // 接管不能释放许可或终止进程。进度与返回计划必须在同一笔记录中持久化。
            var summary = $"执行任务尚未完成，申请主管接管。\n原因：{request.Reason}\n已完成：{request.CompletedWork}\n剩余：{request.RemainingWork}\n运行中操作：{request.RunningOperations}\n资源占用：{request.ResourceState}";
            if (summary.Contains(plan.Token, StringComparison.Ordinal) || permits.Values.Any(p => summary.Contains(p.Token, StringComparison.Ordinal)))
                throw new DispatchException("SECRET_IN_CHECKPOINT", "进度说明不能包含模型或资源令牌，请仅记录关联编号。");
            var returned = plan with { Returning = true, Model = plan.SupervisorModel, Thinking = plan.SupervisorEffort,
                Prompt = summary + "\n交回主管后先核对实际生效，再判断剩余工作。不得重做已完成操作，不得为切换而重跑或终止运行中的工具，也不得擅自释放原窗口资源。\n周期编号：" + decision.Id + "。先get_model_decision读取原周期并优先调用verify_model_runtime核验主管实际运行记录；禁止开启重复周期或把未完成任务报告为完成。" };
            var updated = decision with { Status = "RETURN_READY", Message = "执行中请求主管接管，进度已保存；尚未发送或核验主管。", Checkpoint = checkpoint, Execution = returned };
            store.Save("decision", updated.Id, updated);
            decisions[updated.Id] = updated;
            Log.Write("SUPERVISOR_TAKEOVER_REQUESTED", updated.Message, updated.Id);
            return updated;
        }
    }
}
