# Isolated audible A/B for the pinned good reference and a selectable descendant.
# No signed-driver/APK changes, endpoint rerouting, or edits in the active checkout.
[CmdletBinding()]
param(
    [ValidateSet('PrepareAll','Prepare','Run','Suite','Report','Rate','Status','Next','BeginSteps','Firewall')]
    [string]$Action='Status',
    [ValidateSet('reference','candidate')][string]$Version='reference',
    [string]$CandidateSha='',
    [ValidateRange(10,60)][int]$Seconds=20,
    [ValidatePattern('^f[01]c[01]r[01]$')][string]$Mode='f1c1r1',
    [ValidateSet('Quick','Matrix','Repeat','Full')][string]$Suite='Quick',
    [ValidateRange(1,5)][int]$Rating=3,
    [ValidateSet('heard','not-heard','uncertain')][string]$Heard='uncertain',
    [string]$Notes='',
    [string]$LabRoot='D:\Coding\DeskFuse\vnext-wp001',
    [string]$ABRoot='D:\DeskFuseLocalData\cp001\audio-ab',
    [string]$KeysDir='D:\DeskFuseLocalKeys\m2a-physical-lab',
    [string]$AndroidSdk='D:\Android\Sdk',
    [string]$AndroidNdk='D:\Android\Sdk\ndk\30.0.16248370',
    [string]$WindowsIP='192.168.2.107',
    [string]$PhoneIP='192.168.2.114',
    [string]$EndpointId='{0.0.0.00000000}.{2ae075dc-0b84-43cf-bade-f86449a5f6fa}'
)
$ErrorActionPreference='Stop'
$referenceSha='913a2f6620d906d4539dd3f321cdbf06ece9d112'
$firewallName='DeskFuse Audio AB QUIC UDP 48235 Private'
$port=48235
$runtime=Join-Path $ABRoot 'runtime'
$sender=Join-Path $runtime 'continuous-quic-lab.exe'
$capture=Join-Path $runtime 'deskfuse-wasapi-capture.exe'
$remoteExe='/data/local/tmp/deskfuse-audio-ab'
$remoteSo='/data/local/tmp/libdeskfuse_audio_ab.so'
$fileNames=@('continuous-quic-lab.exe','deskfuse-wasapi-capture.exe','continuous-quic-lab-android','libdeskfuse_android_audio.so')

if (-not (Test-Path -LiteralPath $LabRoot -PathType Container)) { throw 'Development checkout missing' }
$branch=(& git -C $LabRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'feature/vnext-wp001-native-lab') { throw 'Not in isolated development branch' }
$tip=(& git -C $LabRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read development HEAD' }
& git -C $LabRoot merge-base --is-ancestor $referenceSha $tip
if ($LASTEXITCODE -ne 0) { throw 'Pinned reference is not an ancestor of development HEAD' }

function Src([string]$slot) { Join-Path $ABRoot ('src\' + $slot) }
function Art([string]$slot) { Join-Path $ABRoot ('artifacts\' + $slot) }
function ReadPair([string]$slot) {
    $p=Join-Path (Art $slot) 'pair.json'
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { return $null }
    Get-Content -LiteralPath $p -Raw | ConvertFrom-Json
}
function CheckPair([string]$slot) {
    $p=ReadPair $slot
    if ($null -eq $p) { throw "$slot not built: run -Action PrepareAll" }
    foreach ($name in $fileNames) {
        $file=Join-Path (Art $slot) $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file" }
        $actual=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected=$p.hashes.PSObject.Properties[$name].Value
        if ($actual -cne $expected) { throw "Pair hash mismatch: $file" }
    }
    return $p
}
function Checkout([string]$slot,[string]$sha) {
    & git -C $LabRoot cat-file -e ($sha + '^{commit}')
    if ($LASTEXITCODE -ne 0) { throw "Commit not present locally: $sha" }
    $path=Src $slot
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        # This function returns exactly one path. Native Git writes a checkout
        # banner to stdout; leaking it makes Join-Path/CMake receive TWO paths.
        & git -c gc.auto=0 -c maintenance.auto=false -C $LabRoot worktree add --detach $path $sha | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Isolated worktree creation failed' }
    } else {
        if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) { throw "Not a Git worktree: $path" }
        $a=(& git -C $LabRoot rev-parse --path-format=absolute --git-common-dir).Trim()
        $b=(& git -C $path rev-parse --path-format=absolute --git-common-dir).Trim()
        if ($LASTEXITCODE -ne 0 -or $a -ine $b) { throw "Foreign worktree: $path" }
        $dirty=@(& git -C $path status --porcelain --untracked-files=no)
        if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) { throw "Tracked edits in ${path}: refusing to switch" }
        if ((& git -C $path rev-parse HEAD).Trim() -ne $sha) {
            if ($slot -eq 'reference') { throw 'Reference worktree is not pinned' }
            & git -c gc.auto=0 -c maintenance.auto=false -C $path switch --detach $sha | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Candidate checkout failed' }
        }
    }
    if ((& git -C $path rev-parse HEAD).Trim() -ne $sha) { throw 'Worktree SHA mismatch' }
    return $path
}
function BuildTools {
    $vswhere=Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe missing' }
    $vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $vs) { throw 'Visual Studio C++ tools not found' }
    & (Join-Path $vs 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
    $env:Path="$(Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin');$(Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja');$env:Path"
    Get-Command cmake.exe,ninja.exe,cl.exe,cargo.exe,adb.exe -ErrorAction Stop | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'))) { throw 'Pinned NDK missing' }
    $env:ANDROID_HOME=$AndroidSdk
    $env:ANDROID_SDK_ROOT=$AndroidSdk
    $env:ANDROID_NDK_HOME=$AndroidNdk
}
function Prepare([string]$slot,[string]$sha) {
    if ($slot -eq 'reference' -and $sha -ne $referenceSha) { throw 'Reference SHA is immutable' }
    if ($slot -eq 'candidate') {
        & git -C $LabRoot merge-base --is-ancestor $referenceSha $sha
        if ($LASTEXITCODE -ne 0) { throw 'Candidate predates reference' }
        & git -C $LabRoot merge-base --is-ancestor $sha $tip
        if ($LASTEXITCODE -ne 0) { throw 'Candidate not an ancestor of active development HEAD' }
    }
    $previous=ReadPair $slot
    if ($null -ne $previous -and $previous.commit -eq $sha) {
        $null=CheckPair $slot
        Write-Host "$slot already built: $sha"
        return
    }
    $worktree=Checkout $slot $sha
    if ($worktree -isnot [string] -or $worktree -ine (Src $slot)) {
        throw "Checkout must return one exact isolated worktree path for $slot"
    }
    BuildTools
    $m1=Join-Path $worktree 'vnext\m1'
    $build1=Join-Path $m1 'build'
    & cmake -S $m1 -B $build1 -G Ninja -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows CMake configure failed' }
    & cmake --build $build1 --parallel 2
    if ($LASTEXITCODE -ne 0) { throw 'Windows WASAPI build failed' }
    $m2b=Join-Path $worktree 'vnext\m2b\android-native'
    $build2=Join-Path $m2b 'build-android'
    $toolchain=Join-Path $AndroidNdk 'build\cmake\android.toolchain.cmake'
    & cmake -S $m2b -B $build2 -G Ninja "-DCMAKE_TOOLCHAIN_FILE=$toolchain" -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-35 -DANDROID_STL=c++_shared -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw 'Android CMake configure failed' }
    & cmake --build $build2 --parallel 2
    if ($LASTEXITCODE -ne 0) { throw 'Android native renderer build failed' }
    $crate=Join-Path $worktree 'vnext\m2a\quiche-lab'
    $wrapper=Join-Path $crate 'cmake\android-ndk-wrapper.cmake'
    if (-not (Test-Path -LiteralPath $wrapper -PathType Leaf)) { throw 'Selected revision lacks Android Rust wrapper' }
    $env:CMAKE_TOOLCHAIN_FILE_aarch64_linux_android=(Resolve-Path -LiteralPath $wrapper).Path
    $env:CMAKE_GENERATOR_aarch64_linux_android='Ninja'
    Set-Item -Path 'Env:BINDGEN_EXTRA_CLANG_ARGS_aarch64-linux-android' -Value '--target=aarch64-linux-android35'
    Push-Location -LiteralPath $crate
    try {
        & cargo build --locked --bin continuous-quic-lab
        if ($LASTEXITCODE -ne 0) { throw 'Windows Rust build failed' }
        & cargo ndk -t arm64-v8a -P 35 build --bin continuous-quic-lab --locked
        if ($LASTEXITCODE -ne 0) { throw 'Android Rust build failed' }
    } finally { Pop-Location }
    foreach ($cache in @((Join-Path $build1 'CMakeCache.txt'),(Join-Path $build2 'CMakeCache.txt'))) {
        if (-not (Select-String -LiteralPath $cache -Pattern '^CMAKE_BUILD_TYPE:STRING=Release$' -Quiet)) { throw "Native build not Release: $cache" }
    }
    $built=@((Join-Path $crate 'target\debug\continuous-quic-lab.exe'),(Join-Path $build1 'deskfuse-wasapi-capture.exe'),(Join-Path $crate 'target\aarch64-linux-android\debug\continuous-quic-lab'),(Join-Path $build2 'libdeskfuse_android_audio.so'))
    $dst=Art $slot
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    $hashes=@{}
    for ($i=0;$i -lt $built.Count;$i++) {
        if (-not (Test-Path -LiteralPath $built[$i] -PathType Leaf)) { throw "Build output missing: $($built[$i])" }
        $out=Join-Path $dst $fileNames[$i]
        Copy-Item -LiteralPath $built[$i] -Destination $out -Force
        $hashes[$fileNames[$i]]=(Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    # Explicit mode grammar comes from the exact paired source revision.
    $binSource=Get-Content -LiteralPath (Join-Path $crate 'src\bin\continuous-quic-lab.rs') -Raw
    $modes=$binSource.Contains('mode == "client-playback-modes"')
    $strict=$binSource.Contains('mode == "client-playback-strict"')
    $clientMode=if ($modes) { 'client-playback-modes' } else { 'client-playback' }
    $data=[ordered]@{slot=$slot;commit=$sha;mode=$clientMode;supports_modes=$modes;supports_strict=$strict;hashes=$hashes;utc=[DateTime]::UtcNow.ToString('o')}
    $data | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dst 'pair.json') -Encoding UTF8
    $null=CheckPair $slot
    Write-Host "BUILT $slot $sha  (audible mode=$clientMode)"
}
function Devices {
    $ips=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
    if ($ips -notcontains $WindowsIP) { throw "Windows IP not present: $WindowsIP" }
    $adb=(Get-Command adb.exe -ErrorAction Stop).Source
    $connected=@(& $adb devices | Select-String '^\S+\s+device\s*$')
    if ($connected.Count -ne 1) { throw 'Exactly one authorized ADB phone required' }
    $route=(& $adb shell ip route get $WindowsIP) -join ' '
    if ($route -notmatch ('src\s+' + [regex]::Escape($PhoneIP))) { throw "Unexpected Android route: $route" }
    foreach ($f in @('ca.pem','server.pem','server.key')) {
        if (-not (Test-Path -LiteralPath (Join-Path $KeysDir $f) -PathType Leaf)) { throw "Local lab credential missing: $f" }
    }
    foreach ($f in @('df-lab-ca.pem','df-lab-client.pem','df-lab-client.key')) {
        & $adb shell ('test -r /data/local/tmp/' + $f)
        if ($LASTEXITCODE -ne 0) { throw "Android lab credential missing: $f" }
    }
}
function Run([string]$slot,[string]$flags='f1c1r1') {
    $pair=CheckPair $slot
    if ($flags -notmatch '^f[01]c[01]r[01]$') { throw 'Invalid processing flags' }
    if ($pair.mode -ne 'client-playback-modes' -and $flags -ne 'f1c1r1') {
        throw "$slot has no selectable processing flags. It cannot run $flags."
    }
    if (@(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0) { throw "UDP $port already occupied" }
    $rules=@(Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' })
    if ($rules.Count -ne 1) { throw 'Scoped AB firewall rule missing. Run -Action Firewall once as administrator.' }
    Devices
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $art=Art $slot
    Copy-Item -LiteralPath (Join-Path $art 'continuous-quic-lab.exe') -Destination $sender -Force
    Copy-Item -LiteralPath (Join-Path $art 'deskfuse-wasapi-capture.exe') -Destination $capture -Force
    $androidExe=Join-Path $art 'continuous-quic-lab-android'
    $androidSo=Join-Path $art 'libdeskfuse_android_audio.so'
    $adb=(Get-Command adb.exe -ErrorAction Stop).Source
    & $adb push $androidExe $remoteExe
    if ($LASTEXITCODE -ne 0) { throw 'Android executable push failed' }
    & $adb push $androidSo $remoteSo
    if ($LASTEXITCODE -ne 0) { throw 'Android renderer push failed' }
    & $adb shell "chmod 700 $remoteExe"
    if ($LASTEXITCODE -ne 0) { throw 'Android chmod failed' }
    $hashes=@(& $adb shell "sha256sum $remoteExe $remoteSo")
    if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne 2) { throw 'Android hash query failed' }
    $expected=@((Get-FileHash -LiteralPath $androidExe -Algorithm SHA256).Hash.ToLowerInvariant(),(Get-FileHash -LiteralPath $androidSo -Algorithm SHA256).Hash.ToLowerInvariant())
    for ($i=0;$i -lt 2;$i++) {
        if ($hashes[$i] -notmatch '^([a-fA-F0-9]{64})\s+') { throw 'Malformed Android SHA256 output' }
        if ($Matches[1].ToLowerInvariant() -cne $expected[$i]) { throw 'Android binary hash mismatch' }
    }
    $stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $evidence=Join-Path $ABRoot ('runs\' + $stamp + '-' + $slot + '-' + $pair.commit.Substring(0,10))
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    $pair | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'pair.json') -Encoding UTF8
    $startUtc=[DateTime]::UtcNow.ToString('o')
    $out=Join-Path $evidence 'windows-source.stdout.txt'
    $err=Join-Path $evidence 'windows-source.stderr.txt'
    $client=Join-Path $evidence 'android-receiver.txt'
    $args=@('server-wasapi',($WindowsIP + ':' + $port),$PhoneIP,(Join-Path $KeysDir 'ca.pem'),(Join-Path $KeysDir 'server.pem'),(Join-Path $KeysDir 'server.key'),$capture,$EndpointId,[string]$Seconds)
    Write-Host "AUDIBLE $slot commit=$($pair.commit), receiver=$($pair.mode), processing=$(if ($pair.mode -eq 'client-playback-modes') {$flags} else {'reference-native'}), length=$Seconds sec. Set low Xiaomi volume."
    $proc=Start-Process -FilePath $sender -ArgumentList $args -RedirectStandardOutput $out -RedirectStandardError $err -WorkingDirectory $runtime -NoNewWindow -PassThru
    $clientExit=$null
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(12)
        $ready=$false
        while ([DateTime]::UtcNow -lt $deadline) {
            $proc.Refresh()
            if ($proc.HasExited) { throw "Windows source exited before READY ($($proc.ExitCode))" }
            if ((Test-Path -LiteralPath $out) -and (Select-String -LiteralPath $out -Pattern '^READY continuous Windows source ' -Quiet)) { $ready=$true; break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw 'Windows READY timeout; phone not started' }
        $flag=if ($pair.mode -eq 'client-playback-modes') { ' ' + $flags } else { '' }
        $cmd="LD_LIBRARY_PATH=/data/local/tmp $remoteExe $($pair.mode) $WindowsIP" + ":$port /data/local/tmp/df-lab-ca.pem /data/local/tmp/df-lab-client.pem /data/local/tmp/df-lab-client.key $remoteSo$flag $Seconds"
        $helper=Join-Path $LabRoot 'vnext\qualification\invoke-native-audio.ps1'
        $clientExit=& $helper -Executable $adb -Arguments @('shell',$cmd) -LogFile $client
        if ($clientExit -ne 0) { throw "Android audible receiver exit=$clientExit" }
    } finally {
        $proc.Refresh()
        if (-not $proc.HasExited) {
            $proc.WaitForExit(12000) | Out-Null
            $proc.Refresh()
            if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
        }
        $proc.Refresh()
        $observation=[ordered]@{
            schema=1; slot=$slot; commit=$pair.commit; seconds=$Seconds
            client_mode=$pair.mode
            processing_mode=if ($pair.mode -eq 'client-playback-modes') { $flags } else { 'reference-native' }
            started_utc=$startUtc; ended_utc=[DateTime]::UtcNow.ToString('o')
            windows_exit=$proc.ExitCode; android_exit=$clientExit
            windows_ip=$WindowsIP; phone_ip=$PhoneIP
        }
        $observation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'run.json') -Encoding UTF8
        Write-Host "EVIDENCE $evidence / Windows exit=$($proc.ExitCode) / Android exit=$clientExit"
        foreach ($log in @($out,$err,$client)) {
            if (Test-Path -LiteralPath $log) {
                Get-Content -LiteralPath $log | Select-String '^(AUTH|DONE|FAIL|AUDIO|TRACE Windows SOURCE_FILTER_DROP|TRACE Android SOURCE_GAP)' | Select-Object -Last 12
            }
        }
    }
    if ($proc.ExitCode -ne 0) { throw "Windows sender failed (exit=$($proc.ExitCode))" }
    Write-Host 'Playback test completed; acoustic quality is judged by listener.'
}

function RunSuite([string]$kind) {
    $null=CheckPair 'reference'
    $candidate=CheckPair 'candidate'
    # Each item is version/mode; every trial always starts real AAudio.
    # Quick falls back to identical native playback when stepping through old
    # commits that do not yet expose the processing flags.
    $plan=@(switch ($kind) {
        'Quick' {
            if ($candidate.mode -eq 'client-playback-modes') {
                'reference/f1c1r1';'candidate/f0c0r0';'candidate/f1c1r1';'reference/f1c1r1'
            } else {
                'reference/f1c1r1';'candidate/f1c1r1';'candidate/f1c1r1';'reference/f1c1r1'
            }
        }
        'Repeat' { 'reference/f1c1r1';'candidate/f1c1r1';'candidate/f1c1r1';'reference/f1c1r1' }
        'Matrix' {
            if ($candidate.mode -ne 'client-playback-modes') { throw 'Candidate revision does not support selectable DSP matrix' }
            'reference/f1c1r1'
            foreach ($flags in @('f0c0r0','f1c0r0','f0c1r0','f0c0r1','f1c1r0','f1c0r1','f0c1r1','f1c1r1')) {
                'candidate/' + $flags
            }
            'reference/f1c1r1'
        }
        'Full' {
            if ($candidate.mode -ne 'client-playback-modes') { throw 'Full suite requires candidate with selectable modes; use Quick or Repeat' }
            'reference/f1c1r1';'candidate/f0c0r0';'candidate/f1c1r1';'reference/f1c1r1'
            foreach ($flags in @('f1c0r0','f0c1r0','f0c0r1','f1c1r0','f1c0r1','f0c1r1')) {
                'candidate/' + $flags
            }
            'reference/f1c1r1';'candidate/f1c1r1'
        }
    })
    for ($i=0;$i -lt $plan.Count;$i++) {
        $parts=$plan[$i].Split('/')
        if ($parts.Count -ne 2) { throw 'Invalid built-in audible suite' }
        Write-Host ("AUDIBLE SUITE $kind trial $($i+1)/$($plan.Count) : $($parts[0]) $($parts[1])")
        # Keep failures visible, stop immediately, never claim unattended
        # acoustic qualification. Per-run evidence persists on failure.
        Run $parts[0] $parts[1]
    }
    Write-Host "All $($plan.Count) $kind A/B audio trials completed. Run -Action Report."
}
function Report {
    $analyzer=Join-Path $PSScriptRoot 'analyze-audio-ab.py'
    if (-not (Test-Path -LiteralPath $analyzer -PathType Leaf)) { throw 'A/B analyzer missing' }
    $python=Get-Command python.exe -ErrorAction Stop
    & $python.Source $analyzer (Join-Path $ABRoot 'runs')
    if ($LASTEXITCODE -ne 0) { throw 'A/B analyzer failed' }
}
function Rate {
    $runsPath=Join-Path $ABRoot 'runs'
    if (-not (Test-Path -LiteralPath $runsPath -PathType Container)) { throw 'No audible A/B sessions exist' }
    $folders=@(Get-ChildItem -LiteralPath $runsPath -Directory | Sort-Object LastWriteTimeUtc -Descending)
    if ($folders.Count -eq 0) { throw 'No A/B sessions exist' }
    $latest=$folders[0].FullName
    $runPath=Join-Path $latest 'run.json'
    if (-not (Test-Path -LiteralPath $runPath -PathType Leaf)) { throw 'Latest session has no run manifest' }
    $record=Get-Content -LiteralPath $runPath -Raw | ConvertFrom-Json
    if ($record.slot -ne $Version) {
        throw "Last run is $($record.slot); specify -Version $($record.slot) to avoid misattributed listening notes."
    }
    [ordered]@{
        utc=[DateTime]::UtcNow.ToString('o'); heard=$Heard
        rating_1_to_5=$Rating; notes=$Notes
        method='human operator report; no microphone measurement'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $latest 'listening.json') -Encoding UTF8
    Write-Host "Listening notes saved for $Version $($record.commit) in $latest"
}

function Status {
    Write-Host "REFERENCE=$referenceSha; DEV_TIP=$tip"
    foreach ($slot in @('reference','candidate')) {
        $p=ReadPair $slot
        if ($null -eq $p) { Write-Host "$slot : NOT BUILT" }
        else { Write-Host "$slot : $($p.commit) / $($p.mode)" }
    }
}
switch ($Action) {
    'Status' { Status }
    'Firewall' {
        $admin=([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $admin) { throw 'Administrator PowerShell required to add scoped firewall rule' }
        if (Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue) { throw 'A/B rule exists; inspect rather than overwrite it' }
        New-Item -ItemType Directory -Force -Path $runtime | Out-Null
        New-NetFirewallRule -DisplayName $firewallName -Enabled True -Direction Inbound -Action Allow -Profile Private -Protocol UDP -LocalAddress $WindowsIP -LocalPort $port -RemoteAddress $PhoneIP -Program $sender | Out-Null
        Write-Host "Scoped A/B UDP $port Private allow for $PhoneIP installed"
    }
    'PrepareAll' { Prepare 'reference' $referenceSha; Prepare 'candidate' $tip; Status }
    'Prepare' {
        $sha=if ($Version -eq 'reference') { $referenceSha } elseif ($CandidateSha) {
            (& git -C $LabRoot rev-parse --verify ($CandidateSha + '^{commit}')).Trim()
        } else { $tip }
        if ($LASTEXITCODE -ne 0) { throw "Commit unknown: $CandidateSha" }
        Prepare $Version $sha
        Status
    }
    'BeginSteps' { Prepare 'reference' $referenceSha; Prepare 'candidate' $referenceSha; Status }
    'Next' {
        $p=ReadPair 'candidate'
        if ($null -eq $p) { throw 'Prepare candidate first' }
        $history=@(& git -C $LabRoot rev-list --first-parent --reverse ($referenceSha + '..' + $tip))
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read commit progression' }
        $chain=@($referenceSha) + @($history)
        $at=-1
        for ($i=0;$i -lt $chain.Count;$i++) { if ($chain[$i] -eq $p.commit) { $at=$i; break } }
        if ($at -lt 0) { throw 'Candidate is not on first-parent progression' }
        if ($at -ge $history.Count) { throw 'Candidate is already at development HEAD' }
        Prepare 'candidate' $history[$at]
        Status
    }
    'Run' { Run $Version $Mode }
    'Suite' { RunSuite $Suite }
    'Report' { Report }
    'Rate' { Rate }
}
