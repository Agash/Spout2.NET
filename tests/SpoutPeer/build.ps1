#!/usr/bin/env pwsh
# Builds spout_peer.exe, the upstream Spout SDK as an independent peer for the interop tests, from the
# SDK sources in external/Spout2. Test-only: nothing here ships. Requires MSVC (the C++ workload).
[CmdletBinding()]
param([ValidateSet('x64', 'arm64')] [string]$Arch = 'x64')

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$sdk = Join-Path $root 'external/Spout2/SPOUTSDK'
$gl = Join-Path $sdk 'SpoutGL'
$dx = Join-Path $sdk 'SpoutDirectX/SpoutDX'
$out = Join-Path $PSScriptRoot "bin/$Arch"
if (-not (Test-Path (Join-Path $dx 'SpoutDX.h'))) {
    throw "The Spout SDK is missing at $sdk. Run: git submodule update --init"
}

New-Item -ItemType Directory -Force $out | Out-Null
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -latest -prerelease -products * -property installationPath
if (-not $vs) { throw 'No Visual Studio installation with the C++ tools was found.' }
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$Arch -host_arch=$hostArch" | Out-Null

# On Arm64 the SDK maps its SSE copy routines onto NEON through sse2neon.h, which its CMake build
# downloads from master. The peer takes a pinned release, checked by hash.
$includes = @("/I$gl", "/I$dx")
if ($Arch -eq 'arm64') {
    $neon = Join-Path $out 'sse2neon'
    $header = Join-Path $neon 'sse2neon.h'
    $expected = '78632498a57bf7e080e84cb8d74c47ce93b204cece77eab79de2908eb8bc92ed'
    if (-not (Test-Path $header) -or (Get-FileHash $header -Algorithm SHA256).Hash -ne $expected) {
        New-Item -ItemType Directory -Force $neon | Out-Null
        Invoke-WebRequest 'https://raw.githubusercontent.com/DLTcollab/sse2neon/v1.9.1/sse2neon.h' -OutFile $header
        $actual = (Get-FileHash $header -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "sse2neon.h hash mismatch: $actual" }
    }

    $includes += "/I$neon"
}

$sources = @(
    (Join-Path $PSScriptRoot 'spout_peer.cpp'),
    (Join-Path $dx 'SpoutDX.cpp'),
    (Join-Path $gl 'SpoutCopy.cpp'),
    (Join-Path $gl 'SpoutDirectX.cpp'),
    (Join-Path $gl 'SpoutFrameCount.cpp'),
    (Join-Path $gl 'SpoutSenderNames.cpp'),
    (Join-Path $gl 'SpoutSharedMemory.cpp'),
    (Join-Path $gl 'SpoutUtils.cpp')
)
& cl /nologo /O2 /EHsc /std:c++17 /Zc:preprocessor /MT /DNDEBUG /DSPOUT_BUILD_STATIC @includes "/Fo$out/" "/Fe$out/spout_peer.exe" @sources `
    /link d3d11.lib dxgi.lib user32.lib gdi32.lib shell32.lib advapi32.lib comdlg32.lib ole32.lib winmm.lib
if ($LASTEXITCODE -ne 0) { throw 'spout_peer build failed' }
Write-Host "built $out/spout_peer.exe"
