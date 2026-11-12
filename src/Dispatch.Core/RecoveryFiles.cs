using System.Text.Json;

namespace Dispatch.Core;

public static class RecoveryFiles
{
    public static bool IsAddinPerformanceNotice(IEnumerable<string> controls)
    {
        var labels = controls.ToArray();
        return labels.Any(text => text.Trim() == "插件加载性能")
            && labels.Any(text => text.Contains("您的插件影响了 SOLIDWORKS 的启动速度", StringComparison.Ordinal));
    }
    public static bool IsCrashDialog(string title, IEnumerable<string> controls)
    {
        if (IsCrashCaption(title)) return true;
        if (!title.Contains("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) && !title.Contains("SLDWORKS", StringComparison.OrdinalIgnoreCase)) return false;
        var labels = controls.Select(text => text.Replace("&", "").Trim()).ToArray();
        var preview = labels.Any(text => text.Contains("预览报告", StringComparison.OrdinalIgnoreCase) || text.Contains("Preview Report", StringComparison.OrdinalIgnoreCase));
        var restart = labels.Any(text => text.Contains("Restart SOLIDWORKS", StringComparison.OrdinalIgnoreCase) || text.Contains("重新启动", StringComparison.OrdinalIgnoreCase) || text.Contains("重启SOLIDWORKS", StringComparison.OrdinalIgnoreCase));
        return preview && restart;
    }
    public static bool IsCrashCaption(string title)
    {
        if(!title.Contains("SOLIDWORKS",StringComparison.OrdinalIgnoreCase) && !title.Contains("SLDWORKS",StringComparison.OrdinalIgnoreCase))return false;
        return new[]{"错误报告","error report","已停止工作","已停止运行","stopped working","encountered a problem"}.Any(v=>title.Contains(v,StringComparison.OrdinalIgnoreCase));
    }
    // 仅隔离指定恢复目录内的 .swar，绝不按 CAD 后缀删除整个目录。
    public static int Isolate(string recoveryDirectory,string dataRoot,string incidentId,Func<bool> allowed)
    {
        if(!Guid.TryParseExact(incidentId,"N",out _) || !Path.IsPathFullyQualified(recoveryDirectory))throw new InvalidDataException("恢复路径或事故编号无效。");
        var source=Path.GetFullPath(recoveryDirectory);
        if(source.StartsWith("\\\\",StringComparison.Ordinal) || source.TrimEnd(Path.DirectorySeparatorChar)==Path.GetPathRoot(source)?.TrimEnd(Path.DirectorySeparatorChar))throw new InvalidDataException("恢复目录不能是网络或卷根目录。");
        if(!Directory.Exists(source))return 0;
        CheckPath(source);
        var destination=Path.GetFullPath(Path.Combine(dataRoot,"diagnostics","host-recovery",incidentId));
        Directory.CreateDirectory(destination);CheckPath(destination);
        if(destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("隔离目录不能位于恢复目录内。");
        var moved=new List<string>();
        foreach(var path in Directory.EnumerateFiles(source,"*",SearchOption.TopDirectoryOnly).Where(p=>Path.GetExtension(p).Equals(".swar",StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            if(!allowed())throw new OperationCanceledException("设置已关闭，停止后续恢复文件隔离。");
            CheckPath(path);
            var target=Path.Combine(destination,Path.GetFileName(path));
            File.WriteAllText(Path.Combine(destination,"manifest.json"),JsonSerializer.Serialize(new{source,moved,next=Path.GetFileName(path),state="MOVING"},Json.Options));
            File.Move(path,target,false);moved.Add(Path.GetFileName(path));
            File.WriteAllText(Path.Combine(destination,"manifest.json"),JsonSerializer.Serialize(new{source,moved,state="ISOLATED"},Json.Options));
        }
        return moved.Count;
    }
    private static void CheckPath(string path)
    {
        for(var p=Path.GetFullPath(path);p is not null;p=Path.GetDirectoryName(p))
            if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("恢复路径包含重解析点，未移动文件。");
    }
}
