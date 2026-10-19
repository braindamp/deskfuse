# Remove ONLY devnodes whose instance ID or hardware ID identifies DeskFuse FocusIdd.
# DevCon creates ROOT\DISPLAY\xxxx for the Root\FocusIdd hardware ID on Windows 10;
# testing only ROOT\FOCUSIDD\xxxx is NOT sufficient.
[CmdletBinding()]
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FocusDeviceIds {
    # Enumerate present AND non-present devices: failed installs can leave phantom devnodes.
    $devices = @(Get-PnpDevice -ErrorAction Stop)
    $found = @()
    foreach ($device in $devices) {
        $instanceId = [string]$device.InstanceId
        if ($instanceId -match '(?i)^ROOT\\FOCUSIDD\\[^\\]+$') {
            $found += $instanceId
            continue
        }
        if ($instanceId -notmatch '(?i)^ROOT\\DISPLAY\\[^\\]+$') { continue }
        # A ROOT\DISPLAY device is NOT necessarily ours. Check its hardware IDs first.
        try {
            $hardwareProperty = Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop
            $hardwareIds = @($hardwareProperty.Data)
        } catch {
            throw "Cannot inspect hardware IDs for $instanceId; refusing to declare the system clean. $($_.Exception.Message)"
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

$ids = @(Get-FocusDeviceIds)
foreach ($instanceId in $ids) {
    # The identifier has already been checked against device hardware IDs above.
    if (-not $Quiet) { Write-Host "Disabling DeskFuse device only: $instanceId" }
    try { & "$env:SystemRoot\System32\pnputil.exe" /disable-device $instanceId 2>&1 | Out-Null }
    catch { if (-not $Quiet) { Write-Warning "Disable failed (continuing removal): $_" } }
    if (-not $Quiet) { Write-Host "Removing DeskFuse device only: $instanceId" }
    & "$env:SystemRoot\System32\pnputil.exe" /remove-device $instanceId 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "PnP removal failed for $instanceId (exit $LASTEXITCODE)." }
}
$left = @(Get-FocusDeviceIds)
if ($left.Count -gt 0) {
    throw "DeskFuse devnodes remain ($($left -join ', ')). Review in Device Manager as Administrator."
}
if (-not $Quiet) { Write-Host 'Verified: no DeskFuse devnodes remain (including ROOT\DISPLAY instances with Root\FocusIdd hardware ID). Other adapters untouched.' }
