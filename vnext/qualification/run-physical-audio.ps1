# CP-001/MP-001 physical Windows-to-Xiaomi run. No driver/APK install,
# endpoint rerouting, captured audio upload, or GitHub key operations.
[CmdletBinding(DefaultParameterSetName='Endpoint')]
param(
    [Parameter(Mandatory=$true,ParameterSetName='Endpoint')][string]$EndpointId,
    [Parameter(Mandatory=$true,ParameterSetName='DeskFuse')]
    [ValidatePattern('^[0-9a-fA-F]{32}$')][string]$DeskFusePeerId,
    [Parameter(Mandatory=$true,ParameterSetName='Product')][string]$ProductConfig,
    [Parameter(ParameterSetName='Product')][string]$PythonExecutable = 'python',
    [Parameter(ParameterSetName='Product')][ValidateSet('loopback','committed')][string]$WindowsSourceBackend = 'committed',
    [Parameter(Mandatory=$true,ParameterSetName='Endpoint')]
    [Parameter(Mandatory=$true,ParameterSetName='DeskFuse')][string]$AndroidExecutable,
    [Parameter(Mandatory=$true,ParameterSetName='Endpoint')]
    [Parameter(Mandatory=$true,ParameterSetName='DeskFuse')][string]$AndroidNativeSo,
    [ValidatePattern('^f[01]c[01]r[01]$')][string]$Modes = 'f1c1r1',
    [switch]$Strict,
    [switch]$VerifyOnly,
    [ValidateRange(10,1800)][int]$Seconds = 1800,
    [string]$WindowsIP = '192.168.2.107',
    [string]$PhoneIP = '192.168.2.114',
    [string]$KeysDir = 'D:\DeskFuseLocalKeys\m2a-physical-lab',
    [string]$LabRoot = 'D:\Coding\DeskFuse\vnext-wp001',
    [string]$EvidenceRoot = 'D:\DeskFuseLocalData\cp001',
    [string]$Profile = 'audio-only',
    [string]$ExpectedBranch = 'feature/vnext-wp001-native-lab',
    [string]$WindowsExecutable,
    [ValidateSet(0,2,3)][int]$AndroidBufferBursts = 0,
    [string]$WindowsCaptureExecutable,
    [string]$BuildReceipt,
    [ValidateSet('reference','minimum')][string]$WindowsCapturePolicy = 'reference',
    [ValidateSet('reference','minimum')][string]$WindowsEnginePeriod = 'reference',
    [ValidateSet(0,1)][int]$AndroidSharingPolicy = 0,
    [ValidateSet(0,1)][int]$AndroidUsagePolicy = 0,
    [ValidateSet(1440,1200,960,720,480)][int]$NativeReserveFrames = 1440,
    [switch]$MeasureCallbacks
)
$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ParameterSetName -eq 'Product') {
    if ($Strict -or $VerifyOnly -or $Modes -ne 'f1c1r1' -or $AndroidBufferBursts -ne 0 -or $AndroidSharingPolicy -ne 0 -or $AndroidUsagePolicy -ne 0 -or $NativeReserveFrames -ne 1440 -or $MeasureCallbacks) {
        throw 'Product source comparison uses unchanged Android reference settings only'
    }
    & $PythonExecutable (Join-Path $PSScriptRoot '../../tools/audio-lab/product_runner.py') --config $ProductConfig --backend $WindowsSourceBackend --engine-period $WindowsEnginePeriod --capture-policy $WindowsCapturePolicy --seconds $Seconds --profile $Profile
    exit $LASTEXITCODE
}
$dedicatedSource = $PSCmdlet.ParameterSetName -eq 'DeskFuse'
if ($Strict -and $Modes -ne 'f0c0r0') { throw 'Strict PCM verification requires f0c0r0 (no DSP or fade)' }
if ($AndroidBufferBursts -ne 0 -and ($Strict -or $VerifyOnly)) { throw 'Buffer experiment requires adaptive speaker playback' }
if ($Strict -and $VerifyOnly) { throw 'Choose either Strict AAudio verification or silent VerifyOnly, not both' }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$folder = Join-Path $EvidenceRoot ($stamp + '-' + ($Profile -replace '[^\w-]','_') + '-' + $Modes)
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$manifest = Join-Path $folder 'session.txt'
$crate = Join-Path $LabRoot 'vnext\m2a\quiche-lab'
$cargoTarget = Join-Path $crate 'target'
if (-not [string]::IsNullOrWhiteSpace($env:CARGO_TARGET_DIR)) {
    $cargoTarget = if ([IO.Path]::IsPathRooted($env:CARGO_TARGET_DIR)) {
        [IO.Path]::GetFullPath($env:CARGO_TARGET_DIR)
    } else {
        [IO.Path]::GetFullPath((Join-Path $crate $env:CARGO_TARGET_DIR))
    }
}
# Explicit paths support custom/configured Cargo target directories. Default
# to Release and never silently substitute a prior debug executable.
$source = if ([string]::IsNullOrWhiteSpace($WindowsExecutable)) {
    Join-Path $cargoTarget 'release\continuous-quic-lab.exe'
} else { $WindowsExecutable }
$capture = Join-Path $LabRoot 'vnext\m1\build\deskfuse-wasapi-capture.exe'
if ($WindowsCaptureExecutable) { $capture = (Resolve-Path -LiteralPath $WindowsCaptureExecutable).Path }
if (($Strict -or $VerifyOnly) -and ($AndroidSharingPolicy -ne 0 -or $AndroidUsagePolicy -ne 0 -or $NativeReserveFrames -ne 1440)) {
    throw 'Hardware/reserve experiments require adaptive speaker playback'
}
$native = (Resolve-Path -LiteralPath $AndroidNativeSo).Path
$android = (Resolve-Path -LiteralPath $AndroidExecutable).Path
foreach ($p in @($source, $capture, $android, $native, (Join-Path $KeysDir 'ca.pem'), (Join-Path $KeysDir 'server.pem'), (Join-Path $KeysDir 'server.key'))) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Required local input missing: $p" }
}
$source = (Resolve-Path -LiteralPath $source).Path
if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw 'adb not on PATH' }
$branch = (& git -c ("safe.directory=" + $LabRoot) -C $LabRoot branch --show-current).Trim()
$sha = (& git -c ("safe.directory=" + $LabRoot) -C $LabRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne $ExpectedBranch -or
    $branch -in @('main', 'master') -or $branch.StartsWith('reference/')) {
    throw 'Only the isolated development branch is permitted'
}
$worktree = @(& git -c ("safe.directory=" + $LabRoot) -C $LabRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect local Git worktree' }
$worktreeClean = ($worktree.Count -eq 0)
if (-not $worktreeClean) {
    Write-Warning ('Running audio diagnostic with {0} local Git worktree change(s). No local changes will be deleted or stashed. This is not clean-commit qualification evidence.' -f $worktree.Count)
}
$device = (& adb get-serialno).Trim()
if ($LASTEXITCODE -ne 0 -or $device -eq 'unknown') { throw 'Physical ADB device not ready' }
$events = @(
    'start_utc=' + (Get-Date).ToUniversalTime().ToString('o'),
    'git_branch=' + $branch,
    'git_commit=' + $sha,
    'git_worktree_clean=' + $worktreeClean.ToString().ToLowerInvariant(),
    'git_worktree_change_count=' + $worktree.Count,
    'profile=' + $Profile,
    'modes=' + $Modes,
    'android_requested_buffer_bursts=' + $AndroidBufferBursts,
    'windows_capture_policy=' + $WindowsCapturePolicy,
    'windows_engine_period_policy=' + $WindowsEnginePeriod,
    'windows_source_kind=' + $(if ($dedicatedSource) { 'deskfuse-endpoint-loopback' } else { 'explicit-endpoint-loopback' }),
    'deskfuse_enrolled_peer=' + $DeskFusePeerId,
    'android_sharing_policy=' + $AndroidSharingPolicy,
    'android_usage_policy=' + $AndroidUsagePolicy,
    'native_reserve_frames=' + $NativeReserveFrames,
    'measure_callbacks=' + [bool]$MeasureCallbacks,
    'strict_pcm=' + [bool]$Strict,
    'verify_only_no_aaudio=' + [bool]$VerifyOnly,
    'duration_seconds=' + $Seconds,
    'endpoint_id=' + $EndpointId,
    'windows_ip=' + $WindowsIP,
    'phone_ip=' + $PhoneIP,
    'adb_device=' + $device,
    'brave_processes_at_start=' + @(Get-Process brave -ErrorAction SilentlyContinue).Count,
    'brave_restoration_at_5min=OPERATOR_REQUIRED',
    'brave_restoration_at_20min=OPERATOR_REQUIRED',
    'competing_load=NONE unless externally measured and recorded',
    'volume_operations=NONE unless separately logged'
)
$hashes = @($capture,$source,$native,$android) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    'sha256=' + $hash.Hash.ToLowerInvariant() + ' file=' + $_
}
# A filename containing "release" is not proof of its build configuration.
# Match the selected artifacts against the build script's hash-bound receipt.
$receipt = $null
$receiptPath = Join-Path $LabRoot 'vnext\m1\build\audio-pair-build.json'
if ($BuildReceipt) { $receiptPath = (Resolve-Path -LiteralPath $BuildReceipt).Path }
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    try { $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json }
    catch { Write-Warning 'Local build receipt is unreadable; build profiles remain unverified.' }
}
$selectedArtifacts = [ordered]@{windows_rust=$source; android_rust=$android; android_native=$native; windows_capture=$capture}
foreach ($kind in $selectedArtifacts.Keys) {
    $selected = (Resolve-Path -LiteralPath $selectedArtifacts[$kind]).Path
    $selectedHash = (Get-FileHash -LiteralPath $selected -Algorithm SHA256).Hash.ToLowerInvariant()
    $verifiedProfile = 'unverified'
    if ($null -ne $receipt -and $receipt.schema -eq 1) {
        foreach ($artifact in $receipt.artifacts) {
            $artifactRole = $artifact.role
            if (-not $artifactRole) {
                $originalPath = $artifact.path -replace '\\','/'
                $artifactRole = if ($originalPath -match 'aarch64-linux-android/.*/continuous-quic-lab$') { 'android_rust' }
                    elseif ($originalPath -match 'deskfuse-wasapi-capture.exe$') { 'windows_capture' }
                    elseif ($originalPath -match 'libdeskfuse_android_audio.so$') { 'android_native' }
                    elseif ($originalPath -match 'continuous-quic-lab.exe$') { 'windows_rust' } else { '' }
            }
            if ($artifactRole -eq $kind -and $artifact.sha256 -ieq $selectedHash -and
                $artifact.profile -in @('release','debug')) {
                $verifiedProfile = $artifact.profile
            }
        }
    }
    $events += ($kind + '_build_profile=' + $verifiedProfile)
    if ($verifiedProfile -eq 'unverified') { throw ('No role/hash-matched receipt for ' + $kind) }
}
if ($null -ne $receipt) {
    $events += ('build_receipt_source_commit=' + $receipt.source_commit)
}
if ($dedicatedSource -and $receipt.capabilities.windows_endpoint_lease -ne $true) {
    throw 'Dedicated endpoint source requires the rebuilt Windows sender and matching receipt'
}
if ($WindowsEnginePeriod -ne 'reference' -and
    $WindowsEnginePeriod -notin @($receipt.capabilities.windows_engine_period_policies)) {
    throw 'Engine-period control requires the rebuilt capture executable and matching receipt'
}
$enrolledPeerDer = $null
if ($dedicatedSource) {
    # This is the operator's locally provisioned public client certificate,
    # never a certificate learned from an untrusted network request.
    $peerPem = Join-Path $KeysDir 'client.pem'
    if ((Get-Item -LiteralPath $peerPem).Length -gt 65536) { throw 'Client certificate is oversized' }
    $certificates = [regex]::Matches([IO.File]::ReadAllText($peerPem),
        '-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----')
    if ($certificates.Count -ne 1) { throw 'Expected exactly one enrolled client certificate' }
    $der = [Convert]::FromBase64String($certificates[0].Groups[1].Value)
    $peerCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($der)
    try {
        $enrolledPeerDer = Join-Path $folder 'enrolled-client-public.der'
        [IO.File]::WriteAllBytes($enrolledPeerDer,$peerCertificate.RawData)
    } finally { $peerCertificate.Dispose() }
}
$events += ('build_receipt=' + $receiptPath)
$events + $hashes | Set-Content -LiteralPath $manifest -Encoding UTF8
& adb push $android /data/local/tmp/deskfuse-vnext-continuous |
    Tee-Object -FilePath (Join-Path $folder 'adb-push-executable.txt')
if ($LASTEXITCODE -ne 0) { throw 'Android QUIC executable push failed' }
& adb push $native /data/local/tmp/libdeskfuse_android_audio.so |
    Tee-Object -FilePath (Join-Path $folder 'adb-push-renderer.txt')
if ($LASTEXITCODE -ne 0) { throw 'Android AAudio native library push failed' }
& adb shell chmod 700 /data/local/tmp/deskfuse-vnext-continuous
if ($LASTEXITCODE -ne 0) { throw 'Android executable chmod failed' }
$remoteHashOutput = @(& adb shell 'sha256sum /data/local/tmp/deskfuse-vnext-continuous /data/local/tmp/libdeskfuse_android_audio.so')
if ($LASTEXITCODE -ne 0) { throw 'Android remote hash query failed' }
$remoteHashOutput | Set-Content -LiteralPath (Join-Path $folder 'android-sha256.txt') -Encoding UTF8
$remoteHashes = @{}
foreach ($line in $remoteHashOutput) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+(/data/local/tmp/[^\s]+)$') {
        throw ('Malformed Android sha256sum output; no playback started: ' + $line)
    }
    $remoteHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
$expectedRemoteHashes = @{
    '/data/local/tmp/deskfuse-vnext-continuous' = (Get-FileHash -LiteralPath $android -Algorithm SHA256).Hash.ToLowerInvariant()
    '/data/local/tmp/libdeskfuse_android_audio.so' = (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($remotePath in $expectedRemoteHashes.Keys) {
    if (-not $remoteHashes.ContainsKey($remotePath) -or
        $remoteHashes[$remotePath] -cne $expectedRemoteHashes[$remotePath]) {
        throw ('Android binary SHA256 mismatch; refusing to start source for ' + $remotePath)
    }
}
'android_pair_sha256_verified=true' | Add-Content -LiteralPath $manifest

# Preflight existing test credentials without reading, copying, or printing keys.
# No implicit credential generation or production pairing fallbacks.
foreach ($remoteCredential in @('df-lab-ca.pem', 'df-lab-client.pem', 'df-lab-client.key')) {
    & adb shell ('test -r /data/local/tmp/' + $remoteCredential)
    if ($LASTEXITCODE -ne 0) {
        throw ('Required locally provisioned Android lab credential unavailable: ' +
            $remoteCredential)
    }
}
'android_lab_credentials_readable=true' | Add-Content -LiteralPath $manifest

# Local-only environment observations for hardware-profile comparison.
$hardware = Join-Path $folder 'device-and-power.txt'
('android_model=' + ((& adb shell getprop ro.product.model) -join '').Trim()) |
    Set-Content -LiteralPath $hardware -Encoding UTF8
('android_api=' + ((& adb shell getprop ro.build.version.sdk) -join '').Trim()) |
    Add-Content -LiteralPath $hardware
('android_build=' + ((& adb shell getprop ro.build.fingerprint) -join '').Trim()) |
    Add-Content -LiteralPath $hardware
('windows_os=' + [Environment]::OSVersion.VersionString) |
    Add-Content -LiteralPath $hardware
(& powercfg /getactivescheme 2>&1) |
    Out-File -LiteralPath (Join-Path $folder 'windows-power-plan.txt')
(& adb shell dumpsys battery 2>&1) |
    Out-File -LiteralPath (Join-Path $folder 'android-battery.txt')

$serverOut = Join-Path $folder 'windows-source.stdout.txt'
$serverErr = Join-Path $folder 'windows-source.stderr.txt'
$clientLog = Join-Path $folder 'android-receiver.txt'
$sourceArgs = @('server-wasapi',($WindowsIP + ':48235'),$PhoneIP,
    (Join-Path $KeysDir 'ca.pem'),(Join-Path $KeysDir 'server.pem'),
    (Join-Path $KeysDir 'server.key'),$capture,$EndpointId,([string]$Seconds))
if ($dedicatedSource) {
    $sourceArgs = @('server-deskfuse',($WindowsIP + ':48235'),$PhoneIP,
        (Join-Path $KeysDir 'ca.pem'),(Join-Path $KeysDir 'server.pem'),
        (Join-Path $KeysDir 'server.key'),$capture,$DeskFusePeerId,$enrolledPeerDer,([string]$Seconds))
}
# Explicit opt-in diagnostic: the Windows child inherits this local-only path,
# then passes it to its WASAPI capture child. No PCM is written to this CSV.
# Only trace when the receiver has no AAudio output; restore caller environment.
$traceEnv = 'DESKFUSE_WASAPI_PACKET_TRACE'
$priorTrace = [Environment]::GetEnvironmentVariable($traceEnv, 'Process')
$priorCapturePolicy = [Environment]::GetEnvironmentVariable('DESKFUSE_WASAPI_CAPTURE_POLICY','Process')
$priorEnginePeriod = [Environment]::GetEnvironmentVariable('DESKFUSE_WASAPI_ENGINE_PERIOD','Process')
try {
    [Environment]::SetEnvironmentVariable('DESKFUSE_WASAPI_CAPTURE_POLICY',$WindowsCapturePolicy,'Process')
    [Environment]::SetEnvironmentVariable('DESKFUSE_WASAPI_ENGINE_PERIOD',$WindowsEnginePeriod,'Process')
    if ($VerifyOnly) {
        $tracePath = Join-Path $folder 'windows-original-wasapi-packets.csv'
        [Environment]::SetEnvironmentVariable($traceEnv, $tracePath, 'Process')
        ('wasapi_packet_trace=' + $tracePath) | Add-Content -LiteralPath $manifest
    } else {
        [Environment]::SetEnvironmentVariable($traceEnv, $null, 'Process')
    }
    $proc = Start-Process -FilePath $source -ArgumentList $sourceArgs -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -WorkingDirectory (Join-Path $LabRoot 'vnext\m2a\quiche-lab') -WindowStyle Hidden -PassThru
} finally {
    [Environment]::SetEnvironmentVariable($traceEnv, $priorTrace, 'Process')
    [Environment]::SetEnvironmentVariable('DESKFUSE_WASAPI_CAPTURE_POLICY',$priorCapturePolicy,'Process')
    [Environment]::SetEnvironmentVariable('DESKFUSE_WASAPI_ENGINE_PERIOD',$priorEnginePeriod,'Process')
}
$observer = $null
try {
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(12)
    $ready = $false
    while ([DateTime]::UtcNow -lt $startupDeadline) {
        $proc.Refresh()
        if ($proc.HasExited) {
            throw ('Windows source exited before READY, code=' + $proc.ExitCode)
        }
        if ((Test-Path -LiteralPath $serverOut) -and
            (Select-String -LiteralPath $serverOut -Pattern '^READY continuous Windows source ' -Quiet)) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Windows source did not report READY within 12s; phone not started' }
    ('sender_ready_utc=' + (Get-Date).ToUniversalTime().ToString('o')) |
        Add-Content -LiteralPath $manifest

    # Passive host observer only; no scheduling, network or audio publication.
    $observer = Start-Job -ArgumentList $folder, $Seconds -ScriptBlock {
        param([string]$saveFolder, [int]$duration)
        $outFile = Join-Path $saveFolder 'host-resource-samples.csv'
        'utc,source_rss_bytes,source_cpu_seconds,brave_process_count,brave_rss_bytes' |
            Set-Content -LiteralPath $outFile -Encoding UTF8
        $end = [DateTime]::UtcNow.AddSeconds($duration + 12)
        while ([DateTime]::UtcNow -lt $end) {
            $sourceProcesses = @(Get-Process -Name 'continuous-quic-lab' -ErrorAction SilentlyContinue)
            $braveProcesses = @(Get-Process -Name brave -ErrorAction SilentlyContinue)
            $sourceRss = ($sourceProcesses | Measure-Object -Property WorkingSet64 -Sum).Sum
            $sourceCpu = ($sourceProcesses | Measure-Object -Property CPU -Sum).Sum
            $braveRss = ($braveProcesses | Measure-Object -Property WorkingSet64 -Sum).Sum
            ('{0},{1},{2},{3},{4}' -f
                [DateTime]::UtcNow.ToString('o'),
                [long]$sourceRss, [double]$sourceCpu,
                $braveProcesses.Count, [long]$braveRss) |
                Add-Content -LiteralPath $outFile
            Start-Sleep -Seconds 10
        }
    }
    # Operator performs >=15 Brave window restorations at minutes 5/20
    # when running the preregistered loaded profile; record exact times
    # in session.txt. This script does NOT claim those actions occurred.
    # VerifyOnly receives and validates actual authenticated source PCM
    # but never loads the native AAudio renderer and cannot emit phone audio.
    $verb = if ($VerifyOnly) { 'client-capture' } elseif ($Strict) { 'client-playback-strict' } else { 'client-playback-modes' }
    $modeArg = if ($Strict -or $VerifyOnly) { '' } else { $Modes + ' ' }
    if ($AndroidBufferBursts -ne 0) { $verb='client-playback-buffer'; $modeArg=$Modes + ' ' + $AndroidBufferBursts + ' ' }
    if ($AndroidSharingPolicy -ne 0 -or $AndroidUsagePolicy -ne 0 -or $NativeReserveFrames -ne 1440) {
        $verb='client-playback-experiment'
        $modeArg=($Modes,$AndroidBufferBursts,$AndroidSharingPolicy,$AndroidUsagePolicy,$NativeReserveFrames -join ' ') + ' '
    }
    $rendererArg = if ($VerifyOnly) { '' } else { '/data/local/tmp/libdeskfuse_android_audio.so ' }
    $measurementEnv = if ($MeasureCallbacks) { 'DESKFUSE_AUDIO_MEASURE_CALLBACK=1 ' } else { 'DESKFUSE_AUDIO_MEASURE_CALLBACK=0 ' }
    $cmd = 'LD_LIBRARY_PATH=/data/local/tmp ' + $measurementEnv + '/data/local/tmp/deskfuse-vnext-continuous ' + $verb + ' ' + $WindowsIP + ':48235 /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key ' + $rendererArg + $modeArg + $Seconds
    if ($VerifyOnly) {
        Write-Host 'VERIFY ONLY: Android validates incoming QUIC PCM but NEVER starts AAudio. No Xiaomi speaker playback.'
    }
    # Android Rust emits normal diagnostics on stderr. Windows PowerShell
    # 5.1 must not terminate on those lines under ErrorActionPreference=Stop.
    # The helper still returns the REAL adb/remote process exit code.
    $exitCode = & (Join-Path $PSScriptRoot 'invoke-native-audio.ps1') `
        -Executable (Get-Command adb).Source `
        -Arguments @('shell', $cmd) `
        -LogFile $clientLog
    ('client_exit=' + $exitCode) | Add-Content -LiteralPath $manifest
    if ($exitCode -ne 0) { throw ('Android receiver exit=' + $exitCode) }
} finally {
    if ($null -ne $observer) {
        Stop-Job -Job $observer -ErrorAction SilentlyContinue
        Receive-Job -Job $observer -ErrorAction SilentlyContinue |
            Out-File -LiteralPath (Join-Path $folder 'observer-errors.txt')
        Remove-Job -Job $observer -Force -ErrorAction SilentlyContinue
    }
    if (-not $proc.HasExited) {
        $proc.WaitForExit(12000) | Out-Null
        if (-not $proc.HasExited) { $proc.Kill() }
    }
    $proc.Refresh()
    ('sender_exit=' + $proc.ExitCode) | Add-Content -LiteralPath $manifest
    ('end_utc=' + (Get-Date).ToUniversalTime().ToString('o')) | Add-Content -LiteralPath $manifest
    ('brave_processes_at_end=' + @(Get-Process brave -ErrorAction SilentlyContinue).Count) | Add-Content -LiteralPath $manifest
}
Write-Host ('Evidence folder: ' + $folder)
Write-Host 'No endpoint routing or installed driver was changed.'
if ($VerifyOnly) { Write-Host 'This is silent transport diagnosis, NOT an audible or acoustic qualification.' }
