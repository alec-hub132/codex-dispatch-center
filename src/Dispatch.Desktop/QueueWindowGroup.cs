using Dispatch.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Dispatch.Desktop;

public sealed class ResetCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        var next=values.ToArray();if(Items.SequenceEqual(next))return;
        CheckReentrancy();Items.Clear();foreach(var item in next)Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class QueueWindowGroup(string key) : Observable
{
    public bool IsGroup=>true;
    public bool IsSelected {get;set;}
    public ResetCollection<JobRow> Children {get;}=[];
    public string Key {get;}=key;
    public string Title {get;private set;}="";
    public string Subtitle {get;private set;}="";
    public string Identity {get;private set;}="";
    public int Rank {get;private set;}
    public long LatestSequence {get;private set;}
    private bool expanded=true;
    public bool IsExpanded {get=>expanded;set{expanded=value;Changed();}}
    public void Update(QueueOwner owner,Job[] jobs)
    {
        var previous=(Title,Subtitle,Identity,Rank,LatestSequence);
        Title=owner.NameStatus=="UNBOUND"?owner.Name+"（未关联窗口）":owner.Name;
        Identity=owner.ThreadId is {} id?"聊天编号："+id:"来源标签："+owner.Name+"；没有真实聊天关联，未猜测归属。";
        var active=jobs.Where(j=>j.State is JobState.Running or JobState.Cleanup).ToArray();
        var recovery=jobs.Count(j=>j.State==JobState.RecoveryRequired);
        var waiting=jobs.Count(j=>j.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient);
        Rank=active.Length>0?0:recovery>0?1:waiting>0?2:3;LatestSequence=jobs.Max(j=>j.Sequence);
        var state=active.Length>0?"正在占用 "+string.Join("、",active.Select(j=>JobRow.ResourceNames(j.Suite.Resources)).Distinct()):recovery>0?$"{recovery}项隔离待清理":waiting>0?$"{waiting}项等待／待接单":"提交均已结束";
        Subtitle=$"{state}　·　共{jobs.Length}次提交　·　最近 {jobs.Max(j=>j.CreatedAt).ToLocalTime():yyyy-MM-dd HH:mm:ss}"+(owner.ThreadId is {} thread?$"　·　{thread[..Math.Min(thread.Length,8)]}":"");
        if(previous!=(Title,Subtitle,Identity,Rank,LatestSequence))
            foreach(var name in new[]{nameof(Title),nameof(Subtitle),nameof(Identity),nameof(Rank),nameof(LatestSequence)})Changed(name);
    }
}
