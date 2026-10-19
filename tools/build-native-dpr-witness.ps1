# Compile the independent Win32 DPI ruler using the installed Visual Studio C++ toolchain.
# Does not touch driver, display settings, registry, or production binaries.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
}
if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'Visual Studio Installer vswhere.exe is missing.'
}
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) {
    throw 'Visual Studio C++ x64 compiler tools are not installed.'
}
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
if (!(Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "Missing vcvars64.bat: $vcvars" }
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $PSScriptRoot 'df-density-readonly.exe'
$cmd = Join-Path ([System.IO.Path]::GetTempPath()) ('deskfuse-build-native-' + [guid]::NewGuid().ToString('N') + '.cmd')
try {
    @(
        '@echo off'
        'call "' + $vcvars + '" >nul'
        'if errorlevel 1 exit /b 1'
        'cd /d "' + $repo + '"'
        'cl /nologo /W4 /WX /EHsc /std:c++17 /DUNICODE /D_UNICODE "tools\df-density-readonly.cpp" user32.lib shcore.lib /Fe:"tools\df-density-readonly.exe"'
        'exit /b %errorlevel%'
    ) | Set-Content -LiteralPath $cmd -Encoding Ascii
    & cmd.exe /d /c $cmd
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Native DPI ruler failed to compile (exit $LASTEXITCODE)."
    }
    Write-Host "Native Win32 DPI ruler ready: $exe" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $cmd -ErrorAction SilentlyContinue
}
