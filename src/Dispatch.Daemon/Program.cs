using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dispatch.Core;
using Dispatch.Daemon;

Console.OutputEncoding = new UTF8Encoding(false);
var instance = Transport.Argument(args, "--instance", "default");
var data = Transport.Argument(args, "--data", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDispatch"));
var pipeName = Transport.PipeName(instance);
// 句柄存在期间保持单例；不依赖 async continuation 的线程身份释放 Mutex。
using var singleton = new Mutex(false, "Local\\" + pipeName, out var created);
if (!created) { Console.Error.WriteLine("该用户的调度服务实例已经运行。"); return 2; }
var log = new Diagnostics(Path.Combine(data, "logs"));
try
{
    using var scheduler = new Scheduler(new JobStore(data));
    scheduler.UpgradeQueuedResourcePermits();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var workspace = new WorkspaceOperations(scheduler);
    var continuation = new ModelContinuationRunner(scheduler,instance);
    var api = new ServiceApi(scheduler, () => stop.CancelAfter(250), workspace.Execute, continuation.Arm);
    var runner = new ProcessTestRunner(scheduler);
    var queueResumption=new QueueResumptionRunner(scheduler,instance);
    var resumptionLoop=Task.Run(async()=>{try{await queueResumption.Run(stop.Token);}catch{stop.Cancel();throw;}});
    var connections = new System.Collections.Concurrent.ConcurrentDictionary<long, Task>();
    using var connectionSlots = new SemaphoreSlim(24);
    long connectionId = 0;
    var queueLoop = Task.Run(async () =>
    {
        var running = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var completed in running.Where(t => t.IsCompleted).ToArray()) { await completed; running.Remove(completed); }
                foreach (var job in scheduler.Claim())
                    if (job.Suite.Runner != "external-agent") running.Add(runner.Run(job, stop.Token));
                await Task.Delay(30, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { stop.Cancel(); throw; }
        finally { await Task.WhenAll(running); }
    });
    Console.WriteLine("调度服务已启动（模拟测试与 SolidWorks 只读检查）");
    try
    {
        while (!stop.IsCancellationRequested)
        {
            await connectionSlots.WaitAsync(stop.Token);
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(stop.Token); }
            catch { pipe.Dispose(); connectionSlots.Release(); throw; }
            var key = Interlocked.Increment(ref connectionId);
            var task = HandleSlot(pipe);
            connections[key] = task;
            _ = task.ContinueWith(_ => connections.TryRemove(key, out var ignored), TaskScheduler.Default);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    finally { stop.Cancel(); await Task.WhenAll(connections.Values); await Task.WhenAll(queueLoop,resumptionLoop); }
    return 0;

    async Task HandleSlot(NamedPipeServerStream pipe)
    {
        try { await Handle(pipe); }
        finally { connectionSlots.Release(); }
    }

    async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var requestId = "unknown";
            try
            {
                var line = await Transport.ReadLineAsync(reader, timeout.Token);
                if (line is null) return;
                ServiceReply reply;
                try
                {
                    var request = JsonSerializer.Deserialize<ServiceRequest>(line, Json.Options) ?? throw new JsonException();
                    requestId = request.RequestId;
                    if (request.Version != 1) throw new DispatchException("VERSION_MISMATCH", "调度协议版本不匹配。");
                    var value = await api.Execute(request.Method, request.Arguments, timeout.Token);
                    reply = new(requestId, true, JsonSerializer.SerializeToElement(value, Json.Options));
                }
                catch (DispatchException ex) { reply = new(requestId, false, Code: ex.Code, Message: ex.Message); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
                { reply = new(requestId, false, Code: "INVALID_ARGUMENT", Message: "请求参数格式无效。"); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json.Options).AsMemory(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            { log.Write("CLIENT_DISCONNECTED", "客户端断开或请求超时", error: ex); }
            catch (Exception ex)
            {
                log.Write("SERVICE_FAILED", "服务发生异常，停止派发以保护持久化状态", error: ex);
                stop.Cancel();
            }
        }
    }
}
catch (Exception ex) { log.Write("DAEMON_FATAL", "服务已停止，请先检查日志", error: ex); Console.Error.WriteLine(ex.Message); return 1; }
