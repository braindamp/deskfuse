using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

public sealed class DriverManifestBoundsTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "deskfuse-manifest-" + Guid.NewGuid().ToString("N"));
    private static RunManifest Manifest(Guid id, Dictionary<string, string> files) => new(1, id, RunManifest.DeviceId(id),
        @"Root\FocusIdd", RunManifest.DisplayClass, new string('a',64), files, RunManifest.WatchName(id), RunManifest.BootName(id));

    [Fact]
    public void Complete_dependency_manifest_has_a_separate_budget_from_small_journal_transitions()
    {
        var id=Guid.NewGuid();
        var files=Enumerable.Range(0,500).ToDictionary(i => $"probe/System.approved.dependency.{i:D4}.dll", _ => new string('a',64));
        var manifest=Manifest(id,files);
        Assert.True(ContractJson.Write(manifest).Length > 32768);
        var journal=RunJournal.Create(Path.Combine(parent,id.ToString("N")),manifest);
        Assert.Equal(500,journal.Manifest.BundleSha256.Count);
        Assert.False(journal.Snapshot().Armed);
        Assert.Equal(1,journal.Snapshot().Sequence);
    }

    [Fact]
    public void Oversized_manifest_is_rejected_before_creation_and_before_allocating_a_read_buffer()
    {
        var id=Guid.NewGuid(); var directory=Path.Combine(parent,id.ToString("N"));
        var files=Enumerable.Range(0,2500).ToDictionary(i => "probe/"+new string('d',450)+i+".dll", _ => new string('a',64));
        Assert.Throws<InvalidDataException>(() => RunJournal.Create(directory,Manifest(id,files)));
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        using (var file=new FileStream(Path.Combine(directory,"run.json"),FileMode.CreateNew)) file.SetLength(RunJournal.MaxManifestBytes+1);
        Assert.Throws<InvalidDataException>(() => new RunJournal(directory));
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(parent)) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(parent)) Directory.Delete(parent,true);
    }
}
