# Start the separate, localhost-only laboratory. Does not install, build, or
# modify the DeskFuse Git checkout or system devices.
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not (Test-Path -LiteralPath 'config.json' -PathType Leaf)) {
    Copy-Item -LiteralPath 'config.example.json' -Destination 'config.json'
    Write-Host 'Created config.json. Check its binary and repo paths before starting physical tests.'
}
$py = Get-Command py -ErrorAction SilentlyContinue
if ($null -ne $py) {
    & $py.Source -3 (Join-Path $PSScriptRoot 'bridge.py')
} else {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) { throw 'Python 3 not found. Install Python 3 or run with py -3.' }
    & $python.Source (Join-Path $PSScriptRoot 'bridge.py')
}
