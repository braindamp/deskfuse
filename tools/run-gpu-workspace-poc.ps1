[CmdletBinding()]
param(
    [string]$ClientId = "poc-client-a",
    [string[]]$Sequence = @(),
    [string]$OutRoot = "",
    [int]$DurationSec = 20,
    [int]$Fps = 60,
    [int]$Bitrate = 20000000,
    [int]$SimulateInitialScale = 0,
    [int]$HelperTimeoutSec = 300
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$nativeExe = Join-Path $repo "windows\poc\gpu-phone-workspace\build\bin\Release\DeskFuse.GpuWorkspacePoc.exe"
$agentExe = Join-Path $repo "windows\agent\FocusWindow.Agent\bin\Release\net8.0-windows\FocusWindow.Agent.exe"

if (-not $OutRoot) {
    $OutRoot = Join-Path $repo ("gpu-phone-workspace-evidence\runs\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
elseif (-not [System.IO.Path]::IsPathRooted($OutRoot)) {
    $OutRoot = Join-Path $repo $OutRoot
}
$runRoot = [System.IO.Path]::GetFullPath($OutRoot)
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$script:runLog = Join-Path $runRoot "runner.log"

function Log([string]$Message) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $script:runLog -Value $line -Encoding UTF8
}

function Write-JsonFile([string]$Path, $Object) {
    $json = $Object | ConvertTo-Json -Depth 30
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    try { return (Get-Content -Path $Path -Raw -ErrorAction Stop | ConvertFrom-Json) } catch { return $null }
}

function Get-Field($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Read-Kv([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -Path $Path) {
        $index = $line.IndexOf("=")
        if ($index -gt 0) {
            $values[$line.Substring(0, $index)] = $line.Substring($index + 1)
        }
    }
    return $values
}

function Get-TextHash([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Get-DriverSnapshot([string]$Label) {
    $deviceKey = "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY\0000"
    $device = Get-ItemProperty -Path $deviceKey -ErrorAction Stop
    $securityBase64 = ""
    if ($null -ne $device.Security) {
        try { $securityBase64 = [Convert]::ToBase64String([byte[]]$device.Security) }
        catch { $securityBase64 = [string]$device.Security }
    }

    $description = [string]$device.DeviceDesc
    $match = [regex]::Match($description, "@(oem\d+\.inf)")
    $infName = ""
    if ($match.Success) { $infName = $match.Groups[1].Value }
    $infPath = ""
    $infHash = ""
    $infSecurityLine = ""
    if ($infName) {
        $infPath = Join-Path $env:WINDIR ("INF\" + $infName)
        if (Test-Path $infPath) {
            $infHash = (Get-FileHash -Path $infPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $line = Select-String -Path $infPath -Pattern "HKR,,Security" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($line) { $infSecurityLine = $line.Line.Trim() }
        }
    }

    $dllPath = Join-Path $env:WINDIR "System32\drivers\UMDF\FocusIdd.dll"
    $dllHash = ""
    if (Test-Path $dllPath) { $dllHash = (Get-FileHash -Path $dllPath -Algorithm SHA256).Hash.ToLowerInvariant() }

    $pnputilText = (& pnputil.exe /enum-drivers 2>&1 | Out-String)
    Set-Content -Path (Join-Path $runRoot ("pnputil-" + $Label + ".txt")) -Value $pnputilText -Encoding UTF8
    $blocks = $pnputilText -split "(\r?\n){2,}" | Where-Object { $_ -match "focusidd\.inf" }
    $focusPackages = ($blocks -join "`n").Trim()
    $packagesHash = Get-TextHash $focusPackages

    $snapshot = [pscustomobject]@{
        label = $Label
        activeInfName = $infName
        activeInfPath = $infPath
        activeInfSha256 = $infHash
        activeInfSecurityLine = $infSecurityLine
        activeInfIncludesInteractiveUser = ($infSecurityLine -match "\(A;;GA;;;IU\)")
        deviceKey = $deviceKey
        deviceDriver = [string]$device.Driver
        deviceSecurityBase64 = $securityBase64
        containerId = [string]$device.ContainerID
        driverDllPath = $dllPath
        driverDllSha256 = $dllHash
        focusPackagesHash = $packagesHash
        focusPackages = $focusPackages
        capturedUtc = (Get-Date).ToUniversalTime().ToString("O")
    }
    Write-JsonFile (Join-Path $runRoot ("driver-snapshot-" + $Label + ".json")) $snapshot
    return $snapshot
}

function Wait-ForProcessExit($Process, [int]$TimeoutSeconds) {
    if ($null -eq $Process) { return $true }
    try { return $Process.WaitForExit($TimeoutSeconds * 1000) } catch { return $false }
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Start-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$StdOut,
        [Parameter(Mandatory = $true)][string]$StdErr,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Could not start $FilePath." }
    return [pscustomobject]@{
        process = $process
        stdoutTask = $process.StandardOutput.ReadToEndAsync()
        stderrTask = $process.StandardError.ReadToEndAsync()
        stdoutPath = $StdOut
        stderrPath = $StdErr
    }
}

function Save-CapturedOutput($Captured) {
    if ($null -eq $Captured) { return }
    try {
        Set-Content -Path $Captured.stdoutPath -Value $Captured.stdoutTask.Result -Encoding UTF8
    }
    catch {
        Log ("POC-RUN could not save stdout for {0}: {1}" -f $Captured.stdoutPath, $_.Exception.Message)
    }
    try {
        Set-Content -Path $Captured.stderrPath -Value $Captured.stderrTask.Result -Encoding UTF8
    }
    catch {
        Log ("POC-RUN could not save stderr for {0}: {1}" -f $Captured.stderrPath, $_.Exception.Message)
    }
}

function Get-ProcessExitCode($Process) {
    if ($null -eq $Process) { return $null }
    try {
        $Process.WaitForExit()
        $Process.Refresh()
        return $Process.ExitCode
    }
    catch {
        Log ("POC-RUN could not read the exit code: {0}" -f $_.Exception.Message)
        return $null
    }
}

function Get-Elevated {
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { return $false }
}

if (-not (Test-Path $nativeExe)) { throw "Native POC executable missing: $nativeExe (run tools\build-gpu-workspace-poc.ps1)." }
if (-not (Test-Path $agentExe)) { throw "Agent executable missing: $agentExe (run tools\build-gpu-workspace-poc.ps1)." }

$startedUtc = (Get-Date).ToUniversalTime().ToString("O")
$gitSha = "unknown"
try { $gitSha = (& git -C $repo rev-parse HEAD 2>$null).Trim() } catch { }
$elevated = Get-Elevated
$clients = @()
if ($Sequence.Count -gt 0) { $clients = @($Sequence) } else { $clients = @($ClientId) }
$sourceInf = Join-Path $repo "windows\driver\FocusIdd\FocusIdd.inf"
$sourceSecurityLine = ""
if (Test-Path $sourceInf) {
    $line = Select-String -Path $sourceInf -Pattern "HKR,,Security" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($line) { $sourceSecurityLine = $line.Line.Trim() }
}
$sourceHasIU = $sourceSecurityLine -match "\(A;;GA;;;IU\)"

if (-not $elevated) {
    $access = [pscustomobject]@{
        schema = "deskfuse.gpu-poc.access.v1"
        overall = "ACCESS_DENIED_EXPECTED_UNELEVATED"
        elevated = $false
        user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        sourceInfSecurityLine = $sourceSecurityLine
        sourceInfIncludesInteractiveUser = $sourceHasIU
        installedAclFinding = "The hardware phase is elevated for this POC; the installed FocusIdd ACL is not changed. Installed-vs-source interactive-user access is a separate integration issue."
        driverAclModified = $false
        driverInstallOrUpdatePerformed = $false
        brokerImplemented = $false
        note = "The hardware phase must be started elevated. No driver, ACL, registry, or topology change was attempted."
        capturedUtc = (Get-Date).ToUniversalTime().ToString("O")
    }
    Write-JsonFile (Join-Path $runRoot "access.json") $access
    Log "POC-RUN ACCESS_DENIED_EXPECTED_UNELEVATED"
    Log "POC-RUN no driver access attempted; rerun this runner from an elevated shell."
    exit 10
}

$planText = (& $nativeExe --plan 2>&1 | Out-String)
Set-Content -Path (Join-Path $runRoot "native-plan.txt") -Value $planText -Encoding UTF8
Log "POC-RUN elevated hardware preflight"

$driverBeforeText = (& $agentExe driver-status 2>&1 | Out-String)
Set-Content -Path (Join-Path $runRoot "driver-before.txt") -Value $driverBeforeText -Encoding UTF8
if ($driverBeforeText -notmatch "monitor arrived\s+0") {
    throw "Preflight failed: driver does not report monitor arrived 0.`n$driverBeforeText"
}

$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
$busyPorts = @($listeners | Where-Object { $_.Port -in 47810, 47812 } | Select-Object -ExpandProperty Port -Unique)
if ($busyPorts.Count -gt 0) {
    throw ("Preflight failed: TCP port(s) {0} are listening; an active host session may own the display." -f ($busyPorts -join ", "))
}

$displayBeforeText = (& $agentExe display-paths 2>&1 | Out-String)
Set-Content -Path (Join-Path $runRoot "display-paths-before.txt") -Value $displayBeforeText -Encoding UTF8
$snapshotBefore = Get-DriverSnapshot "before"

$installedSecurityLine = $snapshotBefore.activeInfSecurityLine
$installedHasIU = $snapshotBefore.activeInfIncludesInteractiveUser
$installedVsSource = if ($installedHasIU -and $sourceHasIU) { "MATCH_INTERACTIVE_USER_ACCESS" }
    elseif ((-not $installedHasIU) -and $sourceHasIU) { "INSTALLED_INF_PREDATES_SOURCE_IU" }
    elseif ($installedHasIU -and (-not $sourceHasIU)) { "SOURCE_INF_MISSING_IU" }
    else { "NEITHER_INF_HAS_IU" }

$access = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.access.v1"
    overall = "ELEVATED_HARDWARE_PREFLIGHT_OK"
    elevated = $true
    user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    accessMode = "ELEVATED_POC_ONLY"
    sourceInfPath = $sourceInf
    sourceInfSecurityLine = $sourceSecurityLine
    sourceInfIncludesInteractiveUser = $sourceHasIU
    installedInfPath = $snapshotBefore.activeInfPath
    installedInfSecurityLine = $installedSecurityLine
    installedInfIncludesInteractiveUser = $installedHasIU
    installedVsSource = $installedVsSource
    installedAclFinding = "Installed-driver vs source-INF interactive-user access is a separate installation/integration issue. The POC uses elevation and does not change the driver ACL."
    driverAclModified = $false
    driverInstallOrUpdatePerformed = $false
    focusIddReinstalled = $false
    brokerImplemented = $false
    capturedUtc = (Get-Date).ToUniversalTime().ToString("O")
}
Write-JsonFile (Join-Path $runRoot "access.json") $access

$preflight = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.preflight.v1"
    monitorArrivedBefore = $true
    busyPorts = $busyPorts
    releaseFlagAbsent = $true
    nativePlan = $planText.Trim()
    driverStatusBefore = $driverBeforeText.Trim()
    displayPathsBefore = $displayBeforeText.Trim()
    snapshotBefore = $snapshotBefore
    capturedUtc = (Get-Date).ToUniversalTime().ToString("O")
}
Write-JsonFile (Join-Path $runRoot "preflight.json") $preflight

$profileDir = Join-Path $runRoot "profiles"
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null

$attachments = @()
$sequenceAborted = $false
$helperTimeout = [Math]::Max(90, $HelperTimeoutSec)
$nativeTimeout = [Math]::Max(60, $DurationSec + 45)

for ($index = 0; $index -lt $clients.Count; $index++) {
    $client = [string]($clients[$index])
    $safeClient = ($client -replace "[^A-Za-z0-9_-]", "_")
    $attachDir = Join-Path $runRoot ("attach-{0:D2}-{1}" -f ($index + 1), $safeClient)
    New-Item -ItemType Directory -Force -Path $attachDir | Out-Null
    $readyKv = Join-Path $attachDir "monitor-ready.kv"
    $readyJson = Join-Path $attachDir "monitor-ready.json"
    $monitorResultJson = Join-Path $attachDir "monitor-result.json"
    $monitorFailureJson = Join-Path $attachDir "monitor-failure.json"
    $nativeResultJson = Join-Path $attachDir "gpu-poc-result.json"
    $releaseFlag = Join-Path $attachDir "release.flag"
    if (Test-Path $releaseFlag) { Remove-Item -Path $releaseFlag -Force }

    $helper = $null
    $native = $null
    $helperRun = $null
    $nativeRun = $null
    $helperExit = $null
    $nativeExit = $null
    $nativeTimedOut = $false
    $helperKilled = $false
    $releaseCreated = $false
    $attachError = ""
    $helperOutput = Join-Path $attachDir "helper.out.log"
    $helperError = Join-Path $attachDir "helper.err.log"
    $nativeOutput = Join-Path $attachDir "native.out.log"
    $nativeError = Join-Path $attachDir "native.err.log"
    $statusBeforeAttach = Join-Path $attachDir "driver-before-attach.txt"

    Log ("POC-RUN attach={0}/{1} client={2} dir={3}" -f ($index + 1), $clients.Count, $client, $attachDir)
    try {
        $statusText = (& $agentExe driver-status 2>&1 | Out-String)
        Set-Content -Path $statusBeforeAttach -Value $statusText -Encoding UTF8
        if ($statusText -notmatch "monitor arrived\s+0") {
            throw "monitor arrived is not 0 before this attach.`n$statusText"
        }

        $helperArgs = @(
            "poc-monitor",
            "--client-id", $client,
            "--width", "2100",
            "--height", "3120",
            "--refresh", "60",
            "--diag-mm", "170",
            "--scale", "350",
            "--out", $attachDir,
            "--release-flag", $releaseFlag,
            "--timeout-sec", [string]$helperTimeout,
            "--profile-dir", $profileDir
        )
        if ($SimulateInitialScale -gt 0) {
            $helperArgs += @("--simulate-initial-scale", [string]$SimulateInitialScale)
        }

        $helperRun = Start-CapturedProcess -FilePath $agentExe -Arguments $helperArgs `
            -StdOut $helperOutput -StdErr $helperError -WorkingDirectory $repo
        $helper = $helperRun.process
        $readyDeadline = (Get-Date).AddSeconds(90)
        while (-not (Test-Path $readyKv) -and -not $helper.HasExited -and (Get-Date) -lt $readyDeadline) {
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Path $readyKv)) {
            $failureDetail = ""
            if (Test-Path $monitorFailureJson) { $failureDetail = (Get-Content $monitorFailureJson -Raw) }
            throw ("The monitor helper did not become ready. {0}" -f $failureDetail)
        }

        $kv = Read-Kv $readyKv
        foreach ($required in @("width", "height", "refresh_hz", "current_scale", "identity_stable")) {
            if (-not $kv.ContainsKey($required)) { throw "Monitor handoff is missing $required." }
        }
        if ($kv["width"] -ne "2100" -or $kv["height"] -ne "3120" -or $kv["refresh_hz"] -ne "60") {
            throw ("Unexpected monitor mode {0}x{1}@{2}." -f $kv["width"], $kv["height"], $kv["refresh_hz"])
        }
        if ($kv["current_scale"] -ne "350") { throw ("Scale readback is {0}%, expected 350%." -f $kv["current_scale"]) }
        if ($kv["identity_stable"] -ne "true") { throw "The helper did not prove stable per-client EDID identity." }
        Log ("POC-RUN ready monitor={0} scale={1} source={2} identity={3}" -f `
            $kv["gdi_name"], $kv["current_scale"], $kv["scale_source"], $kv["identity_verdict"])

        $nativeArgs = @(
            "--run",
            "--monitor-kv", $readyKv,
            "--out", $attachDir,
            "--duration-sec", [string]$DurationSec,
            "--fps", [string]$Fps,
            "--bitrate", [string]$Bitrate,
            "--git-sha", $gitSha
        )
        $nativeRun = Start-CapturedProcess -FilePath $nativeExe -Arguments $nativeArgs `
            -StdOut $nativeOutput -StdErr $nativeError -WorkingDirectory $repo
        $native = $nativeRun.process
        if (-not (Wait-ForProcessExit $native $nativeTimeout)) {
            $nativeTimedOut = $true
            try { $native.Kill(); $native.WaitForExit(5000) } catch { }
            throw "The native POC did not exit within $nativeTimeout seconds."
        }
        $nativeExit = Get-ProcessExitCode $native
    }
    catch {
        $attachError = $_.Exception.Message
        Log ("POC-RUN attach error: {0}" -f $attachError)
    }
    finally {
        if ($null -ne $helper -and -not $helper.HasExited) {
            try {
                New-Item -ItemType File -Path $releaseFlag -Force | Out-Null
                $releaseCreated = $true
            }
            catch { Log ("POC-RUN release-flag error: {0}" -f $_.Exception.Message) }
            if (-not (Wait-ForProcessExit $helper 45)) {
                $helperKilled = $true
                try { $helper.Kill(); $helper.WaitForExit(5000) } catch { }
            }
        }
        if ($null -ne $helper) {
            $helperExit = Get-ProcessExitCode $helper
        }
        if ($null -ne $native) {
            try {
                if (-not $native.HasExited) { $native.Kill(); $native.WaitForExit(5000) }
            }
            catch { Log ("POC-RUN native cleanup error: {0}" -f $_.Exception.Message) }
            if ($null -eq $nativeExit) { $nativeExit = Get-ProcessExitCode $native }
        }
        Save-CapturedOutput $helperRun
        Save-CapturedOutput $nativeRun
    }

    $monitorResult = Read-JsonFile $monitorResultJson
    $nativeResult = Read-JsonFile $nativeResultJson
    if ($nativeExit -eq $null -and $null -ne $native) {
        try { $native.Refresh(); if ($native.HasExited) { $nativeExit = $native.ExitCode } } catch { }
    }

    $cleanup = Get-Field $monitorResult "cleanup"
    $helperRemoved = [bool](Get-Field $cleanup "monitorRemoved" $false)
    $helperArrived = [bool](Get-Field $cleanup "monitorArrived" $true)
    $physicalUnchanged = [bool](Get-Field $cleanup "physicalUnchanged" $false)
    $helperCleanupPass = $helperRemoved -and (-not $helperArrived) -and $physicalUnchanged -and (-not $helperKilled)

    $reference = Get-Field $nativeResult "reference"
    $capture = Get-Field $nativeResult "capture"
    $encoder = Get-Field $nativeResult "encoder"
    $bitstream = Get-Field $nativeResult "bitstream"
    $window = Get-Field $nativeResult "window"
    $referencePass = [bool](Get-Field $reference "pass" $false)
    $capturePass = [bool](Get-Field $capture "pass" $false)
    $encoderFrames = [int](Get-Field $encoder "framesEncoded" 0)
    $encoderBytes = [long](Get-Field $encoder "bytesWritten" 0)
    $encoderPass = $encoderFrames -gt 0 -and $encoderBytes -gt 0 -and
        [bool](Get-Field $bitstream "annexB" $false) -and
        [bool](Get-Field $bitstream "sps" $false) -and
        [bool](Get-Field $bitstream "pps" $false) -and
        [bool](Get-Field $bitstream "idr" $false)
    $nativeOverall = [string](Get-Field $nativeResult "overall" "")

    $afterText = (& $agentExe driver-status 2>&1 | Out-String)
    Set-Content -Path (Join-Path $attachDir "driver-after.txt") -Value $afterText -Encoding UTF8
    $afterArrivedZero = $afterText -match "monitor arrived\s+0"

    $identity = Get-Field $monitorResult "identity"
    $scale = Get-Field $monitorResult "scale"
    $monitor = Get-Field $monitorResult "monitor"
    $attachment = [pscustomobject]@{
        index = $index + 1
        clientId = $client
        attachDir = $attachDir
        helperExit = $helperExit
        helperKilled = $helperKilled
        releaseCreated = $releaseCreated
        nativeExit = $nativeExit
        nativeTimedOut = $nativeTimedOut
        error = $attachError
        helperOverall = [string](Get-Field $monitorResult "overall" "")
        helperExitZero = ($helperExit -eq 0)
        helperCleanupPass = $helperCleanupPass
        monitorArrivedAfter = $afterArrivedZero
        gdiName = [string](Get-Field $monitor "gdiName" "")
        width = [int](Get-Field $monitor "width" 0)
        height = [int](Get-Field $monitor "height" 0)
        refreshHz = [int](Get-Field $monitor "refreshHz" 0)
        currentScale = [int](Get-Field $scale "currentAfterPercent" 0)
        scaleBefore = [int](Get-Field $scale "currentBeforePercent" 0)
        scaleSource = [string](Get-Field $scale "source" "")
        scaleReapplied = [bool](Get-Field $scale "reapplied" $false)
        simulatedResetApplied = [bool](Get-Field $scale "simulatedResetApplied" $false)
        initial100Observed = [bool](Get-Field $scale "initial100Observed" $false)
        identityStable = [bool](Get-Field $identity "stable" $false)
        identityVerdict = [string](Get-Field $identity "verdict" "")
        serialHex = [string](Get-Field $identity "serialHex" "")
        edidSerial = [string](Get-Field $identity "edidSerial" "")
        edidInstance = [string](Get-Field $identity "edidInstance" "")
        containerId = [string](Get-Field $identity "edidContainerId" "")
        containerExpected = [string](Get-Field $identity "edidContainerExpected" "")
        containerMatch = [bool](Get-Field $identity "containerIdMatch" $false)
        nativeOverall = $nativeOverall
        dpi = [int](Get-Field $window "dpi" 0)
        windowWidth = [int](Get-Field $window "width" 0)
        windowHeight = [int](Get-Field $window "height" 0)
        referencePass = $referencePass
        rulerGreenPixels = [long](Get-Field $reference "greenPixels" 0)
        backgroundMagenta = [bool](Get-Field $reference "backgroundMagenta" $false)
        colorProbeMismatches = [int](Get-Field $reference "colorProbeMismatches" -1)
        colorProbeSamples = [int](Get-Field $reference "colorProbeSamples" 0)
        capturePass = $capturePass
        framesArrived = [long](Get-Field $capture "framesArrived" 0)
        framesProcessed = [long](Get-Field $capture "framesProcessed" 0)
        wrongSizeFrames = [long](Get-Field $capture "wrongSizeFrames" 0)
        droppedNoFreeSlot = [long](Get-Field $capture "droppedNoFreeSlot" 0)
        encoderInitialized = [bool](Get-Field $encoder "initialized" $false)
        encoderFrames = $encoderFrames
        encoderBytes = $encoderBytes
        encoderPass = $encoderPass
        encoderError = [string](Get-Field $encoder "error" "")
        driverStatusAfterArrivedZero = $afterArrivedZero
        pass = ($attachError -eq "" -and ($helperExit -eq 0) -and $helperCleanupPass -and
            (([string](Get-Field $monitorResult "overall" "")) -like "PASS_COMPONENT*") -and
            ($nativeExit -eq 0) -and ($nativeOverall -like "PASS_COMPONENT*") -and
            $afterArrivedZero -and $referencePass -and $capturePass -and $encoderPass -and
            [int](Get-Field $window "dpi" 0) -eq 336)
    }
    $attachments += $attachment
    Log ("POC-RUN attach={0} helper={1} native={2} capture={3} encoder={4} identity={5} cleanup={6}" -f `
        ($index + 1), $helperExit, $nativeExit, $capturePass, $encoderPass, $attachment.identityVerdict, $helperCleanupPass)

    if ($attachError -ne "" -or -not $attachment.pass) {
        $sequenceAborted = $true
        break
    }
    Start-Sleep -Seconds 1
}

$driverAfterText = (& $agentExe driver-status 2>&1 | Out-String)
Set-Content -Path (Join-Path $runRoot "driver-after.txt") -Value $driverAfterText -Encoding UTF8
$displayAfterText = (& $agentExe display-paths 2>&1 | Out-String)
Set-Content -Path (Join-Path $runRoot "display-paths-after.txt") -Value $displayAfterText -Encoding UTF8
$snapshotAfter = Get-DriverSnapshot "after"
$afterArrivedZero = $driverAfterText -match "monitor arrived\s+0"
$physicalDisplayPathsUnchanged = $displayBeforeText.Trim() -eq $displayAfterText.Trim()

$aclChanged = $snapshotBefore.deviceSecurityBase64 -ne $snapshotAfter.deviceSecurityBase64
$infChanged = $snapshotBefore.activeInfSha256 -ne $snapshotAfter.activeInfSha256
$dllChanged = $snapshotBefore.driverDllSha256 -ne $snapshotAfter.driverDllSha256
$packagesChanged = $snapshotBefore.focusPackagesHash -ne $snapshotAfter.focusPackagesHash
$noModificationPass = (-not $aclChanged) -and (-not $infChanged) -and (-not $dllChanged) -and (-not $packagesChanged)
$noModification = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.no-modification.v1"
    driverAclChanged = $aclChanged
    driverInfChanged = $infChanged
    driverDllChanged = $dllChanged
    driverPackagesChanged = $packagesChanged
    driverFilesChanged = ($infChanged -or $dllChanged)
    registryAclValueChanged = $aclChanged
    installedDriverReinstalled = $false
    before = $snapshotBefore
    after = $snapshotAfter
    pass = $noModificationPass
}
Write-JsonFile (Join-Path $runRoot "no-modification.json") $noModification

$identityGroups = @($attachments | Group-Object clientId)
$identityChecks = @()
$identityPass = $true
foreach ($group in $identityGroups) {
    $groupItems = @($group.Group)
    $serials = @($groupItems | ForEach-Object { $_.serialHex } | Where-Object { $_ } | Select-Object -Unique)
    $edids = @($groupItems | ForEach-Object { $_.edidSerial } | Where-Object { $_ } | Select-Object -Unique)
    $containers = @($groupItems | ForEach-Object { $_.containerId } | Where-Object { $_ } | Select-Object -Unique)
    $groupStable = $serials.Count -eq 1 -and $edids.Count -le 1 -and $containers.Count -le 1
    if (-not $groupStable) { $identityPass = $false }
    $identityChecks += [pscustomobject]@{
        clientId = $group.Name
        attaches = $groupItems.Count
        serials = $serials
        edidSerials = $edids
        containerIds = $containers
        containerStable = ($containers.Count -le 1)
        stable = $groupStable
    }
}
if ($identityGroups.Count -gt 1) {
    $firstSerials = @($identityGroups | ForEach-Object { @($_.Group)[0].serialHex } | Where-Object { $_ })
    if (($firstSerials | Select-Object -Unique).Count -ne $firstSerials.Count) {
        $identityPass = $false
    }
}
$identitySummary = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.identity.v1"
    pass = $identityPass
    groups = $identityChecks
    verdict = if ($identityPass) { "PER_CLIENT_MONITOR_IDENTITY_STABLE" } else { "IDENTITY_BLOCKED_OR_UNSTABLE" }
    note = "Registry instance names may be stale; EDID serial and container ID are the identity evidence."
}
Write-JsonFile (Join-Path $runRoot "identity.json") $identitySummary

$scalePass = @($attachments | Where-Object { $_.currentScale -ne 350 }).Count -eq 0
if ($SimulateInitialScale -gt 0) {
    $scalePass = $scalePass -and (@($attachments | Where-Object {
        -not ($_.simulatedResetApplied -or $_.initial100Observed)
    }).Count -eq 0)
}
$scaleSummary = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.scale.v1"
    pass = $scalePass
    simulatedInitialScale = $SimulateInitialScale
    attaches = @($attachments | ForEach-Object {
        [pscustomobject]@{
            clientId = $_.clientId
            scaleBefore = $_.scaleBefore
            currentScale = $_.currentScale
            source = $_.scaleSource
            reapplied = $_.scaleReapplied
            simulatedResetApplied = $_.simulatedResetApplied
            initial100Observed = $_.initial100Observed
        }
    })
}
Write-JsonFile (Join-Path $runRoot "scale.json") $scaleSummary

$allAttachmentsPass = ($attachments.Count -gt 0) -and (@($attachments | Where-Object { -not $_.pass }).Count -eq 0)
$anyEncoderBlocked = @($attachments | Where-Object { -not $_.encoderPass }).Count -gt 0
$overall = "BLOCKED"
$exitCode = 13
if ($allAttachmentsPass -and $identityPass -and $scalePass -and $afterArrivedZero -and
    $physicalDisplayPathsUnchanged -and $noModificationPass -and -not $sequenceAborted) {
    if ($anyEncoderBlocked) {
        $overall = "PARTIAL"
        $exitCode = 20
    }
    else {
        $overall = "PASS"
        $exitCode = 0
    }
}
elseif ($anyEncoderBlocked -and $allAttachmentsPass -and $identityPass -and $scalePass -and $afterArrivedZero) {
    $overall = "PARTIAL"
    $exitCode = 20
}

$verdicts = @()
if ($afterArrivedZero) { $verdicts += "MONITOR_ARRIVED_0_AFTER_CLEANUP" }
if ($physicalDisplayPathsUnchanged) { $verdicts += "PHYSICAL_DISPLAY_PATHS_UNCHANGED" }
if ($noModificationPass) { $verdicts += "FOCUSIDD_FILES_ACL_AND_PACKAGES_UNCHANGED" }
if ($identityPass) { $verdicts += "IDENTITY_STABLE_ACROSS_ATTACHES" }
if ($scalePass) { $verdicts += "SCALE_350_READBACK_VERIFIED_ON_EVERY_ATTACH" }
if ($SimulateInitialScale -gt 0 -and $scalePass) { $verdicts += "SIMULATED_100_RESET_REAPPLIED_350" }
if ($attachments.Count -eq 4 -and $clients[0] -eq $clients[3]) { $verdicts += "A_A_B_A_SEQUENCE_COMPLETE" }
if ($anyEncoderBlocked) { $verdicts += "ENCODER_BLOCKED" }
if ($overall -eq "PASS") { $verdicts += "POC_HARDWARE_PASS" }

$summary = [pscustomobject]@{
    schema = "deskfuse.gpu-poc.run.v1"
    startedUtc = $startedUtc
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    gitSha = $gitSha
    runRoot = $runRoot
    elevated = $true
    clients = $clients
    simulateInitialScale = $SimulateInitialScale
    durationSec = $DurationSec
    fps = $Fps
    bitrate = $Bitrate
    access = $access
    preflight = $preflight
    attachments = $attachments
    identity = $identitySummary
    scale = $scaleSummary
    cleanup = [pscustomobject]@{
        monitorArrivedAfter = $afterArrivedZero
        physicalDisplayPathsUnchanged = $physicalDisplayPathsUnchanged
        displayPathsBefore = $displayBeforeText.Trim()
        displayPathsAfter = $displayAfterText.Trim()
        driverStatusAfter = $driverAfterText.Trim()
    }
    noModification = $noModification
    verdicts = $verdicts
    overall = $overall
}
Write-JsonFile (Join-Path $runRoot "run-summary.json") $summary

Log ("POC-RUN identity={0} scale={1} cleanup={2} no-modification={3}" -f `
    $identityPass, $scalePass, ($afterArrivedZero -and $physicalDisplayPathsUnchanged), $noModificationPass)
Log ("POC-RUN summary={0}" -f (Join-Path $runRoot "run-summary.json"))
Log ("POC-RUN OVERALL {0}" -f $overall)
exit $exitCode
