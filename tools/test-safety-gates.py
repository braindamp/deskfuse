#!/usr/bin/env python3
"""Structured source checks plus real Windows driver-free behavior tests.

--static-only is a portable supplement; it never claims a behavioral safety pass.
The default gate requires Windows PowerShell 5.1 and the real x64 SDK fixture.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--static-only", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    policy = json.loads((root / "tools/driver-test-policy.json").read_text())
    expected = dict(version=1, promptAtMs=[60000, 180000, 300000], ackDeadlineMs=[85000, 205000, 325000],
                    proofReadyDeadlineMs=60000, closeAtMs=325000, cleanupObservationDeadlineMs=350000,
                    heartbeatTimeoutMs=10000, automaticRetryWindowMs=1200000)
    if policy != expected:
        raise ValueError("Shared versioned policy differs from the reviewed contract")
    names = ["safe-driver-proof.ps1", "safety-watchdog.ps1", "safety-boot-recovery.ps1",
             "rollback-focusidd.ps1", "run-proof.ps1", "driver-install-testpc.ps1"]
    forbidden = ("pnputil", "devcon", "/add-driver", "/remove-device", "get-pnpdevice", "done.flag",
                 "register-scheduledtask", "unregister-scheduledtask", "bcdedit", "icacls", "set-executionpolicy")
    for name in names:
        source = (root / "tools" / name).read_text().lower()
        if any(token in source for token in forbidden):
            raise ValueError("Legacy machine-changing or completion-flag behavior in " + name)
        if "[guid]$namespaceid" not in source or "[guid]$runid" not in source:
            raise ValueError("Missing exact namespace/run contract in " + name)
    print("PASS: structured policy and prepared-entrypoint source restrictions", flush=True)
    if args.static_only:
        print("STATIC ONLY: behavioral safety and SYSTEM/hardware qualification were not executed.")
        return 0
    if os.name != "nt":
        raise ValueError("Behavioral gate requires Windows; use --static-only only for a source supplement")
    powershell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    if not powershell.is_file():
        raise ValueError("Windows PowerShell 5.1 is required; no alternate shell fallback")
    env = os.environ.copy()
    env["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"
    fixture = Path(env.get("DESKFUSE_DRIVER_TEST_ABI_FILE", str(root / "out/native-contract/driver-test-abi.json")))
    if not fixture.is_file():
        raise ValueError("Build tools/build-native-contract.ps1 with MSVC x64 first; SDK evidence is required")
    env["DESKFUSE_DRIVER_TEST_ABI_FILE"] = str(fixture.resolve())
    selected = "|".join("FullyQualifiedName~" + name for name in (
        "DriverTest", "DriverOwnerRuntimeTests", "DriverManifestBoundsTests", "RunExecutionTests",
        "TrustedMaintenanceSetupTests", "MaintenanceRunnerScriptTests", "PackageAdmissionOwnerTests",
        "BundleIdentityTests", "SecureMaintenanceRootTests", "ProtectedMaintenanceInspectionTests",
        "WindowsCatalogVerifierTests", "WindowsInfCatalogSelectionTests", "WorkerProcessMonitorTests"))
    command = ["dotnet", "test", "windows/agent/FocusWindow.Tests/FocusWindow.Tests.csproj", "-c", "Release",
               "--no-restore", "--filter", selected, "--verbosity", "minimal"]
    result = subprocess.run(command, cwd=root, env=env, check=False)
    if result.returncode:
        return result.returncode
    print("PASS: production coordinator/setup + Windows PowerShell 5.1 mocked UI/entrypoint behavior")
    print("SYSTEM, signed-package, installed-state and physical qualification remain separate gates.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError) as error:
        print("Safety gate failed: " + str(error))
        raise SystemExit(1)
