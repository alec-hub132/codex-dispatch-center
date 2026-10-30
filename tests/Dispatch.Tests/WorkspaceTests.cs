using Dispatch.Core;
using Xunit;

namespace Dispatch.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Changed_model_or_host_blocks_before_any_write()
    {
        var expected = ContinuationTests.Scene();
        foreach (var actual in new[] { ContinuationTests.Scene(11), expected with { ProcessId = 456 }, expected with { Documents = [] },
            expected with { Documents = [expected.Documents[0] with { ViewHandle = 7 }] },
            expected with { Documents = [expected.Documents[0] with { FileWriteTicks = 7 }] } })
        {
            var host = new Host(actual);
            var result = WorkspaceRestorer.Restore(host, expected);
            Assert.False(result.Success); Assert.False(result.MutationStarted); Assert.Equal(0, host.Writes);
        }
    }
    [Fact]
    public void Invalid_selection_blocks_before_activation_and_view_changes()
    {
        var host = new Host(ContinuationTests.Scene()) { InvalidSelection = true };
        Assert.False(WorkspaceRestorer.Restore(host, ContinuationTests.Scene()).Success);
        Assert.Equal(0, host.Writes);
    }
    [Fact]
    public void Other_windows_documents_are_not_rolled_back_or_used_to_block_original_scope()
    {
        var original = ContinuationTests.Scene();
        var other = original.Documents[0] with { Id = "D:/temporary/B.SLDPRT", Title = "B.SLDPRT", UpdateStamp = 99, Scale = 8 };
        var host = new Host(original with { Documents = [original.Documents[0], other], ActiveDocumentId = other.Id });
        Assert.True(WorkspaceRestorer.Restore(host, original).Success);
        Assert.Equal(other, host.Current.Documents.Single(d => d.Id == other.Id));
        Assert.Equal(original.ActiveDocumentId, host.Current.ActiveDocumentId);
    }
    [Fact]
    public void Matching_snapshot_restores_then_reads_back_but_mismatch_is_failure()
    {
        var scene = ContinuationTests.Scene();
        var host = new Host(scene with { Documents = [scene.Documents[0] with { Scale = 2 }] });
        Assert.True(WorkspaceRestorer.Restore(host, scene).Success);
        Assert.Equal(1, host.Writes); Assert.Equal(2, host.Reads);
        host = new Host(scene) { IgnoreWrites = true };
        var result = WorkspaceRestorer.Restore(host, scene with { Documents = [scene.Documents[0] with { Scale = 3 }] });
        Assert.False(result.Success); Assert.True(result.MutationStarted);
    }
    private sealed class Host(WorkspaceScene initial) : IWorkspaceHost
    {
        private WorkspaceScene current = initial;
        public WorkspaceScene Current => current;
        public int Writes, Reads;
        public bool InvalidSelection, IgnoreWrites;
        public WorkspaceScene ReadScene() { Reads++; return current; }
        public void ValidateSelections(WorkspaceScene scene) { if (InvalidSelection) throw new InvalidOperationException("对象失效"); }
        public void ApplyScene(WorkspaceScene scene) { Writes++; if (!IgnoreWrites) current = scene with { Documents = scene.Documents.Concat(current.Documents.Where(d => !scene.Documents.Any(s => s.Id == d.Id))).ToArray() }; }
        public bool SameSelection(WorkspaceScene expected, WorkspaceScene actual) => true;
    }
}
