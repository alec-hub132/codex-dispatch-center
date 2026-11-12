using System.Security.Cryptography;

namespace Dispatch.Core;

public sealed record ResourceCheckpoint(string Id, string SourceLeaseId, int SourceGeneration, string RequestId, string CompletedWork,
    string RemainingWork, WorkspaceScene Scene, string State, DateTimeOffset CreatedAt, string? ResumeLeaseId = null, string? ResumeRequestId = null);
public sealed record ResourceNotice(long Sequence, string LeaseId, string Kind, string Message, DateTimeOffset CreatedAt, string CheckpointId);
internal sealed record WorkspaceOperation(string LeaseId, int Generation, string RequestId, string Kind, string CompletedWork, string RemainingWork, string State = "Running");

public sealed partial class Scheduler
{
    private ResourceCheckpoint[] Checkpoints() => store.Load<ResourceCheckpoint>("resource-checkpoint").ToArray();
    private WorkspaceOperation? Operation(string leaseId) => store.Load<WorkspaceOperation>("workspace-operation").SingleOrDefault(o => o.LeaseId == leaseId && o.State != "Done");
    private void EnsureNoWorkspaceOperation(string leaseId, bool cleanupConfirmed = false)
    {
        var op = Operation(leaseId);
        if (op is not null && !(op.State == "Interrupted" && cleanupConfirmed))
            throw new DispatchException("WORKSPACE_OPERATION_ACTIVE", "现场读写仍在进行或状态不明；先停止并确认清理，不能释放或重复操作。");
    }
    private void InterruptWorkspaceOperations()
    {
        foreach (var op in store.Load<WorkspaceOperation>("workspace-operation").Where(o => o.State == "Running"))
            store.Save("workspace-operation", op.LeaseId, op with { State = "Interrupted" });
    }
    private Job WorkspacePermit(string leaseId, string token, int generation)
    {
        var p = AuthorizePermit(leaseId, token); CheckPermitExpiry(); var job = Find(leaseId);
        if (p.Request.Resource != "solidworks") throw new DispatchException("INVALID_RESOURCE", "文档现场仅适用于SolidWorks许可。");
        if (job.Generation != generation) throw new DispatchException("STALE_RESULT", "许可代次已改变。");
        if (job.State != JobState.Running) throw new DispatchException("NOT_GRANTED", "当前没有有效的独占许可，不可读取或恢复宿主。");
        return job;
    }
    public ResourceCheckpoint? BeginWorkspaceCapture(string leaseId, string token, int generation, string requestId, string completed, string remaining, bool safePointConfirmed)
    {
        Validate(requestId, "检查点请求编号", 128); Validate(completed, "已完成工作", 4000); Validate(remaining, "剩余工作", 4000);
        lock (gate)
        {
            AuthorizePermit(leaseId, token);
            var prior = Checkpoints().SingleOrDefault(c => c.SourceLeaseId == leaseId && c.RequestId == requestId);
            if (prior is not null)
            {
                if (prior.CompletedWork != completed || prior.RemainingWork != remaining || prior.SourceGeneration != generation || !safePointConfirmed)
                    throw new DispatchException("IDEMPOTENCY_CONFLICT", "检查点请求内容改变。");
                return prior;
            }
            WorkspacePermit(leaseId, token, generation); EnsureNoWorkspaceOperation(leaseId);
            if (RestoreCheckpoint(leaseId) is { State: not "Restored" })
                throw new DispatchException("RESTORE_REQUIRED", "先恢复此操作段关联的原现场，不能用当前现场跳过恢复。");
            if (!safePointConfirmed) throw new DispatchException("SAFE_POINT_REQUIRED", "原窗口须确认命令、部署和异步操作已经停止。");
            var text = completed + remaining;
            if (permits.Values.Any(p => text.Contains(p.Token, StringComparison.Ordinal)) || decisions.Values.Any(d => d.Execution is {} p && text.Contains(p.Token, StringComparison.Ordinal)))
                throw new DispatchException("SECRET_IN_CHECKPOINT", "任务进度不能包含许可或模型令牌。");
            store.Save("workspace-operation", leaseId, new WorkspaceOperation(leaseId, generation, requestId, "Capture", completed, remaining));
            return null;
        }
    }
    public ResourceCheckpoint FinishWorkspaceCapture(string leaseId, string requestId, WorkspaceScene scene)
    {
        WorkspaceRestorer.Validate(scene);
        lock (gate)
        {
            var op = Operation(leaseId) ?? throw new DispatchException("INVALID_STATE", "没有待完成的现场读取。");
            if (op.Kind != "Capture" || op.RequestId != requestId || op.State != "Running" || Find(leaseId).State != JobState.Running)
                throw new DispatchException("INVALID_STATE", "现场读取操作或许可已改变。");
            var cp = new ResourceCheckpoint(Guid.NewGuid().ToString("N"), leaseId, op.Generation, requestId, op.CompletedWork, op.RemainingWork, scene, "Captured", clock.GetUtcNow());
            var writes = Checkpoints().Where(c => c.SourceLeaseId == leaseId && c.State == "Captured")
                .Select(c => new StoreWrite("resource-checkpoint", c.Id, c with { State = "Superseded" })).ToList();
            writes.Add(new("resource-checkpoint", cp.Id, cp)); writes.Add(new("workspace-operation", leaseId, op with { State = "Done" }));
            store.SaveBatch(writes.ToArray());
            Log.Write("WORKSPACE_CAPTURED", "已保存任务进度和实际宿主现场；尚未让出资源。", leaseId);
            return cp;
        }
    }
    public ResourceCheckpoint GetResourceCheckpoint(string leaseId, string token, string checkpointId)
    {
        lock (gate)
        {
            AuthorizePermit(leaseId, token);
            return Checkpoints().SingleOrDefault(c => c.Id == checkpointId && (c.SourceLeaseId == leaseId || c.ResumeLeaseId == leaseId))
                ?? throw new DispatchException("NOT_FOUND", "本许可没有此现场检查点。");
        }
    }
    private StoreWrite Notice(string leaseId, string checkpointId, string kind, string message)
    {
        var next = store.Load<ResourceNotice>("resource-notice").Select(n => n.Sequence).DefaultIfEmpty().Max() + 1;
        return new("resource-notice", next.ToString(System.Globalization.CultureInfo.InvariantCulture), new ResourceNotice(next, leaseId, kind, message, clock.GetUtcNow(), checkpointId));
    }
    public ResourceCheckpoint YieldResource(string leaseId, string token, int generation, string checkpointId, bool cleanupConfirmed)
    {
        lock (gate)
        {
            var cp = GetResourceCheckpoint(leaseId, token, checkpointId);
            if (cp.SourceLeaseId != leaseId || cp.SourceGeneration != generation) throw new DispatchException("STALE_RESULT", "检查点不属于此操作段或代次。");
            if (!cleanupConfirmed) throw new DispatchException("CLEANUP_REQUIRED", "操作未停止或清理未确认，不得让出。");
            if (cp.State == "Superseded") throw new DispatchException("STALE_CHECKPOINT", "已有更新的现场检查点，不使用旧现场让出。");
            if (cp.State != "Captured") return cp; // 只回读原结果，不重复释放新操作段。
            var job = WorkspacePermit(leaseId, token, generation); EnsureNoWorkspaceOperation(leaseId);
            var yielded = job with { State = JobState.Released, EndedAt = clock.GetUtcNow(), CleanupStatus = "OK", Summary = "已保存现场并让出资源，原任务尚未完成，可继续不占用宿主的工作。" };
            var updated = cp with { State = "Yielded" };
            store.SaveBatch(new("job", job.Id, yielded), new("resource-checkpoint", cp.Id, updated),
                Notice(leaseId, cp.Id, "YIELDED", "资源已让出；需要继续操作时重新排队，获准后先恢复现场。"));
            RememberJob(yielded);
            Log.Write("RESOURCE_YIELDED", yielded.Summary, job.Id);
            return updated;
        }
    }
    public ResourcePermit ResumeResource(string leaseId, string token, string checkpointId, string requestId)
    {
        Validate(requestId, "恢复请求编号", 128);
        lock (gate)
        {
            var cp = GetResourceCheckpoint(leaseId, token, checkpointId);
            if (cp.SourceLeaseId != leaseId) throw new DispatchException("INVALID_ARGUMENT", "使用原操作段的许可申请恢复。");
            if (cp.ResumeLeaseId is not null)
            {
                if (cp.ResumeRequestId != requestId) throw new DispatchException("IDEMPOTENCY_CONFLICT", "已有恢复申请，不得重复排队。");
                return PermitView(permits[cp.ResumeLeaseId]);
            }
            RequireQueueEnabled();
            if (cp.State != "Yielded") throw new DispatchException("INVALID_STATE", "尚未让出资源，不能申请恢复。");
            var previous = permits[leaseId];
            var request = previous.Request with { RequestId = "resume-" + cp.Id + "-" + Guid.NewGuid().ToString("N"), Purpose = previous.Request.Purpose };
            var sourceJob = Find(leaseId);
            var job = new Job(Guid.NewGuid().ToString("N"), sequence + 1, new(request.RequestId, request.SessionId, sourceJob.Suite.Id),
                sourceJob.Suite with { Resources = [..sourceJob.Suite.Resources] }, JobState.Queued, 0, clock.GetUtcNow(), Summary: "等待恢复现场的独占许可。", CleanupStatus: "NOT_REQUIRED");
            var permit = new PermitRecord(job.Id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), request, null);
            var updated = cp with { State = "ResumeQueued", ResumeLeaseId = job.Id, ResumeRequestId = requestId };
            store.SaveBatch(new("job", job.Id, job), new("permit", job.Id, permit), new("resource-checkpoint", cp.Id, updated),
                Notice(leaseId, cp.Id, "RESUME_QUEUED", "已在当前队尾申请恢复；等待时不操作宿主。"));
            sequence = job.Sequence; RememberJob(job); permits[job.Id] = permit;
            return PermitView(permit);
        }
    }
    private ResourceCheckpoint? RestoreCheckpoint(string leaseId) => Checkpoints().SingleOrDefault(c => c.ResumeLeaseId == leaseId);
    private void NotifyResourceGranted(string leaseId)
    {
        var cp = RestoreCheckpoint(leaseId);
        if (cp is not null) store.SaveBatch(Notice(cp.SourceLeaseId, cp.Id, "RESTORE_REQUIRED", "已获准使用；先恢复原文档、视图和选择，核验通过后继续原任务。"));
    }
    public ResourceCheckpoint BeginWorkspaceRestore(string leaseId, string token, int generation, string checkpointId)
    {
        lock (gate)
        {
            var cp = GetResourceCheckpoint(leaseId, token, checkpointId);
            if (cp.ResumeLeaseId != leaseId) throw new DispatchException("INVALID_ARGUMENT", "必须取得新的恢复许可。");
            WorkspacePermit(leaseId, token, generation); EnsureNoWorkspaceOperation(leaseId);
            if (cp.State == "Restored") return cp;
            store.Save("workspace-operation", leaseId, new WorkspaceOperation(leaseId, generation, checkpointId, "Restore", "", ""));
            return cp;
        }
    }
    public ResourceCheckpoint FinishWorkspaceRestore(string leaseId, WorkspaceRestoreResult result)
    {
        lock (gate)
        {
            var cp = RestoreCheckpoint(leaseId) ?? throw new DispatchException("NOT_FOUND", "恢复检查点不存在。");
            var op = Operation(leaseId) ?? throw new DispatchException("INVALID_STATE", "没有正在执行的恢复。");
            if (op.Kind != "Restore" || op.State != "Running") throw new DispatchException("INVALID_STATE", "现场恢复操作不匹配。");
            var job = Find(leaseId);
            var ok = result.Success && job.State == JobState.Running;
            var message = !ok && result.Success ? "现场回读匹配，但许可已过期或状态改变，仍保持隔离，不继续业务操作。" : result.Message;
            var updated = cp with { State = ok ? "Restored" : "RestoreBlocked" };
            var next = job with { State = ok ? JobState.Running : JobState.RecoveryRequired, Summary = message, CleanupStatus = "UNCERTAIN" };
            store.SaveBatch(new("resource-checkpoint", cp.Id, updated), new("workspace-operation", leaseId, op with { State = "Done" }), new("job", job.Id, next),
                Notice(cp.SourceLeaseId, cp.Id, ok ? "RESTORED" : "RESTORE_BLOCKED", message));
            RememberJob(next); Log.Write(ok ? "WORKSPACE_RESTORED" : "WORKSPACE_RESTORE_BLOCKED", message, leaseId);
            return updated;
        }
    }
    public void FailWorkspaceOperation(string leaseId, string message)
    {
        lock (gate)
        {
            var op = Operation(leaseId);
            if (op is null) return;
            var job = Find(leaseId) with { State = JobState.RecoveryRequired, CleanupStatus = "UNCERTAIN", Summary = message };
            store.SaveBatch(new("workspace-operation", leaseId, op with { State = "Done" }), new("job", leaseId, job));
            RememberJob(job); Log.Write("WORKSPACE_OPERATION_FAILED", message, leaseId);
        }
    }
    public ResourceNotice[] GetResourceEvents(string leaseId, string token, long afterSequence)
    {
        lock (gate)
        {
            AuthorizePermit(leaseId, token);
            if (afterSequence < 0) throw new DispatchException("INVALID_ARGUMENT", "通知游标不能为负数。");
            var source = RestoreCheckpoint(leaseId)?.SourceLeaseId ?? leaseId;
            return store.Load<ResourceNotice>("resource-notice").Where(n => n.LeaseId == source && n.Sequence > afterSequence).OrderBy(n => n.Sequence).Take(100).ToArray();
        }
    }
}
