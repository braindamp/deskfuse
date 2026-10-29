# Interactive/operator side only. SYSTEM helper owns all native admission and retirement.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-DeskFuseNativeReadOnly {
    if (-not ('DeskFuse.MaintenanceReadOnly' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DeskFuse {
 public static class MaintenanceReadOnly {
  [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
  [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
  public static string DefaultProgramFiles() {
   Guid id = new Guid("6d809377-6af0-444b-8957-a3773f02200e"); IntPtr path;
   Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0x400, IntPtr.Zero, out path));
   try { return Marshal.PtrToStringUni(path); } finally { Marshal.FreeCoTaskMem(path); }
  }
 }
}
'@
    }
}
function Get-DeskFuseUptime { Initialize-DeskFuseNativeReadOnly; return [long][DeskFuse.MaintenanceReadOnly]::GetTickCount64() }
function Get-DeskFuseMaintenanceRoot {
    Initialize-DeskFuseNativeReadOnly
    return Join-Path ([DeskFuse.MaintenanceReadOnly]::DefaultProgramFiles()) 'DeskFuse-Maintenance'
}
function Read-DeskFuseJson([string]$Path, [int]$Maximum = 1048576) {
    $file = $null
    try {
        $file = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($file.Length -le 0 -or $file.Length -gt $Maximum) { throw 'Invalid bounded record length.' }
        $reader = [IO.StreamReader]::new($file, [Text.UTF8Encoding]::new($false, $true), $false)
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { if ($file) { $file.Dispose() } }
}
function Open-DeskFuseMailbox([string]$Path) {
    for ($attempt = 0; ; $attempt++) {
        try { return [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if (($_.Exception.HResult -band 65535) -notin @(32,33) -or $attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 10
        }
    }
}
function Get-DeskFuseWatchStatus([string]$RunDirectory) {
    $file = Open-DeskFuseMailbox (Join-Path $RunDirectory 'watch-status.json')
    try {
        if ($file.Length -le 0 -or $file.Length -gt 16384) { throw 'Invalid watchdog record size.' }
        $reader = [IO.StreamReader]::new($file, [Text.UTF8Encoding]::new($false, $true), $false)
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { $file.Dispose() }
}
function New-DeskFuseRunnerState($Runtime, $Policy, [Guid]$NamespaceId, [string]$RunDirectory, [long]$NowMs) {
    if ($NamespaceId -eq [Guid]::Empty -or [Guid]$Runtime.runId -eq [Guid]::Empty) { throw 'Missing run binding.' }
    return [pscustomobject]@{
        Runtime=$Runtime; Policy=$Policy; NamespaceId=$NamespaceId; RunDirectory=$RunDirectory
        Revision=[long]1; Probe=$null; ProbeRequested=$false; ProofReady=$false; Acknowledge=0; Close=$false
        Prompt=0; Watch=$null; WatchRevision=[long]0; WatchTick=[long]-1; LastWatchAt=$NowMs; StartedAt=$NowMs
        ArmedAt=$null; State=$null; Finished=$false; Reason=''
    }
}
function Close-DeskFuseRunner($Session, [string]$Reason) {
    $Session.Close = $true; $Session.Prompt = 0
    if (-not $Session.Reason) { $Session.Reason = $Reason }
}
function Update-DeskFuseRunner($Session, $Status, [long]$NowMs) {
    if ($Session.Finished) { return 'Retired' }
    if ($NowMs -lt $Session.LastWatchAt) { Close-DeskFuseRunner $Session 'ClockRegressed' }
    if ($null -ne $Status -and $Status.PSObject.Properties.Name -contains 'version') {
        try {
            if ($Status.version -ne 1 -or [Guid]$Status.runId -ne [Guid]$Session.Runtime.runId -or
                [Guid]$Status.state.runId -ne [Guid]$Session.Runtime.runId -or
                [Guid]$Status.watch.nonce -ne [Guid]$Session.Runtime.watchNonce -or $Status.watch.role -ne 'Watch' -or
                [long]$Status.watch.pid -le 0 -or [long]$Status.watch.creationFileTime -le 0 -or
                [long]$Status.revision -lt 1 -or (-not $Status.state.retired -and [long]$Status.observedAtMs -gt $NowMs) -or
                [long]$Status.revision -lt $Session.WatchRevision) { throw 'Wrong or stale watchdog identity.' }
            $identity = '{0}/{1}/{2}' -f $Status.watch.pid,$Status.watch.creationFileTime,$Status.watch.nonce
            if ($null -ne $Session.Watch -and $identity -cne $Session.Watch) { throw 'Watchdog process changed.' }
            if ([long]$Status.revision -eq $Session.WatchRevision -and [long]$Status.observedAtMs -ne $Session.WatchTick) { throw 'Watchdog revision reused.' }
            if ([long]$Status.revision -gt $Session.WatchRevision) {
                if ([long]$Status.observedAtMs -lt $Session.WatchTick) { throw 'Watchdog clock regressed.' }
                $Session.Watch=$identity; $Session.WatchRevision=[long]$Status.revision
                $Session.WatchTick=[long]$Status.observedAtMs; $Session.LastWatchAt=$NowMs
                $Session.State=$Status.state
                if ($Status.state.armed) {
                    if ($null -ne $Session.ArmedAt -and [long]$Status.state.armedAtMs -ne $Session.ArmedAt) { throw 'Arming clock changed.' }
                    $Session.ArmedAt=[long]$Status.state.armedAtMs
                }
                if ($Status.state.retired) {
                    if (-not $Status.state.closing -or @($Status.state.pending).Count -ne 0 -or -not $Status.state.probeStopped) { throw 'Invalid retirement status.' }
                    $Session.Finished=$true; $Session.Prompt=0; return 'Retired'
                }
                if ($Status.state.closing) { Close-DeskFuseRunner $Session 'OwnerClosing' }
            }
        } catch { Close-DeskFuseRunner $Session $_.Exception.Message }
    }
    if ($NowMs - $Session.LastWatchAt -ge [long]$Session.Policy.heartbeatTimeoutMs -or
        ($Session.WatchTick -ge 0 -and $NowMs - $Session.WatchTick -ge [long]$Session.Policy.heartbeatTimeoutMs)) {
        Close-DeskFuseRunner $Session 'WatchdogProgressLost'
    }
    if ($Session.Close) { return 'Closing' }
    if ($null -eq $Session.ArmedAt) { return 'WaitingForWatch' }
    $elapsed = $NowMs - $Session.ArmedAt
    if ($elapsed -lt 0 -or $elapsed -ge [long]$Session.Policy.closeAtMs) { Close-DeskFuseRunner $Session 'ProofHardClose'; return 'Closing' }
    if (-not $Session.State.proofReady -and $elapsed -ge [long]$Session.Policy.proofReadyDeadlineMs) {
        Close-DeskFuseRunner $Session 'ProofReadyDeadline'; return 'Closing'
    }
    if (-not $Session.ProbeRequested -and $Session.State.creatorExited -and $Session.State.permit -eq 'Relinquished') {
        $Session.ProbeRequested=$true; return 'LaunchProbe'
    }
    $accepted = @($Session.State.acknowledged).Count
    for ($i = 0; $i -lt 3; $i++) {
        if ($accepted -le $i -and $Session.Acknowledge -le $i -and $elapsed -ge [long]$Session.Policy.ackDeadlineMs[$i]) {
            Close-DeskFuseRunner $Session 'AcknowledgementDeadline'; return 'Closing'
        }
    }
    if ($Session.State.proofReady -and $accepted -lt 3 -and $Session.Acknowledge -eq $accepted -and
        $elapsed -ge [long]$Session.Policy.promptAtMs[$accepted]) {
        $Session.Prompt=$accepted + 1; return 'Prompt'
    }
    $Session.Prompt=0; return 'Observing'
}
function Submit-DeskFuseCheckpoint($Session, [int]$Index, [long]$NowMs) {
    if ($Session.Close -or $Session.Prompt -ne $Index -or $Index -lt 1 -or $Index -gt 3 -or $null -eq $Session.ArmedAt -or
        $NowMs - $Session.ArmedAt -lt [long]$Session.Policy.promptAtMs[$Index-1] -or
        $NowMs - $Session.ArmedAt -ge [long]$Session.Policy.ackDeadlineMs[$Index-1]) {
        Close-DeskFuseRunner $Session 'InvalidCheckpointRequest'; return $false
    }
    $Session.Acknowledge=$Index; $Session.Prompt=0; return $true
}
function Write-DeskFuseRunnerControl($Session) {
    $Session.Revision=[long]$Session.Revision + 1
    $record = [ordered]@{ version=1; runId=([Guid]$Session.Runtime.runId).ToString(); runnerNonce=([Guid]$Session.Runtime.runner.nonce).ToString()
        revision=$Session.Revision; probe=$Session.Probe; proofReady=[bool]$Session.ProofReady; acknowledge=[int]$Session.Acknowledge; close=[bool]$Session.Close }
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Compress -Depth 6))
    if ($bytes.Length -gt 4096) { throw 'Control record exceeds its bound.' }
    $file=Open-DeskFuseMailbox (Join-Path $Session.RunDirectory 'runner-control.json')
    try { $file.Write($bytes,0,$bytes.Length); $file.SetLength($bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
}
function Assert-DeskFusePreparedTask($Session, [bool]$Recovery) {
    $id=([Guid]$Session.Runtime.runId).ToString('N'); $ns=$Session.NamespaceId.ToString('N')
    $image=Join-Path $Session.RunDirectory 'bundle\DeskFuse.DriverTestOwner.exe'
    if ($Recovery) {
        $name="DeskFuse-FocusIdd-Recover-$id"
        $arguments='recover --namespace {0} --run {1} --nonce {2} --boot' -f $ns,$id,([Guid]$Session.Runtime.bootNonce).ToString('N')
    } else {
        $name="DeskFuse-FocusIdd-Watch-$id"
        $arguments='watch --namespace {0} --run {1} --nonce {2}' -f $ns,$id,([Guid]$Session.Runtime.watchNonce).ToString('N')
    }
    $task=Get-ScheduledTask -TaskPath '\' -TaskName $name -ErrorAction Stop
    if (@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine $image -or $task.Actions[0].Arguments -cne $arguments -or
        $task.Actions[0].WorkingDirectory -ine (Join-Path $Session.RunDirectory 'bundle') -or
        $task.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM') -or [string]$task.Principal.LogonType -notin @('ServiceAccount','5')) { throw 'Prepared task identity differs.' }
    return $name
}
function Start-DeskFusePreparedTask($Session, [bool]$Recovery) {
    $name=Assert-DeskFusePreparedTask $Session $Recovery
    Start-ScheduledTask -TaskPath '\' -TaskName $name -ErrorAction Stop
}

function Open-DeskFusePreparedSession([Guid]$NamespaceId, [Guid]$RunId, [bool]$RequireRunner = $true) {
    if ($NamespaceId -eq [Guid]::Empty -or $RunId -eq [Guid]::Empty -or -not [Environment]::Is64BitProcess) { throw 'Nonempty identities and x64 are required.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'An elevated local operator is required.' }
    } finally { $identity.Dispose() }
    $root=Get-DeskFuseMaintenanceRoot
    $bootstrap=Read-DeskFuseJson (Join-Path $root 'bootstrap.json') 4096
    if ($bootstrap.version -ne 1 -or [Guid]$bootstrap.namespaceId -ne $NamespaceId) { throw 'Namespace differs from trusted setup configuration.' }
    $directory=Join-Path $root ('runs\' + $RunId.ToString('N'))
    $manifest=Read-DeskFuseJson (Join-Path $directory 'run.json')
    if ($manifest.version -ne 2 -or [Guid]$manifest.runId -ne $RunId) { throw 'Prepared run identity differs.' }
    $runtimePath=Join-Path $directory 'bundle\runtime.json'
    $expected=$manifest.bundleSha256.PSObject.Properties['runtime.json'].Value
    if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw 'Runtime differs from the prepared manifest.' }
    $runtime=Read-DeskFuseJson $runtimePath 8192
    if ($runtime.version -ne 1 -or [Guid]$runtime.runId -ne $RunId -or $runtime.runner.role -ne 'Runner') { throw 'Invalid prepared runner profile.' }
    if ($RequireRunner) {
        $current=[Diagnostics.Process]::GetCurrentProcess()
        try {
            if ([int]$runtime.runner.pid -ne $current.Id -or [long]$runtime.runner.creationFileTime -ne $current.StartTime.ToUniversalTime().ToFileTimeUtc()) {
                throw 'This is not the runner process approved by trusted setup. Preparation cannot be transferred to another PowerShell process.'
            }
        } finally { $current.Dispose() }
    }
    $policyPath=Join-Path $directory 'policy.json'
    if ((Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.policySha256) { throw 'Prepared policy differs.' }
    return New-DeskFuseRunnerState $runtime (Read-DeskFuseJson $policyPath 4096) $NamespaceId $directory (Get-DeskFuseUptime)
}
function Test-DeskFuseRecoveryRetired($Session) {
    $path=Join-Path $Session.RunDirectory 'recovery-status.json'
    $file=Open-DeskFuseMailbox $path
    try {
        if ($file.Length -le 0 -or $file.Length -gt 16384) { throw 'Invalid recovery status size.' }
        $reader=[IO.StreamReader]::new($file,[Text.UTF8Encoding]::new($false,$true),$false)
        try { $report=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $file.Dispose() }
    if ($report.PSObject.Properties.Name -notcontains 'version') { return $false }
    if ($report.version -ne 1 -or [Guid]$report.runId -ne [Guid]$Session.Runtime.runId -or
        [Guid]$report.state.runId -ne [Guid]$Session.Runtime.runId -or $report.worker.role -ne 'Recovery' -or
        [int]$report.worker.pid -le 0 -or [long]$report.worker.creationFileTime -le 0 -or [Guid]$report.worker.nonce -eq [Guid]::Empty -or
        ($report.boot -and [Guid]$report.worker.nonce -ne [Guid]$Session.Runtime.bootNonce)) { throw 'Wrong recovery report binding.' }
    $retired=($report.state.retired -and $report.state.closing -and $report.state.probeStopped -and
        @($report.state.pending).Count -eq 0 -and -not $report.state.rebootRequired -and $null -eq $report.state.incompleteReason)
    if ($retired) { $Session.State=$report.state }
    return $retired
}
function Start-DeskFuseApprovedProbe($Session) {
    if (-not $Session.ProbeRequested -or $null -ne $Session.Probe -or $Session.Close) { throw 'Probe launch is not due.' }
    $runtime=$Session.Runtime
    $image=Join-Path $Session.RunDirectory 'bundle\probe\FocusWindow.Agent.exe'
    $gate=Join-Path $Session.RunDirectory 'probe-start.json'
    $release=Join-Path $Session.RunDirectory 'release-monitor.flag'
    foreach ($path in @($image,$gate,$release,$Session.RunDirectory)) {
        if ($path.Contains('"') -or $path.Contains("`r") -or $path.Contains("`n")) { throw 'Invalid prepared path.' }
    }
    $arguments='density-probe --width {0} --height {1} --dpr {2} --diag-mm {3} --refresh {4} --out "{5}" --hold-until-flag "{6}" --owner-start-gate "{7}" --owner-run {8} --owner-nonce {9}' -f
        [int]$runtime.width,[int]$runtime.height,([double]$runtime.dpr).ToString([Globalization.CultureInfo]::InvariantCulture),
        [int]$runtime.diagonalMm,[int]$runtime.refresh,$Session.RunDirectory,$release,$gate,([Guid]$runtime.runId).ToString('N'),([Guid]$runtime.probeNonce).ToString('N')
    $start=[Diagnostics.ProcessStartInfo]::new($image,$arguments)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=Split-Path $image -Parent
    $process=[Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Approved parked probe did not start.' }
    $Session.Probe=[ordered]@{ pid=$process.Id; creationFileTime=$process.StartTime.ToUniversalTime().ToFileTimeUtc(); nonce=([Guid]$runtime.probeNonce).ToString(); role='Probe' }
    return $process
}
function New-DeskFuseRunnerUi {
    Assert-DeskFuseInteractiveOperator
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $form=[Windows.Forms.Form]::new(); $form.Text='DeskFuse temporary display proof'
    $form.Size=[Drawing.Size]::new(600,230); $form.StartPosition='CenterScreen'; $form.TopMost=$true
    $label=[Windows.Forms.Label]::new(); $label.Location=[Drawing.Point]::new(18,18); $label.Size=[Drawing.Size]::new(550,95)
    $label.Text='Waiting for independent recovery. No checkpoint has been accepted.'
    $button=[Windows.Forms.Button]::new(); $button.Text='DESKTOP IS USABLE'; $button.Location=[Drawing.Point]::new(165,125); $button.Size=[Drawing.Size]::new(250,40); $button.Enabled=$false
    $form.Controls.Add($label); $form.Controls.Add($button)
    $timer=[Windows.Forms.Timer]::new(); $timer.Interval=250
    return @{ Form=$form; Label=$label; Button=$button; Timer=$timer }
}
function Assert-DeskFuseInteractiveOperator {
    if (-not [Environment]::UserInteractive -or [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'An interactive Windows PowerShell STA session is required.' }
}
function Test-DeskFuseUiObservationExpired($Session, $ClosingAt, [long]$NowMs) {
    if ($null -eq $ClosingAt) { return $false }
    if ($null -ne $Session.ArmedAt) { return ($NowMs - $Session.ArmedAt -ge [long]$Session.Policy.cleanupObservationDeadlineMs) }
    return ($NowMs - [long]$ClosingAt -ge ([long]$Session.Policy.cleanupObservationDeadlineMs - [long]$Session.Policy.closeAtMs))
}
function New-DeskFuseRunnerPlatform {
    return @{
        Time={ Get-DeskFuseUptime }; Ui={ New-DeskFuseRunnerUi }
        ReadStatus={ param($s) Get-DeskFuseWatchStatus $s.RunDirectory }
        WriteControl={ param($s) Write-DeskFuseRunnerControl $s }
        StartTask={ param($s,$recovery) Start-DeskFusePreparedTask $s $recovery }
        StartProbe={ param($s) Start-DeskFuseApprovedProbe $s }
        ReadRecovery={ param($s) Test-DeskFuseRecoveryRetired $s }
        MonitorHeld={ param($s)
            $path=Join-Path $s.RunDirectory 'monitor-held.flag'
            return ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 0)
        }
    }
}
function Invoke-DeskFuseGuardedRunner($Session, $Platform = $null) {
    if ($null -eq $Platform) { $Platform=New-DeskFuseRunnerPlatform }
    $ui=& $Platform.Ui
    $form=$ui.Form; $label=$ui.Label; $button=$ui.Button; $timer=$ui.Timer
    $resources=@{ Probe=$null; RecoveryRequested=$false; ClosingAt=$null; Error=$null; SubmittedAll=$false }
    $submit=${function:Submit-DeskFuseCheckpoint}; $update=${function:Update-DeskFuseRunner}
    $close=${function:Close-DeskFuseRunner}; $expired=${function:Test-DeskFuseUiObservationExpired}
    $onClick={
        if (& $submit $Session $Session.Prompt (& $Platform.Time)) {
            & $Platform.WriteControl $Session; $button.Enabled=$false
            if ($Session.Acknowledge -eq 3) { $resources.SubmittedAll=$true }
        }
    }.GetNewClosure()
    $onTick={
        try {
            $status=& $Platform.ReadStatus $Session
            $action=& $update $Session $status (& $Platform.Time)
            if ($Session.Finished) { $timer.Stop(); $form.Close(); return }
            if ($action -eq 'LaunchProbe') { $resources.Probe=& $Platform.StartProbe $Session }
            if ($resources.Probe) {
                if ($resources.Probe.HasExited -and -not $Session.Close) { & $close $Session 'ProbeExitedBeforeClosing' }
                if (-not $Session.Close -and -not $Session.ProofReady -and (& $Platform.MonitorHeld $Session)) { $Session.ProofReady=$true }
            }
            & $Platform.WriteControl $Session
            $button.Enabled=($action -eq 'Prompt' -and -not $Session.Close)
            if ($button.Enabled) { $label.Text="Safety check $($Session.Prompt) of 3. Are Windows and your physical displays still usable? Confirm before the current checkpoint expires." }
            elseif ($Session.Close) { $label.Text='Closing. The SYSTEM owner is stopping the probe and removing only this temporary device.' }
            elseif ($action -eq 'WaitingForWatch') { $label.Text='Waiting for independently verified recovery. The proof has not armed yet.' }
            else { $label.Text='Observing the temporary display. Checks occur at 60, 180 and 300 seconds. Earlier confirmations do not disable later checks.' }
            if ($Session.Close -and $null -eq $resources.ClosingAt) { $resources.ClosingAt=& $Platform.Time }
            if ($Session.Close -and -not $resources.RecoveryRequested -and
                (& $Platform.Time) - $Session.LastWatchAt -ge [long]$Session.Policy.heartbeatTimeoutMs) {
                # Independent of a missing/hung watchdog. Native admission still
                # serializes the exact run; IgnoreNew bounds this recovery task.
                & $Platform.StartTask $Session $true
                $resources.RecoveryRequested=$true
            }
            if ($Session.Finished -or ($resources.RecoveryRequested -and (& $Platform.ReadRecovery $Session))) {
                $Session.Finished=$true; $timer.Stop(); $form.Close()
            } elseif (& $expired $Session $resources.ClosingAt (& $Platform.Time)) {
                $resources.Error='Retirement not confirmed in the UI observation window. Recovery state/tasks remain retained.'
                $timer.Stop(); $form.Close()
            }
        } catch {
            & $close $Session $_.Exception.Message
            try { & $Platform.WriteControl $Session } catch { }
            if (-not $resources.RecoveryRequested) {
                try { & $Platform.StartTask $Session $true; $resources.RecoveryRequested=$true; $resources.ClosingAt=& $Platform.Time }
                catch { $resources.Error=$_.Exception.Message; $timer.Stop(); $form.Close() }
            } elseif (& $expired $Session $resources.ClosingAt (& $Platform.Time)) {
                $resources.Error='Recovery observation incomplete; retained tasks/state require inspection.'; $timer.Stop(); $form.Close()
            }
        }
    }.GetNewClosure()
    $onClosing={
        param($sender,$eventArgs)
        if (-not $Session.Finished -and $null -eq $resources.Error) { & $close $Session 'OperatorClosed'; $eventArgs.Cancel=$true }
    }.GetNewClosure()
    $button.Add_Click($onClick); $timer.Add_Tick($onTick); $form.Add_FormClosing($onClosing)
    try {
        & $Platform.WriteControl $Session
        & $Platform.StartTask $Session $false
        $timer.Start(); [void]$form.ShowDialog()
        if ($resources.Error) { throw $resources.Error }
        return ($Session.Finished -and $resources.SubmittedAll -and @($Session.State.acknowledged).Count -eq 3)
    } finally {
        $timer.Stop(); $timer.Dispose(); $form.Dispose()
        if (-not $Session.Finished) {
            & $close $Session 'RunnerLeaving'
            try { & $Platform.WriteControl $Session; & $Platform.StartTask $Session $true } catch { Write-Warning $_.Exception.Message }
        }
        if ($resources.Probe) { $resources.Probe.Dispose() } # only watchdog may terminate its retained exact probe
    }
}

Export-ModuleMember -Function *-DeskFuse*
