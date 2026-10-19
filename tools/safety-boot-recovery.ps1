# Recovery task created BEFORE driver installation. If Windows reboots mid-test,
# remove the temporary FocusIdd device at the next successful startup.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SafetyDirectory)
$ErrorActionPreference='Continue'
if (Test-Path -LiteralPath (Join-Path $SafetyDirectory 'done.flag')) { exit 0 }
$log = Join-Path $SafetyDirectory 'boot-recovery.log'
Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Startup recovery entered" -ErrorAction SilentlyContinue
for ($i=0; $i -lt 30; $i++) {
    if (Test-Path -LiteralPath (Join-Path $SafetyDirectory 'done.flag')) { exit 0 }
    try {
        & (Join-Path $SafetyDirectory 'rollback-focusidd.ps1') -Quiet 2>&1 | Out-Null
        Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Temporary device absent (continuing boot sweep)" -ErrorAction SilentlyContinue
    } catch {
        Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Retry: $($_.Exception.Message)" -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 4
}
exit 1
