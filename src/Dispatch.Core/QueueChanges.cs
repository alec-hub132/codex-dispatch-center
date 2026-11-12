using System.Text.Json;

namespace Dispatch.Core;

public sealed record QueueChangePage(Job[] Items, string Epoch, long UpperRevision, long? NextRevision, bool Reset);

public sealed partial class Scheduler
{
    private readonly string queueEpoch = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, long> jobRevisions = new(StringComparer.Ordinal);
    private long queueRevision;

    // 只在数据库事务提交之后调用。版本域随进程改变，重启时客户端必须重读。
    private void RememberJob(Job job)
    {
        jobs[job.Id] = job;
        jobRevisions[job.Id] = ++queueRevision;
    }

    public QueueChangePage ListChanges(string? epoch = null, long afterRevision = 0, long? upperRevision = null)
    {
        lock (gate)
        {
            if (afterRevision < 0) throw new DispatchException("INVALID_ARGUMENT", "同步版本不能为负数。");
            var reset = epoch != queueEpoch;
            if (reset) { afterRevision = 0; upperRevision = null; }
            var upper = upperRevision ?? queueRevision;
            if (upper < afterRevision || upper > queueRevision)
                throw new DispatchException("INVALID_ARGUMENT", "同步版本无效，请重新读取队列。");
            var rows = new List<Job>();
            var size = 0;
            long last = afterRevision;
            foreach (var pair in jobRevisions.Where(p => p.Value > afterRevision && p.Value <= upper).OrderBy(p => p.Value))
            {
                var job = jobs[pair.Key];
                var length = JsonSerializer.Serialize(job, Json.Options).Length;
                if (rows.Count > 0 && (rows.Count >= 100 || size + length > 64 * 1024))
                    return new(rows.ToArray(), queueEpoch, upper, last, reset);
                if (length > 64 * 1024) throw new DispatchException("HISTORY_ITEM_TOO_LARGE", "单条任务记录过大，请查看该任务诊断记录。");
                rows.Add(Snapshot(job));
                size += length + 1;
                last = pair.Value;
            }
            return new(rows.ToArray(), queueEpoch, upper, null, reset);
        }
    }

    public (int ActivePermits, int Queued, int Recovery, int Offered, int WaitingForClient) GetQueueCounts()
    {
        lock (gate)
        {
            var active = 0; var queued = 0; var recovery = 0; var offered = 0; var waiting = 0;
            foreach (var job in jobs.Values)
            {
                if (job.State == JobState.Running && job.Suite.Runner == "external-agent") active++;
                if (job.State == JobState.Queued) queued++;
                if (job.State == JobState.RecoveryRequired) recovery++;
                if (job.State == JobState.Offered) offered++;
                if (job.State == JobState.WaitingForClient) waiting++;
            }
            return (active, queued, recovery, offered, waiting);
        }
    }
}
