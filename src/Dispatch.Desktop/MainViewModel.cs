using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Dispatch.Core;

namespace Dispatch.Desktop;

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly DesktopBackend backend;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool refreshing;
    private bool busy;
    private int pageIndex;
    public int PageIndex { get => pageIndex; set { pageIndex = value; Changed(); Changed(nameof(PageTitle)); if (value == 3) ReadLogs(); } }
    public string PageTitle => PageIndex switch { 1 => "测试队列", 2 => "Codex 连接", 3 => "诊断与日志", 4 => "高级：产物留档", 5 => "工具管理", _ => "调度总览" };
    private ModuleSettings modules = new();
    public bool QueueEnabled => modules.QueueEnabled;
    public bool ModelsEnabled => modules.ModelsEnabled;
    public string ModuleSummary => $"测试排队：{(QueueEnabled ? "已启用" : "已关闭")}　·　模型调度：{(ModelsEnabled ? "已启用" : "已关闭")}";
    public string QueueModuleState => QueueEnabled ? "已启用 · 接受新任务与资源许可" : "已关闭 · 新任务暂停，已有资源仍可清理释放";
    public string ModelsModuleState => ModelsEnabled ? "已启用 · 接受主管委派；自动切换通道仍待完整验证" : "已关闭 · 停止新委派，已有周期仍可返回主管";
    public string QueueConnectionState { get; private set; } = "尚未检测独立接入";
    public string ModelsConnectionState { get; private set; } = "尚未检测独立接入";
    private QueueResumeConsent[] queueConsents=[];
    private string queueResumeThread="";
    private bool queueResumeEnabled;
    private int queueConsentVersion;
    public string QueueResumeThread {get=>queueResumeThread;set{queueResumeThread=value;Changed();var c=queueConsents.FirstOrDefault(c=>c.ThreadId==value);queueConsentVersion=c?.Version??0;QueueResumeEnabled=c?.Enabled??false;Changed(nameof(QueueResumeSummary));}}
    public bool QueueResumeEnabled {get=>queueResumeEnabled;set{queueResumeEnabled=value;Changed();}}
    public string QueueResumeSummary => queueConsents.FirstOrDefault(c=>c.ThreadId==QueueResumeThread) is {} c ? $"已保存：{(c.Enabled?"允许原排队任务接续":"自动接续已关闭")}（授权版本{c.Version}）" : "该聊天尚未配置排队接续授权。";
    public AsyncCommand SaveQueueResumeCommand {get;}
    public AsyncCommand FillQueueThreadCommand {get;}
    public AsyncCommand ToggleQueueCommand { get; }
    public AsyncCommand ToggleModelsCommand { get; }
    public AsyncCommand ConnectQueueCommand { get; }
    public AsyncCommand ConnectModelsCommand { get; }
    public AsyncCommand DisconnectQueueCommand { get; }
    public AsyncCommand DisconnectModelsCommand { get; }
    public AsyncCommand ConnectBothCommand { get; }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); Changed(nameof(CanOperate)); } }
    public bool CanOperate => !IsBusy;
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public ObservableCollection<DecisionRow> Decisions { get; } = [];
    public ObservableCollection<CandidateRow> Candidates { get; } = [];
    public CandidateRow? SelectedCandidate { get; set; }
    private string candidateSource = "";
    public string CandidateSource { get => candidateSource; set { candidateSource = value; Changed(); } }
    private string candidateName = "", candidatePaths = "";
    public string CandidateName { get => candidateName; set { candidateName = value; Changed(); } }
    public string CandidatePaths { get => candidatePaths; set { candidatePaths = value; Changed(); } }
    public string CandidateResult { get; private set; } = "选择产物目录并填写相对文件路径。冻结操作只生成副本，不部署或执行。";
    public Suite[] Suites { get; } = SuiteCatalog.All();
    public Suite? SelectedSuite { get; set; }
    public JobRow? SelectedJob { get; set; }
    public bool ServiceOnline { get; private set; }
    public bool Paused { get; private set; }
    public string PauseLabel => Paused ? "恢复队列" : "暂停队列";
    public string ServiceSummary => ServiceOnline ? (Paused ? "服务在线 · 队列暂停" : "服务在线") : "服务离线";
    public string ConnectionSummary { get; private set; } = "尚未检测";
    public string RulesSummary { get; private set; } = "尚未检测";
    public string BridgeSummary { get; private set; } = "尚未检测";
    private bool connectionChecked, mcpRegistered, rulesInstalled, bridgeReachable, connectionFailed;
    public bool IsConnectionReady => connectionChecked && !connectionFailed && mcpRegistered && rulesInstalled && bridgeReachable && ServiceOnline;
    public string ConnectionGlyph => connectionFailed ? "!" : IsConnectionReady ? "✓" : mcpRegistered ? "!" : "○";
    public string ConnectionColor => connectionFailed ? "#B92335" : IsConnectionReady ? "#18734B" : mcpRegistered ? "#9B641A" : "#758191";
    public string ConnectionBadge => connectionFailed ? "连接检查失败" : IsConnectionReady ? "连接配置已就绪" : mcpRegistered ? "已注册 · 检查服务" : connectionChecked ? "Codex 未连接" : "Codex 待检测";
    public string ConnectButtonLabel => IsConnectionReady ? "✓ 已就绪 · 重新检查" : "连接 Codex";
    public string ConnectionHint => "图标表示注册、规则、桥与本地服务的检查结果；原窗口是否加载工具仍需实际调用验证。";
    public string HomePath { get; private set; } = "自动识别";
    public string Notice { get; private set; } = "正在准备调度服务…";
    public string Report { get; private set; } = "选择一个任务，点击“查看报告”。";
    public string LogText { get; private set; } = "尚无故障日志。";
    public string DataPath => backend.DataRoot;
    public int RunningCount => Jobs.Count(j => j.Raw.State is JobState.Running or JobState.Cleanup);
    public int WaitingCount => Jobs.Count(j => j.Raw.State is JobState.Queued or JobState.Offered or JobState.WaitingForClient);
    public int CompletedCount => Jobs.Count(j => j.Raw.State == JobState.Passed && j.Raw.Suite.SimulationOnly);
    public int AttentionCount => Jobs.Count(j => j.Raw.State is JobState.Failed or JobState.RecoveryRequired or JobState.TimedOut or JobState.Blocked);
    public AsyncCommand SubmitCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ReportCommand { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RecoverCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand LogCommand { get; }
    public AsyncCommand OpenFolderCommand { get; }
    public AsyncCommand BrowseCandidateCommand { get; }
    public AsyncCommand FreezeCommand { get; }
    public AsyncCommand VerifyCandidateCommand { get; }
    public AsyncCommand OpenCandidateCommand { get; }
    public AsyncCommand ShowArchiveCommand { get; }
    public AsyncCommand CloseArchiveCommand { get; }
    public AsyncCommand ModelAccessCommand { get; }
    public string ModelAccessSummary { get; private set; } = "尚未检测。此检查仅读取能力，不修改模型。";
    public string ModelCatalogText { get; private set; } = "";
    public ObservableCollection<string> SupervisorThreads { get; } = [];
    public ObservableCollection<ModelOption> SupervisorModels { get; } = [];
    private SupervisorConfig[] supervisorConfigs = [];
    private ModelExclusions[] modelExclusions = [];
    public ObservableCollection<ExclusionOption> ExclusionChoices { get; } = [];
    public string ExclusionSummary => "已保存排除：" + (modelExclusions.FirstOrDefault(c=>c.SessionId==SelectedSupervisorThread)?.Models is {Length:>0} models ? string.Join("、",models) : "无");
    public AsyncCommand SaveExclusionsCommand { get; }
    private string? supervisorThread;
    public string? SelectedSupervisorThread { get => supervisorThread; set { supervisorThread=value; Changed(); LoadSupervisorSelection(); } }
    public string SupervisorSummary => supervisorConfigs.FirstOrDefault(c=>c.SessionId==SelectedSupervisorThread) is {} c
        ? $"已保存主管：{c.Model} / {c.Effort}（配置版本 {c.Version}）"
        : "尚未配置主管。请选择并保存后再发起委派。";
    private ModelOption? supervisorModel;
    public ModelOption? SelectedSupervisorModel { get => supervisorModel; set { supervisorModel=value; Changed(); Changed(nameof(SupervisorEfforts)); SelectedSupervisorEffort=value?.Efforts.FirstOrDefault(); Changed(nameof(SelectedSupervisorEffort)); } }
    public string[] SupervisorEfforts => SelectedSupervisorModel?.Efforts ?? [];
    private string? supervisorEffort;
    public string? SelectedSupervisorEffort { get => supervisorEffort; set { supervisorEffort=value; Changed(); } }
    public AsyncCommand SaveSupervisorCommand { get; }

    public MainViewModel(DesktopBackend backend, bool autoRefresh = true)
    {
        this.backend = backend;
        SaveQueueResumeCommand=Command(async()=>{
            var c=(await backend.Call("set_queue_resume_consent",new QueueResumeConsent(QueueResumeThread,QueueResumeEnabled,queueConsentVersion))).Deserialize<QueueResumeConsent>(Json.Options)!;
            queueConsents=queueConsents.Where(x=>x.ThreadId!=c.ThreadId).Append(c).ToArray();queueConsentVersion=c.Version;
            Changed(nameof(QueueResumeSummary));SetNotice("排队接续授权已保存。原窗口还须用真实聊天/轮次绑定排队任务；本次保存不会立即发送消息，也不改变模型。");
        });
        FillQueueThreadCommand=Command(()=>{if(SelectedSupervisorThread is null)throw new InvalidOperationException("尚无主管聊天，请填写原窗口提供的真实聊天编号。");QueueResumeThread=SelectedSupervisorThread;return Task.CompletedTask;});
        ToggleQueueCommand = Command(() => ToggleModule(true));
        ToggleModelsCommand = Command(() => ToggleModule(false));
        ConnectQueueCommand = Command(() => Connection("Connect", "queue"));
        ConnectModelsCommand = Command(() => Connection("Connect", "models"));
        DisconnectQueueCommand = Command(() => Connection("Disconnect", "queue"));
        DisconnectModelsCommand = Command(() => Connection("Disconnect", "models"));
        ConnectBothCommand = Command(() => Connection("Connect"));
        SaveExclusionsCommand = Command(async () =>
        {
            if(SelectedSupervisorThread is null) throw new InvalidOperationException("请先选择已授权聊天。");
            var saved=(await backend.Call("set_model_exclusions",new ModelExclusions(SelectedSupervisorThread,ExclusionChoices.Where(c=>c.IsExcluded).Select(c=>c.Id).ToArray()))).Deserialize<ModelExclusions>(Json.Options)!;
            modelExclusions=modelExclusions.Where(c=>c.SessionId!=saved.SessionId).Append(saved).ToArray();
            Changed(nameof(ExclusionSummary));
            SetNotice("排除名单已保存。新委派会避开这些模型；已执行任务仍按原计划返回主管。此设置不改变主管模型。");
        });
        SaveSupervisorCommand = Command(async () =>
        {
            if (SelectedSupervisorThread is null || SelectedSupervisorModel is null || SelectedSupervisorEffort is null) throw new InvalidOperationException("请先检测模型，并选择已授权聊天、主管模型和强度。");
            var saved = (await backend.Call("set_supervisor_settings", new SupervisorConfig(SelectedSupervisorThread, SelectedSupervisorModel.Id, SelectedSupervisorEffort))).Deserialize<SupervisorConfig>(Json.Options)!;
            supervisorConfigs = supervisorConfigs.Where(c=>c.SessionId!=saved.SessionId).Append(saved).ToArray();
            Changed(nameof(SupervisorSummary));
            SetNotice($"主管配置已保存：{saved.Model} / {saved.Effort}。执行模型由主管规划选择，任务结束必须返回主管。变更配置会阻止旧交接继续。");
        });
        ModelAccessCommand = Command(async () =>
        {
            var result = await backend.InspectModelAccess(HomePath);
            var catalog = result.GetProperty("standaloneCatalog");
            LoadModelCatalog(catalog);
            ModelCatalogText = catalog.TryGetProperty("models", out var models)
                ? string.Join("\n", models.EnumerateArray().Select(m => m.GetProperty("displayName").GetString() + "：" +
                    string.Join(" / ", m.GetProperty("efforts").EnumerateArray().Select(e => e.GetString())))) : "模型目录暂不可读取。";
            ModelAccessSummary = result.GetProperty("existingServer").GetProperty("reachable").GetBoolean()
                ? "后台入口可通信；原窗口绑定及实际切换尚未验证。"
                : "原窗口的后台直接控制入口未接通；已授权聊天可由 Codex 执行交接，实际生效需核验。模型目录如下。";
            Changed(nameof(ModelAccessSummary)); Changed(nameof(ModelCatalogText));
            SetNotice("只读检测完成，报告位于数据目录 diagnostics/model-access.json；未执行模型切换。");
        });
        SelectedSuite = Suites[0];
        SubmitCommand = Command(async () =>
        {
            if (SelectedSuite is null) throw new InvalidOperationException("请先选择模拟套件。");
            await backend.Call("submit_test", new Submission(Guid.NewGuid().ToString("N"), "桌面手动验证", SelectedSuite.Id));
            SetNotice(SelectedSuite.SimulationOnly ? "已提交模拟测试，等待调度。" : "已提交 SolidWorks 只读检查，等待调度。"); await Refresh();
        });
        PauseCommand = Command(async () => { await backend.Call("set_queue_paused", new { paused = !Paused }); await Refresh(); });
        CancelCommand = Command(async () => { if (SelectedJob is null) throw new InvalidOperationException("请先选择任务。"); await backend.Call("cancel_test", new { jobId = SelectedJob.Id }); await Refresh(); });
        ReportCommand = Command(async () =>
        {
            if (SelectedJob is null) throw new InvalidOperationException("请先选择任务。");
            var result = await backend.Call("get_test_report", new { jobId = SelectedJob.Id });
            var job = result.GetProperty("job").Deserialize<Job>(Json.Options)!;
            var scope = job.Suite.Runner == "external-agent" ? "此记录仅表示资源许可状态，不代表测试结果。测试证据由原开发窗口提供。" : job.Suite.SimulationOnly ? "本报告来自模拟测试，未操作真实 SolidWorks。" : "本报告仅验证宿主只读接入，不代表插件功能通过。";
            Report = $"{JobRow.Display(job.State)} · {job.Suite.Name}\n\n{job.Summary}\n\n来源：{job.Request.SessionId}\n清理：{(job.CleanupStatus == "OK" ? "已确认" : job.CleanupStatus == "NOT_REQUIRED" ? "无需清理" : "待确认")}\n\n{scope}\n任务编号：{job.Id}";
            Changed(nameof(Report));
        });
        InspectCommand = Command(() => Connection("Inspect"));
        ConnectCommand = Command(() => Connection(IsConnectionReady ? "Inspect" : "Connect"));
        DisconnectCommand = Command(() => Connection("Disconnect"));
        RecoverCommand = Command(() => Connection("Recover"));
        StartCommand = Command(async () => { await backend.StartService(); await Refresh(); SetNotice("调度服务已就绪。"); });
        StopCommand = Command(async () => { await backend.Call("shutdown_service"); await Task.Delay(400); await Refresh(); SetNotice("已请求安全停止服务，模拟任务会先结束进程并清理。"); });
        RefreshCommand = Command(Refresh);
        LogCommand = Command(() => { ReadLogs(); return Task.CompletedTask; });
        OpenFolderCommand = Command(() => { Directory.CreateDirectory(backend.DataRoot); Process.Start(new ProcessStartInfo("explorer.exe", backend.DataRoot) { UseShellExecute = true }); return Task.CompletedTask; });
        BrowseCandidateCommand = Command(() => { var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择待测产物目录" }; if (dialog.ShowDialog() == true) CandidateSource = dialog.FolderName; return Task.CompletedTask; });
        FreezeCommand = Command(async () =>
        {
            var paths = CandidatePaths.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await backend.Call("freeze_candidate", new FreezeRequest(CandidateName, CandidateSource, paths));
            var candidate = result.Deserialize<Candidate>(Json.Options)!;
            await Refresh(); SelectedCandidate = Candidates.First(c => c.Id == candidate.Id); Changed(nameof(SelectedCandidate));
            CandidateResult = $"已冻结 {candidate.Files.Length} 个文件。候选编号：{candidate.Id}\n内容清单：{candidate.ManifestHash}\n仅完成文件冻结，尚未部署或运行测试。";
            Changed(nameof(CandidateResult));
        });
        VerifyCandidateCommand = Command(async () =>
        {
            if (SelectedCandidate is null) throw new InvalidOperationException("请先选择候选版本。");
            var result = (await backend.Call("verify_candidate", new { candidateId = SelectedCandidate.Id })).Deserialize<CandidateVerification>(Json.Options)!;
            CandidateResult = result.Valid ? "完整性检查通过：所有冻结文件与清单一致。" : "完整性检查失败：" + string.Join("；", result.Errors);
            Changed(nameof(CandidateResult));
        });
        OpenCandidateCommand = Command(() =>
        {
            if (SelectedCandidate is null) throw new InvalidOperationException("请先选择候选版本。");
            Process.Start(new ProcessStartInfo("explorer.exe", SelectedCandidate.Raw.SnapshotRoot) { UseShellExecute = true }); return Task.CompletedTask;
        });
        ShowArchiveCommand = Command(() => { PageIndex = 4; return Task.CompletedTask; });
        CloseArchiveCommand = Command(() => { PageIndex = 3; return Task.CompletedTask; });
        if (autoRefresh) { timer.Tick += async (_, _) => { if (!IsBusy) await Refresh(); }; timer.Start(); }
    }
    public async Task Initialize()
    {
        await StartCommand.Run();
        await InspectCommand.Run();
        var catalogPath=Path.Combine(DataPath,"diagnostics","model-access.json");
        if(File.Exists(catalogPath))
        {
            try { using var doc=JsonDocument.Parse(File.ReadAllText(catalogPath)); LoadModelCatalog(doc.RootElement.GetProperty("standaloneCatalog")); }
            catch(Exception ex) when(ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            { backend.Log.Write("MODEL_CATALOG_READ_FAILED","读取模型目录失败，请重新检测",error:ex); }
        }
    }
    private void LoadModelCatalog(JsonElement catalog)
    {
        SupervisorModels.Clear();
        if(catalog.TryGetProperty("models",out var available)) foreach(var m in available.EnumerateArray())
        {
            var id=m.GetProperty("model").GetString()!;
            SupervisorModels.Add(new(id,m.TryGetProperty("displayName",out var name)?name.GetString()??id:id,m.GetProperty("efforts").EnumerateArray().Select(e=>e.GetString()!).ToArray()));
        }
        LoadSupervisorSelection();
    }
    private void LoadSupervisorSelection()
    {
        var saved=supervisorConfigs.FirstOrDefault(c=>c.SessionId==SelectedSupervisorThread);
        SelectedSupervisorModel=SupervisorModels.FirstOrDefault(m=>m.Id==saved?.Model);
        if(saved is not null && SupervisorEfforts.Contains(saved.Effort)) { SelectedSupervisorEffort=saved.Effort; Changed(nameof(SelectedSupervisorEffort)); }
        Changed(nameof(SupervisorSummary));
        var excluded=modelExclusions.FirstOrDefault(c=>c.SessionId==SelectedSupervisorThread)?.Models??[];
        ExclusionChoices.Clear();
        foreach(var m in SupervisorModels) ExclusionChoices.Add(new(m.Id,m.Name,excluded.Contains(m.Id)));
        foreach(var id in excluded.Where(id=>!SupervisorModels.Any(m=>m.Id==id))) ExclusionChoices.Add(new(id,id+"（目录中暂不可用）",true));
        Changed(nameof(ExclusionSummary));
    }
    private AsyncCommand Command(Func<Task> action) => new(async () =>
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception ex) { backend.Log.Write("UI_OPERATION_FAILED", "界面操作失败", error: ex); SetNotice(ex.Message); }
        finally { IsBusy = false; }
    });
    public async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var health = await backend.Call("get_health");
            if (health.TryGetProperty("modules", out var moduleState)) modules = moduleState.Deserialize<ModuleSettings>(Json.Options)!;
            foreach (var name in new[] { nameof(QueueEnabled), nameof(ModelsEnabled), nameof(ModuleSummary), nameof(QueueModuleState), nameof(ModelsModuleState) }) Changed(name);
            var queueSettings=await backend.Call("get_queue_resumption_settings");
            queueConsents=queueSettings.GetProperty("consents").Deserialize<QueueResumeConsent[]>(Json.Options)!;
            Changed(nameof(QueueResumeSummary));
            var supervisorSettings = await backend.Call("get_supervisor_settings");
            supervisorConfigs = supervisorSettings.GetProperty("configs").Deserialize<SupervisorConfig[]>(Json.Options)!;
            modelExclusions = supervisorSettings.GetProperty("exclusions").Deserialize<ModelExclusions[]>(Json.Options)!;
            var threads = supervisorSettings.GetProperty("threads").EnumerateArray().Select(t=>t.GetString()!).ToArray();
            if (!SupervisorThreads.SequenceEqual(threads)) { SupervisorThreads.Clear(); foreach(var t in threads) SupervisorThreads.Add(t); SelectedSupervisorThread=threads.FirstOrDefault(); Changed(nameof(SelectedSupervisorThread)); }
            Changed(nameof(SupervisorSummary));
            Changed(nameof(ExclusionSummary));
            ServiceOnline = true; Paused = health.GetProperty("paused").GetBoolean();
            var rows = (await backend.Call("list_tests")).Deserialize<Job[]>(Json.Options)!;
            var selected = SelectedJob?.Id;
            Jobs.Clear(); foreach (var job in rows) Jobs.Add(new(job));
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == selected);
            var decisions = (await backend.Call("list_model_decisions")).Deserialize<ModelDecision[]>(Json.Options)!;
            Decisions.Clear(); foreach (var d in decisions) Decisions.Add(new(d));
            var candidateId = SelectedCandidate?.Id;
            var snapshots = (await backend.Call("list_candidates")).Deserialize<Candidate[]>(Json.Options)!;
            Candidates.Clear(); foreach (var candidate in snapshots) Candidates.Add(new(candidate));
            SelectedCandidate = Candidates.FirstOrDefault(c => c.Id == candidateId); Changed(nameof(SelectedCandidate));
        }
        catch (DispatchException ex) when (ex.Code == "TEST_CENTER_OFFLINE") { ServiceOnline = false; }
        catch (Exception ex) { backend.Log.Write("UI_REFRESH_FAILED", "刷新失败", error: ex); SetNotice("刷新失败，请查看诊断日志。"); }
        finally
        {
            refreshing = false;
            foreach (var name in new[] { nameof(ServiceSummary), nameof(RunningCount), nameof(WaitingCount), nameof(CompletedCount), nameof(AttentionCount), nameof(PauseLabel), nameof(ServiceOnline), nameof(Paused), nameof(SelectedJob) }) Changed(name);
            NotifyConnection();
        }
    }
    private async Task ToggleModule(bool queue)
    {
        try
        {
            var desired = queue ? modules with { QueueEnabled = !modules.QueueEnabled } : modules with { ModelsEnabled = !modules.ModelsEnabled };
            await backend.Call("set_module_settings", desired);
            SetNotice("模块设置已即时保存。开关互不影响；已有资源仍须清理，已有模型周期仍须返回主管。无需重载 Codex。");
        }
        finally { await Refresh(); }
    }
    private async Task Connection(string action, string module = "all")
    {
        JsonElement result;
        try { result = await backend.Connection(action, module); }
        catch { connectionFailed = true; NotifyConnection(); throw; }
        if (result.GetProperty("status").GetString() == "RECOVERY_REQUIRED")
            throw new InvalidOperationException("恢复仍被外部修改阻塞，请在数据目录中核对连接备份。");
        if (module != "all" || action is "Disconnect" or "Recover") result = await backend.Connection("Inspect");
        if (result.TryGetProperty("modules", out var registrations))
        {
            foreach (var entry in registrations.EnumerateArray())
            {
                var label = entry.GetProperty("mcpRegistered").GetBoolean() ? "独立工具已登记 · 新登记后需重载 Codex" : "独立工具未接入";
                if (entry.GetProperty("module").GetString() == "queue") QueueConnectionState = label; else ModelsConnectionState = label;
            }
            Changed(nameof(QueueConnectionState)); Changed(nameof(ModelsConnectionState));
        }
        connectionChecked = true; connectionFailed = false;
        mcpRegistered = result.GetProperty("mcpRegistered").GetBoolean();
        rulesInstalled = result.GetProperty("rulesInstalled").GetBoolean();
        bridgeReachable = result.TryGetProperty("bridgeReachable", out var reachable) && reachable.GetBoolean();
        HomePath = result.GetProperty("targetHome").GetString()!;
        if (ServiceOnline) await backend.Call("set_runtime_source", new { home = HomePath });
        ConnectionSummary = result.GetProperty("mcpRegistered").GetBoolean() ? "已注册 · 原窗口待重载" : "未注册";
        RulesSummary = result.GetProperty("rulesInstalled").GetBoolean() ? "已安装模拟规则" : "未安装";
        BridgeSummary = result.TryGetProperty("bridgeReachable", out var reach) && reach.GetBoolean() ? "桥连接可用" : "桥连接失败";
        SetNotice(action switch { "Connect" => "连接已登记。请重载原 Codex 窗口后验证工具。", "Disconnect" => "已解除本工具的连接。", "Recover" => "已检查恢复记录，请核对当前连接状态。", _ => "检测完成。模型交接仅适用于已明确授权并配置主管的聊天。" });
        foreach (var name in new[] { nameof(HomePath), nameof(ConnectionSummary), nameof(RulesSummary), nameof(BridgeSummary) }) Changed(name);
        NotifyConnection();
    }
    private void NotifyConnection()
    {
        foreach (var name in new[] { nameof(IsConnectionReady), nameof(ConnectionGlyph), nameof(ConnectionColor), nameof(ConnectionBadge), nameof(ConnectButtonLabel) }) Changed(name);
    }
    private void ReadLogs()
    {
        var path = Path.Combine(backend.DataRoot, "logs", "bug.jsonl");
        try
        {
            if (!File.Exists(path)) LogText = "尚无故障日志。";
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var offset = Math.Max(0, stream.Length - 65536); stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream); if (offset > 0) reader.ReadLine();
                LogText = reader.ReadToEnd();
            }
        }
        catch (IOException) { LogText = "日志正被占用，请稍后刷新。"; }
        Changed(nameof(LogText));
    }
    private void SetNotice(string text) { Notice = text; Changed(nameof(Notice)); }
    public void Dispose() => timer.Stop();
}

public sealed record JobRow(Job Raw)
{
    public string Id => Raw.Id;
    public string Name => Raw.Suite.Name;
    public string Session => Raw.Request.SessionId;
    public string State => Raw.Suite.Runner == "external-agent" && Raw.State == JobState.Running ? (Raw.Summary.StartsWith("已获得恢复许可", StringComparison.Ordinal) ? "待恢复现场" : "窗口使用中") :
        Raw.State == JobState.Released && Raw.Summary.StartsWith("已保存现场并让出", StringComparison.Ordinal) ? "已让出 · 任务未完" : Display(Raw.State);
    public string Resources => Raw.Suite.Runner == "external-agent" ? (Raw.Suite.Resources.Contains("sw-main") ? "SW / 桌面" : "桌面")
        : Raw.Suite.Resources.Contains("sw-main") ? (Raw.Suite.Resources.Contains("desktop-ui") ? "SW与桌面输入" : "仅SolidWorks")
        : Raw.Suite.Resources.Any(r=>r.StartsWith("app:",StringComparison.Ordinal)) ? string.Join(" / ",Raw.Suite.Resources) : "后台";
    public string Time => Raw.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    public static string Display(JobState state) => state switch { JobState.Queued => "等待中", JobState.Offered => "待原窗口接单",JobState.WaitingForClient => "等待聊天接续", JobState.Running => "执行中", JobState.Cleanup => "清理中", JobState.Passed => "已通过", JobState.Failed => "失败", JobState.Cancelled => "已取消", JobState.TimedOut => "已超时", JobState.Blocked => "环境阻塞", JobState.Released => "许可已释放", _ => "需要恢复" };
}
public sealed record DecisionRow(ModelDecision Raw)
{
    public string Session => Raw.Request.SessionId;
    public string Model => Raw.Execution is {} p ? p.Model + " / " + p.Thinking : Raw.Request.Model;
    public string Reason => Raw.Request.Reason;
    public string State => Raw.Status switch {
        "AWAITING_CALLER_EXECUTION" => "待执行交接", "SENDING" => "执行交接发送中", "SENT_AWAITING_VERIFICATION" => "待确认执行模型", "EXECUTING" => "执行限定任务",
        "RETURN_READY" => Raw.Checkpoint is null ? "待返回主管" : "进度已保存 · 待主管接管", "RETURN_SENDING" => "返回主管发送中", "RETURN_SENT_AWAITING_VERIFICATION" => "待确认主管生效", "RETURN_VERIFIED" => "已确认返回主管",
        "RECOVERY_REQUIRED" => "交接不确定 · 待恢复", "USER_OVERRIDE" => "用户接管 · 自动交接停止", "CANCELLED_BEFORE_SEND" => "发送前取消", _ => "已记录 · 未执行切换" };
}
public sealed record CandidateRow(Candidate Raw)
{
    public string Id => Raw.Id;
    public string Name => Raw.Name;
    public int FileCount => Raw.Files.Length;
    public string Time => Raw.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string State => "已冻结 · 未执行";
}
public sealed record ModelOption(string Id, string Name, string[] Efforts);
public sealed class ExclusionOption(string id,string name,bool excluded) : Observable
{
    public string Id { get; }=id;
    public string Name { get; }=name;
    private bool isExcluded=excluded;
    public bool IsExcluded { get=>isExcluded; set { isExcluded=value; Changed(); } }
}
