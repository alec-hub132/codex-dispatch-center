using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core;

public sealed partial class Scheduler
{
    // 仅服务启动升级调用：未获准的旧排队项改为接单，保持原任务/令牌及资源快照。
    public int UpgradeQueuedResourcePermits()
    {
        lock(gate)
        {
            var count=0;
            foreach(var permit in permits.Values.ToArray())
            {
                var job=Find(permit.JobId);
                if(job.State!=JobState.Queued || permit.Request.RequireAcceptance || permit.ExpiresAt is not null)continue;
                SavePermit(job with{Summary="旧排队申请已升级为接单流程，原窗口accept_resource获准后才可操作。"},
                    permit with{Request=permit.Request with{RequireAcceptance=true}});
                count++;
            }
            if(count>0)Log.Write("QUEUED_PERMITS_UPGRADED",$"已将{count}项尚未授予的旧申请迁为接单；已有隔离及清理责任保持。");
            return count;
        }
    }
    public ResourcePermit RequestResource(ResourceRequest request)
    {
        if(request.ResumeThreadId is not null || request.SourceTurnId is not null || request.RemainingWork is not null)request=request with{RequireAcceptance=true};
        Validate(request.RequestId, "请求编号", 128); Validate(request.SessionId, "会话标签", 128);
        Validate(request.Purpose, "使用目的", 2000);
        if (request.DurationSeconds is < 30 or > 1800) throw new DispatchException("INVALID_ARGUMENT", "使用时限必须在30至1800秒之间。");
        string[] resources = request.Resource switch
        {
            "solidworks" or "app:solidworks" => ["sw-main"],
            "solidworks-ui" or "app-ui:solidworks" => ["sw-main", "desktop-ui"],
            "desktop" => ["desktop-ui"],
            _ when ValidAppResource(request.Resource,"app:") => [request.Resource],
            _ when ValidAppResource(request.Resource,"app-ui:") => ["app:"+request.Resource[7..],"desktop-ui"],
            _ => throw new DispatchException("INVALID_ARGUMENT", "使用solidworks、solidworks-ui、desktop或app:/app-ui:后跟稳定软件标识。")
        };
        lock (gate)
        {
            var previous = jobs.Values.FirstOrDefault(j => j.Request.RequestId == request.RequestId && j.Request.SessionId == request.SessionId);
            if (previous is not null)
            {
                if (!permits.TryGetValue(previous.Id, out var existing) || existing.Request != request)
                    throw new DispatchException("IDEMPOTENCY_CONFLICT", "相同请求编号已被其他内容使用。");
                return PermitView(existing);
            }
            RequireQueueEnabled();
            if(request.OwnerThreadId is {} owner)VerifyIdleCleanupOwner(owner);
            var leaseId=Guid.NewGuid().ToString("N");
            var binding=PrepareQueueBinding(request,leaseId);
            var job = new Job(leaseId, ++sequence,
                new(request.RequestId, request.SessionId, "resource-" + request.Resource),
                new("resource-" + request.Resource, "使用许可：" + request.Purpose, resources, 0, request.DurationSeconds * 1000, "permit", false, "external-agent"),
                JobState.Queued, 0, clock.GetUtcNow(), Summary: "等待共享资源许可；尚不可操作宿主。", CleanupStatus: "NOT_REQUIRED");
            var permit = new PermitRecord(job.Id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), request, null);
            SavePermit(job, permit,binding);
            return PermitView(permit);
        }
    }

    private static bool ValidAppResource(string value,string prefix) => value.StartsWith(prefix,StringComparison.Ordinal) &&
        System.Text.RegularExpressions.Regex.IsMatch(value[prefix.Length..],"^[a-z0-9][a-z0-9._-]{0,23}\\z") && value[prefix.Length..]!="desktop";

    public ResourcePermit GetResource(string id, string token)
    {
        lock (gate) { var permit = AuthorizePermit(id, token); CheckPermitExpiry(); CheckResourceOffers(); return PermitView(permit); }
    }

    public ResourcePermit ReleaseResource(string id, string token, int generation, bool cleanupConfirmed)
    {
        lock (gate)
        {
            try
            {
            var permit = AuthorizePermit(id, token); CheckPermitExpiry();
            var job = Find(id);
            EnsureNoWorkspaceOperation(id, cleanupConfirmed);
            if (job.Generation != generation) throw new DispatchException("STALE_RESULT", "许可代次不匹配，请先读取当前许可状态。");
            if (job.State is JobState.Released or JobState.Cancelled) return PermitView(permit);
            if (job.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient)
                SavePermit(job with { State = JobState.Cancelled, EndedAt = clock.GetUtcNow(), Summary = "申请在授予前撤回。" }, permit);
            else if (job.State is JobState.Running or JobState.Cleanup or JobState.RecoveryRequired)
                SavePermit(job with { State = cleanupConfirmed ? JobState.Released : JobState.RecoveryRequired,
                    EndedAt = cleanupConfirmed ? clock.GetUtcNow() : null, CleanupStatus = cleanupConfirmed ? "OK" : "UNCERTAIN",
                    Summary = cleanupConfirmed ? "原开发窗口已确认测试停止并完成清理，资源许可已释放；不代表测试通过。" : "原窗口未确认清理，继续隔离该资源。" }, permit,cleanupUnconfirmed:!cleanupConfirmed);
            else throw new DispatchException("INVALID_STATE", "当前任务不是可释放的资源许可。");
            return PermitView(permit);
            }
            catch(DispatchException ex)
            {
                Log.Write("RESOURCE_RELEASE_REJECTED","释放请求被拒绝："+ex.Code+"；原许可状态未被强制清除。",permits.ContainsKey(id)?id:null);
                throw;
            }
        }
    }

    private PermitRecord AuthorizePermit(string id, string token)
    {
        if (string.IsNullOrEmpty(token) || !permits.TryGetValue(id, out var permit)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(permit.Token)))
            throw new DispatchException("INVALID_PERMIT_TOKEN", "许可不存在或令牌不匹配。");
        return permit;
    }
    private ResourcePermit PermitView(PermitRecord permit)
    {
        var job = Snapshot(Find(permit.JobId));
        var status = job.State == JobState.Running ? "Granted" : job.State.ToString();
        var checkpoint = RestoreCheckpoint(job.Id);
        var restoreRequired = checkpoint is not null && checkpoint.State != "Restored";
        var instructions = status == "Granted" && restoreRequired ? "此许可属于恢复操作段。先调用restore_workspace，回读核验恢复通过后才能继续业务。" :
            status == "Granted" ? "已获得本段测试许可，不覆盖代码修复、普通编译或分析。SolidWorks默认复用现有实例；许可不授权自动新开软件。脚本测试优先使用包内scripts/Run-ResourceStep.py，在finally真实停止/清理后返回本次清理回执，由执行器立即释放并回读；直接MCP测试须原窗口显式完成同样清理释放。测试失败但清理成功也先释放，再改代码。需要复测用新requestId重新排队，不复用Released许可。进程退出或开始编译不是清理证明；未确认清理仍保留隔离。当前顺序使用，不自动还原现场，不保存用户原始文档。" :
            status == "Offered" ? "仅为短期接单预留，不可操作软件。原窗口核对任务后用offerId、generation和原令牌调用accept_resource，返回Granted才继续。" :
            status == "WaitingForClient" ? "未接单预留已结束，原任务保留且不占用软件；不要按旧通知开始测试。" :
            status == "Queued" ? "尚未获得许可，不可操作共享软件。可以继续不冲突开发，再查询本许可。" :
            status == "RecoveryRequired" ? "操作状态不明，停止宿主操作并确认清理后由原窗口释放；不得自动恢复、重跑或解锁。" : "许可已结束；不代表任务通过。";
        var offer=FindResourceOffer(job.Id);
        return new(job, permit.Token, status, permit.ExpiresAt, instructions, restoreRequired, checkpoint?.Id,offer?.Id,status=="Offered"?offer?.ExpiresAt:null);
    }
    private void CheckPermitExpiry()
    {
        foreach (var permit in permits.Values)
        {
            var job = Find(permit.JobId);
            if (job.State == JobState.Running && permit.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
                SavePermit(job with { State = JobState.RecoveryRequired, CleanupStatus = "UNCERTAIN", Summary = "使用许可已到期，等待原窗口停止操作并确认清理；资源未自动释放。" }, permit);
        }
    }
    private void SavePermit(Job job, PermitRecord permit, QueueResumeBinding? binding=null,QueueSourceCursor? sourceCursor=null,bool cleanupUnconfirmed=false,StoreWrite? audit=null)
    {
        var writes=new List<StoreWrite>{new("job",job.Id,job),new("permit",job.Id,permit)};
        if(audit is not null)writes.Add(audit);
        if(job.State==JobState.Released)writes.AddRange(IdleCleanupConfirmWrites(job.Id));
        else if(cleanupUnconfirmed)writes.AddRange(IdleCleanupUnconfirmedWrites(job.Id));
        if(binding is not null)
        {
            var cursor=sourceCursor??CreateQueueSource(binding);
            writes.Add(new("queue-binding",job.Id,binding));writes.Add(new("queue-source",job.Id,cursor));
        }
        store.SaveBatch([..writes]);
        RememberJob(job); permits[job.Id] = permit;
        Log.Write(job.State.ToString(), job.Summary, job.Id);
    }
}
