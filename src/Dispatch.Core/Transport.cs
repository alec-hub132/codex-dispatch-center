using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core;

public sealed record ServiceRequest(int Version, string RequestId, string Method, JsonElement Arguments);
public sealed record ServiceReply(string RequestId, bool Ok, JsonElement? Result = null, string? Code = null, string? Message = null);

public static class Transport
{
    public const int MaxChars = 128 * 1024;
    public static string PipeName(string instance)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(instance, "^[a-zA-Z0-9-]{1,64}$")) throw new ArgumentException("无效的服务实例名。");
        var identity = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? throw new InvalidOperationException("无法获取用户身份"))))[..20];
        return $"CodexDispatch-{hash}-{instance}";
    }
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= MaxChars) throw new DispatchException("MESSAGE_TOO_LARGE", "消息超过允许大小。");
            result.Append(buffer[0]);
        }
    }
    public static async Task<ServiceReply> Call(string instance, string method, JsonElement arguments, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        using var pipe = new NamedPipeClientStream(".", PipeName(instance), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var request = new ServiceRequest(1, Guid.NewGuid().ToString("N"), method, arguments);
        try
        {
            await pipe.ConnectAsync(1200, deadline.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json.Options).AsMemory(), deadline.Token);
            var line = await ReadLineAsync(reader, deadline.Token) ?? throw new IOException("服务连接已关闭。");
            var reply = JsonSerializer.Deserialize<ServiceReply>(line, Json.Options) ?? throw new IOException("无效服务响应。");
            if (reply.RequestId != request.RequestId) throw new IOException("服务响应编号不匹配。");
            return reply;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return new(request.RequestId, false, Code: "TEST_CENTER_OFFLINE", Message: "调度服务未运行或通信超时；请启动服务，不要绕过队列。");
        }
    }
    public static string Argument(string[] args, string key, string fallback)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{key} 缺少参数。");
        return args[index + 1];
    }
}
