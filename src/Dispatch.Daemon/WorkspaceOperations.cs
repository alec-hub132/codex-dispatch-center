using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;

public sealed class WorkspaceOperations(Scheduler scheduler)
{
    public async Task<object> Execute(string method, JsonElement args, CancellationToken stopping)
    {
        var lease = args.GetProperty("leaseId").GetString() ?? "";
        var token = args.GetProperty("token").GetString() ?? "";
        var generation = args.GetProperty("generation").GetInt32();
        if (method == "capture_workspace")
        {
            var requestId = args.GetProperty("requestId").GetString() ?? "";
            var existing = scheduler.BeginWorkspaceCapture(lease, token, generation, requestId,
                args.GetProperty("completedWork").GetString() ?? "", args.GetProperty("remainingWork").GetString() ?? "", args.GetProperty("safePointConfirmed").GetBoolean());
            if (existing is not null) return existing;
            try
            {
                var output = await Run("capture", null, stopping);
                return scheduler.FinishWorkspaceCapture(lease, requestId, JsonSerializer.Deserialize<WorkspaceScene>(output, Json.Options) ?? throw new InvalidDataException("现场快照无效。"));
            }
            catch (Exception ex) { Failed(lease, ex); throw new DispatchException("WORKSPACE_CAPTURE_FAILED", "现场未成功保存；原许可保持隔离，请先查看bug日志并确认宿主操作已经停止。"); }
        }
        var checkpoint = scheduler.BeginWorkspaceRestore(lease, token, generation, args.GetProperty("checkpointId").GetString() ?? "");
        if (checkpoint.State == "Restored") return checkpoint;
        try
        {
            var output = await Run("restore", checkpoint.Scene, stopping);
            var result = JsonSerializer.Deserialize<WorkspaceRestoreResult>(output, Json.Options) ?? throw new InvalidDataException("恢复回读结果无效。");
            return scheduler.FinishWorkspaceRestore(lease, result);
        }
        catch (Exception ex) { Failed(lease, ex); throw new DispatchException("WORKSPACE_RESTORE_FAILED", "现场恢复未完成确认，资源保持隔离，不继续原业务操作。"); }
    }
    private void Failed(string lease, Exception ex)
    {
        scheduler.Log.Write("WORKSPACE_RUNNER_FAILED", "现场执行器失败，未自动释放宿主许可。", lease, ex);
        scheduler.FailWorkspaceOperation(lease, "现场执行器失败或超时，请核对原宿主操作和bug日志后确认清理。");
    }
    private static async Task<string> Run(string mode, WorkspaceScene? scene, CancellationToken stopping)
    {
        using var process = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "workspace", "Runner.Workspace.dll"));
        process.StartInfo.ArgumentList.Add(mode);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            if (scene is not null) await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(scene, Json.Options).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            // 仅处理本次创建的执行器，绝不结束SolidWorks。COM调用结果不明由许可隔离保留。
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            throw;
        }
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("现场执行器异常退出：" + stderr[..Math.Min(stderr.Length, 4000)]);
        var text = await output;
        if (text.Length > 100 * 1024) throw new InvalidDataException("现场数据超过允许大小。");
        return text;
    }
}
