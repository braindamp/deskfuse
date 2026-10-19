# Safer, time-boxed proof on a single Windows PC. Does NOT enable Windows test signing.
# An independent SYSTEM watchdog is armed BEFORE any PnP installation is attempted.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InfPath,
    [string]$AgentExe = '',
    [int]$Width = 1440,
    [int]$Height = 3120,
    [double]$Dpr = 3.5,
    [int]$DiagonalMm = 170,
    [int]$Refresh = 60,
    [switch]$IUnderstandDriverRisk
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $AgentExe) { $AgentExe = Join-Path $root 'out\host\FocusWindow.Agent.exe' }
$rollbackSource = Join-Path $PSScriptRoot 'rollback-focusidd.ps1'
$watchdogSource = Join-Path $PSScriptRoot 'safety-watchdog.ps1'
$bootRecoverySource = Join-Path $PSScriptRoot 'safety-boot-recovery.ps1'
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$armed = $false
$success = $false
$taskName = $null
$bootTaskName = $null
$safetyDirectory = $null
$probe = $null
$devcon = $null

function Get-UptimeMs {
    return [long]([Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency)
}
function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run Windows PowerShell as Administrator. A separate non-admin user cannot install a display driver.'
    }
}
function Resolve-DevCon {
    # The WDK installs DevCon in a versioned Tools folder, typically not on PATH.
    $onPath = Get-Command devcon.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object -Property Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\devcon.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($candidates.Count -gt 0) { return $candidates[0] }
    }
    throw 'x64 DevCon.exe not found on PATH or in Windows Kits\10\Tools. No driver installed.'
}
function Get-FocusDeviceIds {
    # Enumerate present AND non-present devices. DevCon creates ROOT\DISPLAY\xxxx
    # with Root\FocusIdd as its hardware ID, not necessarily ROOT\FOCUSIDD\xxxx.
    $devices = @(Get-PnpDevice -ErrorAction Stop)
    $found = @()
    foreach ($device in $devices) {
        $instanceId = [string]$device.InstanceId
        if ($instanceId -match '(?i)^ROOT\\FOCUSIDD\\[^\\]+$') {
            $found += $instanceId
            continue
        }
        if ($instanceId -notmatch '(?i)^ROOT\\DISPLAY\\[^\\]+$') { continue }
        try {
            $hardwareProperty = Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop
            $hardwareIds = @($hardwareProperty.Data)
        } catch {
            throw "Cannot inspect hardware IDs for $instanceId; refusing installation. $($_.Exception.Message)"
        }
        foreach ($hardwareId in $hardwareIds) {
            if ([string]$hardwareId -ieq 'ROOT\FocusIdd') {
                $found += $instanceId
                break
            }
        }
    }
    return @($found | Sort-Object -Unique)
}
function Check-NoFocusDevice {
    $ids = @(Get-FocusDeviceIds)
    if ($ids.Count -gt 0) {
        throw "Existing DeskFuse devnode detected: $($ids -join ', '). Run the corrected rollback first."
    }
}
function Write-Mark([string]$name) {
    if (-not $safetyDirectory) { return }
    $path = Join-Path $safetyDirectory $name
    Set-Content -LiteralPath ($path + '.tmp') -Value (Get-Date -Format o) -Encoding Ascii
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
function Wait-Until([long]$relativeMs, [long]$startTick) {
    while (((Get-UptimeMs) - $startTick) -lt $relativeMs) {
        Start-Sleep -Milliseconds 200
        if ($safetyDirectory -and (Test-Path -LiteralPath (Join-Path $safetyDirectory 'rollback-started.flag'))) {
            throw 'Safety watchdog initiated rollback.'
        }
        if ($probe) {
            $probe.Refresh()
            if ($probe.HasExited) {
                # Start-Process -PassThru can leave ExitCode unset until WaitForExit has
                # completed. It is already exited here, so this does not block.
                $probe.WaitForExit()
                $exitCode = [int]$probe.ExitCode
                # Before OK, ANY exit is premature, even if density would have
                # returned 0 (native) or 1 (non-native).
                throw "Virtual monitor probe ended BEFORE safety OK (exit $exitCode); triggering rollback."
            }
        }
    }
}
function Confirm-Within([int]$seconds) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "DeskFuse driver test - final safety check"
    $form.Size = New-Object System.Drawing.Size(570,210)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ShowInTaskbar = $true
    $label = New-Object System.Windows.Forms.Label
    $label.Location = New-Object System.Drawing.Point(18,19)
    $label.Size = New-Object System.Drawing.Size(520,80)
    $label.Font = New-Object System.Drawing.Font('Segoe UI',11)
    $label.Text = "Are Windows and your physical monitors still working?`r`nConfirm within $seconds seconds. The virtual display will be removed immediately afterward.`r`nIf you do not confirm, the watchdog will remove it."
    $form.Controls.Add($label)
    $button = New-Object System.Windows.Forms.Button
    $button.Text = 'DESKTOP IS USABLE (OK)'
    $button.Location = New-Object System.Drawing.Point(160,115)
    $button.Size = New-Object System.Drawing.Size(235,39)
    $button.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $button
    $form.Controls.Add($button)
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 200
    $expiry = (Get-UptimeMs) + $seconds * 1000
    $tickHandler = {
        $tickMs = [long]([Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency)
        if ($tickMs -ge $expiry) {
            $timer.Stop()
            $form.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
            $form.Close()
        }
    }.GetNewClosure()
    $timer.Add_Tick($tickHandler)
    try {
        [System.Media.SystemSounds]::Exclamation.Play()
        $timer.Start()
        return ($form.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Get-UptimeMs) -le $expiry)
    } finally {
        $timer.Stop()
        $timer.Dispose()
        $form.Dispose()
    }
}
function Try-Cleanup {
    if ($probe -and -not $probe.HasExited) {
        try { $probe.Kill() } catch { Write-Warning "Could not stop probe: $_" }
    }
    if ($armed) {
        Write-Mark 'abort.flag'
        for ($attempt=1; $attempt -le 5; $attempt++) {
            try {
                & (Join-Path $PSScriptRoot 'rollback-focusidd.ps1')
                Write-Mark 'done.flag'
                return $true
            } catch {
                Write-Warning "Removal attempt $attempt failed: $($_.Exception.Message)"
                Start-Sleep -Seconds 2
            }
        }
        Write-Warning "Independent watchdog continues retrying. Logs: $safetyDirectory\watchdog.log"
        return $false
    }
    return $true
}

try {
    if (-not $IUnderstandDriverRisk) { throw 'Pass -IUnderstandDriverRisk after reviewing the risks.' }
    Assert-Admin
    if (-not [Environment]::UserInteractive) { throw 'Must run from an interactive Windows desktop.' }
    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
        throw 'Run with Windows PowerShell 5.1 in STA mode: powershell.exe -STA -File .\tools\safe-driver-proof.ps1 ...'
    }
    if ($Width -lt 320 -or $Height -lt 320 -or $Dpr -lt 1 -or $Dpr -gt 5) { throw 'Invalid display dimensions/DPR.' }
    if (-not (Test-Path -LiteralPath $AgentExe -PathType Leaf)) { throw "Missing Windows agent: $AgentExe" }
    # An old single-file EXE silently gave us stale display-detection results even
    # after source updates. Prove the exact executable version BEFORE arming or
    # installing any device. No reliance on file timestamps or source contents.
    $expectedBuild = 'DeskFuse-HoldUntilOK-0.2.29'
    $actualBuild = @(& $AgentExe '--probe-build-id' 2>&1)
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0 -or (($actualBuild -join "`n").Trim()) -cne $expectedBuild) {
        throw "Published host has an unexpected probe build ID (exit $buildExit; got '$($actualBuild -join ' ')'). Rebuild the host before installing any driver."
    }
    Write-Host "Verified running host: $expectedBuild at $AgentExe" -ForegroundColor Green
    $holdSmoke = @(& $AgentExe 'hold-gate-self-test' 2>&1)
    if ($LASTEXITCODE -ne 0 -or (($holdSmoke -join "`n") -notmatch 'PASS: release requires OK flag')) {
        throw "Hold gate preflight failed. Refusing driver installation: $($holdSmoke -join ' ')"
    }
    Write-Host 'PASS: hold gate self-test (no driver installed).' -ForegroundColor Green

    foreach ($script in @($rollbackSource,$watchdogSource,$bootRecoverySource)) {
        if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "Missing file: $script" }
    }
    $inf = (Resolve-Path -LiteralPath $InfPath).Path
    if ([IO.Path]::GetFileName($inf) -ne 'FocusIdd.inf') { throw 'Only FocusIdd.inf is accepted.' }
    $devcon = Resolve-DevCon
    Write-Host "Using x64 DevCon: $devcon"
    if (-not (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue)) { throw 'Get-PnpDevice unavailable.' }
    if (-not (Get-Command Get-PnpDeviceProperty -ErrorAction SilentlyContinue)) { throw 'Get-PnpDeviceProperty unavailable.' }
    if (-not (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue)) { throw 'Task Scheduler PowerShell module unavailable.' }
    Check-NoFocusDevice
    Write-Host 'The test can still cause a crash or display disruption. The watchdog cannot recover a PC that cannot run Windows.' -ForegroundColor Yellow
    $choice = Read-Host 'Type INSTALL FOCUSIDD to create a temporary virtual-display device'
    if ($choice.Trim() -cne 'INSTALL FOCUSIDD') { throw 'Cancelled. No driver installed.' }

    $id = [Guid]::NewGuid().ToString('N')
    $safetyDirectory = Join-Path $env:ProgramData "DesklinkSafety\$id"
    New-Item -ItemType Directory -Path $safetyDirectory -Force | Out-Null
    # Deny ordinary users write access to code subsequently executed by SYSTEM.
    & "$env:SystemRoot\System32\icacls.exe" $safetyDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect watchdog folder; installation refused.' }
    Copy-Item -LiteralPath $rollbackSource -Destination (Join-Path $safetyDirectory 'rollback-focusidd.ps1')
    Copy-Item -LiteralPath $watchdogSource -Destination (Join-Path $safetyDirectory 'safety-watchdog.ps1')
    Copy-Item -LiteralPath $bootRecoverySource -Destination (Join-Path $safetyDirectory 'safety-boot-recovery.ps1')
    $startTick = (Get-UptimeMs)
    @{Id=$id;StartTick=$startTick} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $safetyDirectory 'state.json') -Encoding ASCII

    $bootTaskName = "Desklink-FocusIdd-Recover-$id"
    $bootArgs = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $safetyDirectory 'safety-boot-recovery.ps1') + '" -SafetyDirectory "' + $safetyDirectory + '"'
    $bootAction = New-ScheduledTaskAction -Execute $psExe -Argument $bootArgs
    $bootTrigger = New-ScheduledTaskTrigger -AtStartup
    $bootSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $bootTaskName -Action $bootAction -Trigger $bootTrigger -Settings $bootSettings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    if (-not (Get-ScheduledTask -TaskName $bootTaskName -ErrorAction Stop)) { throw 'Boot recovery task not registered.' }

    $taskName = "Desklink-FocusIdd-Safety-$id"
    $argString = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $safetyDirectory 'safety-watchdog.ps1') + '" -SafetyDirectory "' + $safetyDirectory + '"'
    $action = New-ScheduledTaskAction -Execute $psExe -Argument $argString
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    # On-demand only: no scheduled rerun later if the desktop crashes during this test.
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $definition = New-ScheduledTask -Action $action -Settings $settings -Principal $principal
    Register-ScheduledTask -TaskName $taskName -InputObject $definition -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $readyPath = Join-Path $safetyDirectory 'armed.flag'
    $max = (Get-UptimeMs) + 15000
    while (-not (Test-Path -LiteralPath $readyPath) -and (Get-UptimeMs) -lt $max) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path -LiteralPath $readyPath)) { throw 'Independent SYSTEM watchdog did not arm. Refusing installation.' }
    Start-Sleep -Seconds 2
    if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') {
        throw 'SYSTEM watchdog stopped after arming. Refusing installation.'
    }
    $armed = $true
    Write-Host 'Independent SYSTEM watchdog ARMED. One safety confirmation at 30 seconds.' -ForegroundColor Green

    # Both commands run AFTER watchdog confirmation. The known unique device ID is refused at preflight.
    & "$env:SystemRoot\System32\pnputil.exe" /add-driver $inf /install
    if ($LASTEXITCODE -ne 0) { throw 'PnPUtil driver staging failed.' }
    & $devcon install $inf 'Root\FocusIdd'
    if ($LASTEXITCODE -ne 0) { throw 'DevCon root device installation failed.' }

    $evidence = Join-Path $root 'evidence'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $releaseFlag = Join-Path $safetyDirectory 'release-monitor.flag'
    $readyFlag = Join-Path $safetyDirectory 'monitor-held.flag'
    $argsProbe = "density-probe --width $Width --height $Height --dpr $($Dpr.ToString([Globalization.CultureInfo]::InvariantCulture)) --diag-mm $DiagonalMm --refresh $Refresh --out `"$safetyDirectory`" --hold-until-flag `"$releaseFlag`""
    $probe = Start-Process -FilePath $AgentExe -ArgumentList $argsProbe -RedirectStandardOutput (Join-Path $safetyDirectory 'probe-stdout.txt') -RedirectStandardError (Join-Path $safetyDirectory 'probe-stderr.txt') -PassThru

    # One 30-second observation period, followed by an immediate safety prompt.
    # Whether the user confirms or not, the driver is removed in finally.
    Wait-Until -relativeMs 30000 -startTick $startTick
    if (-not (Test-Path -LiteralPath $readyFlag -PathType Leaf)) {
        throw 'Monitor did not reach the HOLD ACTIVE state before the 30-second safety check. Rolling back.'
    }
    # The monitor must remain present during the modal safety prompt.
    if (-not (Confirm-Within -seconds 15)) { throw 'Safety confirmation missing or timed out. Rolling back.' }
    $probe.Refresh()
    if ($probe.HasExited) { throw 'Monitor probe exited before safety OK could release it. Rolling back.' }
    # Acknowledgement does not disable the independent SYSTEM watchdog (75 s absolute timeout).
    Write-Mark 'ack-1.flag'
    Write-Host 'Safety OK received. Releasing the monitor NOW.' -ForegroundColor Green
    Write-Mark 'release-monitor.flag'
    # The agent removes the monitor only AFTER seeing the release flag.
    $releaseDeadline = (Get-UptimeMs) + 9000
    while ((Get-UptimeMs) -lt $releaseDeadline) {
        $probe.Refresh()
        if ($probe.HasExited) { break }
        if (Test-Path -LiteralPath (Join-Path $safetyDirectory 'rollback-started.flag')) {
            throw 'Watchdog started rollback during monitor release.'
        }
        Start-Sleep -Milliseconds 200
    }
    $probe.Refresh()
    if (-not $probe.HasExited) { throw 'Probe did not release after OK; triggering driver rollback.' }
    $probe.WaitForExit()
    $probeExit = [int]$probe.ExitCode
    if ($probeExit -ne 0 -and $probeExit -ne 1) {
        throw "Monitor lifetime proof failed with exit code $probeExit; rolling back."
    }
    if ($probeExit -eq 1) {
        Write-Host 'Monitor held until OK. DPI is NOT NATIVE (separate known issue).' -ForegroundColor Yellow
    } else {
        Write-Host 'Monitor held until OK. DPI proof also passed.' -ForegroundColor Green
    }
    $success = $true
} catch {
    Write-Warning $_.Exception.Message
} finally {
    $clean = Try-Cleanup
    if ($clean) {
        # Only unregister after we have verified the device has been removed.
        foreach ($task in @($taskName,$bootTaskName)) {
            if ($task) {
                try { Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction Stop }
                catch { Write-Warning "Could not unregister safety task ${task}: $_" }
            }
        }
    }
    if ($safetyDirectory) { Write-Host "Watchdog/probe logs: $safetyDirectory" }
}
if (-not $success) { exit 1 }
Write-Host 'One safety confirmation received; FocusIdd driver device removed.' -ForegroundColor Green
exit 0
