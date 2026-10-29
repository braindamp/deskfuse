# Build-only: never registers tasks, accesses devices, installs drivers or changes trust.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'windows\tools\DeskFuse.DriverTestOwner\DeskFuse.DriverTestOwner.csproj'
$outputParent = Join-Path $root 'out\driver-test-owner'
$output = Join-Path $outputParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$previousCertificateSetting = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
try {
    # Existing cached packages only. Missing SDK/runtime packs are a build gate,
    # not permission to download/install a toolchain or fall back to shared .NET.
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        --source (Join-Path $env:USERPROFILE '.nuget\packages') '-p:NuGetAudit=false' `
        '-p:DebugType=None' '-p:DebugSymbols=false' -o $output --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained x64 owner publication failed; incomplete output retained.' }
    $runnerOutput=Join-Path $output 'runner'
    New-Item -ItemType Directory -Path $runnerOutput | Out-Null
    foreach ($script in @('maintenance-runner.psm1','safe-driver-proof.ps1','safety-watchdog.ps1',
        'safety-boot-recovery.ps1','rollback-focusidd.ps1','run-proof.ps1','driver-install-testpc.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $runnerOutput $script)
    }
    $image = Join-Path $output 'DeskFuse.DriverTestOwner.exe'
    $version = & $image --version
    if ($LASTEXITCODE -ne 0 -or $version -cne 'DeskFuse.DriverTestOwner/1') { throw 'Published helper identity check failed.' }
    foreach ($name in @('DeskFuse.DriverTestOwner.dll','DeskFuse.DriverTestOwner.Core.dll',
        'DeskFuse.WindowsSecurity.dll','DeskFuse.DriverTestOwner.deps.json',
        'DeskFuse.DriverTestOwner.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)) { throw "Missing self-contained dependency: $name" }
    }
    $config = Get-Content -LiteralPath (Join-Path $output 'DeskFuse.DriverTestOwner.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($config.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or
        $config.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') { throw 'Shared runtime dependency is forbidden in this profile.' }
    $entries = @(Get-ChildItem -LiteralPath $output -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) {
        throw 'Published output contains a reparse entry.'
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($output.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $evidence = [ordered]@{ version=1; profile='self-contained-win-x64'; buildOnly=$true; authorizesInstallation=$false; files=$files }
    $evidencePath = Join-Path $outputParent ((Split-Path $output -Leaf) + '.bundle.json')
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Host "PASS: build-only self-contained x64 owner: $output"
    Write-Host "Bundle hashes (not an approval): $evidencePath"
} finally { $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificateSetting }
