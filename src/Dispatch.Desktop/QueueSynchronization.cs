using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Desktop;

public sealed partial class MainViewModel
{
    private string? queueSyncEpoch;
    private long queueSyncRevision;
    private DateTimeOffset? lastOwnerRefresh;
    private Dictionary<string, QueueOwner> queueOwnerCache = new(StringComparer.Ordinal);

    private async Task RefreshQueueRows(bool forceNames)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var changes = new Dictionary<string, Job>(StringComparer.Ordinal);
        var epoch = queueSyncEpoch;
        var cursor = queueSyncRevision;
        long? upper = null;
        var firstPage = true;
        var reset = false;
        while (true)
        {
            var page = (await backend.Call("list_queue_changes", new { epoch, afterRevision = cursor, upperRevision = upper }))
                .Deserialize<QueueChangePage>(Json.Options)!;
            if (firstPage) { reset = page.Reset; epoch = page.Epoch; upper = page.UpperRevision; if (reset) cursor = 0; firstPage = false; }
            else if (page.Reset || page.Epoch != epoch || page.UpperRevision != upper)
            {
                queueSyncEpoch = null;
                throw new InvalidOperationException("同步期间服务已重启，下次刷新将重新读取队列。");
            }
            foreach (var job in page.Items) changes[job.Id] = job;
            if (page.NextRevision is not {} next) break;
            if (next <= cursor) throw new InvalidOperationException("队列同步游标未前进，请查看服务日志。");
            cursor = next;
        }

        var refreshNames = forceNames || reset || lastOwnerRefresh is null || DateTimeOffset.UtcNow - lastOwnerRefresh >= TimeSpan.FromSeconds(30);
        if (changes.Count == 0 && !refreshNames)
        {
            queueSyncEpoch = epoch;
            queueSyncRevision = upper!.Value;
            return;
        }
        var nextRows = reset ? new Dictionary<string, Job>(StringComparer.Ordinal) : Jobs.ToDictionary(row => row.Id, row => row.Raw, StringComparer.Ordinal);
        foreach (var pair in changes) nextRows[pair.Key] = pair.Value;
        var owners = reset ? new Dictionary<string, QueueOwner>(StringComparer.Ordinal) : new Dictionary<string, QueueOwner>(queueOwnerCache, StringComparer.Ordinal);
        var ownerIds = refreshNames ? nextRows.Keys.ToArray() : changes.Keys.ToArray();
        foreach (var ids in ownerIds.Chunk(100))
        {
            var metadata = (await backend.Call("get_queue_owners", new { jobIds = ids })).Deserialize<QueueOwner[]>(Json.Options)!;
            foreach (var owner in metadata) owners[owner.JobId] = owner;
        }
        if (nextRows.Keys.Any(id => !owners.ContainsKey(id))) throw new InvalidOperationException("窗口归属尚未读取完整，请刷新重试。");
        var rows = nextRows.Values.OrderBy(job => job.Sequence).ToArray();
        var selected = SelectedJob?.Id;
        var renamedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(job => owners[job.Id].GroupKey))
        {
            if (!windowGroups.TryGetValue(group.Key, out var display)) windowGroups[group.Key] = display = new(group.Key);
            var oldTitle = display.Title;
            display.Update(owners[group.First().Id], group.ToArray());
            if (display.Title != oldTitle) renamedGroups.Add(group.Key);
        }
        var previousRows = Jobs.ToDictionary(row => row.Id);
        Jobs.ReplaceAll(rows.Select(job =>
        {
            var group = windowGroups[owners[job.Id].GroupKey];
            if (previousRows.TryGetValue(job.Id, out var row) && ReferenceEquals(row.WindowGroup, group))
            { row.Update(job, renamedGroups.Contains(group.Key)); return row; }
            return new JobRow(job, group);
        }));
        foreach (var group in Jobs.GroupBy(row => row.WindowGroup!))
            group.Key.Children.ReplaceAll(group.OrderByDescending(row => row.Raw.CreatedAt).ThenByDescending(row => row.Raw.Sequence));
        QueueGroups.ReplaceAll(Jobs.Select(row => row.WindowGroup!).Distinct().OrderBy(group => group.Rank).ThenByDescending(group => group.LatestSequence));
        Changed(nameof(QueueWindowSummary));
        SelectedJob = Jobs.FirstOrDefault(row => row.Id == selected) ?? SortQueueRows(Jobs).FirstOrDefault();
        RebuildQueueConsole();
        queueOwnerCache = owners;
        queueSyncEpoch = epoch;
        queueSyncRevision = upper!.Value;
        if (refreshNames) lastOwnerRefresh = DateTimeOffset.UtcNow;
        if (watch.ElapsedMilliseconds >= 1000)
            backend.Log.Write("QUEUE_SYNC_SLOW", $"队列同步耗时{watch.ElapsedMilliseconds}ms，变化{changes.Count}项，窗口名称核对{ownerIds.Length}项。");
    }
}
