using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatch.Core;

public sealed record FreezeRequest(string Name, string SourceRoot, string[] RelativePaths);
public sealed record CandidateFile(string RelativePath, long Length, string Sha256);
public sealed record Candidate(string Id, string Name, string SourceRoot, string SnapshotRoot, DateTimeOffset CreatedAt, CandidateFile[] Files, string ManifestHash);
public sealed record CandidateVerification(string Id, bool Valid, string[] Errors);

public sealed class CandidateStore(string root)
{
    private readonly object gate = new();
    private readonly string directory = Path.GetFullPath(root);
    private const long MaxTotal = 256L * 1024 * 1024;

    public Candidate Freeze(FreezeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || string.IsNullOrWhiteSpace(request.SourceRoot)
            || request.RelativePaths is not { Length: > 0 and <= 128 }) throw Invalid("请输入名称、产物目录和1至128个相对文件路径。");
        if (!Path.IsPathFullyQualified(request.SourceRoot) || request.SourceRoot.StartsWith("\\\\", StringComparison.Ordinal)) throw Invalid("请选择本机绝对目录；当前不接入网络共享。");
        lock (gate)
        {
            var source = Path.GetFullPath(request.SourceRoot);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("产物目录不存在。");
            RejectLinks(source);
            var paths = request.RelativePaths.Select(Normalize).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw Invalid("文件清单存在重复路径。");
            var streams = new List<(string Path, FileStream Stream)>();
            string? staging = null;
            try
            {
                long total = 0;
                // 同时持有所有输入，只读共享，防止复制期间构建器改写或替换已打开文件。
                foreach (var relative in paths)
                {
                    var input = Inside(source, relative); RejectLinks(input);
                    var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add((relative, stream));
                    total += stream.Length;
                    if (total > MaxTotal) throw Invalid("本批产物总量超过256MB，请缩小文件清单。");
                }
                Directory.CreateDirectory(directory); RejectLinks(directory);
                var id = Guid.NewGuid().ToString("N");
                staging = Path.Combine(directory, id + ".pending");
                var final = Path.Combine(directory, id);
                Directory.CreateDirectory(Path.Combine(staging, "files"));
                var files = new List<CandidateFile>();
                foreach (var item in streams)
                {
                    var destination = Inside(Path.Combine(staging, "files"), item.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { item.Stream.CopyTo(output); output.Flush(true); }
                    using var copied = File.OpenRead(destination);
                    files.Add(new(item.Path, copied.Length, Convert.ToHexString(SHA256.HashData(copied))));
                }
                var candidate = new Candidate(id, request.Name.Trim(), source, final, DateTimeOffset.UtcNow, files.ToArray(), Hash(files));
                File.WriteAllText(Path.Combine(staging, "candidate.json"), JsonSerializer.Serialize(candidate, Json.Options), new UTF8Encoding(false));
                Directory.Move(staging, final); // 完成后才可被列表发现；未完成目录保留用于诊断。
                return candidate;
            }
            catch (Exception ex)
            {
                new Diagnostics(Path.Combine(directory, "logs")).Write("FREEZE_FAILED", "产物冻结失败；未完成副本不作为可用版本。", staging, ex);
                throw;
            }
            finally { foreach (var item in streams) item.Stream.Dispose(); }
        }
    }

    public IReadOnlyList<Candidate> List()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
                .Where(id => id is not null && IsId(id)).Select(id => Get(id!)).OrderByDescending(c => c.CreatedAt).ToArray();
        }
    }
    public Candidate Get(string id)
    {
        if (!IsId(id)) throw Invalid("无效的候选编号。");
        var folder = Path.Combine(directory, id); RejectLinks(folder);
        var json = Path.Combine(folder, "candidate.json"); RejectLinks(json);
        var candidate = JsonSerializer.Deserialize<Candidate>(File.ReadAllText(json), Json.Options) ?? throw Invalid("候选清单无效。");
        if (candidate.Id != id || !string.Equals(candidate.SnapshotRoot, folder, StringComparison.OrdinalIgnoreCase)
            || candidate.Files is not { Length: > 0 and <= 128 } || candidate.ManifestHash != Hash(candidate.Files)) throw Invalid("候选清单校验失败。");
        foreach (var file in candidate.Files) _ = Normalize(file.RelativePath);
        return candidate;
    }
    public CandidateVerification Verify(string id)
    {
        lock (gate)
        {
            var errors = new List<string>();
            try
            {
                var candidate = Get(id);
                foreach (var file in candidate.Files)
                {
                    var path = Inside(Path.Combine(directory, id, "files"), file.RelativePath); RejectLinks(path);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length != file.Length || Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256) errors.Add("内容不一致：" + file.RelativePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DispatchException or JsonException)
            { errors.Add(ex.Message); }
            return new(id, errors.Count == 0, errors.ToArray());
        }
    }
    private static bool IsId(string value) => value.Length == 32 && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string Hash(IEnumerable<CandidateFile> files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files, Json.Options))));
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':')) throw Invalid("只允许产物目录内的相对路径。");
        var parts = value.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw Invalid("文件路径不能越界或包含空目录段。");
        return string.Join('/', parts);
    }
    private static string Inside(string parent, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(parent, Normalize(relative)));
        if (!path.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("路径越界。");
        return path;
    }
    private static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("不接受符号链接或重解析目录。");
            var parent = Path.GetDirectoryName(current); if (parent is null || parent == current) return; current = parent;
        }
    }
    private static DispatchException Invalid(string message) => new("INVALID_CANDIDATE", message);
}
