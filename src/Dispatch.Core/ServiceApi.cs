using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core;

public sealed class ServiceApi(Scheduler scheduler, Action? stopService = null, Func<string, JsonElement, CancellationToken, Task<object>>? workspace = null, Func<string,string,object>? continuation = null)
{
    private readonly CandidateStore candidates = new(Path.Combine(scheduler.Root, "candidates"));
    public async Task<object> Execute(string method, JsonElement args, CancellationToken token)
    {
        switch (method)
        {
            case "get_health": return new { protocolVersion = 1, serviceVersion = "0.11.3", daemonRunning = true, simulationOnly = false, readonlySolidWorksSupported = true, modelSwitchSupported = false,
                modelHandoffProtocolSupported = true, supervisorReturnRequired = true, independentModulesSupported = true, supervisorTakeoverProtocolSupported = true,
                runtimeModelSwitchSupported = false, automaticContinuationSupported = false, modelContinuationRegistrationSupported = continuation is not null, runtimeEvidenceVerificationSupported = true,
                resourceContinuationProtocolSupported = true, workspaceAdapterAvailable = workspace is not null, automaticResourceWakeupSupported = false,
                modelExclusions = scheduler.GetModelExclusions(), modules = scheduler.GetModules(),
                resourcePermitsSupported = true, activeResourcePermits = scheduler.List().Count(j => j.Suite.Runner == "external-agent" && j.State == JobState.Running),
                paused = scheduler.Paused, queueDepth = scheduler.List().Count(j => j.State == JobState.Queued),
                recoveryRequired = scheduler.List().Count(j => j.State == JobState.RecoveryRequired) };
            case "arm_model_continuation":
                if(continuation is null)throw new DispatchException("UNSUPPORTED","当前服务没有配置单次续跑执行器。");
                return continuation(Text(args,"decisionId"),Text(args,"token"));
            case "get_module_settings": return scheduler.GetModules();
            case "set_runtime_source": return scheduler.SetRuntimeSource(Text(args, "home"));
            case "get_runtime_observation": return new { observation = scheduler.ObserveModelRuntime(Text(args, "sessionId")) };
            case "verify_model_runtime": return scheduler.VerifyModelRuntime(Text(args, "decisionId"), Text(args, "token"));
            case "set_module_settings": return scheduler.SetModules(args.Deserialize<ModuleSettings>(Json.Options) ?? throw Invalid());
            case "list_test_suites": return SuiteCatalog.All();
            case "submit_test": return scheduler.Submit(args.Deserialize<Submission>(Json.Options) ?? throw Invalid());
            case "get_queue_resumption_settings": return new{consents=scheduler.GetQueueResumeConsents(),bindings=scheduler.GetQueueBindings(),sources=scheduler.GetQueueSourceStates(),wakes=scheduler.GetQueueWakes()};
            case "begin_queue_wake_send": return new{allowed=scheduler.BeginQueueWakeSend(Text(args,"leaseId"),Text(args,"token"),Text(args,"wakeId"))};
            case "set_queue_resume_consent": return scheduler.SetQueueResumeConsent(args.Deserialize<QueueResumeConsent>(Json.Options)??throw Invalid());
            case "get_queue_binding": return scheduler.GetQueueBindingState(Text(args,"leaseId"),Text(args,"token"));
            case "accept_resource": return scheduler.AcceptResource(Text(args,"leaseId"),Text(args,"token"),Text(args,"offerId"),args.GetProperty("generation").GetInt32());
            case "retry_resource": return scheduler.RequeueResource(Text(args,"leaseId"),Text(args,"token"),args.TryGetProperty("sourceTurnId",out var queueTurn)?queueTurn.GetString():null);
            case "request_resource":
                var resourceRequest=args.Deserialize<ResourceRequest>(Json.Options)??throw Invalid();
                // MCP默认采用接单协议；显式false仅保留旧客户端兼容，不改变既有许可快照。
                if(!args.TryGetProperty("requireAcceptance",out _))resourceRequest=resourceRequest with{RequireAcceptance=true};
                return scheduler.RequestResource(resourceRequest);
            case "capture_workspace":
            case "restore_workspace":
                if (workspace is null) throw new DispatchException("UNSUPPORTED", "当前服务没有配置实际宿主现场适配器。");
                return await workspace(method, args, token);
            case "yield_resource": return scheduler.YieldResource(Text(args, "leaseId"), Text(args, "token"), args.GetProperty("generation").GetInt32(), Text(args, "checkpointId"), args.GetProperty("cleanupConfirmed").GetBoolean());
            case "resume_resource": return scheduler.ResumeResource(Text(args, "leaseId"), Text(args, "token"), Text(args, "checkpointId"), Text(args, "requestId"));
            case "get_resource_checkpoint": return scheduler.GetResourceCheckpoint(Text(args, "leaseId"), Text(args, "token"), Text(args, "checkpointId"));
            case "get_resource_events":
                var lease = Text(args, "leaseId"); var secret = Text(args, "token");
                var afterSequence = args.TryGetProperty("afterSequence", out var cursor) ? cursor.GetInt64() : 0;
                var maxWait = args.TryGetProperty("maxWaitSeconds", out var wait) ? wait.GetInt32() : 0;
                if (maxWait < 0 || maxWait > 20) throw Invalid();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWait);
                var notices = scheduler.GetResourceEvents(lease, secret, afterSequence);
                while (notices.Length == 0 && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(50, token); notices = scheduler.GetResourceEvents(lease, secret, afterSequence);
                }
                return notices;
            case "get_resource": return scheduler.GetResource(Text(args, "leaseId"), Text(args, "token"));
            case "release_resource": return scheduler.ReleaseResource(Text(args, "leaseId"), Text(args, "token"),
                args.GetProperty("generation").GetInt32(), args.GetProperty("cleanupConfirmed").GetBoolean());
            case "list_tests": return scheduler.List();
            case "freeze_candidate": return CandidateOperation(() => candidates.Freeze(args.Deserialize<FreezeRequest>(Json.Options) ?? throw Invalid()));
            case "list_candidates": return CandidateOperation(candidates.List);
            case "verify_candidate": return CandidateOperation(() => candidates.Verify(Text(args, "candidateId")));
            case "list_model_decisions": return scheduler.ListDecisions();
            case "get_supervisor_settings": return new { threads = scheduler.GetAuthorizedThreads(), configs = scheduler.GetAuthorizedThreads().Select(s => scheduler.GetSupervisor(s)).Where(c => c is not null).ToArray(), exclusions = scheduler.GetModelExclusions() };
            case "set_model_exclusions": return scheduler.SetModelExclusions(args.Deserialize<ModelExclusions>(Json.Options) ?? throw Invalid());
            case "set_supervisor_settings": return scheduler.SetSupervisor(args.Deserialize<SupervisorConfig>(Json.Options) ?? throw Invalid());
            case "shutdown_service":
                if (stopService is null) throw new DispatchException("UNSUPPORTED", "该服务不支持停止请求。");
                stopService(); return new { stopping = true };
            case "get_test_status": return scheduler.Get(Text(args, "jobId"));
            case "wait_test":
                var seconds = args.TryGetProperty("maxWaitSeconds", out var secondsElement) ? secondsElement.GetInt32() : 1;
                if (seconds < 0 || seconds > 20) throw Invalid();
                var id = Text(args, "jobId");
                var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
                var job = scheduler.Get(id);
                while (DateTimeOffset.UtcNow < until && job.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient or JobState.Running or JobState.Cleanup)
                {
                    await Task.Delay(50, token);
                    job = scheduler.Get(id);
                }
                return job;
            case "get_test_report":
                var reportJob = scheduler.Get(Text(args, "jobId"));
                return new { job = reportJob, simulationOnly = reportJob.Suite.SimulationOnly, resourcePermit = reportJob.Suite.Runner == "external-agent",
                    suiteHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reportJob.Suite, Json.Options)))),
                    reportReady = reportJob.State is not (JobState.Queued or JobState.Offered or JobState.WaitingForClient or JobState.Running or JobState.Cleanup),
                    logPath = Path.Combine(scheduler.Root, "logs", "bug.jsonl") };
            case "cancel_test":
                var cancelId = Text(args, "jobId");
                scheduler.Cancel(cancelId);
                return scheduler.Get(cancelId);
            case "set_queue_paused": scheduler.SetPaused(args.GetProperty("paused").GetBoolean()); return new { paused = scheduler.Paused };
            case "request_model_switch": return scheduler.RequestModel(args.Deserialize<ModelRequest>(Json.Options) ?? throw Invalid());
            case "request_supervisor_takeover": return scheduler.RequestTakeover(args.Deserialize<TakeoverRequest>(Json.Options) ?? throw Invalid());
            case "get_model_decision": return scheduler.GetDecision(Text(args, "decisionId"));
            case "record_model_handoff": return scheduler.RecordHandoff(Text(args, "decisionId"), Text(args, "token"), Text(args, "outcome"), Text(args, "evidence"), args.TryGetProperty("observedModel", out var om) ? om.GetString() : null, args.TryGetProperty("observedEffort", out var oe) ? oe.GetString() : null);
            default: throw new DispatchException("UNKNOWN_METHOD", "未知调度工具。");
        }
    }
    private static DispatchException Invalid() => new("INVALID_ARGUMENT", "请求参数无效。");
    private static T CandidateOperation<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new DispatchException("CANDIDATE_IO_ERROR", "候选文件不存在、正被写入或清单不可读取，请检查目录与文件清单。"); }
    }
    private static string Text(JsonElement args, string name) => args.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw Invalid();
}
