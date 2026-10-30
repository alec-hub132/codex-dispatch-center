using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dispatch.Core;

public sealed record RuntimeSource(string Home);
public sealed record RuntimeObservation(string ThreadId, string TurnId, string Model, string Effort, DateTimeOffset Timestamp, string SourceFile);
public sealed record RuntimeVerification(string Status, string Message, RuntimeObservation? Observation = null, ModelDecision? Decision = null);

public sealed partial class Scheduler
{
    public RuntimeSource SetRuntimeSource(string home)
    {
        if (!Path.IsPathFullyQualified(home) || !Directory.Exists(home))
            throw new DispatchException("INVALID_ARGUMENT", "Codex运行记录根目录必须是已存在的绝对路径。");
        lock (gate)
        {
            var source = new RuntimeSource(Path.GetFullPath(home));
            if (store.Load<RuntimeSource>("runtime-source").SingleOrDefault() != source)
            {
                store.Save("runtime-source", "local", source);
                Log.Write("RUNTIME_SOURCE_BOUND", "已绑定连接检测确定的Codex运行记录目录；仅用于只读核验，不修改会话。");
            }
            return source;
        }
    }

    public RuntimeVerification VerifyModelRuntime(string id, string token)
    {
        lock (gate)
        {
            var decision = GetDecision(id);
            if (decision.Execution is not {} plan || string.IsNullOrEmpty(token) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(plan.Token)))
                throw new DispatchException("INVALID_HANDOFF_TOKEN", "交接令牌不匹配。");
            if (decision.Status is not ("SENT_AWAITING_VERIFICATION" or "RETURN_SENT_AWAITING_VERIFICATION"))
                throw new DispatchException("INVALID_STATE", "仅在已发送且等待生效的交接方向读取运行证据。");
            if (!AuthorizedThreads().Contains(plan.ThreadId)) throw new DispatchException("UNAUTHORIZED_THREAD", "聊天交接授权已撤销。");
            if (GetSupervisor(plan.ThreadId)?.Version != plan.SupervisorVersion) throw new DispatchException("SUPERVISOR_CHANGED", "主管设置已改变，停止旧周期核验。");
            var source = store.Load<RuntimeSource>("runtime-source").SingleOrDefault();
            RuntimeObservation? observed = null;
            try { if (source is not null) observed = ReadObservation(source.Home, plan.ThreadId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            { Log.Write("RUNTIME_READ_UNAVAILABLE", "实际运行记录暂不可读取，保留待核验状态；不据此切换或重发。"); }
            if (observed is null) return new("SOURCE_UNAVAILABLE", "尚未取得本聊天的有效运行记录；未确认模型生效。");
            var since = decision.VerificationNotBefore ?? decision.CreatedAt;
            if (observed.Timestamp < since || observed.Timestamp > clock.GetUtcNow().AddSeconds(5) || observed.Model != plan.Model || observed.Effort != plan.Thinking)
                return new("AWAITING_MATCH", "运行记录尚未匹配本次交接方向和发送时间；不按目标值伪造确认，不自动重发。", observed);
            var verified = RecordHandoffCore(id, token, "runtime_verified", $"服务只读核验本聊天运行记录；turnId={observed.TurnId}，时间={observed.Timestamp:O}，来源={observed.SourceFile}。这是轮次上下文证据，不证明每个推理步骤均已切换。", observed);
            return new("VERIFIED", "实际轮次上下文与本次模型及强度一致，已登记生效。", observed, verified);
        }
    }

    public RuntimeObservation? ObserveModelRuntime(string threadId)
    {
        lock (gate)
        {
            if (!AuthorizedThreads().Contains(threadId)) throw new DispatchException("UNAUTHORIZED_THREAD", "仅可读取已授权聊天的运行元数据。");
            var source = store.Load<RuntimeSource>("runtime-source").SingleOrDefault();
            try { return source is null ? null : ReadObservation(source.Home, threadId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            { Log.Write("RUNTIME_READ_UNAVAILABLE", "授权聊天的运行元数据暂不可读取。"); return null; }
        }
    }

    private static RuntimeObservation? ReadObservation(string home, string threadId)
    {
        if (!Regex.IsMatch(threadId, "^[a-zA-Z0-9-]{1,128}$")) return null;
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions) || (File.GetAttributes(sessions) & FileAttributes.ReparsePoint) != 0) return null;
        var files = Directory.EnumerateFiles(sessions, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => Path.GetFileName(f).EndsWith("-" + threadId + ".jsonl", StringComparison.Ordinal)).Take(2).ToArray();
        if (files.Length != 1) return null;
        using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // 首行限定长度，只取session_meta编号，不保存聊天正文。
        var first = new List<byte>();
        for (var i = 0; i < 65536; i++) { var b = stream.ReadByte(); if (b < 0 || b == '\n') break; first.Add((byte)b); }
        using (var meta = JsonDocument.Parse(first.ToArray()))
            if (meta.RootElement.GetProperty("type").GetString() != "session_meta" || meta.RootElement.GetProperty("payload").GetProperty("id").GetString() != threadId) return null;
        var offset = Math.Max(0, stream.Length - 2 * 1024 * 1024);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (offset > 0) reader.ReadLine();
        var tail = reader.ReadToEnd();
        if (!tail.EndsWith('\n')) return null;
        RuntimeObservation? last = null;
        foreach (var line in tail.Split('\n'))
        {
            if (line.Length == 0 || !line.Contains("turn_context", StringComparison.Ordinal)) continue;
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("type").GetString() != "turn_context") continue;
            var value = record.RootElement.GetProperty("payload");
            var turn = value.TryGetProperty("turn_id", out var t) ? t.GetString() : null;
            var model = value.TryGetProperty("model", out var m) ? m.GetString() : null;
            var effort = value.TryGetProperty("effort", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(turn) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort)) return null;
            last = new(threadId, turn, model, effort, record.RootElement.GetProperty("timestamp").GetDateTimeOffset(), Path.GetFileName(files[0]));
        }
        return last;
    }
}
