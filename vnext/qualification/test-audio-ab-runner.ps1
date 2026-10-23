# Pure offline test of selected A/B runner functions. No devices, builds,
# firewall, driver, source checkout changes, or real playback involved.
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot 'audio-ab.ps1'
$tokens=$null
$errorsFound=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $script).Path,[ref]$tokens,[ref]$errorsFound)
if ($errorsFound.Count -gt 0) { throw 'A/B runner has PowerShell syntax errors' }
$defs=@($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
},$true))
foreach ($name in @('Src','Checkout','RunSuite')) {
    $match=@($defs | Where-Object Name -EQ $name)
    if ($match.Count -ne 1) { throw "Missing or duplicate runner function: $name" }
    # Execute only the requested function declaration, not the top-level
    # script that connects to physical hardware.
    Invoke-Expression $match[0].Extent.Text
}
$referenceSha='913a2f6620d906d4539dd3f321cdbf06ece9d112'
$LabRoot='FAKE-LAB'
$global:LASTEXITCODE=0

function git {
    $arguments=@($args)
    $global:LASTEXITCODE=0
    if ($arguments -contains 'cat-file') { return }
    if ($arguments -contains 'worktree') {
        $to=$arguments[$arguments.Count-2]
        New-Item -ItemType Directory -Force -Path $to | Out-Null
        # Simulate the exact Git banner that broke CMake path handling.
        'HEAD is now at 913a2f6 Reference'
        return
    }
    if ($arguments -contains 'rev-parse') { $referenceSha; return }
    throw "Unexpected fake Git command: $($arguments -join ' ')"
}

$temp=Join-Path ([System.IO.Path]::GetTempPath()) ('deskfuse-ab-test-' + [guid]::NewGuid().ToString('N'))
try {
    $ABRoot=$temp
    $actual=@(Checkout 'reference' $referenceSha)
    if ($actual.Count -ne 1 -or $actual[0] -cne (Src 'reference')) {
        throw "Checkout leaked stdout instead of one worktree path: $($actual -join '|')"
    }
    Write-Host 'PASS: Git stdout never contaminates CMake worktree path'
} finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
$script:played=@()
$script:modeCapable=$true
function CheckPair([string]$slot) {
    [pscustomobject]@{
        slot=$slot
        mode=if ($script:modeCapable -and $slot -eq 'candidate') {
            'client-playback-modes'
        } else { 'client-playback' }
    }
}
function Run([string]$slot,[string]$flags) {
    if ($slot -notin @('reference','candidate')) { throw "Wrong test slot: $slot" }
    if ($flags -notmatch '^f[01]c[01]r[01]$') { throw "Wrong test mode: $flags" }
    $script:played+=($slot + '/' + $flags)
}
$matrix=@(
    @{kind='Quick';length=4;first='reference/f1c1r1';last='reference/f1c1r1'},
    @{kind='Repeat';length=4;first='reference/f1c1r1';last='reference/f1c1r1'},
    @{kind='Matrix';length=10;first='reference/f1c1r1';last='reference/f1c1r1'},
    @{kind='Full';length=12;first='reference/f1c1r1';last='candidate/f1c1r1'}
)
foreach ($test in $matrix) {
    $script:played=@()
    RunSuite $test.kind
    if ($script:played.Count -ne $test.length -or
        $script:played[0] -ne $test.first -or
        $script:played[-1] -ne $test.last) {
        throw "Wrong $($test.kind) suite plan: $($script:played -join ',')"
    }
    if ($test.kind -eq 'Matrix' -and
        (@($script:played | Where-Object { $_ -like 'candidate/*' } |
            Sort-Object -Unique)).Count -ne 8) {
        throw 'Matrix must cover all eight distinct candidate processing modes'
    }
    Write-Host "PASS: $($test.kind) audible suite calls exact version/flags $($test.length) times"
}
$script:modeCapable=$false
$script:played=@()
RunSuite 'Quick'
$unexpected=@($script:played | Where-Object { $_ -ne 'reference/f1c1r1' -and $_ -ne 'candidate/f1c1r1' })
if ($unexpected.Count -ne 0) {
    throw 'Old candidate suite incorrectly requested unsupported processing flags'
}
Write-Host 'PASS: older candidate reverts to native audible-only comparison, never inventing supported flags'

foreach ($kind in @('Matrix','Full')) {
    $failed=$false
    try { RunSuite $kind } catch { $failed=$true }
    if (-not $failed) { throw "Old candidate must reject unsupported $kind suite" }
}
Write-Host 'PASS: unsupported matrix/full suites fail closed'
