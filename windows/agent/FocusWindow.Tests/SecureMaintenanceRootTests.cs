using System.Runtime.Versioning;
using System.Security.Principal;
using DeskFuse.DriverTestOwner;
using DeskFuse.DriverTestOwner.Core;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class SecureMaintenanceRootTests
{
    [Fact]
    public async Task Concurrent_fresh_bootstraps_share_one_durable_reservation_name()
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        void CreateNew(string path)
        {
            lock (gate)
                if (!created.Add(path)) throw new IOException("already exists");
        }
        async Task<Guid?> Attempt() => await Task.Run(() =>
        {
            try { return (Guid?)MaintenanceBootstrapReservation.Reserve(@"C:\Program Files", CreateNew).NamespaceId; }
            catch (IOException) { return null; }
        });
        var outcomes = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(outcomes, id => id is not null);
        Assert.Equal(new[] { @"C:\Program Files\DeskFuse-Maintenance" }, created);
    }

    [Fact]
    public void Crash_after_root_creation_cannot_make_a_second_namespace_or_erase_the_reservation()
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Throws<IOException>(() => MaintenanceBootstrapReservation.Reserve(@"C:\Program Files", path =>
        {
            Assert.True(created.Add(path));
            throw new IOException("crash after create, before marker");
        }));
        Assert.Throws<IOException>(() => MaintenanceBootstrapReservation.Reserve(@"C:\Program Files", path =>
        {
            if (!created.Add(path)) throw new IOException("already exists");
        }));
        Assert.Single(created);
    }

    private sealed class FaultingSource : MemoryStream
    {
        public FaultingSource() : base(new byte[100000]) { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position != 0) throw new IOException("injected source read failure");
            return base.Read(buffer, offset, Math.Min(count, 3));
        }
    }

    [Fact]
    public void Private_copy_verifies_destination_bytes_and_never_adopts_nonempty_output()
    {
        var bytes = new byte[] { 7, 11, 13 };
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        PrivateFileCopy.CopyAndVerify(source, destination, ContractJson.Hash(bytes), 3);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(0, destination.Position);
        source.Position = 0;
        Assert.Throws<InvalidDataException>(() => PrivateFileCopy.CopyAndVerify(source, destination, ContractJson.Hash(bytes), 3));
    }

    [Fact]
    public void Private_copy_rejects_changed_oversized_failed_and_cancelled_sources()
    {
        var bytes = new byte[] { 7, 11, 13 };
        using var wrongSource = new MemoryStream(bytes);
        using var wrongDestination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => PrivateFileCopy.CopyAndVerify(wrongSource, wrongDestination, new string('a', 64), 3));
        using var largeSource = new MemoryStream(bytes);
        using var largeDestination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => PrivateFileCopy.CopyAndVerify(largeSource, largeDestination, ContractJson.Hash(bytes), 2));
        using var fault = new FaultingSource();
        using var partialDestination = new MemoryStream();
        Assert.Throws<IOException>(() => PrivateFileCopy.CopyAndVerify(fault, partialDestination, ContractJson.Hash(bytes), 100000));
        Assert.Equal(3, partialDestination.Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var cancelledSource = new MemoryStream(bytes);
        using var cancelledDestination = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => PrivateFileCopy.CopyAndVerify(cancelledSource, cancelledDestination,
            ContractJson.Hash(bytes), 3, cancelled.Token));
        Assert.Equal(0, cancelledDestination.Length);
    }

    [Theory]
    [InlineData("../package")]
    [InlineData("runs/../package")]
    [InlineData("runs//package")]
    [InlineData("runs/./package")]
    [InlineData("runs/file:stream")]
    [InlineData("runs/file.")]
    [InlineData("runs/file ")]
    [InlineData("/outside")]
    [InlineData("runs/CON")]
    [InlineData("runs/nul.json")]
    [InlineData("runs/COM1.log")]
    [InlineData("runs/LPT9")]
    [InlineData("runs/COM\u00b9")]
    public void Protected_relative_path_cannot_escape_or_select_an_alternate_stream(string relative)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidDataException>(() => SecureMaintenanceRoot.ResolveRelative(@"C:\trusted\namespace", relative));
    }

    [Fact]
    public void Bootstrap_record_binds_guid_volume_and_full_file_identity()
    {
        var id = Guid.NewGuid();
        var record = new MaintenanceBootstrapRecord(1, id, 3, 11, 17);
        record.Require(id, new(3, 11, 17));
        Assert.Throws<InvalidDataException>(() => record.Require(Guid.NewGuid(), new(3, 11, 17)));
        Assert.Throws<InvalidDataException>(() => record.Require(id, new(4, 11, 17)));
        Assert.Throws<InvalidDataException>(() => record.Require(id, new(3, 12, 17)));
        Assert.Throws<InvalidDataException>(() => record.Require(id, new(3, 11, 18)));
        Assert.Throws<InvalidDataException>(() => (record with { Version = 2 }).Require(id, new(3, 11, 17)));
        Assert.Throws<InvalidDataException>(() => new MaintenanceBootstrapRecord(1, id, 3, 0, 0).Require(id, new(3, 0, 0)));
    }

    [Fact]
    public void Relative_resolution_keeps_typed_run_layout_beneath_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var run = Guid.NewGuid().ToString("N");
        Assert.Equal(@"C:\trusted\namespace\runs\" + run + @"\bundle\package\FocusIdd.inf",
            SecureMaintenanceRoot.ResolveRelative(@"C:\trusted\namespace", $"runs/{run}/bundle/package/FocusIdd.inf"));
    }

    [Fact]
    public void Non_system_caller_is_denied_before_creating_or_reopening_a_namespace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true) return;
        Assert.Throws<UnauthorizedAccessException>(() => SecureMaintenanceRoot.CreateFresh());
        Assert.Throws<UnauthorizedAccessException>(() => SecureMaintenanceRoot.OpenExisting(Guid.NewGuid()));
    }

    [Fact]
    public void Protected_record_read_is_bounded_and_resets_stream_position()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "deskfuse-marker-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            file.Position = 2;
            Assert.Equal(new byte[] { 1, 2, 3 }, SecureMaintenanceRoot.ReadBounded(file, 3));
            Assert.Equal(0, file.Position);
            Assert.Throws<InvalidDataException>(() => SecureMaintenanceRoot.ReadBounded(file, 2));
        }
        finally { File.Delete(path); }
    }
}
