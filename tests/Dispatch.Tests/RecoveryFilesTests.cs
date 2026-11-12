using Dispatch.Core;
using Xunit;
namespace Dispatch.Tests;
public sealed class RecoveryFilesTests
{
    [Fact] public void Addin_notice_requires_both_localized_title_and_description()
    {
        Assert.True(RecoveryFiles.IsAddinPerformanceNotice(new[]{"插件加载性能","您的插件影响了 SOLIDWORKS 的启动速度。请在插件对话框内查看。","管理插件"}));
        Assert.False(RecoveryFiles.IsAddinPerformanceNotice(new[]{"插件加载性能","保存零件？"}));
        Assert.False(RecoveryFiles.IsAddinPerformanceNotice(new[]{"保存","取消"}));
    }
    [Fact] public void Generic_report_title_requires_both_report_and_restart_controls()
    {
        Assert.True(RecoveryFiles.IsCrashDialog("SOLIDWORKS",new[]{"预览报告内容","重新启动 SOLIDWORKS","完成"}));
        Assert.True(RecoveryFiles.IsCrashDialog("SOLIDWORKS",new[]{"Preview Report Contents","Restart SOLIDWORKS","Done"}));
        Assert.False(RecoveryFiles.IsCrashDialog("SOLIDWORKS",new[]{"保存","取消","重新启动"}));
        Assert.False(RecoveryFiles.IsCrashDialog("其他程序",new[]{"Preview Report Contents","Restart SOLIDWORKS"}));
    }
    [Theory]
    [InlineData("SOLIDWORKS 错误报告",true)] [InlineData("SOLIDWORKS Error Report",true)]
    [InlineData("SLDWORKS.exe 已停止工作",true)] [InlineData("SOLIDWORKS 无响应",false)]
    [InlineData("SOLIDWORKS 文件另存为",false)] [InlineData("其他软件 Error Report",false)]
    public void Only_known_crash_captions_match(string title,bool expected)=>Assert.Equal(expected,RecoveryFiles.IsCrashCaption(title));
    [Fact] public void Only_swar_is_isolated_original_models_are_kept_and_manifest_is_retained()
    {
        var root=Path.Combine(Path.GetTempPath(),"recovery-files-"+Guid.NewGuid().ToString("N"));var source=Path.Combine(root,"swxauto");Directory.CreateDirectory(source);
        foreach(var name in new[]{"自动恢复.swar","原件.sldprt","原装配.sldasm","说明.swar.txt"})File.WriteAllText(Path.Combine(source,name),"fixture");
        var incident=Guid.NewGuid().ToString("N");Assert.Equal(1,RecoveryFiles.Isolate(source,Path.Combine(root,"data"),incident,()=>true));
        Assert.Equal(3,Directory.GetFiles(source).Length);
        Assert.True(File.Exists(Path.Combine(root,"data","diagnostics","host-recovery",incident,"自动恢复.swar")));
        File.WriteAllText(Path.Combine(source,"第二次.swar"),"keep");
        Assert.Throws<OperationCanceledException>(()=>RecoveryFiles.Isolate(source,Path.Combine(root,"data"),Guid.NewGuid().ToString("N"),()=>false));
        Assert.True(File.Exists(Path.Combine(source,"第二次.swar")));
        Assert.Throws<InvalidDataException>(()=>RecoveryFiles.Isolate(source,root,"../escape",()=>true));
    }
}
