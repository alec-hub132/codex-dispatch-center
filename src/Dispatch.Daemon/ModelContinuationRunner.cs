using System.Diagnostics;
using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Daemon;
public sealed class ModelContinuationRunner(Scheduler scheduler,string instance)
{
    public object Arm(string id,string token)
    {
        var ticket=scheduler.PrepareModelContinuation(id,token);
        if(!scheduler.ClaimModelContinuation(ticket.Id))return scheduler.GetModelContinuation(ticket.Id);
        try
        {
            var plan=scheduler.BuildModelContinuationPlan(ticket,instance);
            var folder=Path.Combine(scheduler.Root,"diagnostics");Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,"model-continuation-plan-"+ticket.Id+".json");
            using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(stream,plan,Json.Options);
            var info=new ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true};
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"continuation","Start-CodexContinuation.mjs"));info.ArgumentList.Add(file);
            var process=Process.Start(info)??throw new InvalidOperationException("无法启动固定续跑执行器。");
            var result=scheduler.RecordModelContinuation(ticket.Id,"WAITING",process.Id);
            _=Observe(process,ticket,plan);return result;
        }
        catch(Exception ex)
        {
            scheduler.Log.Write("MODEL_CONTINUATION_LAUNCH_FAILED","续跑执行器未确认启动，不自动重试。",id,ex);
            return scheduler.RecordModelContinuation(ticket.Id,"STOPPED_NO_RETRY");
        }
    }
    private async Task Observe(Process process,ModelContinuation ticket,object plan)
    {
        try
        {
            await process.WaitForExitAsync();
            var thread=JsonSerializer.SerializeToElement(plan,Json.Options).GetProperty("threadId").GetString();
            var journal=Path.Combine(scheduler.Root,"diagnostics",$"continuation-{thread}-{ticket.SourceTurnId}.json");
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(journal));
            var state=doc.RootElement.GetProperty("status").GetString();
            scheduler.RecordModelContinuation(ticket.Id,state=="STARTED_AWAITING_RUNTIME"?state:"STOPPED_NO_RETRY");
        }
        catch { scheduler.RecordModelContinuation(ticket.Id,"STOPPED_NO_RETRY"); }
        finally{process.Dispose();}
    }
}
