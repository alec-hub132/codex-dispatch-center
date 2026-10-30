using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;

public sealed class ProcessTestRunner(Scheduler scheduler)
{
    public async Task Run(Job job, CancellationToken stopping)
    {
        using var process = new Process();
        var started = false;
        var native = !job.Suite.SimulationOnly;
        try
        {
            process.StartInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            if (native)
            {
                if (job.Suite.Id != "sw-readonly" || job.Suite.Runner != "solidworks-readonly") throw new InvalidOperationException("未登记的真实执行器。");
                process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "solidworks", "Runner.SolidWorks.dll"));
            }
            else
            {
                process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runner", "Runner.Mock.dll"));
                process.StartInfo.ArgumentList.Add(job.Suite.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                process.StartInfo.ArgumentList.Add(job.Suite.Mode);
            }
            process.Start();
            started = true;
            scheduler.AttachRunner(job.Id, job.Generation, process.Id, new DateTimeOffset(process.StartTime));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            JobState? stopState = null;
            while (!process.HasExited)
            {
                if (stopping.IsCancellationRequested || scheduler.Get(job.Id).CancelRequested) stopState = JobState.Cancelled;
                else if (watch.ElapsedMilliseconds >= job.Suite.TimeoutMs) stopState = JobState.TimedOut;
                if (stopState is not null)
                {
                    scheduler.Cleaning(job.Id, job.Generation);
                    process.Kill(); // 仅结束自有执行器，不结束 SolidWorks。
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    break;
                }
                await Task.Delay(25);
            }
            var stderr = await error;
            if (!string.IsNullOrWhiteSpace(stderr)) scheduler.Log.Write("RUNNER_STDERR", stderr, job.Id);
            if (stopState is not null)
            {
                scheduler.Complete(job.Id, job.Generation, stopState.Value, !native,
                    native ? "只读执行器已停止，COM 调用状态不确定，资源保持隔离。" : "模拟执行已停止且进程退出已确认");
                return;
            }
            scheduler.Cleaning(job.Id, job.Generation);
            if (process.ExitCode != 0) throw new InvalidOperationException($"执行器异常退出：{process.ExitCode}");
            using var result = JsonDocument.Parse(await output);
            var state = result.RootElement.TryGetProperty("blocked", out var block) && block.GetBoolean() ? JobState.Blocked
                : result.RootElement.GetProperty("passed").GetBoolean() ? JobState.Passed : JobState.Failed;
            scheduler.Complete(job.Id, job.Generation, state,
                result.RootElement.GetProperty("cleanupOk").GetBoolean(), result.RootElement.GetProperty("summary").GetString()!,
                result.RootElement.TryGetProperty("details", out var details) ? details.Clone() : null);
        }
        catch (Exception ex)
        {
            scheduler.Log.Write("RUNNER_FAILED", "执行器异常，保留资源隔离", job.Id, ex);
            if (started)
            {
                try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
                catch (Exception cleanup) { scheduler.Log.Write("CLEANUP_FAILED", "无法确认模拟进程退出", job.Id, cleanup); }
            }
            scheduler.Complete(job.Id, job.Generation, JobState.Failed, !started, "模拟执行器异常；请查看 bug.jsonl");
        }
    }
}
