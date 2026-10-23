# Executable regression for native stderr under both pwsh and Windows PS 5.1.
# No ADB, network, Android device, or real credentials required.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'invoke-native-audio.ps1'
$exe = (Get-Command powershell.exe).Source
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('deskfuse-stderr-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $goodLog = Join-Path $tmp 'good.txt'
    $goodScript = Join-Path $tmp 'diagnostics.ps1'
    @'
[Console]::Out.WriteLine("AUTH Android: verified")
[Console]::Error.WriteLine("TRACE Android SOURCE_GAP frames=240")
exit 0
'@ | Set-Content -LiteralPath $goodScript -Encoding ASCII
    $code = & $script -Executable $exe -Arguments @('-NoProfile','-NonInteractive','-File',$goodScript) -LogFile $goodLog
    if ($code -ne 0) { throw ('Benign stderr caused failure: ' + $code) }
    $log = Get-Content -LiteralPath $goodLog -Raw
    if ($log -notmatch 'AUTH Android: verified' -or
        $log -notmatch 'TRACE Android SOURCE_GAP frames=240') {
        throw 'Combined Android stdout/stderr evidence was lost'
    }

    $badLog = Join-Path $tmp 'bad.txt'
    $badScript = Join-Path $tmp 'failure.ps1'
    @'
[Console]::Error.WriteLine("actual receiver failure")
exit 23
'@ | Set-Content -LiteralPath $badScript -Encoding ASCII
    $code = & $script -Executable $exe -Arguments @('-NoProfile','-NonInteractive','-File',$badScript) -LogFile $badLog
    if ($code -ne 23) { throw ('Receiver nonzero exit was masked: ' + $code) }
    if ((Get-Content -LiteralPath $badLog -Raw) -notmatch 'actual receiver failure') {
        throw 'Receiver failure diagnostics were not retained'
    }
    Write-Host 'PASS: native stderr is diagnostic text, nonzero receiver status is retained'
} finally {
    Remove-Item -LiteralPath $tmp -Force -Recurse -ErrorAction SilentlyContinue
}
