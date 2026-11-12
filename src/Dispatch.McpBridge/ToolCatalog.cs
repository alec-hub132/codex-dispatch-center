namespace Dispatch.McpBridge;

public static class ToolCatalog
{
    private static object Text(int max = 128) => new { type = "string", minLength = 1, maxLength = max };
    private static object Tool(string name, string description, Dictionary<string, object>? properties = null, string[]? required = null) =>
        new { name, description, inputSchema = new { type = "object", properties = properties ?? [], required = required ?? [], additionalProperties = false } };
    private static Dictionary<string, object> Job() => new() { ["jobId"] = Text() };
    private static Dictionary<string, object> Checkpoint() => new() { ["leaseId"] = Text(), ["token"] = Text(), ["checkpointId"] = Text() };
    public static object[] All(string module = "all")
    {
        if (module is not ("all" or "queue" or "models")) throw new ArgumentException("未知工具模块。");
        return Definitions().Where(t =>
        {
            var name = System.Text.Json.JsonSerializer.SerializeToElement(t).GetProperty("name").GetString()!;
            if (name is "get_health" or "get_module_settings" || module == "all") return true;
            var models = name is "arm_model_continuation" or "request_model_switch" or "get_model_decision" or "record_model_handoff" or "request_supervisor_takeover" or "verify_model_runtime";
            return module == "models" ? models : !models;
        }).ToArray();
    }
    private static object[] Definitions() =>
    [
        Tool("get_health", "查询本地服务、排队与模型两个独立模块、排除名单及实际支持的能力。工具启用不代表模型切换已验证成功。"),
        Tool("get_module_settings", "读取测试排队与模型调度的独立开关。关闭某模块不影响另一模块；已有资源仍须清理，已有模型任务仍须返回主管。"),
        Tool("capture_workspace", "持有Granted的SolidWorks许可时，由实际STA执行器保存文档身份、视图、有序选择和任务进度。safePointConfirmed须由原窗口确认命令、部署及异步操作已停止；失败不让出。不保存、打开或关闭文档。不支持未保存文档。捕获后不得继续修改现场再用旧快照让出。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["generation"] = new { type = "integer", minimum = 1, maximum = int.MaxValue }, ["requestId"] = Text(), ["completedWork"] = Text(4000), ["remainingWork"] = Text(4000), ["safePointConfirmed"] = new { type = "boolean" } },
            ["leaseId", "token", "generation", "requestId", "completedWork", "remainingWork", "safePointConfirmed"]),
        Tool("yield_resource", "保存现场成功且已停止并清理宿主操作后，让出当前操作段，整项任务仍未完成。原窗口可继续思考或代码工作；恢复时须resume_resource重新排队。禁止按静默时间抢占。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["generation"] = new { type = "integer", minimum = 1, maximum = int.MaxValue }, ["checkpointId"] = Text(), ["cleanupConfirmed"] = new { type = "boolean" } }, ["leaseId", "token", "generation", "checkpointId", "cleanupConfirmed"]),
        Tool("resume_resource", "原窗口准备继续宿主操作时，用原许可申请恢复，排在当前已有等待者之后。返回新许可及令牌；Granted后仍须restore_workspace实际恢复并回读通过，才能继续原任务。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["checkpointId"] = Text(), ["requestId"] = Text() }, ["leaseId", "token", "checkpointId", "requestId"]),
        Tool("get_resource_checkpoint", "读取本许可关联的任务进度及现场记录。记录存在不代表当前现场已恢复；令牌不得公开。", Checkpoint(), ["leaseId", "token", "checkpointId"]),
        Tool("restore_workspace", "仅在新恢复许可Granted后，由实际STA执行器预检全部文档和选择引用，再恢复活动文档、视图和有序选择并回读核验。文档变化或恢复不明时阻塞并隔离，不继续原业务、不自动打开或保存文件。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["generation"] = new { type = "integer", minimum = 1, maximum = int.MaxValue }, ["checkpointId"] = Text() }, ["leaseId", "token", "generation", "checkpointId"]),
        Tool("get_resource_events", "按原许可或恢复许可读取持久化通知：已让出、已排队、获准需先恢复、恢复通过/阻塞。afterSequence为已处理游标。此查询不代表服务能自动唤醒空闲Codex聊天；未接通唤醒时不得声称提醒已送达。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["afterSequence"] = new { type = "integer", minimum = 0, maximum = int.MaxValue }, ["maxWaitSeconds"] = new { type = "integer", minimum = 0, maximum = 20 } }, ["leaseId", "token"]),
        Tool("request_resource", "按软件申请许可：solidworks仅用于MCP/COM；solidworks-ui同时占桌面输入；app:稳定软件标识仅锁该软件，app-ui:标识同时锁桌面输入；desktop只用于共享鼠标键盘。不同软件允许并行，编译和独立后台测试无需许可。请提供ownerThreadId为本聊天真实编号，服务核对运行记录用于无活动清理关联；sessionId仅为标签。MCP新申请默认必须接单，未启用唤醒也必须accept_resource；不要设置false跳过接单。许可不授权新开SolidWorks，默认复用现有实例，连接失败停止诊断，不循环创建。若用户单独授权排队接续，携带真实resumeThreadId、当前sourceTurnId和不含令牌的remainingWork原子绑定；会强制requireAcceptance。Offered只是预留，accept_resource返回Granted才操作。无绑定保持旧手动协议。Queued时禁止操作宿主；令牌只用于原许可，不转交其他窗口。最长1800秒，Granted过期保留隔离。",
            new() { ["requestId"] = Text(), ["sessionId"] = Text(), ["resource"] = Text(32), ["purpose"] = Text(2000), ["durationSeconds"] = new { type = "integer", minimum = 30, maximum = 1800 }, ["requireAcceptance"] = new {type="boolean",@default=true},["resumeThreadId"]=Text(),["sourceTurnId"]=Text(),["remainingWork"]=Text(4000),["ownerThreadId"]=Text() },
            ["requestId", "sessionId", "resource", "purpose"]),
        Tool("get_queue_binding", "用原许可令牌读取原任务的聊天绑定与接单状态；别名不是已验证窗口身份，绑定不代表已获许可。",new(){["leaseId"]=Text(),["token"]=Text()},["leaseId","token"]),
        Tool("accept_resource", "仅Offered可核对原任务并确认接单，需原令牌、offerId及当前generation。只有返回Granted才操作软件；过期通知不能当作许可。",new(){["leaseId"]=Text(),["token"]=Text(),["offerId"]=Text(),["generation"]=new{type="integer",minimum=1,maximum=int.MaxValue}},["leaseId","token","offerId","generation"]),
        Tool("retry_resource", "尚未Granted且处于WaitingForClient的原任务可在退避后排到队尾，最多三次接单机会。绑定聊天需本轮真实sourceTurnId；不得用于续期已授予或恢复隔离许可，不重建原测试。",new(){["leaseId"]=Text(),["token"]=Text(),["sourceTurnId"]=Text()},["leaseId","token"]),
        Tool("get_resource", "查询原窗口的许可。仅Granted可操作软件；RecoveryRequired须停止操作并清理。需要原令牌，不能将排队当作已获得资源。",
            new() { ["leaseId"] = Text(), ["token"] = Text() }, ["leaseId", "token"]),
        Tool("release_resource", "本段测试停止并清理后立即释放，再修改代码、编译或分析；测试失败但清理成功也应释放，让下一窗口并行测试。复测用新requestId重新排队。提供当前代次与令牌；cleanupConfirmed=false保留隔离。释放不代表测试通过。",
            new() { ["leaseId"] = Text(), ["token"] = Text(), ["generation"] = new { type = "integer", minimum = 0, maximum = int.MaxValue }, ["cleanupConfirmed"] = new { type = "boolean" } },
            ["leaseId", "token", "generation", "cleanupConfirmed"]),
        Tool("list_test_suites", "列出已登记套件。SimulationOnly 区分模拟与真实只读检查；sw-readonly 只读取已运行宿主的版本和文档数。"),
        Tool("submit_test", "立即提交登记套件并返回任务。sw-readonly 只连接已运行的 SolidWorks；不修改文档。sessionId 是客户端关联标签，不是已验证身份。",
            new() { ["requestId"] = Text(), ["sessionId"] = Text(), ["suiteId"] = Text() }, ["requestId", "sessionId", "suiteId"]),
        Tool("list_tests", "列出本地调度任务和状态。"),
        Tool("get_test_status", "读取测试状态；Queued 不代表通过。", Job(), ["jobId"]),
        Tool("wait_test", "有界等待测试，最长 20 秒。", new() { ["jobId"] = Text(), ["maxWaitSeconds"] = new { type = "integer", minimum = 0, maximum = 20 } }, ["jobId"]),
        Tool("get_test_report", "读取报告、套件摘要及日志。simulationOnly=true 表示模拟；真实只读报告也不能作为插件功能验收。", Job(), ["jobId"]),
        Tool("cancel_test", "取消排队或运行任务；不会清除恢复隔离。", Job(), ["jobId"]),
        Tool("set_queue_paused", "暂停或恢复派发，运行中的测试保持执行。", new() { ["paused"] = new { type = "boolean" } }, ["paused"]),
        Tool("record_model_handoff", "主管周期回执：sending→sent→user_verified(执行生效)→work_done/work_failed/work_cancelled/work_timeout→return_sending→return_sent→supervisor_verified。user_verified/supervisor_verified需用户原始确认；runtime_verified须从本轮真实运行记录读取observedModel和observedEffort并附证据，不能照抄目标值；工作结束必须使用返回计划回主管。failed表示发送结果不明并保持隔离；user_override仅用于用户明确改变设置或停止交接。不得自动重发。交接提示须追加decisionId和回执方法，让下一轮延续同一周期，token不得公开。",
            new() { ["decisionId"] = Text(), ["token"] = Text(), ["outcome"] = Text(32), ["evidence"] = Text(4000), ["observedModel"] = Text(), ["observedEffort"] = Text(32) }, ["decisionId", "token", "outcome", "evidence"]),
        Tool("request_model_switch", "由用户配置的主管模型规划限定任务并选择执行模型/强度；必须提供plannerModel、plannerEffort及completionCriteria，调用方核对实际主管身份，不可伪报。未授权聊天返回UNSUPPORTED；已授权且已配置主管时返回Execution计划，Codex核对真实聊天和授权后用内置send_message_to_thread执行。先登记sending，至多发送一次。执行结束必须读取同周期返回计划并返回主管；执行模型不能开启下一委派。程序不直接发送，发送回执与模型生效分别核验。",
            new() { ["requestId"] = Text(), ["sessionId"] = Text(), ["model"] = Text(), ["effort"] = Text(32), ["reason"] = Text(2000), ["handoff"] = Text(16000), ["plannerModel"] = Text(), ["plannerEffort"] = Text(32), ["completionCriteria"] = Text(2000) },
            ["requestId", "sessionId", "model", "effort", "reason", "handoff"]),
        Tool("arm_model_continuation", "已授权当前聊天发送一次交接并登记sent/return_sent后，沿同周期登记一次续跑。只在已发送待核验方向使用，等待当前轮正常结束后再唤起。调用前核对实际聊天与人类授权；已有登记不重复启动，失败不自动重试。返回WAITING不代表下一轮或模型已生效，下一轮仍须verify_model_runtime。", new() { ["decisionId"] = Text(), ["token"] = Text() }, ["decisionId", "token"]),
        Tool("get_model_decision", "读取已登记的模型建议及执行能力状态。", new() { ["decisionId"] = Text() }, ["decisionId"]),
        Tool("verify_model_runtime", "由服务只读核验已绑定Codex目录中本聊天实际轮次上下文，校验聊天编号、模型、强度和本方向发送时间，再登记生效；不用调用方照抄目标值。不匹配或不可读取时保持等待，不能据此重发、重跑操作或覆盖用户设置。上下文证据不保证每个推理步骤的实际模型。",
            new() { ["decisionId"] = Text(), ["token"] = Text() }, ["decisionId", "token"]),
        Tool("request_supervisor_takeover", "执行模型在任务未完成时请求已配置主管接管。保存当前进度并生成同周期返回计划；不是新委派，不重跑工具、不终止进程、不释放许可。关闭模型调度后仍可返回主管。按原返回计划登记return_sending、发送一次、return_sent并核验主管真实生效。内容只能包含关联编号，不得包含任何令牌。",
            new() { ["decisionId"] = Text(), ["token"] = Text(), ["reason"] = Text(2000), ["completedWork"] = Text(4000), ["remainingWork"] = Text(4000), ["runningOperations"] = Text(2000), ["resourceState"] = Text(2000) },
            ["decisionId", "token", "reason", "completedWork", "remainingWork", "runningOperations", "resourceState"])
    ];
}
