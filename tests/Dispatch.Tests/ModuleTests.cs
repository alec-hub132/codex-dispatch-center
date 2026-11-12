using Dispatch.Core;
using Xunit;

public sealed class ModuleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "module-tests"), Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Scheduler Open() => new(new JobStore(root));

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Module_choices_persist_and_queue_gate_is_independent(bool queue, bool models)
    {
        using (var s = Open())
        {
            s.SetModules(new(queue, models, s.GetModules().Version));
            if (queue) Assert.NotNull(s.Submit(new("new", "window", "mock-sw")));
            else Assert.Equal("QUEUE_DISABLED", Assert.Throws<DispatchException>(() => s.Submit(new("new", "window", "mock-sw"))).Code);
        }
        using var restored = Open();
        Assert.Equal(queue, restored.GetModules().QueueEnabled);
        Assert.Equal(models, restored.GetModules().ModelsEnabled);
        if (!models)
            Assert.Equal("MODELS_DISABLED", Assert.Throws<DispatchException>(() => restored.RequestModel(new("model", "window", "worker", "medium", "理由", "限定任务"))).Code);
    }

    [Fact]
    public void Disable_stops_new_grants_but_never_unlocks_or_prevents_cleanup()
    {
        using var s = Open();
        var a = s.RequestResource(new("a", "A", "desktop", "许可测试"));
        s.Claim(); a = s.GetResource(a.Job.Id, a.Token);
        var b = s.RequestResource(new("b", "B", "desktop", "等待测试"));
        var old = s.GetModules(); s.SetModules(old with { QueueEnabled = false });
        Assert.Empty(s.Claim());
        Assert.Equal("Granted", s.GetResource(a.Job.Id, a.Token).Status);
        Assert.Equal("QUEUE_DISABLED", Assert.Throws<DispatchException>(() => s.RequestResource(new("c", "C", "desktop", "新许可"))).Code);
        Assert.Equal("Released", s.ReleaseResource(a.Job.Id, a.Token, a.Job.Generation, true).Status);
        Assert.Empty(s.Claim());
        Assert.Equal("Queued", s.GetResource(b.Job.Id, b.Token).Status);
        s.SetModules(s.GetModules() with { QueueEnabled = true });
        Assert.Single(s.Claim());
    }

    [Fact]
    public void Stale_settings_cannot_overwrite_another_windows_choice()
    {
        using var s = Open(); var old = s.GetModules();
        var saved = s.SetModules(old with { QueueEnabled = false });
        Assert.Equal("SETTINGS_CHANGED", Assert.Throws<DispatchException>(() => s.SetModules(old with { ModelsEnabled = false })).Code);
        Assert.Equal(saved, s.GetModules());
    }
}
