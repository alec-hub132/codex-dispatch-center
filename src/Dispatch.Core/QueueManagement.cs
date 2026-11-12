using Microsoft.Data.Sqlite;

namespace Dispatch.Core;

public sealed record QueueOwner(string JobId,string GroupKey,string? ThreadId,string Name,string NameStatus);
public sealed record QueueManualRelease(string JobId,int Generation,JobState ExpectedState,bool OperationsStopped,bool OwnedResourcesCleaned,string Reason);
public sealed record QueueManualReleaseAudit(string JobId,int Generation,JobState Before,JobState After,DateTimeOffset At,string Reason,string Actor="desktop-user");

public sealed partial class Scheduler
{
    private string? lastQueueNameError;
    public QueueOwner[] GetQueueOwners(string[] jobIds)
    {
        if(jobIds.Length>100)throw new DispatchException("INVALID_ARGUMENT","每次最多查询100项窗口归属。");
        QueueOwner[] owners;string? home;
        lock(gate)
        {
            home=store.Load<RuntimeSource>("runtime-source").SingleOrDefault()?.Home;
            var explicitOwners=store.Load<IdleCleanupOwnerBinding>("idle-cleanup-owner").ToDictionary(o=>o.LeaseId,o=>o.ThreadId);
            var bindings=GetQueueBindings().ToDictionary(b=>b.LeaseId,b=>b.ThreadId);
            owners=jobIds.Distinct(StringComparer.Ordinal).Select(id=>
            {
                var job=Find(id);var thread=explicitOwners.GetValueOrDefault(id)??(permits.TryGetValue(id,out var permit)?permit.Request.OwnerThreadId:null)??bindings.GetValueOrDefault(id);
                var label=job.Request.SessionId.StartsWith("Codex-",StringComparison.Ordinal)?job.Request.SessionId[6..]:job.Request.SessionId;
                if(thread is null && Guid.TryParseExact(label,"D",out _))thread=label;
                if(thread is not null && Guid.TryParseExact(thread,"D",out var guid))thread=guid.ToString("D");
                return new QueueOwner(id,thread is null?"legacy:"+job.Request.SessionId:"thread:"+thread,thread,
                    thread is null?job.Request.SessionId:"未读取名称的窗口",thread is null?"UNBOUND":"UNAVAILABLE");
            }).ToArray();
        }
        // 仅只读必要元数据，不锁住调度器等待 Codex 数据库，也不读取聊天正文。
        var names=NativeThreadNames.Read(home,owners.Where(o=>o.ThreadId is not null).Select(o=>o.ThreadId!).Distinct().ToArray(),out var error);
        lock(gate)
        {
            if(error!=lastQueueNameError){if(error is not null)Log.Write("QUEUE_NAMES_UNAVAILABLE","窗口名称暂不可读取（"+error+"），继续显示原队列和真实编号。");lastQueueNameError=error;}
        }
        return owners.Select(o=>o.ThreadId is {} id && names.TryGetValue(id,out var n)?o with{Name=n.Name,NameStatus=n.Source}:o).ToArray();
    }

    // 仅本机桌面管理接口；不注册成 MCP 工具。确认来自用户在界面所选的单次提交。
    public Job ManualReleaseResource(QueueManualRelease request)
    {
        lock(gate)
        {
            try
            {
                CheckPermitExpiry();CheckResourceOffers();var job=Find(request.JobId);
                if(job.Generation!=request.Generation || job.State!=request.ExpectedState)
                    throw new DispatchException("QUEUE_ITEM_CHANGED","该记录的状态或代次已改变，请刷新后重新核对，未执行解除。");
                Validate(request.Reason,"手动解除说明",500);
                var waiting=job.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient;
                if(!waiting && (job.Suite.Runner!="external-agent" || job.State is not (JobState.Running or JobState.Cleanup or JobState.RecoveryRequired)))
                    throw new DispatchException("INVALID_STATE","此记录无需手动解除；固定套件执行中请使用安全取消并等待清理。");
                if(!waiting)
                {
                    if(!request.OperationsStopped || !request.OwnedResourcesCleaned)
                        throw new DispatchException("CLEANUP_CONFIRMATION_REQUIRED","请确认相关操作已停止且自建测试资源已清理，再解除该许可。");
                    if(HasRegisteredRunningProcess(job))throw new DispatchException("PROCESS_STILL_RUNNING","已登记测试进程仍运行或身份不明，先结束该段测试并完成清理。");
                    EnsureNoWorkspaceOperation(job.Id,true);
                }
                var next=job with{State=waiting?JobState.Cancelled:JobState.Released,EndedAt=clock.GetUtcNow(),CancelRequested=waiting,
                    CleanupStatus=waiting?job.CleanupStatus:"OK",Summary=waiting?"用户在桌面撤回了这一次尚未获准的提交。":"用户在桌面确认操作停止、测试资源已清理，手动释放了这一次许可；不代表测试通过。"};
                var audit=new StoreWrite("queue-manual-release",Guid.NewGuid().ToString("N"),new QueueManualReleaseAudit(job.Id,job.Generation,job.State,next.State,clock.GetUtcNow(),request.Reason));
                if(permits.TryGetValue(job.Id,out var permit))SavePermit(next,permit,audit:audit);
                else {store.SaveBatch(new("job",next.Id,next),audit);RememberJob(next);}
                Log.Write("QUEUE_MANUAL_RELEASE",next.Summary,job.Id);return Snapshot(next);
            }
            catch(DispatchException ex){Log.Write("QUEUE_MANUAL_RELEASE_REJECTED","手动解除未执行："+ex.Code,request.JobId);throw;}
        }
    }
}

internal static class NativeThreadNames
{
    internal sealed record NativeName(string Name,string Source);
    public static Dictionary<string,NativeName> Read(string? home,string[] ids,out string? error)
    {
        error=null;var result=new Dictionary<string,NativeName>(StringComparer.Ordinal);
        if(ids.Length==0)return result;
        try
        {
            var path=home is null?null:Directory.EnumerateFiles(home,"state_*.sqlite",SearchOption.TopDirectoryOnly)
                .Select(p=>(Path:p,Version:int.TryParse(Path.GetFileNameWithoutExtension(p)[6..],out var v)?v:0)).Where(p=>p.Version>0)
                .OrderByDescending(p=>p.Version).Select(p=>p.Path).FirstOrDefault();
            if(path is null){error="MISSING_METADATA";return result;}
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());db.Open();
            using var schema=db.CreateCommand();schema.CommandText="PRAGMA table_info(threads)";
            var columns=new HashSet<string>();using(var reader=schema.ExecuteReader())while(reader.Read())columns.Add(reader.GetString(1));
            if(!columns.Contains("id") || !columns.Contains("title")){error="UNSUPPORTED_SCHEMA";return result;}
            using var command=db.CreateCommand();command.CommandTimeout=1;
            var keys=ids.Select((id,i)=>{var key="$id"+i;command.Parameters.AddWithValue(key,id);return key;}).ToArray();
            command.CommandText=$"SELECT id,{(columns.Contains("name")?"name":"NULL")},title FROM threads WHERE id IN ({string.Join(',',keys)})";
            using var rows=command.ExecuteReader();
            while(rows.Read())
            {
                var name=rows.IsDBNull(1)?null:Clean(rows.GetString(1));var title=rows.IsDBNull(2)?null:Clean(rows.GetString(2));
                if(name is not null || title is not null)result[rows.GetString(0)]=new(name??title!,name is null?"TITLE":"NAME");
            }
        }
        catch(Exception ex) when(ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {error=ex is SqliteException?"METADATA_BUSY_OR_INVALID":"METADATA_UNAVAILABLE";}
        return result;
    }
    private static string? Clean(string text)
    {
        var first=text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(first)?null:first[..Math.Min(first.Length,100)];
    }
}
