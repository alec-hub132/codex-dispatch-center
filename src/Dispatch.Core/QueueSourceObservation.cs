using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core;

// 只保存元数据及读取边界摘要，不持久化运行日志正文。
public sealed record QueueSourceCursor(string LeaseId,string Home,string File,string IdentityHash,long Offset,string BoundaryHash,
    string LatestTurnId,bool Finished,bool Blocked,bool CaughtUp,DateTimeOffset ReadyUntil,bool WakeMessageSeen=false);
public sealed record QueueWake(string LeaseId,string ThreadId,string SourceTurnId,string OfferId,int Generation,string State,string? NewTurnId=null)
{public string Id=>LeaseId+"-"+Generation;}

public sealed partial class Scheduler
{
    public QueueSourceCursor[] GetQueueSourceStates(){lock(gate)return store.Load<QueueSourceCursor>("queue-source").ToArray();}
    public QueueWake[] GetQueueWakes(){lock(gate)return store.Load<QueueWake>("queue-wake").ToArray();}
    private QueueWake? FindQueueWake(string lease)=>GetQueueWakes().Where(w=>w.LeaseId==lease).OrderBy(w=>w.Generation).LastOrDefault();
    private QueueSourceCursor? FindQueueSource(string lease)=>GetQueueSourceStates().SingleOrDefault(c=>c.LeaseId==lease);
    private static readonly UTF8Encoding QueueUtf8=new(false,true);
    private static string QueueHash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static FileStream OpenQueueSource(string file)=>new(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    private static string Boundary(FileStream file,long offset)
    {
        var start=Math.Max(0,offset-256);var bytes=new byte[(int)(offset-start)];file.Position=start;file.ReadExactly(bytes);return QueueHash(bytes);
    }
    private static string QueueIdentity(FileStream file,string thread)
    {
        file.Position=0;var head=new List<byte>();
        for(var i=0;i<65536;i++){var b=file.ReadByte();if(b<0)throw new InvalidDataException();if(b=='\n')break;head.Add((byte)b);}
        using var meta=JsonDocument.Parse(head.ToArray());var r=meta.RootElement;
        if(r.GetProperty("type").GetString()!="session_meta" || r.GetProperty("payload").GetProperty("id").GetString()!=thread)throw new InvalidDataException();
        return QueueHash(head.ToArray());
    }
    private string LocateQueueSource(QueueResumeBinding binding,string home)
    {
        var folder=Path.Combine(home,"sessions");
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
        var files=Directory.EnumerateFiles(folder,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint})
            .Where(f=>Path.GetFileName(f)==binding.SourceFile && Path.GetFileName(f).EndsWith("-"+binding.ThreadId+".jsonl",StringComparison.Ordinal)).Take(2).ToArray();
        if(files.Length!=1)throw new InvalidDataException();return Path.GetFullPath(files[0]);
    }
    private QueueSourceCursor CreateQueueSource(QueueResumeBinding binding)
    {
        try
        {
            var home=store.Load<RuntimeSource>("runtime-source").Single().Home;var path=LocateQueueSource(binding,home);
            using var file=OpenQueueSource(path);var identity=QueueIdentity(file,binding.ThreadId);var length=file.Length;
            var start=Math.Max(0,length-2*1024*1024);file.Position=start;
            if(start>0){while(file.Position<length && file.ReadByte()!='\n'){}start=file.Position;}
            var bytes=new byte[(int)(length-start)];file.ReadExactly(bytes);
            if(bytes.Length==0 || bytes[^1]!='\n')throw new InvalidDataException();
            string? turn=null;var finished=false;var blocked=false;
            foreach(var line in QueueUtf8.GetString(bytes).Split('\n'))
            {
                if(line.Length==0)continue;using var doc=JsonDocument.Parse(line);var r=doc.RootElement;var p=r.GetProperty("payload");
                if(r.GetProperty("type").GetString()=="turn_context"){turn=p.GetProperty("turn_id").GetString();finished=false;blocked=false;}
                if(r.GetProperty("type").GetString()=="event_msg" && p.TryGetProperty("turn_id",out var t) && t.GetString()==turn)
                {if(p.GetProperty("type").GetString()=="task_complete")finished=true;if(p.GetProperty("type").GetString()=="task_aborted")blocked=true;}
            }
            if(turn!=binding.SourceTurnId)throw new InvalidDataException();
            return new(binding.LeaseId,home,path,identity,length,Boundary(file,length),turn!,finished,blocked,true,clock.GetUtcNow().AddSeconds(15));
        }
        catch(Exception ex) when(QueueReadError(ex)){throw new DispatchException("QUEUE_SOURCE_MISMATCH","绑定时实际源轮或日志边界已改变，未创建任务；请重新核对。");}
    }
    private static bool QueueReadError(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or DecoderFallbackException;
    private QueueSourceCursor RefreshQueueSource(QueueResumeBinding binding,QueueSourceCursor cursor)
    {
        if(cursor.Blocked)return cursor;
        try
        {
            var home=store.Load<RuntimeSource>("runtime-source").SingleOrDefault()?.Home;
            if(home!=cursor.Home || LocateQueueSource(binding,home)!=cursor.File)throw new InvalidDataException();
            using var file=OpenQueueSource(cursor.File);
            if(file.Length<cursor.Offset || QueueIdentity(file,binding.ThreadId)!=cursor.IdentityHash || Boundary(file,cursor.Offset)!=cursor.BoundaryHash)throw new InvalidDataException();
            // 每次最多16MiB；只推进完整行。半行不当作空闲，下一次从原边界重读。
            var length=file.Length;var count=(int)Math.Min(length-cursor.Offset,16*1024*1024);var bytes=new byte[count];file.Position=cursor.Offset;file.ReadExactly(bytes);
            var end=Array.LastIndexOf(bytes,(byte)'\n');
            if(end<0)return cursor with{CaughtUp=count==0,Blocked=count==16*1024*1024};
            var next=cursor;var consumed=0;var wake=FindQueueWake(binding.LeaseId);
            while(consumed<=end)
            {
                var nl=Array.IndexOf(bytes,(byte)'\n',consumed);if(nl<0)break;
                using var doc=JsonDocument.Parse(bytes.AsMemory(consumed,nl-consumed));var r=doc.RootElement;
                var type=r.GetProperty("type").GetString();var p=r.GetProperty("payload");
                var isUser=type=="response_item" && p.TryGetProperty("type",out var k) && k.GetString()=="message" && p.TryGetProperty("role",out var role) && role.GetString()=="user";
                var turn=type=="turn_context"?p.GetProperty("turn_id").GetString():
                    type=="event_msg" && p.TryGetProperty("type",out var et) && et.GetString()=="task_started"?p.GetProperty("turn_id").GetString():null;
                // IPC可能已启动新轮，但回执还在落库；保留读取边界，等真实回执后再解析该段。
                if((isUser || turn is not null && turn!=binding.SourceTurnId) && wake?.State is "DISPATCHING" or "SENDING")break;
                if(turn is not null)
                {
                    if(turn!=binding.SourceTurnId && (wake?.State!="SENT" || turn!=wake.NewTurnId))next=next with{Blocked=true};
                    if(turn!=next.LatestTurnId)next=next with{LatestTurnId=turn,Finished=false};
                }
                if(type=="event_msg" && p.TryGetProperty("turn_id",out var tid) && tid.GetString()==next.LatestTurnId)
                {
                    var evt=p.GetProperty("type").GetString();if(evt=="task_complete")next=next with{Finished=true};
                    if(evt=="task_aborted")next=next with{Blocked=true};
                }
                if(isUser)
                {
                    var text=p.TryGetProperty("content",out var content) && content.ValueKind==JsonValueKind.Array?
                        string.Concat(content.EnumerateArray().Select(c=>c.TryGetProperty("text",out var t)?t.GetString():"")):"";
                    if(!next.WakeMessageSeen && wake?.State=="SENT" && text==QueueWakePrompt(binding.LeaseId))next=next with{WakeMessageSeen=true};
                    else next=next with{Blocked=true};
                }
                consumed=nl+1;if(next.Blocked)break;
            }
            var offset=cursor.Offset+consumed;
            return next with{Offset=offset,BoundaryHash=Boundary(file,offset),CaughtUp=offset==length};
        }
        catch(Exception ex) when(QueueReadError(ex))
        {Log.Write("QUEUE_SOURCE_CHANGED","排队源记录不可核对或已改变，暂停旧任务自动接续。",binding.LeaseId);return cursor with{Blocked=true,CaughtUp=false};}
    }
    private bool QueueSourceAllowsOffer(string lease)
    {
        if(GetQueueBindings().All(b=>b.LeaseId!=lease))return true;
        var c=FindQueueSource(lease);var wake=FindQueueWake(lease);
        return c is {Blocked:false,CaughtUp:true} && wake?.State!="STOPPED_NO_RETRY" && (c.Finished || c.ReadyUntil>clock.GetUtcNow());
    }
    public void ObserveQueueResumptions()
    {
        lock(gate)
        {
            CheckResourceOffers();
            foreach(var b in GetQueueBindings())
            {
                var job=Find(b.LeaseId);if(job.State is not (JobState.Queued or JobState.Offered or JobState.WaitingForClient))continue;
                var c=FindQueueSource(b.LeaseId);
                if(c is null){Save(job with{State=JobState.WaitingForClient,Summary="缺少绑定读取边界，等待原窗口显式确认；不猜测空闲。"});continue;}
                var next=RefreshQueueSource(b,c);if(next!=c)store.Save("queue-source",b.LeaseId,next);
                // 15秒心跳只控制能否创建新预留。既有预留按自身30秒期限结束；
                // 聊天开始处理自己的接续消息不应把预留提前撤销。
                if(job.State==JobState.Offered && !next.Blocked && QueueBindingAllowed(b.LeaseId))continue;
                if(!QueueBindingAllowed(b.LeaseId) || !QueueSourceAllowsOffer(b.LeaseId))
                {
                    if(job.State!=JobState.WaitingForClient)Save(job with{State=JobState.WaitingForClient,Summary=next.Blocked?"原聊天有新任务、停止或日志变化，旧任务等待确认。":"原聊天尚在执行或记录未完整，未占用软件，等待接续。"});
                    continue;
                }
                var offer=FindResourceOffer(b.LeaseId);
                var lastWake=FindQueueWake(b.LeaseId);
                var canContinue=lastWake is null || lastWake.State=="SENT" && lastWake.NewTurnId==b.SourceTurnId && next.LatestTurnId==b.SourceTurnId;
                if(job.State==JobState.WaitingForClient && next.Finished && canContinue && job.Generation<3 && GetModules().QueueEnabled && !paused &&
                   (offer is null || clock.GetUtcNow()>=offer.ExpiresAt.AddSeconds(2)))
                    Save(job with{State=JobState.Queued,Sequence=++sequence,Summary="原聊天已正常结束本轮，原任务回到队尾等待接单。"});
            }
        }
    }
    private void RequireQueueAcceptanceRuntime(string lease)
    {
        var b=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==lease);if(b is null)return;
        var c=FindQueueSource(lease)??throw new DispatchException("SOURCE_UNAVAILABLE","缺少原任务的运行证据。");
        var wake=FindQueueWake(lease);
        if(wake?.State is "DISPATCHING" or "SENDING")throw new DispatchException("QUEUE_RECEIPT_PENDING","原聊天续跑回执尚未确认，请短暂等待；不重发或重做。");
        c=RefreshQueueSource(b,c);store.Save("queue-source",lease,c);
        if(c.Blocked || !c.CaughtUp || c.Finished || (c.LatestTurnId!=b.SourceTurnId && (wake?.State!="SENT" || c.LatestTurnId!=wake.NewTurnId)))
            throw new DispatchException("QUEUE_SOURCE_MISMATCH","当前实际轮次不属于原任务的可接单轮，未授予操作许可。");
    }
    public QueueWake? ClaimQueueWake()
    {
        lock(gate)
        {
            ObserveQueueResumptions();if(paused || !GetModules().QueueEnabled)return null;
            foreach(var job in jobs.Values.Where(j=>j.State==JobState.Offered).OrderBy(j=>j.Sequence))
            {
                var b=GetQueueBindings().SingleOrDefault(b=>b.LeaseId==job.Id);var c=FindQueueSource(job.Id);
                var previous=FindQueueWake(job.Id);
                if(b is null || c is not {Blocked:false,CaughtUp:true,Finished:true} || !QueueBindingAllowed(job.Id) ||
                    previous is not null && (previous.Generation==job.Generation || previous.State!="SENT" || previous.NewTurnId!=b.SourceTurnId) ||
                    GetQueueWakes().Any(w=>w.ThreadId==b.ThreadId && (w.State is "DISPATCHING" or "SENDING" || w.LeaseId!=job.Id && w.State=="SENT" &&
                        Find(w.LeaseId).State is JobState.Queued or JobState.Offered or JobState.WaitingForClient or JobState.Running or JobState.Cleanup or JobState.RecoveryRequired)))continue;
                var offer=FindResourceOffer(job.Id)!;var wake=new QueueWake(job.Id,b.ThreadId,b.SourceTurnId,offer.Id,job.Generation,"DISPATCHING");
                store.SaveBatch(new("queue-wake",wake.Id,wake),new("queue-source",job.Id,c with{WakeMessageSeen=false}));
                Log.Write("QUEUE_WAKE_DISPATCHING","本接单代次的原聊天单次唤醒已登记；尚未授予操作许可。",job.Id);return wake;
            }
            return null;
        }
    }
    public bool BeginQueueWakeSend(string lease,string token,string? wakeId=null)
    {
        lock(gate)
        {
            AuthorizePermit(lease,token);ObserveQueueResumptions();var wake=FindQueueWake(lease);
            if(wake?.State!="DISPATCHING" || wakeId is not null && wake.Id!=wakeId)return false;
            var c=FindQueueSource(lease);var job=Find(lease);var offer=FindResourceOffer(lease);
            var allowed=!paused && GetModules().QueueEnabled && QueueBindingAllowed(lease) && job.State==JobState.Offered &&
                offer?.Id==wake.OfferId && c is {Blocked:false,CaughtUp:true,Finished:true} && c.LatestTurnId==wake.SourceTurnId;
            store.Save("queue-wake",wake.Id,wake with{State=allowed?"SENDING":"STOPPED_NO_RETRY"});return allowed;
        }
    }
    public void RecordQueueWake(string lease,string? newTurnId,string? wakeId=null)
    {
        lock(gate)
        {
            var wake=FindQueueWake(lease)??throw new DispatchException("NOT_FOUND","接续登记不存在。");
            if(wake.State is not ("DISPATCHING" or "SENDING") || wakeId is not null && wake.Id!=wakeId)return;
            var valid=wake.State=="SENDING" && newTurnId!=wake.SourceTurnId && System.Text.RegularExpressions.Regex.IsMatch(newTurnId??"","^[a-zA-Z0-9-]{1,128}\\z");
            store.Save("queue-wake",wake.Id,wake with{State=valid?"SENT":"STOPPED_NO_RETRY",NewTurnId=valid?newTurnId:null});
            Log.Write(valid?"QUEUE_WAKE_SENT":"QUEUE_WAKE_STOPPED","接续发送状态已记录，发送回执不等于原窗口接单；结果不明不重发。",lease);
        }
    }
    private void InterruptQueueWakes()
    {
        foreach(var w in GetQueueWakes().Where(w=>w.State is "DISPATCHING" or "SENDING"))
            store.Save("queue-wake",w.Id,w with{State="STOPPED_NO_RETRY"});
    }
    public string QueueWakePrompt(string lease)
    {
        lock(gate)
        {
            var b=GetQueueBindings().Single(b=>b.LeaseId==lease);
            return $"这是用户已在排队工具单独授权的原聊天接续提醒。原许可编号：{lease}。只接续原任务，不新建申请、不重复已完成操作、不切换模型。"+
                "从本聊天原记录取原许可token（不得输出），先get_module_settings、get_queue_binding和get_resource。Offered只表示预留，必须用最新offerId/generation调用accept_resource，返回Granted才执行。"+
                "若WaitingForClient，核对本聊天实际新轮次后retry_resource原许可，再查询接单；不按旧通知操作。若取消、授权撤销、状态不明或用户已有新任务则停止旧任务，不重发唤醒。"+
                "完成操作先停止并清理，再用原token和当前generation释放；不自动恢复现场，不扩大原授权。剩余步骤："+b.RemainingWork;
        }
    }
    // 仅供包内固定发送器写入本机私有计划；不返回MCP/界面，也不记录令牌。
    public object BuildQueueWakePlan(string lease,string instance)
    {
        lock(gate)
        {
            var wake=FindQueueWake(lease)??throw new DispatchException("NOT_FOUND","接续登记不存在。");
            if(wake.State!="DISPATCHING")throw new DispatchException("INVALID_STATE","本次唤醒已处理，不重新启动。");
            return new{leaseId=lease,wakeId=wake.Id,threadId=wake.ThreadId,sourceTurnId=wake.SourceTurnId,token=permits[lease].Token,
                prompt=QueueWakePrompt(lease),instance,dataRoot=Root};
        }
    }
}
