using System.Text.Json;
namespace Dispatch.Core;

public sealed record JobPage(Job[] Items,long UpperSequence,long? NextSequence);
public sealed partial class Scheduler
{
    public JobPage ListPage(long afterSequence=0,long? upperSequence=null)
    {
        lock(gate)
        {
            var upper=upperSequence??sequence;
            if(afterSequence<0 || upper<afterSequence || upper>sequence)
                throw new DispatchException("INVALID_ARGUMENT","任务分页游标无效，请重新刷新。");
            var rows=new List<Job>();var size=0;
            foreach(var job in jobs.Values.Where(j=>j.Sequence>afterSequence && j.Sequence<=upper).OrderBy(j=>j.Sequence))
            {
                var length=JsonSerializer.Serialize(job,Json.Options).Length;
                if(rows.Count>0 && (rows.Count>=100 || size+length>64*1024))
                    return new(rows.ToArray(),upper,rows[^1].Sequence);
                if(length>64*1024)throw new DispatchException("HISTORY_ITEM_TOO_LARGE","单条任务记录过大，请查看该任务诊断记录。");
                rows.Add(Snapshot(job));size+=length+1;
            }
            return new(rows.ToArray(),upper,null);
        }
    }
}
