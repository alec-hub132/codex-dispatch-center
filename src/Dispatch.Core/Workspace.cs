namespace Dispatch.Core;

public sealed record WorkspaceSelection(string Reference, int Mark);
public sealed record WorkspaceDocument(string Id, string Title, int Type, int UpdateStamp, bool Dirty, string? Configuration,
    string? Sheet, double[] Orientation, double[] Translation, double Scale, WorkspaceSelection[] Selection,
    long ViewHandle = 0, long FileLength = 0, long FileWriteTicks = 0);
public sealed record WorkspaceScene(int ProcessId, DateTimeOffset ProcessStartedAt, string Revision, WorkspaceDocument[] Documents, string? ActiveDocumentId);
public sealed record WorkspaceRestoreResult(bool Success, bool MutationStarted, string Message);
public interface IWorkspaceHost
{
    WorkspaceScene ReadScene();
    void ValidateSelections(WorkspaceScene scene);
    void ApplyScene(WorkspaceScene scene);
    bool SameSelection(WorkspaceScene expected, WorkspaceScene actual);
}

public static class WorkspaceRestorer
{
    public static void Validate(WorkspaceScene scene, bool requireActiveIncluded = true)
    {
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(scene, Json.Options).Length > 80 * 1024)
            throw new DispatchException("WORKSPACE_TOO_LARGE", "现场超过传输上限，保留资源占用，不创建不完整快照。");
        if (scene.ProcessId <= 0 || string.IsNullOrWhiteSpace(scene.Revision) || scene.Documents is null || scene.Documents.Length > 64 ||
            scene.Documents.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scene.Documents.Length ||
            (requireActiveIncluded && scene.ActiveDocumentId is not null && !scene.Documents.Any(d => string.Equals(d.Id, scene.ActiveDocumentId, StringComparison.OrdinalIgnoreCase))))
            throw new DispatchException("INVALID_WORKSPACE", "宿主或文档身份信息无效。");
        foreach (var d in scene.Documents)
        {
            if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > 2000 || d.Title.Length > 512 || d.Type is < 1 or > 3 ||
                d.Orientation.Length != 16 || d.Translation.Length != 3 || !double.IsFinite(d.Scale) || d.Scale <= 0 ||
                d.Orientation.Concat(d.Translation).Any(v => !double.IsFinite(v)) || d.Selection.Length > 128)
                throw new DispatchException("INVALID_WORKSPACE", "文档、视图或选择快照无效或超过上限。");
            foreach (var selection in d.Selection)
                if (selection.Mark < 0 || selection.Reference.Length > 8192 || Convert.FromBase64String(selection.Reference).Length == 0)
                    throw new DispatchException("INVALID_WORKSPACE", "选择持久引用无效。");
        }
    }
    public static WorkspaceRestoreResult Restore(IWorkspaceHost host, WorkspaceScene expected)
    {
        var changed = false;
        try
        {
            Validate(expected);
            var before = host.ReadScene(); Validate(before, false);
            CheckDocuments(expected, before);
            host.ValidateSelections(expected); // 所有引用都先解析，失败时不能先清空选择或激活文档。
            changed = true;
            host.ApplyScene(expected);
            var after = host.ReadScene(); Validate(after, false);
            CheckDocuments(expected, after);
            if (!string.Equals(expected.ActiveDocumentId, after.ActiveDocumentId, StringComparison.OrdinalIgnoreCase) || !host.SameSelection(expected, after))
                throw new DispatchException("WORKSPACE_MISMATCH", "恢复后活动文档或选择未匹配。");
            foreach (var d in expected.Documents)
            {
                var actual = after.Documents.Single(x => string.Equals(x.Id, d.Id, StringComparison.OrdinalIgnoreCase));
                if (!Close(d.Scale, actual.Scale) || !d.Orientation.Zip(actual.Orientation).All(p => Close(p.First, p.Second)) ||
                    !d.Translation.Zip(actual.Translation).All(p => Close(p.First, p.Second)))
                    throw new DispatchException("WORKSPACE_MISMATCH", "恢复后视图回读未匹配。");
            }
            return new(true, changed, "已核对原文档、视图及有序选择，现场恢复并回读通过；可以继续原任务。");
        }
        catch (Exception ex) { return new(false, changed, "现场未确认恢复：" + ex.Message); }
    }
    private static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-8 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
    private static void CheckDocuments(WorkspaceScene expected, WorkspaceScene actual)
    {
        if (expected.ProcessId != actual.ProcessId || expected.ProcessStartedAt != actual.ProcessStartedAt || expected.Revision != actual.Revision)
            throw new DispatchException("HOST_CHANGED", "原SolidWorks宿主已改变，不恢复旧现场。");
        if (expected.ActiveDocumentId is null && actual.ActiveDocumentId is not null)
            throw new DispatchException("DOCUMENT_CHANGED", "原现场没有活动文档，当前已有文档；不通过关闭文档恢复空现场。");
        foreach (var d in expected.Documents)
        {
            var a = actual.Documents.SingleOrDefault(x => string.Equals(x.Id, d.Id, StringComparison.OrdinalIgnoreCase));
            if (a is null || a.Title != d.Title || a.Type != d.Type || a.UpdateStamp != d.UpdateStamp || a.Dirty != d.Dirty || a.Configuration != d.Configuration || a.Sheet != d.Sheet ||
                a.ViewHandle != d.ViewHandle || a.FileLength != d.FileLength || a.FileWriteTicks != d.FileWriteTicks)
                throw new DispatchException("DOCUMENT_CHANGED", "原文档已关闭、模型或配置/图纸发生变化；停止自动恢复。");
        }
    }
}
