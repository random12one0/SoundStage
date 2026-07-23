# Builds the Soundstage APO (SoundstageApo.dll).
#
#   pwsh native/soundstage-apo/build.ps1
#
# Produces a 64-bit DLL beside this script. Installing it is a separate, elevated step - see
# install-apo.ps1 - because attaching a plugin to a playback device is a machine-wide change.

param([string]$Config = "Release")

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$engineInclude = Join-Path $here "..\soundstage-dsp\include"

$vcvars = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    $vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
}
if (-not (Test-Path $vcvars)) { throw "Could not find vcvars64.bat - install the C++ build tools." }

$opt = if ($Config -eq "Debug") { "/Od /Zi" } else { "/O2 /DNDEBUG" }

# /EHsc for the standard library, /GS for stack checks - this runs inside the audio service, so the
# usual hardening stays on even in release.
$cl = @(
    "/nologo /std:c++17 /W3 /EHsc /GS $opt /MD /LD",
    # NOMINMAX matters: windows.h defines min/max as macros, which mangles every std::min and
    # std::max in the DSP headers into a syntax error.
    "/DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /DNOMINMAX",
    "/I `"$engineInclude`"",
    "SoundstageApo.cpp DllMain.cpp",
    "/link /DEF:Soundstage.def /OUT:SoundstageApo.dll",
    "ole32.lib oleaut32.lib advapi32.lib user32.lib"
) -join " "

Push-Location $here
try {
    cmd /c "call `"$vcvars`" >nul && cl $cl"
    if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

    $dll = Join-Path $here "SoundstageApo.dll"
    if (-not (Test-Path $dll)) { throw "no DLL produced" }

    Write-Host ""
    Write-Host "Built: $dll  ($([Math]::Round((Get-Item $dll).Length / 1KB, 1)) KB)"
    # dumpbin only exists inside the build environment, so ask for it there. Purely informational -
    # a missing dumpbin must not fail a successful build.
    Write-Host "Exports:"
    $exports = cmd /c "call `"$vcvars`" >nul && dumpbin /exports `"$dll`"" 2>$null
    $found = $exports | Select-String -Pattern "\bDll\w+" | ForEach-Object { "  $($_.Line.Trim())" }
    if ($found) { $found } else { Write-Host "  (dumpbin unavailable - skipped)" }
}
finally {
    Pop-Location
}
