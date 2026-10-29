using System.Runtime.Versioning;
using System.Security.AccessControl;
using DeskFuse.DriverTestOwner;
using Xunit;

namespace FocusWindow.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProtectedMaintenanceInspectionTests
{
    private static RawSecurityDescriptor Descriptor(string ace, string owner = "SY") =>
        new($"O:{owner}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA){ace}");

    [Fact]
    public void Ordinary_read_and_traverse_are_permitted_by_the_descriptor_rule()
    {
        MaintenanceDescriptorPolicy.Validate(Descriptor("(A;;GRGX;;;BU)"), protectedRoot: true);
    }

    [Fact]
    public void Ancestor_can_allow_sibling_creation_but_protected_root_cannot()
    {
        var acl = Descriptor("(A;;0x00000006;;;BU)");
        MaintenanceDescriptorPolicy.Validate(acl, protectedRoot: false);
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(acl, protectedRoot: true));
    }

    [Theory]
    [InlineData("0x00000040")] // delete child
    [InlineData("0x00010000")] // delete
    [InlineData("0x00040000")] // write DACL
    [InlineData("0x00080000")] // write owner
    [InlineData("0x00000100")] // write attributes
    [InlineData("0x00000010")] // write extended attributes
    [InlineData("GW")]
    [InlineData("GA")]
    public void Ancestor_rejects_untrusted_mutating_permissions(string mask)
    {
        var acl = Descriptor($"(A;;{mask};;;AU)");
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(acl, protectedRoot: false));
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(acl, protectedRoot: true));
    }

    [Fact]
    public void Inherited_write_is_rejected_and_inherit_only_parent_grant_never_grants_the_parent()
    {
        var inherited = Descriptor("(A;ID;GW;;;AU)");
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(inherited, protectedRoot: true));
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(inherited, protectedRoot: false));
        var inheritOnly = Descriptor("(A;CIIO;GW;;;AU)");
        MaintenanceDescriptorPolicy.Validate(inheritOnly, protectedRoot: false);
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(inheritOnly, protectedRoot: true));
    }

    [Fact]
    public void Rejects_untrusted_owner_and_null_dacl()
    {
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(Descriptor("", "BU"), true));
        var noDacl = new RawSecurityDescriptor("O:SYG:SY");
        Assert.Throws<InvalidDataException>(() => MaintenanceDescriptorPolicy.Validate(noDacl, true));
    }
}
