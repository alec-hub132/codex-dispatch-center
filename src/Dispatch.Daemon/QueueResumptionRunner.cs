using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;

// 一个服务循环长期观察全部绑定，最多两个短时发送器；不为每个等待项保留Node进程。
public sealed class QueueResumptionRunner(Scheduler scheduler,string instance)
{
    public async Task Run(CancellationToken stop)
    {
        var running=new List<Task>();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                foreach(var task in running.Where(t=>t.IsCompleted).ToArray()){await task;running.Remove(task);}
                scheduler.ObserveQueueResumptions();
                if(running.Count<2 && scheduler.ClaimQueueWake() is {} wake)running.Add(Send(wake,stop));
                await Task.Delay(1000,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally{await Task.WhenAll(running);}
    }
    private async Task Send(QueueWake wake,CancellationToken stop)
    {
        Process? process=null;string? newTurn=null;
        try
        {
            var folder=Path.Combine(scheduler.Root,"diagnostics");Directory.CreateDirectory(folder);
            var planFile=Path.Combine(folder,"queue-wake-plan-"+wake.Id+".json");
            using(var file=new FileStream(planFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                JsonSerializer.Serialize(file,scheduler.BuildQueueWakePlan(wake.LeaseId,instance),Json.Options);
            var info=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"continuation","Send-QueueWake.mjs"));info.ArgumentList.Add(planFile);
            process=Process.Start(info)??throw new InvalidOperationException();
            // 丢弃子进程输出，诊断只写关联编号及状态，避免原始响应进入日志。
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(22));
            await process.WaitForExitAsync(timeout.Token);await Task.WhenAll(stdout,stderr);
            var journal=Path.Combine(folder,"queue-wake-"+wake.Id+".json");
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(journal,stop));var r=doc.RootElement;
            if(process.ExitCode==0 && r.GetProperty("status").GetString()=="SENT" && r.GetProperty("wakeId").GetString()==wake.Id && r.GetProperty("leaseId").GetString()==wake.LeaseId &&
                r.GetProperty("threadId").GetString()==wake.ThreadId && r.GetProperty("sourceTurnId").GetString()==wake.SourceTurnId)
                newTurn=r.GetProperty("newTurnId").GetString();
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {scheduler.Log.Write("QUEUE_WAKE_UNCONFIRMED","原聊天接续未确认送达，不重发、不重做原测试。",wake.LeaseId);}
        finally
        {
            if(process is not null){if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();}
            scheduler.RecordQueueWake(wake.LeaseId,newTurn,wake.Id);
        }
    }
}
