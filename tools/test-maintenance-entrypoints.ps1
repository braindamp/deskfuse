[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('watch','recover','rollback','safe','legacy','risk','describe')][string]$Case)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$namespace=[Guid]::NewGuid(); $run=[Guid]::NewGuid()
$testState=[pscustomobject]@{ Tasks=[Collections.Generic.List[bool]]::new(); InvokedRunner=$false; Failed=$false; Opens=0 }
$session=[pscustomobject]@{ Runtime=[pscustomobject]@{runId=$run}; RunDirectory='mock-private-run' }
function Import-Module { } # all machine-facing module operations below are explicit mocks
function Open-DeskFusePreparedSession { param($ns,$id,$runner) $testState.Opens++; if ($ns -ne $namespace -or $id -ne $run) { throw 'Wrong identity' }; return $session }
function Assert-DeskFuseInteractiveOperator { }
function Start-DeskFusePreparedTask { param($s,$recovery) if ($s -ne $session) { throw 'Wrong session' }; $testState.Tasks.Add([bool]$recovery) }
function Test-DeskFuseRecoveryRetired { param($s) return $true }
function Invoke-DeskFuseGuardedRunner { param($s) $testState.InvokedRunner=$true; return $true }
function Read-Host { param($prompt) return ('START PREPARED FOCUSIDD RUN ' + $run.ToString('N')) }
function Start-ScheduledTask { throw 'UNMOCKED SCHEDULER OPERATION' }
function Get-PnpDevice { throw 'UNMOCKED DEVICE OPERATION' }
function Register-ScheduledTask { throw 'UNMOCKED TASK REGISTRATION' }
function Unregister-ScheduledTask { throw 'UNMOCKED TASK REMOVAL' }
function Start-Process { throw 'UNMOCKED PROCESS LAUNCH' }
try {
    switch ($Case) {
        'watch' { & (Join-Path $PSScriptRoot 'safety-watchdog.ps1') -NamespaceId $namespace -RunId $run }
        'recover' { & (Join-Path $PSScriptRoot 'safety-boot-recovery.ps1') -NamespaceId $namespace -RunId $run }
        'rollback' { & (Join-Path $PSScriptRoot 'rollback-focusidd.ps1') -NamespaceId $namespace -RunId $run -Quiet }
        'safe' { & (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -NamespaceId $namespace -RunId $run -IUnderstandDriverRisk }
        'legacy' { & (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -InfPath 'unapproved.inf' -IUnderstandDriverRisk }
        'risk' { & (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -NamespaceId $namespace -RunId $run }
        'describe' {
            $descriptor=& (Join-Path $PSScriptRoot 'safe-driver-proof.ps1') -DescribeRunner | ConvertFrom-Json
            if ($descriptor.authorizesInstallation -or $descriptor.pid -ne $PID) { throw 'Descriptor became authority or lost process binding.' }
        }
    }
} catch { $testState.Failed=$true }
if (($Case -in @('legacy','risk')) -ne $testState.Failed) { throw 'Unexpected wrapper outcome.' }
if ($Case -in @('legacy','risk','describe') -and ($testState.Opens -ne 0 -or $testState.Tasks.Count -ne 0)) { throw 'Invalid request reached runtime.' }
if ($Case -eq 'safe' -and -not $testState.InvokedRunner) { throw 'Runner was bypassed.' }
if ($Case -eq 'watch' -and ($testState.Tasks.Count -ne 1 -or $testState.Tasks[0])) { throw 'Wrong watch task request.' }
if ($Case -eq 'recover' -and ($testState.Tasks.Count -ne 1 -or -not $testState.Tasks[0])) { throw 'Wrong recovery task request.' }
if ($Case -eq 'rollback' -and $testState.Tasks.Count -ne 0) { throw 'Retired run requested more work.' }
Write-Output "PASS: prepared entrypoint $Case (machine operations mocked)"
