using Dispatch.Core;
using System.Text.Json;
using Xunit;
namespace Dispatch.Tests;
public sealed class HistoryPagingTests
{
    [Fact] public void Large_history_pages_preserve_every_job_and_exclude_new_arrivals()
    {
        var root=Path.Combine(Path.GetTempPath(),"dispatch-history-"+Guid.NewGuid().ToString("N"));
        using var s=new Scheduler(new JobStore(root));
        for(var i=0;i<80;i++)s.RequestResource(new("r"+i,"fixture","app:fixture",new string('测',1000)));
        Assert.True(JsonSerializer.Serialize(s.List(),Json.Options).Length>Transport.MaxChars);
        var page=s.ListPage();var upper=page.UpperSequence;var all=new List<Job>();
        s.Submit(new("later","fixture","mock-sw"));
        while(true)
        {
            Assert.True(JsonSerializer.Serialize(page,Json.Options).Length<Transport.MaxChars);
            all.AddRange(page.Items);
            if(page.NextSequence is not {} next)break;
            Assert.Equal(page.Items.Last().Sequence,next);
            page=s.ListPage(next,upper);
        }
        Assert.Equal(80,all.Count);Assert.Equal(80,all.Select(x=>x.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1,80).Select(x=>(long)x),all.Select(x=>x.Sequence));
        Assert.Throws<DispatchException>(()=>s.ListPage(-1));
    }
    [Fact] public async Task Reply_budget_is_separate_from_small_request_budget()
    {
        var line=new string('x',Transport.MaxChars+4096)+"\n";
        await Assert.ThrowsAsync<DispatchException>(()=>Transport.ReadLineAsync(new StringReader(line),default));
        Assert.Equal(line.TrimEnd('\n'),await Transport.ReadLineAsync(new StringReader(line),default,Transport.MaxReplyChars));
    }
}
