using System.Text.Json;
using Dispatch.Core;
namespace Dispatch.Desktop;

public sealed partial class MainViewModel
{
    private HostRecoverySettings hostRecoverySaved=new();
    private bool hostPathLoaded;
    public string SolidWorksPath{get;set;}="";
    public AsyncCommand DetectSolidWorksPathCommand{get;private set;}=null!;
    public AsyncCommand SaveSolidWorksPathCommand{get;private set;}=null!;
    public AsyncCommand OpenSolidWorksCommand{get;private set;}=null!;
    public AsyncCommand CloseSolidWorksCommand{get;private set;}=null!;
    public AsyncCommand DetectSolidWorksErrorsCommand{get;private set;}=null!;
    public bool HostRecoveryEnabled=>hostRecoverySaved.Enabled;
    public string HostRecoverySummary=>!HostRecoveryEnabled?"已关闭 · 不自动关闭错误窗或重新启动SolidWorks":!QueueEnabled?"已保存开启 · 测试排队关闭期间暂停自动处理":"已开启 · 崩溃时关闭对应错误窗并单次启动空白实例";
    public string HostRecoveryProgress{get;private set;}="尚未读取宿主恢复状态";
    public AsyncCommand ToggleHostRecoveryCommand{get;private set;}=null!;
    public AsyncCommand RecheckHostRecoveryCommand{get;private set;}=null!;
    private void InitializeHostRecovery()
    {
        async Task SavePath()
        {
            hostRecoverySaved=(await backend.Call("set_host_recovery_settings",hostRecoverySaved with{Executable=SolidWorksPath.Trim()})).Deserialize<HostRecoverySettings>(Json.Options)!;
            SetNotice("SolidWorks程序路径已保存。");
        }
        async Task Control(string method)
        {
            var result=await backend.Call(method);SetNotice(result.GetProperty("message").GetString()!);
        }
        DetectSolidWorksPathCommand=Command(async()=>
        {
            var result=await backend.Call("detect_solidworks_path");SolidWorksPath=result.GetProperty("executable").GetString()!;Changed(nameof(SolidWorksPath));await SavePath();
        });
        SaveSolidWorksPathCommand=Command(SavePath);
        OpenSolidWorksCommand=Command(()=>Control("open_solidworks"));
        CloseSolidWorksCommand=Command(()=>Control("close_solidworks"));
        DetectSolidWorksErrorsCommand=Command(()=>Control("detect_solidworks_errors"));
        ToggleHostRecoveryCommand=Command(async()=>
        {
            await backend.Call("set_host_recovery_settings",hostRecoverySaved with{Enabled=!HostRecoveryEnabled});
            await Refresh();SetNotice(HostRecoveryEnabled?"已保存并开启崩溃自动处理。正常关闭不重启，恢复文件不打开。":"已保存并关闭后续自动处理，已启动的SolidWorks保留。");
        });
        RecheckHostRecoveryCommand=Command(async()=>
        {
            await backend.Call("recheck_host_recovery");await Refresh();SetNotice("已只读复核宿主状态，原任务仍按清理和接单流程继续。");
        });
    }
    private async Task RefreshHostRecovery()
    {
        var view=await backend.Call("get_host_recovery_settings");
        hostRecoverySaved=view.GetProperty("settings").Deserialize<HostRecoverySettings>(Json.Options)!;
        if(!hostPathLoaded){SolidWorksPath=hostRecoverySaved.Executable;hostPathLoaded=true;Changed(nameof(SolidWorksPath));}
        var incident=view.GetProperty("incident");
        HostRecoveryProgress=incident.ValueKind==JsonValueKind.Null?"尚无崩溃处理记录。开启后监测当前唯一实例，正常关闭或尚未打开软件时不启动新实例。":incident.Deserialize<HostRecoveryIncident>(Json.Options)!.Message;
        if(view.TryGetProperty("controlPending",out var pending) && pending.GetBoolean())HostRecoveryProgress="宿主管理结果待确认，请处理提示后点击重新检查宿主。";
        else if(incident.ValueKind==JsonValueKind.Null && view.TryGetProperty("monitor",out var monitor) && monitor.ValueKind==JsonValueKind.String)HostRecoveryProgress=monitor.GetString()!;
        Changed(nameof(HostRecoveryEnabled));Changed(nameof(HostRecoverySummary));Changed(nameof(HostRecoveryProgress));
    }
}
