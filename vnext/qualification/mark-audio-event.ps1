# Explicit operator observation during the existing physical audio test.
# Does not itself restore Brave, change system volume or drive media.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('brave_restored','volume_changed','pop_heard',
                 'dropout_heard','route_changed','screen_off','screen_on')]
    [string]$Event,
    [string]$EvidenceRoot = 'D:\DeskFuseLocalData\cp001',
    [string]$SessionFolder,
    [string]$Note = ''
)
$ErrorActionPreference = 'Stop'
if ($Note.Contains([char]10) -or $Note.Contains([char]13)) {
    throw 'Event notes must be one line'
}
if ($Note.Length -gt 160) { throw 'Event note exceeds 160 characters' }
if ([string]::IsNullOrWhiteSpace($SessionFolder)) {
    $folders = @(Get-ChildItem -LiteralPath $EvidenceRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'session.txt') } |
        Sort-Object -Property CreationTimeUtc -Descending)
    if ($folders.Count -eq 0) { throw 'No local physical audio session found' }
    $SessionFolder = $folders[0].FullName
}
$session = Join-Path $SessionFolder 'session.txt'
if (-not (Test-Path -LiteralPath $session -PathType Leaf)) {
    throw 'An existing physical audio session is required'
}
$eventFile = Join-Path $SessionFolder 'operator-events.csv'
$utc = [DateTime]::UtcNow.ToString('o')
$escaped = $Note.Replace('"', '""')
if (-not (Test-Path -LiteralPath $eventFile)) {
    'utc,event,note' | Set-Content -LiteralPath $eventFile -Encoding UTF8
}
('"{0}","{1}","{2}"' -f $utc, $Event, $escaped) |
    Add-Content -LiteralPath $eventFile -Encoding UTF8
Write-Host ('Recorded observed ' + $Event + ' at ' + $utc)
Write-Host ('Session: ' + $SessionFolder)
