using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class CandidateTests
{
    private readonly string root = Path.Combine(Path.Combine(Path.GetTempPath(), "candidate-tests"), Guid.NewGuid().ToString("N"));
    private string Prepare()
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "sample.dll"), "version-one"); return source;
    }
    [Fact]
    public void Frozen_copy_is_immutable_relative_to_source_and_tamper_is_detected()
    {
        var source = Prepare(); var store = new CandidateStore(Path.Combine(root, "candidates"));
        var candidate = store.Freeze(new("测试候选", source, ["sample.dll"]));
        File.WriteAllText(Path.Combine(source, "sample.dll"), "version-two");
        Assert.True(store.Verify(candidate.Id).Valid);
        Assert.Equal("version-one", File.ReadAllText(Path.Combine(candidate.SnapshotRoot, "files", "sample.dll")));
        File.WriteAllText(Path.Combine(candidate.SnapshotRoot, "files", "sample.dll"), "tampered");
        Assert.False(store.Verify(candidate.Id).Valid);
    }
    [Fact]
    public void Traversal_duplicates_and_missing_files_never_create_committed_candidate()
    {
        var source = Prepare(); var store = new CandidateStore(Path.Combine(root, "candidates"));
        Assert.Throws<DispatchException>(() => store.Freeze(new("越界", source, ["../outside.dll"])));
        Assert.Throws<DispatchException>(() => store.Freeze(new("重复", source, ["sample.dll", "sample.dll"])));
        Assert.Throws<DispatchException>(() => store.Freeze(new("网络目录", "\\\\not-contacted\\share", ["sample.dll"])));
        Assert.ThrowsAny<IOException>(() => store.Freeze(new("不存在", source, ["missing.dll"])));
        Assert.Empty(store.List());
    }
    [Fact]
    public void Manifest_is_persistent_and_lookup_id_is_validated()
    {
        var source = Prepare(); var directory = Path.Combine(root, "candidates");
        var candidate = new CandidateStore(directory).Freeze(new("持久化", source, ["sample.dll"]));
        var restored = Assert.Single(new CandidateStore(directory).List());
        Assert.Equal(candidate.ManifestHash, restored.ManifestHash);
        Assert.Equal(64, restored.Files[0].Sha256.Length);
        Assert.Throws<DispatchException>(() => new CandidateStore(directory).Get("../source"));
    }
    [Fact]
    public void Active_writer_prevents_freeze_without_committing_partial_version()
    {
        var source = Prepare(); var store = new CandidateStore(Path.Combine(root, "candidates"));
        using var writer = new FileStream(Path.Combine(source, "sample.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.ThrowsAny<IOException>(() => store.Freeze(new("构建中", source, ["sample.dll"])));
        Assert.Empty(store.List());
    }
}
