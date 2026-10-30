using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed class Diagnostics(string root)
{
    private readonly object gate = new();
    public void Write(string code, string message, string? jobId = null, Exception? error = null)
    {
        var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, code, message, jobId, error = error?.ToString() }, Json.Options);
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "bug.jsonl"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("日志写入失败：" + ex.Message);
            }
        }
    }
}
