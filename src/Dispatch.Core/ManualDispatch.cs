namespace Dispatch.Core;

public sealed record ManualDispatchRequest(string JobId, int Generation, JobState ExpectedState);
public sealed record ManualDispatchAudit(string JobId, int Generation, DateTimeOffset At);
public sealed record ManualDispatchResult(Job Job, string Message);

public sealed partial class Scheduler
{
    public ManualDispatchResult RequestManualDispatch(ManualDispatchRequest request)
    {
        lock (gate)
        {
            try
            {
                CheckPermitExpiry();
                CheckResourceOffers();
                RequireQueueEnabled();
                if (paused) throw new DispatchException("QUEUE_PAUSED", "请先恢复队列派发，再优先执行。");
                var job = Find(request.JobId);
                if(job.Suite.Resources.Contains("sw-main") && HostRecoveryBlocking)throw new DispatchException("HOST_RECOVERY_REQUIRED","SolidWorks仍在崩溃处理或等待核验，请先查看宿主状态。");
                if (job.Generation != request.Generation || job.State != request.ExpectedState)
                    throw new DispatchException("QUEUE_ITEM_CHANGED", "状态或代次已变化，请刷新后重新选择。");
                if (job.State is not (JobState.Queued or JobState.WaitingForClient))
                    throw new DispatchException("INVALID_STATE", "请选择等待排队或等待接续的提交，待接单任务由原窗口确认。");
                var blocker = jobs.Values.FirstOrDefault(other => other.Id != job.Id && HoldsResources(other)
                    && other.Suite.Resources.Intersect(job.Suite.Resources).Any());
                if (blocker is not null)
                    throw new DispatchException("RESOURCE_BUSY", "同一软件仍被占用或隔离。请核对上一窗口，确认停止并清理释放后，再优先执行。");
                if (job.Suite.Runner == "external-agent")
                {
                    var binding = GetQueueBindings().SingleOrDefault(item => item.LeaseId == job.Id);
                    if (binding is not null)
                    {
                        RequireQueueBindingAllowed(job.Id);
                        var source = FindQueueSource(job.Id)
                            ?? throw new DispatchException("SOURCE_UNAVAILABLE", "原窗口运行记录暂不可读取。");
                        source = RefreshQueueSource(binding, source);
                        store.Save("queue-source", job.Id, source);
                        var wake = FindQueueWake(job.Id);
                        if (!QueueSourceAllowsOffer(job.Id) || wake is not null
                            && (wake.State != "SENT" || wake.NewTurnId != binding.SourceTurnId))
                            throw new DispatchException("QUEUE_SOURCE_MISMATCH", "原窗口尚未准备接单、已有新任务或接续回执不明，请先在原窗口核对。");
                    }
                    var offer = FindResourceOffer(job.Id);
                    if (job.State == JobState.WaitingForClient && offer is not null
                        && clock.GetUtcNow() < offer.ExpiresAt.AddSeconds(2))
                        throw new DispatchException("RESUME_BACKOFF", "前一次预留刚结束，请稍后刷新再操作。");
                }
                var message = job.Suite.Runner != "external-agent"
                    ? "已安排优先执行，执行器启动后会显示运行中。"
                    : GetQueueBindings().Any(binding => binding.LeaseId == job.Id)
                        ? "已优先安排原任务，后台沿既有授权接续，原窗口接单后显示运行中。"
                        : "已优先安排。该任务没有自动接续绑定，请在原窗口沿原许可接单。";
                if (job.UserPriority) return new(Snapshot(job), message);
                var next = job with
                {
                    State = JobState.Queued,
                    UserPriority = true,
                    Summary = "用户已选择优先执行，等待执行器派发；开发窗口仍须接单后才获得操作许可。"
                };
                var audit = new StoreWrite("manual-dispatch", Guid.NewGuid().ToString("N"),
                    new ManualDispatchAudit(job.Id, job.Generation, clock.GetUtcNow()));
                if (permits.TryGetValue(job.Id, out var permit))
                    SavePermit(next, permit with { Request = permit.Request with { RequireAcceptance = true } }, audit: audit);
                else
                {
                    store.SaveBatch(new("job", job.Id, next), audit);
                    RememberJob(next);
                }
                Log.Write("MANUAL_DISPATCH_REQUESTED", "用户选择该提交优先执行，等待真实派发与接单。", job.Id);
                return new(Snapshot(next), message);
            }
            catch (DispatchException error)
            {
                Log.Write("MANUAL_DISPATCH_REJECTED", "优先执行未安排：" + error.Code, request.JobId);
                throw;
            }
        }
    }
}
