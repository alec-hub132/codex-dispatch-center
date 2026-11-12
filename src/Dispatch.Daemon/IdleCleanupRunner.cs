using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;

// 统一无活动观察器，最多一个短时清理发送器，不直接操作或关闭宿主。
public sealed class IdleCleanupRunner(Scheduler scheduler,string instance)
{
    public async Task Run(CancellationToken stop)
    {
        var running=new List<Task>();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                foreach(var task in running.Where(t=>t.IsCompleted).ToArray()){await task;running.Remove(task);}

                if(running.Count<1 && scheduler.ClaimIdleCleanup() is {} wake)running.Add(Send(wake,stop));
                await Task.Delay(1000,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally{await Task.WhenAll(running);}
    }
    private async Task Send(IdleCleanupTicket wake,CancellationToken stop)
    {
        Process? process=null;string? newTurn=null;
        try
        {
            var folder=Path.Combine(scheduler.Root,"diagnostics");Directory.CreateDirectory(folder);
            var planFile=Path.Combine(folder,"idle-cleanup-plan-"+wake.Id+".json");
            using(var file=new FileStream(planFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                JsonSerializer.Serialize(file,scheduler.BuildIdleCleanupPlan(wake.LeaseId,instance),Json.Options);
            var info=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"continuation","Send-QueueWake.mjs"));info.ArgumentList.Add(planFile);
            process=Process.Start(info)??throw new InvalidOperationException();
            // 丢弃子进程输出，诊断只写关联编号及状态，避免原始响应进入日志。
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(22));
            await process.WaitForExitAsync(timeout.Token);await Task.WhenAll(stdout,stderr);
            var journal=Path.Combine(folder,"idle-cleanup-"+wake.Id+".json");
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(journal,stop));var r=doc.RootElement;
            if(process.ExitCode==0 && r.GetProperty("status").GetString()=="SENT" && r.GetProperty("wakeId").GetString()==wake.Id && r.GetProperty("leaseId").GetString()==wake.LeaseId &&
                r.GetProperty("threadId").GetString()==wake.ThreadId && r.GetProperty("sourceTurnId").GetString()==wake.SourceTurnId)
                newTurn=r.GetProperty("newTurnId").GetString();
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {scheduler.Log.Write("IDLE_CLEANUP_UNCONFIRMED","原窗口清理唤醒未确认送达，不重发，不自动解锁。",wake.LeaseId);}
        finally
        {
            try
            {
                if(process is not null && !process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
            }
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {scheduler.Log.Write("IDLE_CLEANUP_SENDER_EXIT_UNCONFIRMED","仅自有发送器的退出未确认；不重复发送，不操作宿主。",wake.LeaseId);newTurn=null;}
            finally{process?.Dispose();scheduler.RecordIdleCleanupSend(wake.LeaseId,wake.Id,newTurn);}
        }
    }
}
