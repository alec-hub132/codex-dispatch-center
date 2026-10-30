using System.Diagnostics;
using System.Runtime.InteropServices;
using Dispatch.Core;
using SolidWorks.Interop.sldworks;

namespace Runner.Workspace;

internal sealed class SolidWorksHost : IWorkspaceHost, IDisposable
{
    private readonly List<object> references = [];
    private readonly ISldWorks app;
    private readonly Process process;
    private readonly Mutex exclusive;
    private readonly string[]? scope;
    private readonly Dictionary<string, IModelDoc2> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object[]> resolved = new(StringComparer.OrdinalIgnoreCase);
    private SolidWorksHost(object application, Process host, Mutex mutex, string[]? documentScope)
    {
        app = Track((ISldWorks)application); process = host; exclusive = mutex; scope = documentScope;
        if (app.GetProcessID() != host.Id) throw new InvalidOperationException("COM对象与目标宿主进程不匹配。");
    }
    private T Track<T>(T value) where T : class
    {
        if (!references.Any(r => ReferenceEquals(r, value))) references.Add(value);
        return value;
    }
    public static SolidWorksHost Connect(string[]? documentScope = null)
    {
        var processes = Process.GetProcessesByName("SLDWORKS");
        if (processes.Length != 1 || processes[0].SessionId != Process.GetCurrentProcess().SessionId)
        { foreach (var p in processes) p.Dispose(); throw new InvalidOperationException("需要唯一且同一Windows登录会话的SolidWorks宿主。"); }
        var mutex = new Mutex(false, "Global\\CodexDispatch-SolidWorks-Readonly");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { mutex.ReleaseMutex(); throw new InvalidOperationException("宿主适配器上次异常退出，需确认现场后再试。"); }
            if (!acquired) throw new InvalidOperationException("另一宿主适配器正在运行。");
            Marshal.ThrowExceptionForHR(CLSIDFromProgID("SldWorks.Application", out var clsid));
            Marshal.ThrowExceptionForHR(GetActiveObject(ref clsid, IntPtr.Zero, out var application));
            return new(application, processes[0], mutex, documentScope);
        }
        catch { if (acquired) mutex.ReleaseMutex(); mutex.Dispose(); processes[0].Dispose(); throw; }
    }
    private void Idle()
    {
        app.GetRunningCommandInfo(out _, out _, out var active);
        if (active) throw new InvalidOperationException("SolidWorks命令或属性管理页仍在运行，当前不能让出或恢复。");
        if (app.CommandInProgress) throw new InvalidOperationException("SolidWorks仍处于程序命令序列中。");
    }
    public WorkspaceScene ReadScene()
    {
        Idle(); documents.Clear();
        var values = app.GetDocuments() as object[] ?? [];
        if (values.Length > 64) throw new InvalidOperationException("打开文档超过现场快照上限。");
        if (values.Length == 0 && app.GetDocumentCount() != 0) throw new InvalidOperationException("无法取得完整的打开文档清单。");
        var active = app.ActiveDoc is {} obj ? Track((IModelDoc2)obj) : null;
        var activePath = active?.GetPathName();
        if (scope is null && active is not null && string.IsNullOrWhiteSpace(activePath))
            throw new InvalidOperationException("当前文档未保存，无法可靠恢复文档身份；保留原占用。");
        var requested = new HashSet<string>(scope ?? (activePath is { Length: > 0 } ? [Path.GetFullPath(activePath)] : []), StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<WorkspaceDocument>();
        var titles = new List<string>();
        foreach (var item in values)
        {
            var doc = Track((IModelDoc2)item);
            titles.Add(doc.GetTitle());
            var id = doc.GetPathName();
            if (string.IsNullOrWhiteSpace(id)) continue;
            id = Path.GetFullPath(id);
            if (!requested.Contains(id)) continue;
            var stamp = doc.GetUpdateStamp(); var dirty = doc.GetSaveFlag();
            var file = new FileInfo(id);
            if (!file.Exists) throw new InvalidOperationException("原文档文件不存在，不能可靠恢复。");
            if (!documents.TryAdd(id, doc)) throw new InvalidOperationException("文档路径不唯一。");
            var view = Track((IModelView)doc.ActiveView);
            var matrix = Track((IMathTransform)view.Orientation3);
            var translation = Track((IMathVector)view.Translation3);
            var selection = Track((ISelectionMgr)doc.SelectionManager);
            var extension = Track((IModelDocExtension)doc.Extension);
            var count = selection.GetSelectedObjectCount2(-1);
            if (count > 128) throw new InvalidOperationException("选中对象超过恢复上限。");
            var selections = new List<WorkspaceSelection>();
            for (var i = 1; i <= count; i++)
            {
                var selected = Track(selection.GetSelectedObject6(i, -1));
                if (extension.GetPersistReference3(selected) is not byte[] reference || reference.Length == 0)
                    throw new InvalidOperationException("选中对象不支持持久引用，不能安全让出。");
                selections.Add(new(Convert.ToBase64String(reference), selection.GetSelectedObjectMark(i)));
            }
            var type = doc.GetType();
            var configuration = type == 3 ? null : Track((IConfiguration)Track((IConfigurationManager)doc.ConfigurationManager).ActiveConfiguration).Name;
            var sheet = type == 3 ? Track((ISheet)((IDrawingDoc)doc).GetCurrentSheet()).GetName() : null;
            if (doc.GetUpdateStamp() != stamp || doc.GetSaveFlag() != dirty) throw new InvalidOperationException("读取现场时模型发生变化，快照无效。");
            snapshots.Add(new(id, doc.GetTitle(), type, stamp, dirty, configuration, sheet,
                (double[])matrix.ArrayData, (double[])translation.ArrayData, view.Scale2, selections.ToArray(), view.GetViewHWndx64(), file.Length, file.LastWriteTimeUtc.Ticks));
        }
        if (snapshots.Count != requested.Count) throw new InvalidOperationException("检查点关联的原文档已关闭，停止恢复。");
        if (snapshots.Any(d => titles.Count(t => string.Equals(t, d.Title, StringComparison.OrdinalIgnoreCase)) > 1))
            throw new InvalidOperationException("打开文档标题重复，无法保证安全激活。");
        var scene = new WorkspaceScene(process.Id, process.StartTime.ToUniversalTime(), app.RevisionNumber(), snapshots.ToArray(),
            active is null ? null : string.IsNullOrWhiteSpace(activePath) ? "unsaved:" + active.GetTitle() : Path.GetFullPath(activePath));
        WorkspaceRestorer.Validate(scene, false); Idle(); return scene;
    }
    public void ValidateSelections(WorkspaceScene scene)
    {
        resolved.Clear();
        foreach (var snapshot in scene.Documents)
        {
            var ext = Track((IModelDocExtension)documents[snapshot.Id].Extension);
            resolved[snapshot.Id] = snapshot.Selection.Select(s =>
            {
                var value = ext.GetObjectByPersistReference3(Convert.FromBase64String(s.Reference), out var error);
                if (error != 0 || value is null) throw new InvalidOperationException("原选择对象已失效。");
                return Track(value);
            }).ToArray();
        }
    }
    private void Activate(WorkspaceDocument d)
    {
        var error = 0;
        var active = app.ActivateDoc3(d.Title, false, 1 /* swDontRebuildActiveDoc */, ref error);
        if (error != 0 || active is null || !string.Equals(Track((IModelDoc2)active).GetPathName(), d.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("未能激活已打开的原文档；未打开其他文件。");
    }
    public void ApplyScene(WorkspaceScene scene)
    {
        Idle();
        var math = Track((IMathUtility)app.GetMathUtility());
        foreach (var snapshot in scene.Documents)
        {
            var doc = documents[snapshot.Id];
            if (doc.GetUpdateStamp() != snapshot.UpdateStamp || doc.GetSaveFlag() != snapshot.Dirty)
                throw new InvalidOperationException("恢复前文档再次发生变化。");
            Activate(snapshot);
            var view = Track((IModelView)doc.ActiveView);
            view.Orientation3 = Track((MathTransform)math.CreateTransform(snapshot.Orientation));
            view.Scale2 = snapshot.Scale;
            view.Translation3 = Track((MathVector)math.CreateVector(snapshot.Translation));
            var selection = Track((ISelectionMgr)doc.SelectionManager);
            var extension = Track((IModelDocExtension)doc.Extension);
            doc.ClearSelection2(true);
            for (var i = 0; i < snapshot.Selection.Length; i++)
            {
                var data = Track((ISelectData)selection.CreateSelectData()); data.Mark = snapshot.Selection[i].Mark;
                if (extension.MultiSelect2(new[] { resolved[snapshot.Id][i] }, true, data) != 1)
                    throw new InvalidOperationException("恢复有序选择失败。");
            }
            doc.GraphicsRedraw2();
        }
        if (scene.ActiveDocumentId is {} id) Activate(scene.Documents.Single(d => d.Id == id));
        Idle();
    }
    public bool SameSelection(WorkspaceScene expected, WorkspaceScene actual)
    {
        foreach (var d in expected.Documents)
        {
            var other = actual.Documents.Single(a => a.Id == d.Id);
            if (other.Selection.Length != d.Selection.Length) return false;
            var ext = Track((IModelDocExtension)documents[d.Id].Extension);
            for (var i = 0; i < d.Selection.Length; i++)
                if (other.Selection[i].Mark != d.Selection[i].Mark || ext.IsSamePersistentID(Convert.FromBase64String(d.Selection[i].Reference), Convert.FromBase64String(other.Selection[i].Reference)) != 1)
                    return false;
        }
        return true;
    }
    public void Dispose()
    {
        Exception? failure = null;
        foreach (var reference in references.AsEnumerable().Reverse())
            try { if (Marshal.IsComObject(reference)) Marshal.FinalReleaseComObject(reference); }
            catch (Exception ex) { failure ??= ex; }
        process.Dispose(); exclusive.ReleaseMutex(); exclusive.Dispose();
        if (failure is not null) throw new InvalidOperationException("COM引用清理状态不明。", failure);
    }
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = true)] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object application);
}
