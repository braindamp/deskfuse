using System.Reflection;
using System.Runtime.Versioning;
using DeskFuse.DriverTestOwner.Core;
using DeskFuse.WindowsSecurity;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class PrivateStorageProfileTests
{
    [Fact]
    public void Only_two_fixed_profiles_and_their_required_structure_are_supported()
    {
        Assert.Equal(new[] { PrivateStorageProfile.Maintenance, PrivateStorageProfile.BrokerAuthority },
            Enum.GetValues<PrivateStorageProfile>());
        Assert.Equal(Path.Combine("anchor", "DeskFuse-Maintenance"),
            PrivateStorageBootstrapReservation.DirectoryForAnchor("anchor", PrivateStorageProfile.Maintenance));
        Assert.Equal(Path.Combine("anchor", "DeskFuse-Authority"),
            PrivateStorageBootstrapReservation.DirectoryForAnchor("anchor", PrivateStorageProfile.BrokerAuthority));
        Assert.Equal(new[] { "approvals", "runs" }, PrivateStorageProfiles.InitialDirectories(PrivateStorageProfile.Maintenance));
        Assert.Equal(new[] { "enrollments", "bootstrap", "ceremonies" }, PrivateStorageProfiles.InitialDirectories(PrivateStorageProfile.BrokerAuthority));
        Assert.Empty(PrivateStorageProfiles.InitialFiles(PrivateStorageProfile.Maintenance));
        Assert.Equal(new[] { "owner.lock" }, PrivateStorageProfiles.InitialFiles(PrivateStorageProfile.BrokerAuthority));
    }

    [Fact]
    public void Both_profiles_use_a_single_collision_preserving_reservation()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void CreateNew(string path)
        {
            if (!reserved.Add(path)) throw new IOException("already reserved");
        }
        foreach (var profile in Enum.GetValues<PrivateStorageProfile>())
        {
            var first = PrivateStorageBootstrapReservation.Reserve("anchor", profile, CreateNew);
            Assert.NotEqual(Guid.Empty, first.NamespaceId);
            Assert.Throws<IOException>(() => PrivateStorageBootstrapReservation.Reserve("anchor", profile, CreateNew));
        }
        Assert.Equal(2, reserved.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Unknown_profile_is_rejected_before_any_creation_callback(int value)
    {
        var called = false;
        Assert.Throws<InvalidDataException>(() => PrivateStorageBootstrapReservation.Reserve("anchor",
            (PrivateStorageProfile)value, _ => called = true));
        Assert.False(called);
        Assert.Throws<InvalidDataException>(() => PrivateStorageProfiles.InitialDirectories((PrivateStorageProfile)value));
        Assert.Throws<InvalidDataException>(() => PrivateStorageProfiles.InitialFiles((PrivateStorageProfile)value));
    }

    [Fact]
    public void Broker_owner_lease_is_fixed_existing_exclusive_and_not_a_mutable_stream_api()
    {
        BrokerOwnerLeasePolicy.RequireProfile(PrivateStorageProfile.BrokerAuthority);
        Assert.Throws<InvalidOperationException>(() => BrokerOwnerLeasePolicy.RequireProfile(PrivateStorageProfile.Maintenance));
        Assert.Throws<InvalidOperationException>(() => BrokerOwnerLeasePolicy.RequireProfile((PrivateStorageProfile)3));
        Assert.Equal("owner.lock", BrokerOwnerLeasePolicy.FileName);
        Assert.Equal(0xc0020080u, BrokerOwnerLeasePolicy.DesiredAccess);
        Assert.Equal(0u, BrokerOwnerLeasePolicy.ShareMode);
        Assert.Equal(3u, BrokerOwnerLeasePolicy.OpenExisting);
        Assert.Equal(0x00200000u, BrokerOwnerLeasePolicy.OpenReparsePoint);
        var acquire = typeof(SecurePrivateStorageRoot).GetMethod(nameof(SecurePrivateStorageRoot.AcquireBrokerOwnerLease))!;
        Assert.Empty(acquire.GetParameters());
        Assert.Equal(typeof(IDisposable), acquire.ReturnType);
    }

    [Fact]
    public void Shared_bootstrap_keeps_the_existing_deterministic_record_encoding()
    {
        var id = Guid.NewGuid();
        var legacy = new DeskFuse.DriverTestOwner.MaintenanceBootstrapRecord(1, id, 3, 11, 17);
        var shared = new PrivateStorageBootstrapRecord(1, id, 3, 11, 17);
        var bytes = PrivateStorageJson.Write(shared);
        Assert.Equal(ContractJson.Write(legacy), bytes);
        Assert.Equal(shared, PrivateStorageJson.Read<PrivateStorageBootstrapRecord>(bytes));
        Assert.Throws<System.Text.Json.JsonException>(() => PrivateStorageJson.Read<PrivateStorageBootstrapRecord>(
            "{\"version\":1,\"unexpected\":true}"u8));
    }

    [Fact]
    public void Public_root_factories_accept_only_profile_and_explicit_identity()
    {
        var type = typeof(SecurePrivateStorageRoot);
        Assert.Empty(type.GetConstructors());
        Assert.Equal(new[] { typeof(PrivateStorageProfile) },
            type.GetMethod(nameof(SecurePrivateStorageRoot.CreateFresh))!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(new[] { typeof(PrivateStorageProfile), typeof(Guid) },
            type.GetMethod(nameof(SecurePrivateStorageRoot.OpenExisting))!.GetParameters().Select(p => p.ParameterType));
        Assert.Null(type.GetProperty(nameof(SecurePrivateStorageRoot.Profile))!.SetMethod);
        Assert.Empty(typeof(ProtectedDirectoryInspection).GetMethods(BindingFlags.Public | BindingFlags.Static));
        Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("DeskFuse.DriverTestOwner", StringComparison.Ordinal) == true);
    }
}
