using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Dispatch.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Large_protocol_lines_are_read_in_blocks_and_keep_the_next_message()
    {
        var line = new string('测', 120 * 1024);
        using var reader = new CountingReader(line + "\r\n第二条\n第三条");
        var watch = Stopwatch.StartNew();
        Assert.Equal(line, await Transport.ReadLineAsync(reader, default));
        var calls = reader.ReadCalls;
        output.WriteLine($"120Ki字符读取次数 {calls}，耗时 {watch.Elapsed.TotalMilliseconds:F2}ms");
        Assert.Equal("第二条", await Transport.ReadLineAsync(reader, default));
        Assert.Equal("第三条", await Transport.ReadLineAsync(reader, default));
        Assert.Null(await Transport.ReadLineAsync(reader, default));
        Assert.True(calls <= 64, $"单条120Ki字符消息进行了{calls}次读取，应按块读取且保留下一条消息。");
    }

    [Fact]
    public async Task Buffered_protocol_keeps_limits_crlf_and_cancellation()
    {
        using var reader = new StringReader("1234\n12345\n");
        Assert.Equal("1234", await Transport.ReadLineAsync(reader, default, 4));
        Assert.Equal("MESSAGE_TOO_LARGE", (await Assert.ThrowsAsync<DispatchException>(
            () => Transport.ReadLineAsync(reader, default, 4))).Code);
        using var empty = new StringReader("\n\r\n");
        Assert.Equal("", await Transport.ReadLineAsync(empty, default));
        Assert.Equal("", await Transport.ReadLineAsync(empty, default));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transport.ReadLineAsync(new StringReader("消息\n"), stop.Token));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_once_without_corrupting_the_next_message()
    {
        using var reader = new StringReader(new string('x', 20000) + "\n合法消息\n");
        await Assert.ThrowsAsync<DispatchException>(() => Transport.ReadLineAsync(reader, default, 1024));
        Assert.Equal("合法消息", await Transport.ReadLineAsync(reader, default, 1024));
        Assert.Null(await Transport.ReadLineAsync(reader, default, 1024));
    }

    [Fact]
    public void Current_permit_queries_do_not_deserialize_all_historical_offers()
    {
        var root = Path.Combine(Path.GetTempPath(), "dispatch-performance-" + Guid.NewGuid().ToString("N"));
        using var scheduler = new Scheduler(new JobStore(root));
        var permit = scheduler.RequestResource(new("current", "fixture", "app:perf", "隔离虚拟性能检查", RequireAcceptance: true));
        scheduler.Claim();
        long Measure()
        {
            for (var i = 0; i < 5; i++) scheduler.GetResource(permit.Job.Id, permit.Token);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++) scheduler.GetResource(permit.Job.Id, permit.Token);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var small = Measure();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "dispatch.db")))
        {
            db.Open();
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO records(kind,id,body) VALUES('resource-offer',$id,$body)";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var body = command.Parameters.Add("$body", SqliteType.Text);
            for (var i = 0; i < 1500; i++)
            {
                id.Value = "finished-" + i;
                body.Value = JsonSerializer.Serialize(new ResourceOffer((string)id.Value, "old-offer", 1, DateTimeOffset.UtcNow.AddDays(-1)), Json.Options);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var watch = Stopwatch.StartNew();
        var large = Measure();
        output.WriteLine($"20次许可查询分配，少量历史 {small}B，1500条历史 {large}B，后者耗时 {watch.Elapsed.TotalMilliseconds:F2}ms");
        Assert.True(large <= small * 3 + 128 * 1024, $"历史预留造成查询分配增长，基线{small}B，历史增加后{large}B。");
        var offered = scheduler.GetResource(permit.Job.Id, permit.Token);
        var accepted = scheduler.AcceptResource(permit.Job.Id, permit.Token, offered.OfferId!, offered.Job.Generation);
        Assert.Equal("Granted", accepted.Status);
        Assert.Equal("OK", scheduler.ReleaseResource(permit.Job.Id, permit.Token, accepted.Job.Generation, true).Job.CleanupStatus);
    }

    private sealed class CountingReader(string text) : StringReader(text)
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
