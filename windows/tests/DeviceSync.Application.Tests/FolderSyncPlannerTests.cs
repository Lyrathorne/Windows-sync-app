using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class FolderSyncPlannerTests
{
    [Fact]
    public void Build_ProducesUploadsDownloadsAndConflictsWithoutDeletes()
    {
        var local = Manifest(
            Entry("local.txt", "hash-local"),
            Entry("same.txt", "hash-same"),
            Entry("conflict.txt", "hash-a"));
        var remote = Manifest(
            Entry("remote.txt", "hash-remote"),
            Entry("same.txt", "hash-same"),
            Entry("conflict.txt", "hash-b"));

        var plan = FolderSyncPlanner.Build(local, remote);

        Assert.Contains(plan.Operations, operation => operation.RelativePath == "local.txt" && operation.Action == "upload");
        Assert.Contains(plan.Operations, operation => operation.RelativePath == "remote.txt" && operation.Action == "download");
        Assert.Contains(plan.Operations, operation => operation.RelativePath == "conflict.txt" && operation.Action == "conflict");
        Assert.DoesNotContain(plan.Operations, operation => operation.RelativePath == "same.txt" || operation.Action == "delete");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/C:secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder//file.txt")]
    public void Normalize_RejectsUnsafePaths(string path)
        => Assert.Throws<InvalidDataException>(() => FolderSyncPlanner.Normalize(path));

    private static FolderManifestPayload Manifest(params FolderManifestEntryPayload[] entries) => new()
    {
        SyncId = "sync-1", RootId = "root", GeneratedAtUtc = "2026-07-14T00:00:00Z", Entries = entries,
    };
    private static FolderManifestEntryPayload Entry(string path, string hash) => new()
    {
        RelativePath = path, SizeBytes = 1, LastModifiedUtc = "2026-07-14T00:00:00Z", Sha256 = hash,
    };
}
