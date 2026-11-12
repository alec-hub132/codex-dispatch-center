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
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1440, Height = 920 };
            window.Show(); // 测试自己的离屏 WPF 窗口，不操作其他软件或桌面焦点。
            Check(vm.PageIndex==1 && vm.QueueLayout=="队列顺序","打开程序默认进入队列操作台并按队列排序");
            await vm.Initialize();
            Check(vm.ServiceOnline, "界面启动真实隔离服务");
            Check(!vm.HostRecoveryEnabled,"崩溃自动处理默认关闭");
            await vm.ToggleHostRecoveryCommand.Run();
            Check(vm.HostRecoveryEnabled && vm.HostRecoverySummary.Contains("已开启"),"独立开关即时保存开启状态");
            using(var recoveryReopened=new MainViewModel(backend,autoRefresh:false))
            {await recoveryReopened.Initialize();Check(recoveryReopened.HostRecoveryEnabled,"重开界面保留崩溃处理开关");}
            await vm.ToggleHostRecoveryCommand.Run();
            Check(!vm.HostRecoveryEnabled,"崩溃处理可单独关闭");
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
            window.QueueResumePanel.IsExpanded=false;window.IdleCleanupPanel.IsExpanded=true;
            Check(!vm.IdleCleanupEnabled && vm.IdleMinutes==10,"无活动清理默认关闭且门限为10分钟，不继承接续授权");
            vm.IdleMinutes=7;vm.IdleCleanupEnabled=true;await vm.SaveIdleCleanupCommand.Run();
            Check(vm.IdleCleanupSummary.Contains("7分钟"),"界面保存自动清理授权及门限");
            vm.IdleMinutes=12;vm.IdleCleanupEnabled=false;await vm.Refresh();
            Check(vm.IdleMinutes==12 && !vm.IdleCleanupEnabled,"后台刷新保留尚未保存的清理设置");
            using(var cleanupReopened=new MainViewModel(backend,autoRefresh:false))
            {await cleanupReopened.Initialize();Check(cleanupReopened.IdleCleanupEnabled && cleanupReopened.IdleMinutes==7,"重开界面保留已保存清理设置");}
            vm.IdleMinutes=7;await vm.SaveIdleCleanupCommand.Run();
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
            var historyIds=new List<string>();
            var historyPaused=(await backend.Call("get_health")).GetProperty("paused").GetBoolean();
            await backend.Call("set_queue_paused",new{paused=true});
            var historyCount=vm.Jobs.Count;
            for(var i=0;i<80;i++)
                historyIds.Add((await backend.Call("request_resource",new{requestId="bulk-history-"+i,sessionId="fixture",resource="app:bulk-history",purpose=new string('测',1000)})).GetProperty("job").GetProperty("id").GetString()!);
            Check((await backend.Call("list_tests")).GetRawText().Length>Dispatch.Core.Transport.MaxChars,"兼容接口可读取超过旧128KB上限的任务历史");
            await vm.Refresh();
            Check(vm.Jobs.Count==historyCount+80 && historyIds.All(id=>vm.Jobs.Any(j=>j.Id==id)),"大历史刷新分页完整保留全部任务");
            foreach(var id in historyIds)await backend.Call("cancel_test",new{jobId=id});
            await backend.Call("set_queue_paused",new{paused=historyPaused});
            var threadA="11111111-1111-4111-8111-111111111111";var threadB="22222222-2222-4222-8222-222222222222";
            foreach(var thread in new[]{threadA,threadB})
                File.WriteAllText(Path.Combine(queueSessions,"rollout-"+thread+".jsonl"),JsonSerializer.Serialize(new{type="session_meta",payload=new{id=thread}})+"\n"+
                    JsonSerializer.Serialize(new{type="turn_context",timestamp=DateTimeOffset.UtcNow,payload=new{turn_id="fixture-turn",model="fixture",effort="medium"}})+"\n");
            // 独立的只读名称夹具，不改前面app-server自建的元数据库。
            using(var names=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(vm.HomePath,"state_999.sqlite")}"))
            {
                names.Open();using var c=names.CreateCommand();c.CommandText="CREATE TABLE threads(id TEXT PRIMARY KEY,name TEXT,title TEXT);INSERT INTO threads VALUES($a,'齿轮插件','初始消息'),($b,'齿轮插件','另一初始消息');";
                c.Parameters.AddWithValue("$a",threadA);c.Parameters.AddWithValue("$b",threadB);c.ExecuteNonQuery();
            }
            await backend.Call("set_runtime_source",new{home=vm.HomePath});
            async Task<Dispatch.Core.ResourcePermit> GroupPermit(string id,string label,string thread)=>
                (await backend.Call("request_resource",new{requestId=id,sessionId=label,ownerThreadId=thread,resource="app:group-fixture",purpose="窗口分组隔离验收",durationSeconds=120})).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;
            var ga=await GroupPermit("group-a","任意标签甲",threadA);var ga2=await GroupPermit("group-a2","任意标签乙",threadA);var gb=await GroupPermit("group-b","任意标签甲",threadB);
            for(var i=0;i<30 && ga.Status=="Queued";i++){await Task.Delay(50);ga=(await backend.Call("get_resource",new{leaseId=ga.Job.Id,token=ga.Token})).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;}
            ga=(await backend.Call("accept_resource",new{leaseId=ga.Job.Id,token=ga.Token,offerId=ga.OfferId,generation=ga.Job.Generation})).Deserialize<Dispatch.Core.ResourcePermit>(Dispatch.Core.Json.Options)!;
            await vm.Refresh();var first=vm.Jobs.Single(j=>j.Id==ga.Job.Id);var second=vm.Jobs.Single(j=>j.Id==ga2.Job.Id);var other=vm.Jobs.Single(j=>j.Id==gb.Job.Id);
            Check(first.Session=="齿轮插件" && ReferenceEquals(first.WindowGroup,second.WindowGroup),"真实聊天名称归组，不同提交标签仍属于同一窗口");
            Check(!ReferenceEquals(first.WindowGroup,other.WindowGroup) && first.Session==other.Session,"同名不同真实聊天保持两个独立组");
            Check(first.Resources=="group-fixture" && first.Time.Contains('-'),"软件资源显示准确并包含日期时间");
            var unchangedNotifications=0;
            System.ComponentModel.PropertyChangedEventHandler unchangedHandler=(_,_)=>unchangedNotifications++;
            first.PropertyChanged+=unchangedHandler;
            await vm.Refresh();
            first.PropertyChanged-=unchangedHandler;
            Check(unchangedNotifications==0,"无变化同步不重新通知历史行属性，避免重复布局与绘制");
            using(var names=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(vm.HomePath,"state_999.sqlite")))
            {
                names.Open();using var rename=names.CreateCommand();rename.CommandText="UPDATE threads SET name=$name WHERE id=$id";
                var renameNotifications=0;
                System.ComponentModel.PropertyChangedEventHandler renameHandler=(_,eventArgs)=>{if(eventArgs.PropertyName==nameof(JobRow.TaskHeading))renameNotifications++;};
                first.PropertyChanged+=renameHandler;
                rename.Parameters.AddWithValue("$name","齿轮插件已改名");rename.Parameters.AddWithValue("$id",threadA);rename.ExecuteNonQuery();
                await vm.RefreshCommand.Run();
                first.PropertyChanged-=renameHandler;
                Check(first.Session=="齿轮插件已改名" && vm.Jobs.Single(j=>j.Id==gb.Job.Id).Session=="齿轮插件" && renameNotifications>0,"手动刷新立即读取窗口改名并更新真实绑定，保留不同聊天分组");
                rename.Parameters["$name"].Value="齿轮插件";rename.ExecuteNonQuery();await vm.RefreshCommand.Run();
            }
            var groupResets=0;vm.QueueGroups.CollectionChanged+=(_,_)=>groupResets++;
            first.WindowGroup!.IsExpanded=false;vm.SelectedJob=second;await vm.Refresh();
            Check(!vm.Jobs.Single(j=>j.Id==ga.Job.Id).WindowGroup!.IsExpanded && vm.SelectedJob?.Id==ga2.Job.Id,"自动刷新保留组折叠状态和所选提交");
            Check(groupResets==0 && ReferenceEquals(second,vm.SelectedJob),"无变化刷新复用行与组，不重置树列表滚动及选中容器");
            first.WindowGroup.IsExpanded=true;
            Check(vm.DisplayGroups.SelectMany(g=>g.Children).Select(j=>j.SortPriority).SequenceEqual(vm.DisplayGroups.SelectMany(g=>g.Children).Select(j=>j.SortPriority).Order()),"运行等待在前、完成记录置底");
            vm.SelectedJob=first;
            Check(vm.SelectedWindow=="齿轮插件" && vm.SelectedResource=="group-fixture" && vm.SelectedState=="运行中","右侧详情反映真实窗口与资源占用");
            Check(first.StateForeground=="#15734D" && second.StateForeground=="#245BB9","运行绿色、等待蓝色");
            var ordered=vm.DisplayGroups.SelectMany(g=>g.Children).ToArray();
            Check(ordered.Where(j=>j.Raw.State==Dispatch.Core.JobState.Queued).Select(j=>j.Raw.Sequence).SequenceEqual(ordered.Where(j=>j.Raw.State==Dispatch.Core.JobState.Queued).Select(j=>j.Raw.Sequence).Order()),"默认等待提交按申请顺序排列");
            vm.QueueLayout="窗口分组";Check(vm.DisplayGroups.Any(g=>g.Children.Count(j=>j.WindowGroup==first.WindowGroup)==2),"可切换为窗口分组查看多次提交");
            vm.QueueFilter="已结束";Check(vm.DisplayGroups.SelectMany(g=>g.Children).All(j=>j.IsTerminal),"已结束筛选覆盖许可释放和固定测试");
            vm.QueueFilter="全部提交";vm.QueueSearch="齿轮插件";Check(vm.DisplayGroups.SelectMany(g=>g.Children).All(j=>j.Session=="齿轮插件"),"按真实窗口名搜索");
            vm.QueueSearch="";vm.QueueLayout="队列顺序";
            using(var offlineView=new MainViewModel(backend,autoRefresh:false))
            {
                offlineView.SelectedJob=first;
                Check(!offlineView.CanManualRelease && offlineView.SelectedState.StartsWith("上次记录"),"未刷新成功时不允许依据历史状态结束许可");
            }
            var dialog=new ManualReleaseDialog(first);Check(!dialog.CanConfirm,"占用许可手动解除默认不能确认");
            dialog.Stopped.IsChecked=true;Check(!dialog.CanConfirm,"仅停止确认仍不能解除");dialog.Cleaned.IsChecked=true;Check(dialog.CanConfirm,"两项清理确认齐全才允许手动解除");dialog.Close();
            vm.SelectedJob=first;vm.ConfirmManualRelease=_=>Task.FromResult(false);await vm.ManualReleaseCommand.Run();
            Check((await backend.Call("get_test_status",new{jobId=first.Id})).GetProperty("state").GetString()=="Running","取消确认框保留原占用");
            vm.ConfirmManualRelease=_=>Task.FromResult(true);vm.SelectedJob=vm.Jobs.Single(j=>j.Id==ga2.Job.Id);await vm.ManualReleaseCommand.Run();
            Check(vm.Jobs.Single(j=>j.Id==ga2.Job.Id).Raw.State==Dispatch.Core.JobState.Cancelled && vm.Jobs.Single(j=>j.Id==ga.Job.Id).Raw.State==Dispatch.Core.JobState.Running,"手动撤回仅作用于所选一次提交，不解除同组占用");
            for (var page = 0; page < 6; page++)
            {
                vm.PageIndex = page;
                if(page==3) { window.ModelAccessPanel.IsExpanded=false; window.SupervisorPanel.IsExpanded=true; window.ExclusionsPanel.IsExpanded=true; }
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                if(page==1)
                {
                    window.QueueJobsTree.UpdateLayout();
                    IEnumerable<System.Windows.Controls.TextBlock> Texts(DependencyObject node)
                    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is System.Windows.Controls.TextBlock text)yield return text;foreach(var nested in Texts(child))yield return nested;}}
                    var texts=Texts(window.QueueJobsTree).Select(t=>new{t.Text,t.ActualWidth,t.ActualHeight}).ToArray();
                    File.WriteAllText(Path.Combine(output,"queue-layout.json"),JsonSerializer.Serialize(texts));
                    Check(texts.Any(t=>t.Text==first.Time && t.ActualWidth>=120) && texts.Any(t=>t.Text==first.Resources && t.ActualWidth>=115),"分组队列实际渲染提交时间和软件资源且列宽完整");
                }
                foreach (var scale in new[] { 1.25, 1.5 })
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(1440, 880)); content.Arrange(new Rect(0, 0, 1440, 880)); content.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                    var bitmap = new RenderTargetBitmap((int)(1440 * scale), (int)(880 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"page-{page}-{scale:0.00}.png")); encoder.Save(stream);
                }
            }
            passed.Add("六页面在125%与150%生成实际WPF渲染图");
            vm.SelectedJob=vm.Jobs.Single(j=>j.Id==gb.Job.Id);
            Check(vm.CanManualDispatch,"等待提交提供优先执行入口");
            await vm.ManualDispatchCommand.Run();
            Check(vm.Notice.Contains("占用") && vm.Jobs.Single(j=>j.Id==ga.Job.Id).Raw.State==Dispatch.Core.JobState.Running,"优先执行拒绝占用冲突并保留原任务");
            vm.SelectedJob=vm.Jobs.Single(j=>j.Id==ga.Job.Id);await vm.ManualReleaseCommand.Run();
            Check(vm.Jobs.Single(j=>j.Id==ga.Job.Id).Raw is {State:Dispatch.Core.JobState.Released,CleanupStatus:"OK"},"用户确认后手动释放并回读清理成功");
            vm.SelectedJob=vm.Jobs.Single(j=>j.Id==gb.Job.Id);
            // 资源释放后旧等待可能已自然进入Offered，同一接单机会由原窗口确认。
            await vm.ManualReleaseCommand.Run();
            Check(vm.Jobs.Single(j=>j.Id==gb.Job.Id).Raw.State==Dispatch.Core.JobState.Cancelled,"另一个窗口的等待记录可独立撤回");
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
            Check(!File.ReadLines(Path.Combine(backend.DataRoot,"logs","bug.jsonl")).Any(line=>JsonDocument.Parse(line).RootElement.GetProperty("code").GetString()=="DESKTOP_ERROR"),"离屏交互和渲染没有未处理的界面异常");
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
