using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dispatch.Desktop;

public static class DesktopSmoke
{
    public static async Task Run(DesktopBackend backend, string output)
    {
        Directory.CreateDirectory(output);
        var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("烟测失败：" + name); passed.Add(name); }
        try
        {
            Directory.CreateDirectory(backend.DataRoot);
            File.WriteAllText(Path.Combine(backend.DataRoot,"model-handoff-authorization.json"), """{"threads":["smoke-supervisor-thread"]}""");
            var vm = new MainViewModel(backend, autoRefresh: false);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1120, Height = 780 };
            window.Show(); // 测试自己的离屏 WPF 窗口，不操作其他软件或桌面焦点。
            await vm.Initialize();
            Check(vm.ServiceOnline, "界面启动真实隔离服务");
            Check(vm.QueueEnabled && vm.ModelsEnabled, "旧数据默认保留两个模块的启用状态");
            await vm.ToggleQueueCommand.Run();
            Check(!vm.QueueEnabled && vm.ModelsEnabled, "只关闭排队不影响模型调度");
            await vm.ToggleModelsCommand.Run();
            Check(!vm.QueueEnabled && !vm.ModelsEnabled, "两个模块可全部关闭");
            using(var moduleReopened=new MainViewModel(backend,autoRefresh:false))
            {
                await moduleReopened.Initialize();
                Check(!moduleReopened.QueueEnabled && !moduleReopened.ModelsEnabled,"重开界面保留两个模块的独立开关");
            }
            await vm.ToggleQueueCommand.Run();
            Check(vm.QueueEnabled && !vm.ModelsEnabled, "只启用排队不影响关闭的模型调度");
            await vm.ToggleModelsCommand.Run();
            await vm.ConnectCommand.Run();
            Check(vm.ConnectionSummary.Contains("重载"), "连接状态提示原窗口重载");
            Check(vm.IsConnectionReady && vm.ConnectionGlyph == "✓" && vm.ConnectionColor == "#18734B", "连接就绪时显示绿色勾图标");
            Check(vm.BridgeSummary.Contains("可用"), "界面读取桥握手结果");
            Check(vm.QueueConnectionState.Contains("已登记") && vm.ModelsConnectionState.Contains("已登记"),"两个独立MCP入口均通过界面接入");
            await vm.DisconnectQueueCommand.Run();
            Check(vm.QueueConnectionState.Contains("未接入") && vm.ModelsConnectionState.Contains("已登记"),"单独解除排队接入保留模型工具");
            await vm.ConnectQueueCommand.Run();
            Check(vm.QueueConnectionState.Contains("已登记") && vm.ModelsConnectionState.Contains("已登记"),"可单独重新接入排队工具");
            window.QueueResumePanel.IsExpanded=true;
            var queueThread="smoke-queue-thread";
            var queueSessions=Path.Combine(vm.HomePath,"sessions");Directory.CreateDirectory(queueSessions);
            File.WriteAllText(Path.Combine(queueSessions,"rollout-"+queueThread+".jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id=queueThread}})+"\n"+
                JsonSerializer.Serialize(new{type="turn_context",timestamp=DateTimeOffset.UtcNow,payload=new{turn_id="smoke-source",model="fixture",effort="medium"}})+"\n");
            vm.QueueResumeThread=queueThread;Check(!vm.QueueResumeEnabled,"新聊天默认没有排队接续授权");
            vm.QueueResumeEnabled=true;await vm.SaveQueueResumeCommand.Run();
            Check(vm.QueueResumeSummary.Contains("允许原排队任务接续"),"界面单独保存排队接续授权");
            vm.QueueResumeEnabled=false;await vm.Refresh();Check(!vm.QueueResumeEnabled,"自动刷新不覆盖尚未保存的授权选择");
            using(var queueReopened=new MainViewModel(backend,autoRefresh:false))
            {await queueReopened.Initialize();queueReopened.QueueResumeThread=queueThread;Check(queueReopened.QueueResumeEnabled,"重开界面保留已保存的排队接续授权");}
            vm.QueueResumeEnabled=true;
            await vm.ModelAccessCommand.Run();
            Check(File.Exists(Path.Combine(backend.DataRoot, "diagnostics", "model-access.json")) && vm.ModelAccessSummary.Contains("原窗口"), "只读模型检测生成报告且不宣称切换成功");
            Check(vm.SelectedSupervisorModel is null && vm.SupervisorSummary.Contains("尚未"), "主管未配置时不替用户默认选择模型");
            vm.SelectedSupervisorModel=vm.SupervisorModels.First();
            var chosen=vm.SelectedSupervisorModel.Id;
            var effort=vm.SelectedSupervisorModel.Efforts.Last();vm.SelectedSupervisorEffort=effort;
            await vm.SaveSupervisorCommand.Run();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            Check((string?)window.SupervisorEffortSelector.SelectedItem==effort,"主管强度下拉框显示与已保存配置一致");
            var settings=await backend.Call("get_supervisor_settings");
            Check(settings.GetProperty("configs")[0].GetProperty("effort").GetString()==effort && vm.SupervisorSummary.Contains(chosen), "主管模型与强度通过界面保存并回读");
            vm.ExclusionChoices.First().IsExcluded=true;
            await vm.SaveExclusionsCommand.Run();
            Check((await backend.Call("get_health")).GetProperty("modelExclusions")[0].GetProperty("models")[0].GetString()==chosen,"排除名单通过界面保存并在健康接口公开给主管");
            using(var reopened=new MainViewModel(backend,autoRefresh:false))
            {
                await reopened.Initialize();
                Check(reopened.SelectedSupervisorModel?.Id==chosen && reopened.SelectedSupervisorEffort==effort,"重新打开界面恢复已保存主管配置");
                Check(reopened.ExclusionChoices.Single(c=>c.Id==chosen).IsExcluded,"重新打开界面保留排除模型勾选");
            }
            await vm.SubmitCommand.Run();
            for (var i = 0; i < 30 && vm.CompletedCount == 0; i++) { await Task.Delay(100); await vm.Refresh(); }
            Check(vm.CompletedCount == 1, "界面提交模拟任务并刷新完成状态");
            vm.SelectedJob = vm.Jobs.Single();
            await vm.ReportCommand.Run();
            Check(vm.Report.Contains("模拟"), "界面读取实际报告");
            await vm.PauseCommand.Run();
            Check(vm.Paused, "暂停队列按钮实际生效");
            await vm.SubmitCommand.Run();
            vm.SelectedJob = vm.Jobs.Last();
            await vm.CancelCommand.Run();
            Check(vm.Jobs.Last().State == "已取消", "取消按钮实际取消排队任务");
            await vm.PauseCommand.Run();
            await backend.Call("request_model_switch", new { requestId = "smoke-model", sessionId = "界面测试", model = "模型自主选择", effort = "high", reason = "审查资源竞争", handoff = "检查清理与恢复" });
            await vm.Refresh();
            Check(vm.Decisions.Count == 1 && vm.Decisions[0].State.Contains("未执行"), "模型建议页准确显示未执行状态");
            var permit = (await backend.Call("request_resource", new { requestId = "smoke-permit", sessionId = "原开发窗口A", resource = "solidworks", purpose = "原窗口许可排队烟测（不操作宿主）", durationSeconds = 120 })).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;
            for (var i = 0; i < 30 && permit.Status == "Queued"; i++)
            {
                await Task.Delay(50);
                permit = (await backend.Call("get_resource", new { leaseId = permit.Job.Id, token = permit.Token })).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;
            }
            Check(permit.Status == "Offered", "普通申请默认只预留，原窗口未接单不能操作");
            permit=(await backend.Call("accept_resource",new{leaseId=permit.Job.Id,token=permit.Token,offerId=permit.OfferId,generation=permit.Job.Generation})).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;
            Check(permit.Status == "Granted", "原窗口实际接单后获得资源许可");
            await vm.SubmitCommand.Run();
            Check(vm.Jobs.Single(j => j.Id == permit.Job.Id).State == "窗口使用中" && vm.WaitingCount == 1, "界面显示原窗口占用与后续任务排队");
            vm.SelectedJob = vm.Jobs.Single(j => j.Id == permit.Job.Id);
            await vm.ReportCommand.Run();
            Check(vm.Report.Contains("不代表测试结果"), "许可报告不冒充测试通过");
            var source = Path.Combine(backend.DataRoot, "candidate-source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "sample.dll"), "sample-version-one");
            vm.CandidateName = "自动烟测候选"; vm.CandidateSource = source; vm.CandidatePaths = "sample.dll";
            await vm.FreezeCommand.Run();
            Check(vm.Candidates.Count == 1, "界面冻结临时产物并显示候选版本");
            await vm.VerifyCandidateCommand.Run();
            Check(vm.CandidateResult.Contains("通过"), "界面校验冻结副本完整性");
            await vm.ShowArchiveCommand.Run();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            Check(vm.PageIndex == 4, "高级留档入口可单独打开");
            await vm.CloseArchiveCommand.Run();
            Check(vm.PageIndex == 3, "高级留档可返回诊断页面");
            for (var page = 0; page < 6; page++)
            {
                vm.PageIndex = page;
                if(page==3) { window.ModelAccessPanel.IsExpanded=false; window.SupervisorPanel.IsExpanded=true; window.ExclusionsPanel.IsExpanded=true; }
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                foreach (var scale in new[] { 1.25, 1.5 })
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(1120, 740)); content.Arrange(new Rect(0, 0, 1120, 740)); content.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                    var bitmap = new RenderTargetBitmap((int)(1120 * scale), (int)(740 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"page-{page}-{scale:0.00}.png")); encoder.Save(stream);
                }
            }
            passed.Add("六页面在125%与150%生成实际WPF渲染图");
            await backend.Call("release_resource", new { leaseId = permit.Job.Id, token = permit.Token, generation = permit.Job.Generation, cleanupConfirmed = true });
            for (var i = 0; i < 30 && vm.CompletedCount < 2; i++) { await Task.Delay(100); await vm.Refresh(); }
            Check(vm.Jobs.Single(j => j.Id == permit.Job.Id).State == "许可已释放" && vm.CompletedCount == 2, "释放后后续测试继续，许可不计入模拟通过数");
            await vm.DisconnectCommand.Run();
            Check(vm.ConnectionSummary.Contains("未注册"), "界面解除隔离配置连接");
            Check(!vm.IsConnectionReady && vm.ConnectionGlyph == "○", "解除连接后图标恢复灰色");
            vm.SelectedSuite = vm.Suites.Single(s => s.Id == "mock-timeout");
            await vm.SubmitCommand.Run();
            for (var i = 0; i < 10 && vm.RunningCount == 0; i++) { await Task.Delay(20); await vm.Refresh(); }
            await vm.StopCommand.Run();
            if (backend.OwnedService is { } service) await service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            using (var recovered = new Dispatch.Core.Scheduler(new Dispatch.Core.JobStore(backend.DataRoot)))
                Check(recovered.List().Last().State == Dispatch.Core.JobState.Cancelled, "安全停止服务会清理运行中的模拟任务");
            vm.Dispose();
            window.Close();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = passed }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (backend.OwnedService is { HasExited: false } process)
            {
                await backend.Call("shutdown_service");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
    }
}
