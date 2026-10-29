#define UNICODE
#define _UNICODE
#include <windows.h>
#include <setupapi.h>
#include <devpropdef.h>
#include <wintrust.h>
#include <cstddef>
#include <cstdio>

// Compile with real MSVC x64/SDK headers. Never opens or changes a device.
int main() {
    static_assert(sizeof(void*) == 8, "Driver-test ABI qualification requires x64");
    std::printf("{\n");
#define VALUE(key, expression) std::printf("\"%s\":%llu,\n", key, static_cast<unsigned long long>(expression))
    VALUE("pointer_size", sizeof(void*));
    VALUE("maintenance_security_attributes_size", sizeof(SECURITY_ATTRIBUTES));
    VALUE("maintenance_security_descriptor_offset", offsetof(SECURITY_ATTRIBUTES, lpSecurityDescriptor));
    VALUE("maintenance_file_id_size", sizeof(FILE_ID_INFO));
    VALUE("maintenance_file_id_offset", offsetof(FILE_ID_INFO, FileId));
    VALUE("device_info_size", sizeof(SP_DEVINFO_DATA));
    VALUE("device_info_reserved", offsetof(SP_DEVINFO_DATA, Reserved));
    VALUE("install_params_size", sizeof(SP_DEVINSTALL_PARAMS_W));
    VALUE("install_params_driver_path", offsetof(SP_DEVINSTALL_PARAMS_W, DriverPath));
    VALUE("driver_info_size", sizeof(SP_DRVINFO_DATA_W));
    VALUE("driver_info_version", offsetof(SP_DRVINFO_DATA_W, DriverVersion));
    VALUE("driver_detail_size", sizeof(SP_DRVINFO_DETAIL_DATA_W));
    VALUE("driver_detail_inf", offsetof(SP_DRVINFO_DETAIL_DATA_W, InfFileName));
    VALUE("driver_detail_hardware", offsetof(SP_DRVINFO_DETAIL_DATA_W, HardwareID));
    VALUE("property_key_size", sizeof(DEVPROPKEY));
    VALUE("dif_register_device", DIF_REGISTERDEVICE);
    VALUE("spdrp_hardware_id", SPDRP_HARDWAREID);
    VALUE("digcf_all_classes", DIGCF_ALLCLASSES);
    VALUE("spdit_compat_driver", SPDIT_COMPATDRIVER);
    VALUE("di_enum_single_inf", DI_ENUMSINGLEINF);
    VALUE("di_quiet_install", DI_QUIETINSTALL);
    VALUE("di_need_restart", DI_NEEDRESTART);
    VALUE("trust_data_size", sizeof(WINTRUST_DATA));
    VALUE("trust_data_info", offsetof(WINTRUST_DATA, pFile));
    VALUE("trust_data_state", offsetof(WINTRUST_DATA, hWVTStateData));
    VALUE("trust_data_signatures", offsetof(WINTRUST_DATA, pSignatureSettings));
    VALUE("trust_file_size", sizeof(WINTRUST_FILE_INFO));
    VALUE("trust_catalog_size", sizeof(WINTRUST_CATALOG_INFO));
    VALUE("trust_catalog_hash", offsetof(WINTRUST_CATALOG_INFO, pbCalculatedFileHash));
    VALUE("trust_catalog_admin", offsetof(WINTRUST_CATALOG_INFO, hCatAdmin));
    VALUE("trust_signature_settings_size", sizeof(WINTRUST_SIGNATURE_SETTINGS));
    VALUE("trust_ui_none", WTD_UI_NONE);
    VALUE("trust_cache_only", WTD_CACHE_ONLY_URL_RETRIEVAL);
    VALUE("trust_revoke_chain_no_root", WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT);
    VALUE("provider_cert_context", offsetof(CRYPT_PROVIDER_CERT, pCert));
    VALUE("cert_context_encoded", offsetof(CERT_CONTEXT, pbCertEncoded));
    VALUE("cert_context_encoded_length", offsetof(CERT_CONTEXT, cbCertEncoded));
    VALUE("trust_local_machine", WTD_USE_LOCAL_MACHINE_CERTS);
    VALUE("trust_secondary_count", WSS_GET_SECONDARY_SIG_COUNT);
    VALUE("provider_signer_chain", offsetof(CRYPT_PROVIDER_SGNR, pChainContext));
    VALUE("chain_context_chains", offsetof(CERT_CHAIN_CONTEXT, rgpChain));
    VALUE("simple_chain_elements", offsetof(CERT_SIMPLE_CHAIN, rgpElement));
    VALUE("chain_element_cert", offsetof(CERT_CHAIN_ELEMENT, pCertContext));
    VALUE("chain_element_status", offsetof(CERT_CHAIN_ELEMENT, TrustStatus));
    VALUE("chain_element_revocation", offsetof(CERT_CHAIN_ELEMENT, pRevocationInfo));
    VALUE("revocation_info_result", offsetof(CERT_REVOCATION_INFO, dwRevocationResult));
    VALUE("original_file_info_size", sizeof(SP_ORIGINAL_FILE_INFO_W));
    VALUE("original_file_info_catalog", offsetof(SP_ORIGINAL_FILE_INFO_W, OriginalCatalogName));
    VALUE("broker_security_attributes_size", sizeof(SECURITY_ATTRIBUTES));
    VALUE("broker_startup_size", sizeof(STARTUPINFOW));
    VALUE("broker_startup_desktop", offsetof(STARTUPINFOW, lpDesktop));
    VALUE("broker_startup_ex_size", sizeof(STARTUPINFOEXW));
    VALUE("broker_startup_ex_attributes", offsetof(STARTUPINFOEXW, lpAttributeList));
    VALUE("broker_process_information_size", sizeof(PROCESS_INFORMATION));
    VALUE("broker_token_statistics_size", sizeof(TOKEN_STATISTICS));
    VALUE("broker_token_statistics_authentication", offsetof(TOKEN_STATISTICS, AuthenticationId));
    VALUE("broker_token_default_dacl_size", sizeof(TOKEN_DEFAULT_DACL));
    VALUE("broker_token_default_dacl_information", TokenDefaultDacl);
    VALUE("broker_token_all_access", TOKEN_ALL_ACCESS);
    VALUE("broker_token_query_access", READ_CONTROL | TOKEN_QUERY);
    VALUE("broker_create_no_window", CREATE_NO_WINDOW);
    VALUE("broker_extended_limits_size", sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
    VALUE("broker_basic_accounting_size", sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
    VALUE("broker_active_processes_offset", offsetof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, ActiveProcesses));
    VALUE("broker_creation_mitigations", PROCESS_CREATION_MITIGATION_POLICY_EXTENSION_POINT_DISABLE_ALWAYS_ON |
        PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_REMOTE_ALWAYS_ON |
        PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_NO_LOW_LABEL_ALWAYS_ON |
        PROCESS_CREATION_MITIGATION_POLICY_IMAGE_LOAD_PREFER_SYSTEM32_ALWAYS_ON);
    VALUE("broker_attribute_mitigation", PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY);
    VALUE("broker_attribute_job_list", PROC_THREAD_ATTRIBUTE_JOB_LIST);
    VALUE("broker_attribute_child_policy", PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY);
    VALUE("broker_child_restricted", PROCESS_CREATION_CHILD_PROCESS_RESTRICTED);
    VALUE("broker_process_all_access", PROCESS_ALL_ACCESS);
    VALUE("broker_thread_all_access", THREAD_ALL_ACCESS);
    VALUE("broker_job_all_access", JOB_OBJECT_ALL_ACCESS);
    VALUE("broker_extension_policy", ProcessExtensionPointDisablePolicy);
    VALUE("broker_child_policy", ProcessChildProcessPolicy);
    VALUE("broker_pipe_security_size", sizeof(SECURITY_ATTRIBUTES));
    VALUE("broker_pipe_client_access", FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE | READ_CONTROL);
    VALUE("broker_pipe_open_mode", PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE);
    VALUE("broker_pipe_local_mode", PIPE_REJECT_REMOTE_CLIENTS);
    std::printf("\"di_need_reboot\":%lu\n}\n", static_cast<unsigned long>(DI_NEEDREBOOT));
}
