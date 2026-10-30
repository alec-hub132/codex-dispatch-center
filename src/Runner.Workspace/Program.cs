using System.Text.Json;
using Dispatch.Core;
using Runner.Workspace;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 只由服务在有效资源许可及操作锁内调用。没有泛用脚本或保存/打开/关闭入口。
        try
        {
            if (args.Length != 1 || args[0] is not ("capture" or "restore" or "--environment-only")) return 2;
            if (args[0] == "--environment-only")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { environmentOnly = true, hostAccessed = false })); return 0;
            }
            WorkspaceScene? scene = null;
            if (args[0] == "restore")
            {
                var input = Transport.ReadLineAsync(Console.In, CancellationToken.None).GetAwaiter().GetResult();
                scene = JsonSerializer.Deserialize<WorkspaceScene>(input ?? "", Json.Options) ?? throw new InvalidDataException("现场数据为空。");
                WorkspaceRestorer.Validate(scene);
            }
            object result;
            using (var host = SolidWorksHost.Connect(scene?.Documents.Select(d => d.Id).ToArray()))
            {
                if (args[0] == "capture") result = host.ReadScene();
                else
                {
                    result = WorkspaceRestorer.Restore(host, scene!);
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(result, Json.Options));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("宿主现场适配器失败：" + ex); return 1; }
    }
}
