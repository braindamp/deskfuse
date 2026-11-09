# F0b / T03–T04: exact candidate closure for independently signed installer approval.
# This is a build/staging operation. It never creates keys, installs code or starts SCM.
[CmdletBinding()]
param(
    [string]$Broker = 'out/audio-broker',
    [string]$Setup = 'out/authority-roles/setup',
    [string]$Enrollment = 'out/authority-roles/enrollment-worker',
    [string]$NativeWorker = 'out/audio-worker/payload',
    [string]$Output = 'out/authority-closure'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot bind closure to an exact commit.' }
if ((& git -C $root status --porcelain --untracked-files=normal).Count -ne 0) {
    throw 'Authority closure requires a clean checkout. Uncommitted source cannot be independently approved.'
}
$roles = @(
    @{ Name = 'Broker'; Root = $Broker; Entry = 'DeskFuse.AudioBroker.exe' },
    @{ Name = 'Setup'; Root = $Setup; Entry = 'DeskFuse.Setup.exe' },
    @{ Name = 'Enrollment'; Root = $Enrollment; Entry = 'DeskFuse.EnrollmentWorker.exe' },
    @{ Name = 'AudioWorker'; Root = $NativeWorker; Entry = 'DeskFuse.AudioWorker.exe' }
)
$destination = Join-Path $root $Output
if (Test-Path -LiteralPath $destination) { throw 'Candidate closure destination already exists; use a fresh build staging directory.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$hashes = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($role in $roles) {
        $directory = Join-Path $root $role.Root
        if (-not (Test-Path -LiteralPath (Join-Path $directory $role.Entry) -PathType Leaf)) {
            throw "Missing fixed native/managed role: $($role.Entry)"
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $directory -File)) {
            # Managed deployment closure requires runtime files, not debug symbols.
            if ($file.Name -notmatch '(?i)\.(exe|dll|json)$') { continue }
            if ($file.Name -match '(?i)\.runtimeconfig\.dev\.json$') {
                throw "Development runtime config is not an approved product artifact: $($file.Name)"
            }
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hashes.ContainsKey($file.Name)) {
                if ($hashes[$file.Name] -cne $hash) {
                    throw "Managed/native dependency collision with different bytes: $($file.Name)"
                }
                continue
            }
            $hashes.Add($file.Name, $hash)
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)
        }
    }
    $required = @(
        'DeskFuse.AudioBroker.exe','DeskFuse.AudioBroker.dll','DeskFuse.AudioBroker.deps.json','DeskFuse.AudioBroker.runtimeconfig.json',
        'DeskFuse.Setup.exe','DeskFuse.Setup.dll','DeskFuse.Setup.deps.json','DeskFuse.Setup.runtimeconfig.json',
        'DeskFuse.EnrollmentWorker.exe','DeskFuse.EnrollmentWorker.dll','DeskFuse.EnrollmentWorker.deps.json','DeskFuse.EnrollmentWorker.runtimeconfig.json',
        'DeskFuse.AudioWorker.exe','DeskFuse.Audio.dll','DeskFuse.WindowsSecurity.dll','deskfuse-source-control.dll'
    )
    foreach ($name in $required) {
        if (-not $hashes.ContainsKey($name)) { throw "Incomplete exact authority closure: $name" }
    }
    if ($hashes.Count -lt 16 -or $hashes.Count -gt 4096) { throw 'Invalid authority closure size.' }
    $ordered = [ordered]@{}
    foreach ($name in @($hashes.Keys | Sort-Object -CaseSensitive)) {
        $ordered[$name] = $hashes[$name]
    }
    $manifest = [ordered]@{
        schema = 1
        commit = $sha
        approved = $false
        installed = $false
        files = $ordered
    }
    $outputDir = Split-Path -Parent $destination
    $manifestPath = Join-Path $outputDir 'authority-candidate.json'
    [System.IO.File]::WriteAllText($manifestPath,
        ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "F0b / T03–T04 staged candidate $sha; $($hashes.Count) files. Not signed, approved or installed."
    Write-Host "Candidate manifest: $manifestPath"
}
catch {
    # Fail closed; keep partial bytes for diagnosis, but never issue an approval.
    throw
}
