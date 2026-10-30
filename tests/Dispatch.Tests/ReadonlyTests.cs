using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class ReadonlyTests
{
    [Fact]
    public void Native_readonly_suite_is_not_reported_as_simulation()
    {
        var suite = SuiteCatalog.Get("sw-readonly");
        Assert.False(suite.SimulationOnly);
        Assert.Equal("solidworks-readonly", suite.Runner);
        Assert.Contains("sw-main", suite.Resources);
        Assert.DoesNotContain("desktop-ui", suite.Resources);
    }

    [Fact]
    public void Environment_block_does_not_count_as_pass_or_keep_clean_resource_locked()
    {
        var root = Path.Combine(Path.Combine(Path.GetTempPath(), "CodexDispatch.Tests", "dispatch-tests"), Guid.NewGuid().ToString("N"));
        using var scheduler = new Scheduler(new JobStore(root));
        scheduler.Submit(new("native-test", "只读测试", "sw-readonly"));
        var native = Assert.Single(scheduler.Claim());
        scheduler.Submit(new("following", "只读测试", "mock-sw"));
        scheduler.Complete(native.Id, native.Generation, JobState.Blocked, true, "请先手动打开 SolidWorks");
        Assert.Equal(JobState.Blocked, scheduler.Get(native.Id).State);
        Assert.Single(scheduler.Claim());
    }
}
