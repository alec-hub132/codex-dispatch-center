using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Dispatch.Core;

// 仅轮次/调用编号及读取边界摘要；不保存聊天、工具输出或清理令牌。
public sealed record IdleActivity(string Home,string ThreadId,string File,string Identity,long Offset,string Boundary,
    string? TurnId,DateTimeOffset? LastActivity,bool Active,bool CaughtUp,bool Unknown,string[] PendingCalls);
public static class IdleActivityReader
{
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static string Boundary(FileStream f,long offset)
    {var start=Math.Max(0,offset-256);var b=new byte[(int)(offset-start)];f.Position=start;f.ReadExactly(b);return Hash(b);}
    public static IdleActivity Read(string home,string thread,IdleActivity? previous,DateTimeOffset now)
    {
        var next=previous??new(home,thread,"","",0,"",null,null,true,false,false,[]);
        if(next.Unknown)return next;
        try
        {
            if(next.Home!=home || next.ThreadId!=thread || !System.Text.RegularExpressions.Regex.IsMatch(thread,"^[a-zA-Z0-9-]{1,128}\\z"))throw new InvalidDataException();
            var folder=Path.Combine(home,"sessions");if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException();
            var files=Directory.EnumerateFiles(folder,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint})
                .Where(f=>Path.GetFileName(f).EndsWith("-"+thread+".jsonl",StringComparison.Ordinal)).Take(2).ToArray();
            if(files.Length!=1 || next.File!="" && next.File!=files[0])throw new InvalidDataException();
            using var f=new FileStream(files[0],FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var first=new List<byte>();var complete=false;
            for(var i=0;i<65536;i++){var b=f.ReadByte();if(b<0)break;if(b=='\n'){complete=true;break;}first.Add((byte)b);}
            if(!complete)throw new InvalidDataException();using(var meta=JsonDocument.Parse(first.ToArray()))
                if(meta.RootElement.GetProperty("type").GetString()!="session_meta"||meta.RootElement.GetProperty("payload").GetProperty("id").GetString()!=thread)throw new InvalidDataException();
            var identity=Hash(first.ToArray());
            if(previous is not null && (identity!=next.Identity || f.Length<next.Offset || Boundary(f,next.Offset)!=next.Boundary))throw new InvalidDataException();
            next=next with{File=files[0],Identity=identity};var length=f.Length;
            var count=(int)Math.Min(length-next.Offset,16*1024*1024);var bytes=new byte[count];f.Position=next.Offset;f.ReadExactly(bytes);
            if(count==0 && previous is {CaughtUp:true})return previous;
            var consumed=0;var pending=next.PendingCalls.ToHashSet(StringComparer.Ordinal);
            while(consumed<count)
            {
                var end=Array.IndexOf(bytes,(byte)'\n',consumed);if(end<0)break;
                if(end==consumed){consumed=end+1;continue;}
                using var doc=JsonDocument.Parse(bytes.AsMemory(consumed,end-consumed));var r=doc.RootElement;consumed=end+1;
                var type=r.GetProperty("type").GetString();if(type=="session_meta")continue;var p=r.GetProperty("payload");
                if(type is not ("turn_context" or "event_msg" or "response_item"))continue;
                if(!r.TryGetProperty("timestamp",out var time) || !time.TryGetDateTimeOffset(out var at) || at>now.AddSeconds(5))throw new InvalidDataException();
                if(next.LastActivity is null || at>next.LastActivity)next=next with{LastActivity=at};
                var kind=p.TryGetProperty("type",out var kt)?kt.GetString():null;
                if(type=="turn_context" || type=="event_msg" && kind=="task_started")
                {
                    var turn=p.GetProperty("turn_id").GetString();if(string.IsNullOrWhiteSpace(turn))throw new InvalidDataException();
                    if(turn!=next.TurnId){pending.Clear();next=next with{TurnId=turn,Active=true};}
                    else if(kind=="task_started")next=next with{Active=true};
                }
                if(type=="event_msg" && kind is "task_complete" or "task_aborted" && p.GetProperty("turn_id").GetString()==next.TurnId)
                    next=next with{Active=false};
                if(type=="response_item")
                {
                    if(kind=="message" && p.TryGetProperty("role",out var role) && role.GetString()=="user")next=next with{Active=true};
                    if(kind is "function_call" or "custom_tool_call")
                    {var call=p.GetProperty("call_id").GetString();if(string.IsNullOrEmpty(call))throw new InvalidDataException();pending.Add(call);}
                    if(kind is "function_call_output" or "custom_tool_call_output")
                    {if(p.TryGetProperty("call_id",out var call) && call.GetString() is {} id)pending.Remove(id);}
                }
            }
            var offset=next.Offset+consumed;
            return next with{Offset=offset,Boundary=Boundary(f,offset),CaughtUp=offset==length,Unknown=consumed==0&&count==16*1024*1024,PendingCalls=pending.ToArray()};
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {return next with{Unknown=true,CaughtUp=false};}
    }
}
