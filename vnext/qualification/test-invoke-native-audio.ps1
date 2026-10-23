# Executable regression for native stderr under both pwsh and Windows PS 5.1.
# No ADB, network, Android device, or real credentials required.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'invoke-native-audio.ps1'
$exe = (Get-Command powershell.exe).Source
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('deskfuse-stderr-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $goodLog = Join-Path $tmp 'good.txt'
    $cmd = '[Console]::Out.WriteLine("AUTH Android: verified"); [Console]::Error.WriteLine("TRACE Android SOURCE_GAP frames=240"); exit 0'
    $code = & $script -Executable $exe -Arguments @('-NoProfile','-NonInteractive','-Command',$cmd) -LogFile $goodLog
    if ($code -ne 0) { throw ('Benign stderr caused failure: ' + $code) }
    $log = Get-Content -LiteralPath $goodLog -Raw
    if ($log -notmatch 'AUTH Android: verified' -or
        $log -notmatch 'TRACE Android SOURCE_GAP frames=240') {
        throw 'Combined Android stdout/stderr evidence was lost'
    }

    $badLog = Join-Path $tmp 'bad.txt'
    $bad = '[Console]::Error.WriteLine("actual receiver failure"); exit 23'
    $code = & $script -Executable $exe -Arguments @('-NoProfile','-NonInteractive','-Command',$bad) -LogFile $badLog
    if ($code -ne 23) { throw ('Receiver nonzero exit was masked: ' + $code) }
    if ((Get-Content -LiteralPath $badLog -Raw) -notmatch 'actual receiver failure') {
        throw 'Receiver failure diagnostics were not retained'
    }
    Write-Host 'PASS: native stderr is diagnostic text, nonzero receiver status is retained'
} finally {
    Remove-Item -LiteralPath $tmp -Force -Recurse -ErrorAction SilentlyContinue
}
