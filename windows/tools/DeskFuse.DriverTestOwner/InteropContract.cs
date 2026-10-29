using System.Runtime.InteropServices;
using MaintenanceNativeLayouts = DeskFuse.WindowsSecurity.PrivateStorageNativeLayouts;
using DeskFuse.DriverTestOwner.Core;

namespace DeskFuse.DriverTestOwner;

/// <summary>Pure metadata check; no Windows device function is called.</summary>
public static class InteropContract
{
    public static IReadOnlyDictionary<string, long> Values() => new Dictionary<string, long>
    {
        ["pointer_size"] = nint.Size,
        ["maintenance_security_attributes_size"] = Marshal.SizeOf<MaintenanceNativeLayouts.SecurityAttributes>(),
        ["maintenance_security_descriptor_offset"] = Marshal.OffsetOf<MaintenanceNativeLayouts.SecurityAttributes>(nameof(MaintenanceNativeLayouts.SecurityAttributes.Descriptor)).ToInt64(),
        ["maintenance_file_id_size"] = Marshal.SizeOf<MaintenanceNativeLayouts.FileIdInfo>(),
        ["maintenance_file_id_offset"] = Marshal.OffsetOf<MaintenanceNativeLayouts.FileIdInfo>(nameof(MaintenanceNativeLayouts.FileIdInfo.Low)).ToInt64(),
        ["device_info_size"] = Marshal.SizeOf<SetupApi.DeviceInfo>(),
        ["device_info_reserved"] = Marshal.OffsetOf<SetupApi.DeviceInfo>(nameof(SetupApi.DeviceInfo.Reserved)).ToInt64(),
        ["install_params_size"] = Marshal.SizeOf<SetupApi.InstallParameters>(),
        ["install_params_driver_path"] = Marshal.OffsetOf<SetupApi.InstallParameters>(nameof(SetupApi.InstallParameters.DriverPath)).ToInt64(),
        ["driver_info_size"] = Marshal.SizeOf<SetupApi.DriverInfo>(),
        ["driver_info_version"] = Marshal.OffsetOf<SetupApi.DriverInfo>(nameof(SetupApi.DriverInfo.Version)).ToInt64(),
        ["driver_detail_size"] = Marshal.SizeOf<SetupApi.DriverDetail>(),
        ["driver_detail_inf"] = Marshal.OffsetOf<SetupApi.DriverDetail>(nameof(SetupApi.DriverDetail.InfFileName)).ToInt64(),
        ["driver_detail_hardware"] = Marshal.OffsetOf<SetupApi.DriverDetail>(nameof(SetupApi.DriverDetail.HardwareId)).ToInt64(),
        ["property_key_size"] = Marshal.SizeOf<SetupApi.PropertyKey>(),
        ["dif_register_device"] = SetupApi.DifRegisterDevice,
        ["spdrp_hardware_id"] = SetupApi.SpdrpHardwareId,
        ["digcf_all_classes"] = SetupApi.DigcfAllClasses,
        ["spdit_compat_driver"] = SetupApi.SpditCompatDriver,
        ["di_enum_single_inf"] = SetupApi.DiEnumSingleInf,
        ["di_quiet_install"] = SetupApi.DiQuietInstall,
        ["di_need_restart"] = SetupApi.DiNeedRestart,
        ["trust_data_size"] = Marshal.SizeOf<WindowsTrustInterop.TrustData>(),
        ["trust_data_info"] = Marshal.OffsetOf<WindowsTrustInterop.TrustData>(nameof(WindowsTrustInterop.TrustData.Info)).ToInt64(),
        ["trust_data_state"] = Marshal.OffsetOf<WindowsTrustInterop.TrustData>(nameof(WindowsTrustInterop.TrustData.StateData)).ToInt64(),
        ["trust_data_signatures"] = Marshal.OffsetOf<WindowsTrustInterop.TrustData>(nameof(WindowsTrustInterop.TrustData.SignatureSettings)).ToInt64(),
        ["trust_file_size"] = Marshal.SizeOf<WindowsTrustInterop.FileInfo>(),
        ["trust_catalog_size"] = Marshal.SizeOf<WindowsTrustInterop.CatalogInfo>(),
        ["trust_catalog_hash"] = Marshal.OffsetOf<WindowsTrustInterop.CatalogInfo>(nameof(WindowsTrustInterop.CatalogInfo.CalculatedHash)).ToInt64(),
        ["trust_catalog_admin"] = Marshal.OffsetOf<WindowsTrustInterop.CatalogInfo>(nameof(WindowsTrustInterop.CatalogInfo.CatalogAdmin)).ToInt64(),
        ["trust_signature_settings_size"] = Marshal.SizeOf<WindowsTrustInterop.SignatureSettings>(),
        ["trust_ui_none"] = WindowsTrustInterop.UiNone,
        ["trust_cache_only"] = WindowsTrustInterop.CacheOnlyUrlRetrieval,
        ["trust_revoke_chain_no_root"] = WindowsTrustInterop.RevocationChainExcludeRoot,
        ["provider_cert_context"] = Marshal.OffsetOf<WindowsTrustInterop.ProviderCertificatePrefix>(nameof(WindowsTrustInterop.ProviderCertificatePrefix.CertContext)).ToInt64(),
        ["cert_context_encoded"] = Marshal.OffsetOf<WindowsTrustInterop.CertContextPrefix>(nameof(WindowsTrustInterop.CertContextPrefix.Encoded)).ToInt64(),
        ["cert_context_encoded_length"] = Marshal.OffsetOf<WindowsTrustInterop.CertContextPrefix>(nameof(WindowsTrustInterop.CertContextPrefix.EncodedLength)).ToInt64(),
        ["trust_local_machine"] = WindowsTrustInterop.UseLocalMachineCerts,
        ["trust_secondary_count"] = WindowsTrustInterop.GetSecondarySignatureCount,
        ["provider_signer_chain"] = Marshal.OffsetOf<WindowsTrustInterop.ProviderSignerPrefix>(nameof(WindowsTrustInterop.ProviderSignerPrefix.ChainContext)).ToInt64(),
        ["chain_context_chains"] = Marshal.OffsetOf<WindowsTrustInterop.ChainContextPrefix>(nameof(WindowsTrustInterop.ChainContextPrefix.Chains)).ToInt64(),
        ["simple_chain_elements"] = Marshal.OffsetOf<WindowsTrustInterop.SimpleChainPrefix>(nameof(WindowsTrustInterop.SimpleChainPrefix.Elements)).ToInt64(),
        ["chain_element_cert"] = Marshal.OffsetOf<WindowsTrustInterop.ChainElementPrefix>(nameof(WindowsTrustInterop.ChainElementPrefix.CertContext)).ToInt64(),
        ["chain_element_status"] = Marshal.OffsetOf<WindowsTrustInterop.ChainElementPrefix>(nameof(WindowsTrustInterop.ChainElementPrefix.ErrorStatus)).ToInt64(),
        ["chain_element_revocation"] = Marshal.OffsetOf<WindowsTrustInterop.ChainElementPrefix>(nameof(WindowsTrustInterop.ChainElementPrefix.RevocationInfo)).ToInt64(),
        ["revocation_info_result"] = Marshal.OffsetOf<WindowsTrustInterop.RevocationInfoPrefix>(nameof(WindowsTrustInterop.RevocationInfoPrefix.Result)).ToInt64(),
        ["original_file_info_size"] = Marshal.SizeOf<WindowsInfCatalogSelection.OriginalFileInfo>(),
        ["original_file_info_catalog"] = Marshal.OffsetOf<WindowsInfCatalogSelection.OriginalFileInfo>(nameof(WindowsInfCatalogSelection.OriginalFileInfo.OriginalCatalogName)).ToInt64(),
        ["di_need_reboot"] = SetupApi.DiNeedReboot
    };

    public static void Validate(ReadOnlySpan<byte> fixture)
    {
        var native = ContractJson.Read<Dictionary<string, long>>(fixture);
        var managed = Values();
        // The same native fixture also qualifies the product broker in its own
        // test. Every non-broker field remains exact and mandatory here.
        if (native.Count(p => !p.Key.StartsWith("broker_", StringComparison.Ordinal)) != managed.Count ||
            managed.Any(p => !native.TryGetValue(p.Key, out var value) || value != p.Value))
            throw new InvalidDataException("Real SDK x64 SetupAPI ABI differs from managed declarations.");
    }
}
