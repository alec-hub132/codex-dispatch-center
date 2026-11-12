using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;
public sealed class HostRecoveryTests
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"dispatch-host-recovery-"+Guid.NewGuid().ToString("N"));
    private const string Exe="C:/fixture/SLDWORKS.exe";
    [Fact] public void Program_path_and_interrupted_host_control_survive_restart()
    {
        using(var s=new Scheduler(new JobStore(root)))
        {
            s.SetHostRecoverySettings(new(false,0,Exe));s.BeginHostControl();
            s.Submit(new("queued","fixture","mock-sw"));Assert.Empty(s.Claim());
        }
        using var reopened=new Scheduler(new JobStore(root));
        Assert.Equal(Exe,reopened.GetHostRecoverySettings().Executable);Assert.True(reopened.HostControlBusy);
        Assert.Empty(reopened.Claim());reopened.EndHostControl();Assert.Single(reopened.Claim());
    }
    [Fact] public void Default_is_off_and_independent_setting_is_persistent_and_versioned()
    {
        using(var s=new Scheduler(new JobStore(root)))
        {
            Assert.False(s.GetHostRecoverySettings().Enabled);
            Assert.Null(s.BeginHostRecovery(10,DateTimeOffset.UtcNow,Exe));
            var saved=s.SetHostRecoverySettings(new(true));Assert.Equal(1,saved.Version);
            Assert.Throws<DispatchException>(()=>s.SetHostRecoverySettings(new(false)));
            Assert.True(s.GetModules().ModelsEnabled);
        }
        using var reopened=new Scheduler(new JobStore(root));Assert.True(reopened.GetHostRecoverySettings().Enabled);
    }
    [Fact] public void Crash_blocks_only_solidworks_and_never_releases_original_permit()
    {
        using var s=new Scheduler(new JobStore(root));
        var p=s.RequestResource(new("old","A","solidworks","虚拟宿主",1800));s.Claim();
        s.SetHostRecoverySettings(new(true));var incident=s.BeginHostRecovery(10,DateTimeOffset.UtcNow,Exe)!;
        Assert.Equal("RecoveryRequired",s.GetResource(p.Job.Id,p.Token).Status);
        var next=s.RequestResource(new("next","B","solidworks","等待新宿主",RequireAcceptance:true));
        var app=s.RequestResource(new("other","C","app:editor","其他软件"));
        Assert.Single(s.Claim());Assert.Equal(JobState.Running,s.Get(app.Job.Id).State);
        s.ReleaseResource(p.Job.Id,p.Token,p.Job.Generation+1,true);s.Claim();
        Assert.Equal(JobState.Queued,s.Get(next.Job.Id).State);
        s.FinishHostRecovery(incident.Id,20,true,"仅虚拟空白核验");s.Claim();
        Assert.Equal(JobState.Offered,s.Get(next.Job.Id).State);
    }
    [Fact] public void Disable_prevents_further_actions_and_does_not_clear_uncertain_host()
    {
        using var s=new Scheduler(new JobStore(root));s.SetHostRecoverySettings(new(true));
        var incident=s.BeginHostRecovery(10,DateTimeOffset.UtcNow,Exe)!;
        Assert.True(s.HostRecoveryActionAllowed(incident.Id));
        s.SetHostRecoverySettings(s.GetHostRecoverySettings() with{Enabled=false});
        Assert.False(s.HostRecoveryActionAllowed(incident.Id));
        Assert.Equal("Blocked",s.FinishHostRecovery(incident.Id,null,false,"用户关闭，未继续").State);
        Assert.Null(s.BeginHostRecovery(10,incident.OldStart,Exe));
    }
    [Fact] public void Restart_does_not_replay_uncertain_launch_or_old_crash()
    {
        HostRecoveryIncident incident;
        using(var s=new Scheduler(new JobStore(root))){s.SetHostRecoverySettings(new(true));incident=s.BeginHostRecovery(10,DateTimeOffset.UtcNow,Exe)!;}
        using var reopened=new Scheduler(new JobStore(root));
        Assert.Equal("Blocked",reopened.GetHostRecovery()!.State);
        Assert.False(reopened.HostRecoveryActionAllowed(incident.Id));
        Assert.Null(reopened.BeginHostRecovery(10,incident.OldStart,Exe));
    }
    [Fact] public void Duplicate_crash_and_restart_storm_do_not_launch_again()
    {
        using var s=new Scheduler(new JobStore(root));s.SetHostRecoverySettings(new(true));
        for(var i=0;i<3;i++)
        {
            var at=DateTimeOffset.UtcNow.AddSeconds(i);var incident=s.BeginHostRecovery(10+i,at,Exe)!;
            Assert.Null(s.BeginHostRecovery(10+i,at,Exe));
            Assert.Equal("Recovering",incident.State);s.FinishHostRecovery(incident.Id,100+i,true,"虚拟检查");
            Assert.Null(s.BeginHostRecovery(10+i,at,Exe));
        }
        Assert.Equal("Blocked",s.BeginHostRecovery(13,DateTimeOffset.UtcNow.AddSeconds(4),Exe)!.State);
    }
    [Fact] public void Registered_runner_retains_its_cleanup_receipt_and_manual_priority_cannot_bypass_recovery()
    {
        using var s=new Scheduler(new JobStore(root));var running=s.Submit(new("builtin","fixture","mock-sw"));s.Claim();
        s.SetHostRecoverySettings(new(true));s.BeginHostRecovery(10,DateTimeOffset.UtcNow,Exe);
        Assert.Equal(JobState.Running,s.Get(running.Id).State);
        s.Cleaning(running.Id,1);s.Complete(running.Id,1,JobState.Failed,true,"虚拟异常但已清理");
        var next=s.Submit(new("next","fixture","mock-sw"));
        Assert.Equal("HOST_RECOVERY_REQUIRED",Assert.Throws<DispatchException>(()=>s.RequestManualDispatch(new(next.Id,0,JobState.Queued))).Code);
        Assert.Empty(s.Claim());
    }
}
