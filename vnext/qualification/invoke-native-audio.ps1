# Preserve native stdout and stderr as diagnostic text while keeping the
# true process exit code. Windows PowerShell 5.1 otherwise promotes native
# stderr (including benign eprintln! traces) to NativeCommandError if the
# caller sets $ErrorActionPreference = 'Stop'.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string[]]$Arguments,
    [Parameter(Mandatory=$true)][string]$LogFile
)
$previous = $ErrorActionPreference
$nativeExit = $null
try {
    # Keep this scope local to the native invocation. The caller retains Stop
    # for all PowerShell operations and checks a nonzero native exit itself.
    $ErrorActionPreference = 'Continue'
    & $Executable @Arguments 2>&1 |
        ForEach-Object { $_.ToString() } |
        Tee-Object -FilePath $LogFile |
        Out-Host
    $nativeExit = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previous
}
if ($null -eq $nativeExit) {
    throw 'Native audio process did not expose an exit code'
}
Write-Output ([int]$nativeExit)
