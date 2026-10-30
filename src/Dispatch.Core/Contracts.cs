namespace Dispatch.Core;

public enum JobState { Queued, Running, Cleanup, Passed, Failed, Cancelled, TimedOut, RecoveryRequired, Blocked, Released, Offered, WaitingForClient }
public sealed record Submission(string RequestId, string SessionId, string SuiteId);
public sealed record Suite(string Id, string Name, string[] Resources, int DurationMs, int TimeoutMs, string Mode,
    bool SimulationOnly = true, string Runner = "mock");
public sealed record Job(string Id, long Sequence, Submission Request, Suite Suite, JobState State, int Generation,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null,
    bool CancelRequested = false, string Summary = "等待资源", string CleanupStatus = "NOT_REQUIRED",
    int? RunnerPid = null, DateTimeOffset? RunnerStartedAt = null, System.Text.Json.JsonElement? Evidence = null);
public sealed record ModelRequest(string RequestId, string SessionId, string Model, string Effort, string Reason, string Handoff,
    string? PlannerModel = null, string? PlannerEffort = null, string? CompletionCriteria = null);
public sealed record ModelDecision(string Id, ModelRequest Request, string Status, string Message, DateTimeOffset CreatedAt, HandoffPlan? Execution = null, HandoffCheckpoint? Checkpoint = null, DateTimeOffset? VerificationNotBefore = null);
public sealed record HandoffPlan(string ThreadId, string Model, string Thinking, string Prompt, string Token,
    string SupervisorModel, string SupervisorEffort, int SupervisorVersion, bool Returning = false);
public sealed record SupervisorConfig(string SessionId, string Model, string Effort, int Version = 0);
public sealed record ModelExclusions(string SessionId, string[] Models);
public sealed record ResourceRequest(string RequestId, string SessionId, string Resource, string Purpose, int DurationSeconds = 600, bool RequireAcceptance = false, string? ResumeThreadId = null, string? SourceTurnId = null, string? RemainingWork = null);
public sealed record ResourcePermit(Job Job, string Token, string Status, DateTimeOffset? ExpiresAt, string Instructions, bool RestoreRequired = false, string? CheckpointId = null, string? OfferId = null, DateTimeOffset? OfferExpiresAt = null);
internal sealed record PermitRecord(string JobId, string Token, ResourceRequest Request, DateTimeOffset? ExpiresAt);
internal sealed record StoreWrite(string Kind, string Id, object Value);
public sealed class DispatchException(string code, string message) : Exception(message) { public string Code { get; } = code; }
