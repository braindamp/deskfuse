# CP-001/MP-001 physical Windows-to-Xiaomi run. No driver/APK install,
# endpoint rerouting, captured audio upload, or GitHub key operations.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$EndpointId,
    [Parameter(Mandatory=$true)][string]$AndroidExecutable,
    [Parameter(Mandatory=$true)][string]$AndroidNativeSo,
    [ValidatePattern('^f[01]c[01]r[01]$')][string]$Modes = 'f1c1r1',
    [switch]$Strict,
    [ValidateRange(10,1800)][int]$Seconds = 1800,
    [string]$WindowsIP = '192.168.2.107',
    [string]$PhoneIP = '192.168.2.114',
    [string]$KeysDir = 'D:\DeskFuseLocalKeys\m2a-physical-lab',
    [string]$LabRoot = 'D:\Coding\DeskFuse\vnext-wp001',
    [string]$EvidenceRoot = 'D:\DeskFuseLocalData\cp001',
    [string]$Profile = 'audio-only'
)
$ErrorActionPreference = 'Stop'
if ($Strict -and $Modes -ne 'f0c0r0') { throw 'Strict PCM verification requires f0c0r0 (no DSP or fade)' }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$folder = Join-Path $EvidenceRoot ($stamp + '-' + ($Profile -replace '[^\w-]','_') + '-' + $Modes)
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$manifest = Join-Path $folder 'session.txt'
$source = Join-Path $LabRoot 'vnext\m2a\quiche-lab\target\debug\continuous-quic-lab.exe'
$capture = Join-Path $LabRoot 'vnext\m1\build\deskfuse-wasapi-capture.exe'
$native = (Resolve-Path -LiteralPath $AndroidNativeSo).Path
$android = (Resolve-Path -LiteralPath $AndroidExecutable).Path
foreach ($p in @($source, $capture, $android, $native, (Join-Path $KeysDir 'ca.pem'), (Join-Path $KeysDir 'server.pem'), (Join-Path $KeysDir 'server.key'))) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Required local input missing: $p" }
}
if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw 'adb not on PATH' }
$branch = (& git -C $LabRoot branch --show-current).Trim()
$sha = (& git -C $LabRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'feature/vnext-wp001-native-lab') {
    throw 'Only the isolated development branch is permitted'
}
$worktree = (& git -C $LabRoot status --porcelain) -join [Environment]::NewLine
if ($worktree) { throw 'Commit or remove worktree modifications to preserve exact evidence first' }
$device = (& adb get-serialno).Trim()
if ($LASTEXITCODE -ne 0 -or $device -eq 'unknown') { throw 'Physical ADB device not ready' }
$events = @(
    'start_utc=' + (Get-Date).ToUniversalTime().ToString('o'),
    'git_branch=' + $branch,
    'git_commit=' + $sha,
    'profile=' + $Profile,
    'modes=' + $Modes,
    'strict_pcm=' + [bool]$Strict,
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
$events + $hashes | Set-Content -LiteralPath $manifest -Encoding UTF8
& adb push $android /data/local/tmp/deskfuse-vnext-continuous |
    Tee-Object -FilePath (Join-Path $folder 'adb-push-executable.txt')
if ($LASTEXITCODE -ne 0) { throw 'Android QUIC executable push failed' }
& adb push $native /data/local/tmp/libdeskfuse_android_audio.so |
    Tee-Object -FilePath (Join-Path $folder 'adb-push-renderer.txt')
if ($LASTEXITCODE -ne 0) { throw 'Android AAudio native library push failed' }
& adb shell chmod 700 /data/local/tmp/deskfuse-vnext-continuous
if ($LASTEXITCODE -ne 0) { throw 'Android executable chmod failed' }
& adb shell 'sha256sum /data/local/tmp/deskfuse-vnext-continuous /data/local/tmp/libdeskfuse_android_audio.so' |
    Tee-Object -FilePath (Join-Path $folder 'android-sha256.txt')
if ($LASTEXITCODE -ne 0) { throw 'Android remote hash verification failed' }
$serverOut = Join-Path $folder 'windows-source.stdout.txt'
$serverErr = Join-Path $folder 'windows-source.stderr.txt'
$clientLog = Join-Path $folder 'android-receiver.txt'
$sourceArgs = @('server-wasapi',($WindowsIP + ':48235'),$PhoneIP,
    (Join-Path $KeysDir 'ca.pem'),(Join-Path $KeysDir 'server.pem'),
    (Join-Path $KeysDir 'server.key'),$capture,$EndpointId,([string]$Seconds))
$proc = Start-Process -FilePath $source -ArgumentList $sourceArgs -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr -WorkingDirectory (Join-Path $LabRoot 'vnext\m2a\quiche-lab') -NoNewWindow -PassThru
try {
    # Operator performs >=15 Brave window restorations at minutes 5/20
    # when running the preregistered loaded profile; record exact times
    # in session.txt. This script does NOT claim those actions occurred.
    $verb = if ($Strict) { 'client-playback-strict' } else { 'client-playback-modes' }
    $modeArg = if ($Strict) { '' } else { $Modes + ' ' }
    $cmd = 'LD_LIBRARY_PATH=/data/local/tmp /data/local/tmp/deskfuse-vnext-continuous ' + $verb + ' ' + $WindowsIP + ':48235 /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key /data/local/tmp/libdeskfuse_android_audio.so ' + $modeArg + $Seconds
    & adb shell $cmd 2>&1 | Tee-Object -FilePath $clientLog
    $exitCode = $LASTEXITCODE
    ('client_exit=' + $exitCode) | Add-Content -LiteralPath $manifest
    if ($exitCode -ne 0) { throw ('Android receiver exit=' + $exitCode) }
} finally {
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
