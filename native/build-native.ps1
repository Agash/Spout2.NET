#!/usr/bin/env pwsh
# Build the self-contained native shim (spout_shim.dll): compiles the Spout2 SpoutDX sources
# (vendored submodule) together with the shim into one DLL, so the result has no external Spout
# dependency. Windows x64. Requires MSVC (Visual Studio or Build Tools with the C++ workload).
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$vendor = Join-Path $scriptDir 'vendor/Spout2/SPOUTSDK'
$gl = Join-Path $vendor 'SpoutGL'
$dx = Join-Path $vendor 'SpoutDirectX/SpoutDX'
$inc = Join-Path $scriptDir 'include'
$build = Join-Path $scriptDir 'build'

if (-not (Test-Path (Join-Path $dx 'SpoutDX.h'))) {
    throw "Spout2 submodule missing at $vendor. Run: git submodule update --init --recursive"
}
New-Item -ItemType Directory -Force -Path $build | Out-Null

# Enter the MSVC x64 developer environment so cl/link and the Windows SDK are on PATH.
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vsPath = & $vswhere -latest -prerelease -products * -property installationPath
if (-not $vsPath) { throw 'No Visual Studio / MSVC installation found.' }
Import-Module (Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

# Spout's own BSD-licensed sources only (SpoutDX + utilities); the OpenGL path is excluded.
$sources = @(
    (Join-Path $scriptDir 'src/spout_shim.cpp'),
    (Join-Path $dx 'SpoutDX.cpp'),
    (Join-Path $gl 'SpoutCopy.cpp'),
    (Join-Path $gl 'SpoutDirectX.cpp'),
    (Join-Path $gl 'SpoutFrameCount.cpp'),
    (Join-Path $gl 'SpoutSenderNames.cpp'),
    (Join-Path $gl 'SpoutSharedMemory.cpp'),
    (Join-Path $gl 'SpoutUtils.cpp')
)

# /MT statically links the CRT so the shipped DLL needs no Visual C++ runtime redistributable.
$clArgs = @(
    '/nologo', '/LD', '/O2', '/EHsc', '/std:c++17', '/MT', '/DNDEBUG', '/DSPOUT_BUILD_STATIC',
    "/I$gl", "/I$dx", "/I$inc",
    "/Fo$build/", "/Fe$build/spout_shim.dll"
) + $sources + @(
    '/link', 'd3d11.lib', 'dxgi.lib', 'user32.lib', 'gdi32.lib', 'shell32.lib', 'advapi32.lib',
    'comdlg32.lib', 'ole32.lib'
)

& cl @clArgs
if ($LASTEXITCODE -ne 0) { throw 'BUILD FAILED' }
Write-Host "built $build/spout_shim.dll"
