using System.Text.Json;
using Dispatch.Core;

namespace Dispatch.Desktop;

public sealed partial class MainViewModel
{
    public AsyncCommand ManualDispatchCommand { get; private set; } = null!;
    public bool CanManualDispatch => CanOperate && QueueDataFresh && QueueEnabled && !Paused
        && SelectedJob is { Raw.UserPriority: false }
        && SelectedJob.Raw.State is JobState.Queued or JobState.WaitingForClient;

    public string ManualDispatchHint => !QueueDataFresh ? "请先刷新并核对当前状态。"
        : SelectedJob?.Raw.UserPriority == true ? "已安排优先执行，等待派发。"
        : SelectedJob?.Raw.State switch
        {
            JobState.Offered => "已预留资源，等待原窗口接单。",
            JobState.Queued or JobState.WaitingForClient => "优先安排这次提交，开发窗口接单后进入运行。",
            JobState.Running or JobState.Cleanup => "当前任务正在执行或清理。",
            _ => "选择尚未开始的提交可优先执行。"
        };

    private void InitializeManualDispatch()
    {
        ManualDispatchCommand = Command(async () =>
        {
            if (!QueueDataFresh || SelectedJob is not { } selected)
                throw new InvalidOperationException("请先刷新并选择等待提交。");
            var result = (await backend.Call("manual_dispatch_test",
                new ManualDispatchRequest(selected.Id, selected.Raw.Generation, selected.Raw.State)))
                .Deserialize<ManualDispatchResult>(Json.Options)!;
            await Task.Delay(300);
            await Refresh();
            SetNotice(result.Message);
        });
    }

    public ResetCollection<QueueWindowGroup> DisplayGroups { get; } = [];
    public string[] QueueLayouts { get; } = ["队列顺序", "窗口分组"];
    public string[] QueueFilters { get; } = ["全部提交", "运行与等待", "已结束", "待清理"];
    private readonly Dictionary<string, QueueWindowGroup> consoleGroups = new();
    private string queueLayout = "队列顺序";
    private string queueFilter = "全部提交";
    private string queueSearch = "";
    public bool QueueDataFresh { get; private set; }
    public DateTimeOffset? LastQueueSync { get; private set; }
    public string QueueLayout
    {
        get => queueLayout;
        set { queueLayout = value; Changed(); RebuildQueueConsole(); }
    }
    public string QueueFilter
    {
        get => queueFilter;
        set { queueFilter = value; Changed(); RebuildQueueConsole(); }
    }
    public string QueueSearch
    {
        get => queueSearch;
        set { queueSearch = value; Changed(); RebuildQueueConsole(); }
    }
    public string ConsoleResultSummary => DisplayGroups.Count == 0 ? "没有符合筛选条件的提交" : $"当前显示 {DisplayGroups.Sum(group => group.Children.Count)} 次提交";
    public int EndedCount => Jobs.Count(row => row.IsTerminal);
    public string QueueSyncSummary => QueueDataFresh
        ? $"最近同步 {LastQueueSync:HH:mm:ss}"
        : LastQueueSync is { } time ? $"状态待同步 · 上次 {time:HH:mm:ss}" : "正在读取队列状态";
    public string ConsoleServiceSummary => QueueDataFresh
        ? !QueueEnabled ? "服务运行中 · 测试排队已关闭"
            : Paused ? "服务运行中 · 派发已暂停" : "服务运行中 · 派发已启用"
        : ServiceOnline ? "队列状态读取失败" : "服务离线";
    public string SelectedWindow => SelectedJob?.Session ?? "选择一次提交";
    public string SelectedPurpose => SelectedJob?.Name ?? "点击左侧任务查看当前状态和操作";
    public string SelectedState => SelectedJob is { } row
        ? QueueDataFresh ? row.ConsoleState : "上次记录 · " + row.ConsoleState
        : "尚未选择";
    public string SelectedColor => QueueDataFresh ? SelectedJob?.StateForeground ?? "#667183" : "#697383";
    public string SelectedBackground => QueueDataFresh ? SelectedJob?.StateBackground ?? "#F4F5F7" : "#F4F5F7";
    public string ConsoleServiceColor => QueueDataFresh ? "#15734D" : "#935B21";
    public string RunningLabel => QueueDataFresh ? "● 运行中" : "● 运行中 · 上次记录";
    public string WaitingLabel => QueueDataFresh ? "◷ 等待中" : "◷ 等待中 · 上次记录";
    public string SelectedResource => SelectedJob?.Resources ?? "—";
    public string SelectedPosition => SelectedJob?.QueuePosition ?? "—";
    public string SelectedCreated => SelectedJob?.Raw.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—";
    public string SelectedStarted => SelectedJob?.Raw.StartedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚未开始";
    public string SelectedEnded => SelectedJob?.Raw.EndedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚未结束";
    public string SelectedCleanup => SelectedJob?.Raw.CleanupStatus switch
    {
        "OK" => "已确认清理",
        "NOT_REQUIRED" => "尚未占用或无需清理",
        null => "—",
        _ => "待停止与清理确认"
    };
    public string SelectedStatusDescription => SelectedJob is { } row
        ? QueueDataFresh ? row.Raw.Summary : "当前显示上次同步记录，请刷新后核对当前状态。"
        : "选择窗口中的一次提交，查看本段测试进度。";
    public string ManualReleaseLabel => SelectedJob?.Raw.State switch
    {
        JobState.Queued or JobState.Offered or JobState.WaitingForClient => "撤回这次排队",
        JobState.Running or JobState.Cleanup or JobState.RecoveryRequired => "手动结束并释放许可",
        null => "选择一次提交",
        _ => "本次提交已结束"
    };
    public bool CanCancelSelected => CanOperate && QueueDataFresh && SelectedJob is { IsTerminal: false }
        && SelectedJob.Raw.State != JobState.RecoveryRequired
        && SelectedJob.Raw.Suite.Runner != "external-agent";

    private static IOrderedEnumerable<JobRow> SortQueueRows(IEnumerable<JobRow> rows) => rows
        .OrderBy(row => row.SortPriority)
        .ThenBy(row => row.Raw.State == JobState.Offered ? 0 : row.Raw.UserPriority ? 1 : 2)
        .ThenBy(row => row.IsTerminal ? -row.Raw.Sequence : row.Raw.Sequence);

    private void RebuildQueueConsole()
    {
        foreach (var row in Jobs)
        {
            row.ShowWindowName = QueueLayout == "队列顺序";
            row.QueuePosition = GetQueuePosition(row);
        }
        var visible = Jobs.Where(row => QueueFilter switch
        {
            "运行与等待" => !row.IsTerminal,
            "已结束" => row.IsTerminal,
            "待清理" => row.Raw.State == JobState.RecoveryRequired,
            _ => true
        }).Where(row => string.IsNullOrWhiteSpace(QueueSearch)
            || (row.Session + " " + row.Name).Contains(QueueSearch.Trim(), StringComparison.OrdinalIgnoreCase));
        var sorted = SortQueueRows(visible).ToArray();
        var groups = QueueLayout == "窗口分组"
            ? sorted.GroupBy(row => row.WindowGroup!.Key)
            : sorted.GroupBy(row => "phase-" + row.SortPriority);
        var displays = new List<QueueWindowGroup>();
        foreach (var group in groups)
        {
            var key = QueueLayout + "/" + group.Key;
            if (!consoleGroups.TryGetValue(key, out var display)) consoleGroups[key] = display = new(key);
            var first = group.First();
            var title = QueueLayout == "窗口分组" ? first.Session : first.SortPriority switch
            {
                0 => "运行中",
                1 => "等待队列",
                2 => "待停止与清理",
                _ => "已结束"
            };
            display.Update(new("", key, null, title, "NAME"), group.Select(row => row.Raw).ToArray());
            display.Children.ReplaceAll(group);
            displays.Add(display);
        }
        DisplayGroups.ReplaceAll(displays);
        NotifyQueueSelection();
    }

    private string GetQueuePosition(JobRow row)
    {
        if (row.Raw.State is JobState.Running or JobState.Cleanup) return "执行中";
        if (row.Raw.State == JobState.Offered) return "待接单";
        if (row.Raw.State == JobState.WaitingForClient) return "待接续";
        if (row.Raw.State != JobState.Queued) return "—";
        var position = Jobs.Count(other => (other.Raw.State == JobState.Offered || other.Raw.UserPriority && !row.Raw.UserPriority
                || other.Raw.UserPriority == row.Raw.UserPriority && other.Raw.Sequence <= row.Raw.Sequence)
            && other.Raw.State is JobState.Queued or JobState.Offered
            && other.Raw.Suite.Resources.Intersect(row.Raw.Suite.Resources).Any());
        return $"第{position}位";
    }

    private void NotifyQueueSelection()
    {
        foreach (var name in new[]
        {
            nameof(EndedCount), nameof(QueueSyncSummary), nameof(ConsoleServiceSummary), nameof(SelectedWindow),
            nameof(SelectedPurpose), nameof(SelectedState), nameof(SelectedColor), nameof(SelectedBackground),
            nameof(SelectedResource), nameof(SelectedPosition), nameof(SelectedCreated), nameof(SelectedStarted),
            nameof(SelectedEnded), nameof(SelectedCleanup), nameof(SelectedStatusDescription), nameof(ManualReleaseLabel),
            nameof(CanManualRelease), nameof(CanCancelSelected), nameof(CanManualDispatch), nameof(ManualDispatchHint), nameof(ConsoleResultSummary), nameof(ConsoleServiceColor), nameof(RunningLabel), nameof(WaitingLabel)
        }) Changed(name);
    }
}

public sealed partial class JobRow
{
    private string queuePosition = "—";
    private bool showWindowName;
    public string QueuePosition { get => queuePosition; set { if (queuePosition == value) return; queuePosition = value; Changed(); } }
    public bool ShowWindowName { get => showWindowName; set { if (showWindowName == value) return; showWindowName = value; Changed(nameof(TaskHeading)); } }
    public string TaskHeading => ShowWindowName ? Session + " · " + Name : Name;
    public int SortPriority => Raw.State switch
    {
        JobState.Running or JobState.Cleanup => 0,
        JobState.Queued or JobState.Offered or JobState.WaitingForClient => 1,
        JobState.RecoveryRequired => 2,
        _ => 3
    };
    public string ConsoleState => Raw.State == JobState.Running ? "运行中" : State;
    public bool IsTerminal => SortPriority == 3;
    public string StateForeground => SortPriority switch
    {
        0 => "#15734D",
        1 => "#245BB9",
        2 => "#935B21",
        _ => "#697383"
    };
    public string StateBackground => SortPriority switch
    {
        0 => "#EDF8F2",
        1 => "#EDF3FF",
        2 => "#FFF7E9",
        _ => "#F1F3F6"
    };
    public string TaskForeground => IsTerminal ? "#76808E" : "#202936";
}
