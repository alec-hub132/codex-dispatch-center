using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Desktop;

public sealed class DesktopBackend(string instance, string data, string targetHome)
{
    public string Instance { get; } = instance;
    public string DataRoot { get; } = System.IO.Path.GetFullPath(data);
    public Diagnostics Log => new(System.IO.Path.Combine(DataRoot, "logs"));
    public Process? OwnedService { get; private set; }

    public async Task<JsonElement> Call(string method, object? args = null)
    {
        ServiceReply reply;
        try { reply = await Transport.Call(Instance, method, JsonSerializer.SerializeToElement(args ?? new { }, Json.Options)); }
        catch(DispatchException ex) { Log.Write("DESKTOP_API_FAILED","读取服务接口失败："+method+"；错误码："+ex.Code,error:ex);throw; }
        if (!reply.Ok) throw new DispatchException(reply.Code ?? "ERROR", reply.Message ?? "请求失败");
        return reply.Result!.Value;
    }

    public async Task StartService()
    {
        try { await Call("get_health"); return; }
        catch (DispatchException ex) when (ex.Code == "TEST_CENTER_OFFLINE") { }
        var info = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var value in new[] { System.IO.Path.Combine(AppContext.BaseDirectory, "service", "Dispatch.Daemon.dll"), "--instance", Instance, "--data", DataRoot }) info.ArgumentList.Add(value);
        OwnedService = Process.Start(info) ?? throw new InvalidOperationException("后台服务未能启动。");
        for (var i = 0; i < 15; i++)
        {
            try { await Call("get_health"); return; }
            catch (DispatchException ex) when (ex.Code == "TEST_CENTER_OFFLINE") { await Task.Delay(100); }
            if (OwnedService.HasExited) throw new InvalidOperationException("后台服务启动失败，请查看日志。");
        }
        throw new TimeoutException("后台服务启动超时。");
    }

    public async Task<JsonElement> Connection(string action, string module = "all")
    {
        if (action == "Disconnect")
        {
            var jobs = (await Call("list_tests")).Deserialize<Job[]>(Json.Options)!;
            if ((module is "all" or "queue") && jobs.Any(j => j.State is JobState.Queued or JobState.Running or JobState.Cleanup or JobState.RecoveryRequired))
                throw new InvalidOperationException("排队工具仍有待执行、占用或待清理任务，请先处理后再解除接入。关闭模块开关可以停止新任务并保留清理通道。");
            var decisions = (await Call("list_model_decisions")).Deserialize<ModelDecision[]>(Json.Options)!;
            if ((module is "all" or "models") && decisions.Any(d => d.Execution is not null && d.Status is not ("RETURN_VERIFIED" or "CANCELLED_BEFORE_SEND" or "USER_OVERRIDE")))
                throw new InvalidOperationException("模型工具仍有未完成交接周期，请先返回主管或处理恢复状态。关闭模块开关可以停止新委派并保留返回通道。");
        }
        if (module == "all") action = action switch { "Connect" => "Split", "Inspect" => "InspectModules", "Disconnect" => "DisconnectModules", _ => action };
        var info = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var value in new[] { System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "connection.py"), "--action", action,
            "--bridge", System.IO.Path.Combine(AppContext.BaseDirectory, "mcp", "Dispatch.McpBridge.dll"), "--instance", Instance,
            "--backups", System.IO.Path.Combine(DataRoot, "connections"), "--module", module }) info.ArgumentList.Add(value);
        if (!string.IsNullOrWhiteSpace(targetHome)) { info.ArgumentList.Add("--home"); info.ArgumentList.Add(targetHome); }
        var userHome = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userHome)) { info.ArgumentList.Add("--user-home"); info.ArgumentList.Add(userHome); }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动连接安装器，请检查 Python 3.11。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); throw new TimeoutException("连接操作超时，请在诊断页检查恢复记录。"); }
        var output = await stdout;
        var error = await stderr;
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("连接安装器没有返回结果。" + (string.IsNullOrWhiteSpace(error) ? "" : "请检查 Python 版本。"));
        using var doc = JsonDocument.Parse(output);
        if (process.ExitCode != 0) throw new InvalidOperationException(doc.RootElement.TryGetProperty("message", out var message) ? message.GetString() : "连接失败");
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> InspectModelAccess(string home)
    {
        if (!System.IO.Path.IsPathFullyQualified(home)) throw new InvalidOperationException("请先检测Codex连接，确认配置目录。");
        var report = System.IO.Path.Combine(DataRoot, "diagnostics", "model-access.json");
        var info = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "Inspect-CodexModelAccess.py"),
            "--home", home, "--output", report }) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动模型接入检测。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); throw new TimeoutException("模型接入检测超时，请查看日志。"); }
        await stdout; await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("模型接入检测失败，请检查Python与Codex安装。");
        using var doc = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(report));
        return doc.RootElement.Clone();
    }
}
