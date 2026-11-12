using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Dispatch.Core;

public sealed record ServiceRequest(int Version, string RequestId, string Method, JsonElement Arguments);
public sealed record ServiceReply(string RequestId, bool Ok, JsonElement? Result = null, string? Code = null, string? Message = null);

public static class Transport
{
    public const int MaxChars = 128 * 1024;
    public const int MaxReplyChars = 4 * 1024 * 1024;
    private sealed class LineBuffer
    {
        public char[] Chars { get; } = new char[4096];
        public int Offset { get; set; }
        public int Count { get; set; }
        public bool DiscardUntilNewline { get; set; }
    }
    // MCP同一输入流可连续收到多条消息，预读尾部须随reader保留，不能吞掉下一条。
    private static readonly ConditionalWeakTable<TextReader, LineBuffer> LineBuffers = new();
    public static string PipeName(string instance)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(instance, "^[a-zA-Z0-9-]{1,64}$")) throw new ArgumentException("无效的服务实例名。");
        var identity = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : Environment.UserName;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? throw new InvalidOperationException("无法获取用户身份"))))[..20];
        return $"CodexDispatch-{hash}-{instance}";
    }
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token,int maxChars=MaxChars)
    {
        var result = new StringBuilder();
        var buffer = LineBuffers.GetValue(reader, _ => new LineBuffer());
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Offset == buffer.Count)
            {
                buffer.Count = await reader.ReadAsync(buffer.Chars.AsMemory(), token);
                buffer.Offset = 0;
                if (buffer.Count == 0) return result.Length == 0 ? null : result.ToString();
            }
            var newline = Array.IndexOf(buffer.Chars, '\n', buffer.Offset, buffer.Count - buffer.Offset);
            if (buffer.DiscardUntilNewline)
            {
                buffer.Offset = newline < 0 ? buffer.Count : newline + 1;
                buffer.DiscardUntilNewline = newline < 0;
                continue;
            }
            var length = (newline < 0 ? buffer.Count : newline) - buffer.Offset;
            var start = buffer.Offset;
            buffer.Offset += length + (newline < 0 ? 0 : 1);
            if ((long)result.Length + length > maxChars)
            {
                buffer.DiscardUntilNewline = newline < 0;
                throw new DispatchException("MESSAGE_TOO_LARGE", "消息超过允许大小。");
            }
            result.Append(buffer.Chars, start, length);
            if (newline >= 0) return result.ToString().TrimEnd('\r');
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
            var line = await ReadLineAsync(reader, deadline.Token,MaxReplyChars) ?? throw new IOException("服务连接已关闭。");
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
