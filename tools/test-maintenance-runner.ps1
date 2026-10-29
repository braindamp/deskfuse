# Invoked by the .NET test fixture with statuses emitted by the real coordinator.
# No SYSTEM, Scheduler, native process launch, driver or WinForms operation is permitted.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$FixturePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'maintenance-runner.psm1') -Force -DisableNameChecking
function global:Start-ScheduledTask { throw 'UNMOCKED MACHINE OPERATION' }
function global:Register-ScheduledTask { throw 'UNMOCKED MACHINE OPERATION' }
function global:Unregister-ScheduledTask { throw 'UNMOCKED MACHINE OPERATION' }
function global:Get-PnpDevice { throw 'UNMOCKED DEVICE OPERATION' }
function global:Start-Process { throw 'UNMOCKED PROCESS LAUNCH' }
$fixture=Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$h=[pscustomobject]@{ Now=[long]$fixture.startAt; Frame=$null; Tasks=[Collections.Generic.List[bool]]::new()
    Writes=[Collections.Generic.List[object]]::new(); ProbeStarts=0; Ui=$null; Session=$null; Failure=$null }
$h.Session=New-DeskFuseRunnerState $fixture.runtime $fixture.policy ([Guid]$fixture.namespaceId) (Split-Path $FixturePath -Parent) $h.Now
$timer=[pscustomobject]@{ Handler=$null; Running=$false }
$timer | Add-Member ScriptMethod Add_Tick { param($handler) $this.Handler=$handler }
$timer | Add-Member ScriptMethod Start { $this.Running=$true }
$timer | Add-Member ScriptMethod Stop { $this.Running=$false }
$timer | Add-Member ScriptMethod Dispose { }
$button=[pscustomobject]@{ Handler=$null; Enabled=$false }
$button | Add-Member ScriptMethod Add_Click { param($handler) $this.Handler=$handler }
$label=[pscustomobject]@{ Text='' }
$form=[pscustomobject]@{ Closed=$false; Closing=$null; Harness=$h; Frames=@($fixture.frames) }
$form | Add-Member ScriptMethod Add_FormClosing { param($handler) $this.Closing=$handler }
$form | Add-Member ScriptMethod Close {
    $eventArgs=[pscustomobject]@{ Cancel=$false }
    & $this.Closing $this $eventArgs
    if (-not $eventArgs.Cancel) { $this.Closed=$true }
}
$form | Add-Member ScriptMethod Dispose { }
$form | Add-Member ScriptMethod ShowDialog {
    foreach ($frame in $this.Frames) {
        $this.Harness.Frame=$frame; $this.Harness.Now=[long]$frame.now
        & $this.Harness.Ui.Timer.Handler $this $null
        if ($this.Closed) { return }
        if ($frame.click -and $this.Harness.Ui.Button.Enabled) { & $this.Harness.Ui.Button.Handler $this $null }
        if ($frame.cancel) { $this.Close() }
    }
    if (-not $this.Closed) { throw 'Mock UI did not reach its expected terminal observation.' }
}
$h.Ui=@{ Form=$form; Label=$label; Button=$button; Timer=$timer }
$platform=@{
    Time={ $h.Now }.GetNewClosure()
    Ui={ $h.Ui }.GetNewClosure()
    ReadStatus={ param($s) if ($h.Frame.failRead) { throw 'Injected status failure' }; $h.Frame.status }.GetNewClosure()
    WriteControl={ param($s) $h.Writes.Add([pscustomobject]@{ close=$s.Close; acknowledge=$s.Acknowledge; proofReady=$s.ProofReady }) }.GetNewClosure()
    StartTask={ param($s,$recovery) $h.Tasks.Add([bool]$recovery) }.GetNewClosure()
    StartProbe={ param($s)
        $h.ProbeStarts++; $s.Probe=[ordered]@{pid=12345;creationFileTime=123456;nonce=$s.Runtime.probeNonce;role='Probe'}
        $p=[pscustomobject]@{ HasExited=$false }; $p | Add-Member ScriptMethod Dispose { }; return $p
    }.GetNewClosure()
    ReadRecovery={ param($s) return $false }
    MonitorHeld={ param($s) return $true }
}
$passed=$false
try { $passed=Invoke-DeskFuseGuardedRunner $h.Session $platform }
catch { $h.Failure=$_.Exception.Message }
[ordered]@{ passed=[bool]$passed; failure=$h.Failure; finished=$h.Session.Finished; close=$h.Session.Close
    tasks=@($h.Tasks.ToArray()); probeStarts=$h.ProbeStarts; writes=@($h.Writes.ToArray()) } | ConvertTo-Json -Compress -Depth 6
