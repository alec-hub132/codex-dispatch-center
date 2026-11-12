namespace Dispatch.Core;

public sealed partial class Scheduler : IDisposable
{
    private readonly object gate = new();
    private readonly JobStore store;
    private readonly Dictionary<string, Job> jobs;
    private readonly Dictionary<string, ModelDecision> decisions;
    private readonly Dictionary<string, PermitRecord> permits;
    private readonly TimeProvider clock;
    private bool paused;
    private long sequence;
    public Diagnostics Log { get; }
    public string Root => store.Root;
    public bool Paused { get { lock (gate) return paused; } }

    public Scheduler(JobStore store, TimeProvider? clock = null)
    {
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
        Log = new(Path.Combine(store.Root, "logs"));
        jobs = store.Load<Job>("job").ToDictionary(j => j.Id);
        foreach (var job in jobs.Values) jobRevisions[job.Id] = ++queueRevision;
        decisions = store.Load<ModelDecision>("decision").ToDictionary(d => d.Id);
        permits = store.Load<PermitRecord>("permit").ToDictionary(p => p.JobId);
        paused = store.Load<bool>("paused").FirstOrDefault();
        hostControlBusy=store.Load<bool>("host-control").FirstOrDefault();
        sequence = jobs.Values.Select(j => j.Sequence).DefaultIfEmpty().Max();
        InterruptHostRecovery();
        InterruptWorkspaceOperations();
        InterruptQueueWakes();
        InterruptIdleCleanup();
        CheckResourceOffers(true);
        foreach (var job in jobs.Values.Where(j => j.State is JobState.Running or JobState.Cleanup).ToArray())
            Save(job with { State = JobState.RecoveryRequired, CleanupStatus = "UNCERTAIN", Summary = "服务曾中断，资源保持隔离；未自动重跑。" });
    }

    public Job Submit(Submission request)
    {
        Validate(request.RequestId, "请求编号", 128);
        Validate(request.SessionId, "会话编号", 128);
        lock (gate)
        {
            var previous = jobs.Values.FirstOrDefault(j => j.Request.RequestId == request.RequestId && j.Request.SessionId == request.SessionId);
            if (previous is not null)
            {
                if (previous.Suite.Runner == "external-agent" || previous.Request != request) throw new DispatchException("IDEMPOTENCY_CONFLICT", "相同请求编号的内容发生变化。");
                return Snapshot(previous);
            }
            RequireQueueEnabled();
            var suite = SuiteCatalog.Get(request.SuiteId);
            var job = new Job(Guid.NewGuid().ToString("N"), ++sequence, request, suite, JobState.Queued, 0, DateTimeOffset.UtcNow);
            Save(job);
            return Snapshot(job);
        }
    }

    public IReadOnlyList<Job> Claim()
    {
        lock (gate)
        {
            CheckPermitExpiry();
            CheckResourceOffers();
            if (paused || !GetModules().QueueEnabled) return [];
            var busy = jobs.Values.Where(HoldsResources).SelectMany(j => j.Suite.Resources).ToHashSet(StringComparer.Ordinal);
            if(HostRecoveryBlocking || hostControlBusy)busy.Add("sw-main");
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            var claimed = new List<Job>();
            foreach (var job in jobs.Values.Where(j => j.State == JobState.Queued).OrderByDescending(j => j.UserPriority).ThenBy(j => j.Sequence).ToArray())
            {
                if (job.Suite.Resources.Any(r => busy.Contains(r) || reserved.Contains(r)))
                {
                    reserved.UnionWith(job.Suite.Resources); // 保留较早任务对交叉资源的先到顺序。
                    continue;
                }
                if(TryCreateResourceOffer(job)){if(Find(job.Id).State==JobState.Offered)busy.UnionWith(job.Suite.Resources);continue;}
                var next = job with { State = JobState.Running, UserPriority = false, Generation = job.Generation + 1, StartedAt = DateTimeOffset.UtcNow, Summary = job.Suite.SimulationOnly ? "模拟执行中" : "真实只读检查执行中", CleanupStatus = "UNCERTAIN" };
                if (next.Suite.Runner == "external-agent")
                {
                    if (RestoreCheckpoint(next.Id) is not null) next = next with { Summary = "已获得恢复许可；先恢复文档、视图与选择并回读，尚不能继续原业务。" };
                    var permit = permits[next.Id] with { ExpiresAt = clock.GetUtcNow().AddSeconds(permits[next.Id].Request.DurationSeconds) };
                    SavePermit(next with { Summary = "已授予使用许可，等待原窗口完成测试并确认清理。" }, permit);
                    next = jobs[next.Id];
                    NotifyResourceGranted(next.Id);
                }
                else Save(next); // 持久化成功后才允许启动执行器。
                busy.UnionWith(job.Suite.Resources);
                claimed.Add(Snapshot(next));
            }
            return claimed;
        }
    }

    public void AttachRunner(string id, int generation, int pid, DateTimeOffset started)
    {
        lock (gate) { var job = Active(id, generation); Save(job with { RunnerPid = pid, RunnerStartedAt = started }); }
    }

    public void Cleaning(string id, int generation)
    {
        lock (gate) { var job = Active(id, generation); Save(job with { State = JobState.Cleanup, Summary = job.Suite.SimulationOnly ? "正在确认模拟进程退出" : "正在确认只读执行器退出" }); }
    }

    public void Complete(string id, int generation, JobState state, bool cleanupOk, string summary, System.Text.Json.JsonElement? evidence = null)
    {
        lock (gate)
        {
            var job = Active(id, generation);
            if (job.Suite.Runner == "external-agent") throw new DispatchException("PERMIT_OWNER_REQUIRED", "资源许可必须由持有令牌的原窗口确认清理后释放。");
            if (state is not (JobState.Passed or JobState.Failed or JobState.Cancelled or JobState.TimedOut or JobState.Blocked))
                throw new DispatchException("INVALID_STATE", "无效的执行结果状态。");
            var final = cleanupOk ? (job.CancelRequested ? JobState.Cancelled : state) : JobState.RecoveryRequired;
            Save(job with { State = final, EndedAt = DateTimeOffset.UtcNow, Summary = summary, CleanupStatus = cleanupOk ? "OK" : "UNCERTAIN", Evidence = evidence });
        }
    }

    public Job Get(string id) { lock (gate) return Snapshot(Find(id)); }
    public IReadOnlyList<Job> List() { lock (gate) return jobs.Values.OrderBy(j => j.Sequence).Select(Snapshot).ToArray(); }
    public void Cancel(string id)
    {
        lock (gate)
        {
            var job = Find(id);
            if (job.Suite.Runner == "external-agent" && job.State is JobState.Running or JobState.Cleanup)
                throw new DispatchException("PERMIT_OWNER_REQUIRED", "资源由原开发窗口持有，请由原窗口完成清理并释放；取消不能代替清理确认。");
            if (job.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient) Save(job with { State = JobState.Cancelled, CancelRequested = true, EndedAt = DateTimeOffset.UtcNow, Summary = "排队任务已取消" });
            else if (job.State is JobState.Running or JobState.Cleanup) Save(job with { CancelRequested = true, Summary = "正在安全取消" });
            else if (job.State == JobState.RecoveryRequired) throw new DispatchException("RECOVERY_REQUIRED", "取消不能解除不确定的资源占用。");
        }
    }
    public void SetPaused(bool value) { lock (gate) { store.Save("paused", "global", value); paused = value; } }

    public ModelDecision RequestModel(ModelRequest request)
    {
        Validate(request.RequestId, "请求编号", 128); Validate(request.SessionId, "会话编号", 128);
        Validate(request.Model, "模型", 128); Validate(request.Effort, "推理强度", 32);
        Validate(request.Reason, "理由", 2000); Validate(request.Handoff, "交接内容", 16000);
        lock (gate)
        {
            var previous = decisions.Values.FirstOrDefault(d => d.Request.SessionId == request.SessionId && d.Request.RequestId == request.RequestId);
            if (previous is not null)
            {
                if (previous.Request != request) throw new DispatchException("IDEMPOTENCY_CONFLICT", "模型建议请求内容发生变化。");
                return previous;
            }
            RequireModelsEnabled();
            var plan = PrepareHandoff(request);
            var decisionId = Guid.NewGuid().ToString("N");
            if (plan is not null) plan = plan with { Prompt = plan.Prompt + "\n周期编号：" + decisionId + "。开始时先调用get_model_decision读取本周期和令牌，不新建周期。优先调用verify_model_runtime由服务只读核对实际轮次；VERIFIED才执行任务，未匹配时不伪报生效、不自动重发。完成通过record_model_handoff登记结果；中途需要主管判断用request_supervisor_takeover保存进度，沿原周期返回。" };
            var decision = new ModelDecision(decisionId, request, plan is null ? "UNSUPPORTED" : "AWAITING_CALLER_EXECUTION",
                plan is null ? "已记录模型建议；此会话未授权交接，未执行切换。" : "交接计划已登记。仅由已授权的当前Codex环境执行内置发送工具；请先登记sending，再发送一次并登记sent。回执不代表模型实际生效。", DateTimeOffset.UtcNow, plan);
            store.Save("decision", decision.Id, decision);
            decisions.Add(decision.Id, decision);
            return decision;
        }
    }
    public ModelDecision GetDecision(string id) { lock (gate) return decisions.TryGetValue(id, out var value) ? value : throw new DispatchException("NOT_FOUND", "模型建议不存在。"); }
    public IReadOnlyList<ModelDecision> ListDecisions() { lock (gate) return decisions.Values.OrderByDescending(d => d.CreatedAt).ToArray(); }
    private Job Find(string id) => jobs.TryGetValue(id, out var job) ? job : throw new DispatchException("NOT_FOUND", "测试任务不存在。");
    private Job Active(string id, int generation)
    {
        var job = Find(id);
        if (job.Generation != generation || job.State is not (JobState.Running or JobState.Cleanup))
            throw new DispatchException("STALE_RESULT", "回执租约已失效或任务已经结束。");
        return job;
    }
    private void Save(Job job)
    {
        store.Save("job", job.Id, job);
        RememberJob(job);
        Log.Write(job.State.ToString(), job.Summary, job.Id);
    }
    private static bool HoldsResources(Job job) => job.State is JobState.Running or JobState.Cleanup or JobState.RecoveryRequired or JobState.Offered;
    private static Job Snapshot(Job job) => job with { Suite = job.Suite with { Resources = [.. job.Suite.Resources] } };
    private static void Validate(string value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new DispatchException("INVALID_ARGUMENT", $"{field}不能为空且不能超过 {max} 字符。");
    }
    public void Dispose() => store.Dispose();
}
