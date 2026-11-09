# F0b / F1 / T03–T04: invoke a separately approved, signed authority installation.
# This never elevates itself or infers approval from a candidate manifest.
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Fresh', 'Upgrade')][string]$Mode = 'Fresh',
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$ReviewManifest,
    [Parameter(Mandatory = $true)][string]$IndependentlyApprovedSha256,
    [Parameter(Mandatory = $true)][guid]$DeploymentId,
    [guid]$MaintenanceNamespace = [guid]::Empty,
    [guid]$AuthorityNamespace = [guid]::Empty,
    [guid]$PreviousDeploymentId = [guid]::Empty,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
if ($DeploymentId -eq [guid]::Empty -or
    $IndependentlyApprovedSha256 -cnotmatch '^[a-f0-9]{64}$') {
    throw 'Exact deployment ID and externally approved lowercase SHA-256 required.'
}
if ($Mode -eq 'Upgrade' -and ($MaintenanceNamespace -eq [guid]::Empty -or
    $AuthorityNamespace -eq [guid]::Empty -or $PreviousDeploymentId -eq [guid]::Empty -or
    $PreviousDeploymentId -eq $DeploymentId)) {
    throw 'Upgrade requires exact previous and new deployment identities.'
}
$source = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$review = (Resolve-Path -LiteralPath $ReviewManifest).ProviderPath
$broker = Join-Path $source 'DeskFuse.AudioBroker.exe'
if (-not (Test-Path -LiteralPath $broker -PathType Leaf)) {
    throw 'The staged candidate is missing the fixed protected authority executable.'
}
$observed = (Get-FileHash -LiteralPath $review -Algorithm SHA256).Hash.ToLowerInvariant()
if ($observed -cne $IndependentlyApprovedSha256) {
    throw 'Reviewed bytes do not match the independently approved manifest SHA-256.'
}
# A valid Windows signature is necessary but does not confer approval to a
# generated candidate digest; release review must independently authorize it.
$signature = Get-AuthenticodeSignature -LiteralPath $broker
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw 'The exact authority executable lacks a Windows-trusted Authenticode signature.'
}
$setup = Join-Path $source 'DeskFuse.Setup.exe'
if (-not (Test-Path -LiteralPath $setup -PathType Leaf) -or
    (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') {
    throw 'The exact installed setup launcher is not Windows signature-qualified.'
}
$argsForAuthority = [System.Collections.Generic.List[string]]::new()
if ($Mode -eq 'Fresh') {
    $argsForAuthority.Add('--install-authority-fresh')
    $argsForAuthority.Add('--deployment=' + $DeploymentId.ToString('N'))
} else {
    $argsForAuthority.Add('--install-authority-upgrade')
    $argsForAuthority.Add('--maintenance=' + $MaintenanceNamespace.ToString('N'))
    $argsForAuthority.Add('--authority=' + $AuthorityNamespace.ToString('N'))
    $argsForAuthority.Add('--previous=' + $PreviousDeploymentId.ToString('N'))
    $argsForAuthority.Add('--deployment=' + $DeploymentId.ToString('N'))
}
$argsForAuthority.Add('--source=' + $source)
$argsForAuthority.Add('--review=' + $review)
$argsForAuthority.Add('--approved-sha256=' + $IndependentlyApprovedSha256)
Write-Host "F0b / F1 / T03–T04 — $Mode candidate: $($DeploymentId.ToString('N'))"
Write-Host "Authority source: $broker"
Write-Host "Signer: $($signature.SignerCertificate.Subject)"
Write-Host "Review SHA-256: $observed (externally approved input, not generated approval)"
if (-not $Apply) {
    Write-Host 'PLAN ONLY. No service, root, key, device or OS setting was changed.'
    Write-Host 'Apply requires an identified rollback, separately approved digest, signed package and actual LocalSystem invocation.'
    return
}
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ($identity.User.Value -cne 'S-1-5-18') {
        throw 'Application requires the trusted installer to run as actual LocalSystem; this script does not self-elevate.'
    }
} finally { $identity.Dispose() }
if (-not $PSCmdlet.ShouldProcess($DeploymentId.ToString('N'),
    "Prepare protected authority deployment, publish disabled service and activate exact installed role")) {
    return
}
$commandArguments = $argsForAuthority.ToArray()
& $broker @commandArguments
if ($LASTEXITCODE -ne 0) {
    throw "Protected authority installation returned $LASTEXITCODE. Retain the exact source, review and system state for reconciliation."
}
Write-Host 'Exact protected authority install/activation completed. Perform installed-state verification before claiming playback.'
