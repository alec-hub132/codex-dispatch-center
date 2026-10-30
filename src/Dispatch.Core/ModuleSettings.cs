namespace Dispatch.Core;

public sealed record ModuleSettings(bool QueueEnabled = true, bool ModelsEnabled = true, int Version = 0);

public sealed partial class Scheduler
{
    public ModuleSettings GetModules()
    {
        lock (gate) return store.Load<ModuleSettings>("modules").SingleOrDefault() ?? new();
    }

    public ModuleSettings SetModules(ModuleSettings requested)
    {
        lock (gate)
        {
            var current = GetModules();
            if (requested.Version != current.Version)
                throw new DispatchException("SETTINGS_CHANGED", "模块设置已被其他窗口修改，请刷新后重新选择。");
            if (requested.QueueEnabled == current.QueueEnabled && requested.ModelsEnabled == current.ModelsEnabled) return current;
            var next = requested with { Version = checked(current.Version + 1) };
            store.Save("modules", "global", next);
            Log.Write("MODULES_UPDATED", $"测试排队：{(next.QueueEnabled ? "启用" : "关闭")}；模型调度：{(next.ModelsEnabled ? "启用" : "关闭")}。已有资源仍需清理，已有模型周期仍须返回主管。");
            return next;
        }
    }

    private void RequireQueueEnabled()
    {
        if (!GetModules().QueueEnabled)
            throw new DispatchException("QUEUE_DISABLED", "测试排队已关闭，不接受新任务或许可。关闭不代表可以绕过共享资源占用；已有许可仍可清理释放。");
    }

    private void RequireModelsEnabled()
    {
        if (!GetModules().ModelsEnabled)
            throw new DispatchException("MODELS_DISABLED", "模型调度已关闭，不接受新委派。已有任务仍可请求主管接管并按原周期返回。");
    }
}
