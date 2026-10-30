using System.Text.Json;

if (args.Length != 2 || !int.TryParse(args[0], out var duration) || duration is < 0 or > 30000 ||
    args[1] is not ("pass" or "fail" or "crash" or "cleanup-fail")) return 2;
await Task.Delay(duration);
if (args[1] == "crash") return 17;
Console.WriteLine(JsonSerializer.Serialize(new { passed = args[1] == "pass", cleanupOk = args[1] != "cleanup-fail", summary = "模拟测试结束；未操作 SolidWorks" }));
return 0;
