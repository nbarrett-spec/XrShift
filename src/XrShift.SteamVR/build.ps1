# Builds XrShift's SteamVR driver into xrshift\bin\win64\driver_xrshift.dll.
# Needs Microsoft's C++ build tools (Visual Studio Build Tools with "Desktop development with C++"); GitHub's
# windows-latest runners have them. Exits non-zero on any failure.
#   -Verify: afterwards check the DLL is 64-bit and exports HmdDriverFactory (the one function SteamVR looks for).
# Flags: /O2 (optimised) /MT (the C++ runtime is linked in, so a PC needs no Visual C++ redistributable)
#        /EHsc /std:c++17 /LD (build a DLL), every .cpp beside this script, vendored headers under third_party.
param([switch]$Verify)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "xrshift\bin\win64"
$dll = Join-Path $out "driver_xrshift.dll"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Write-Host "The C++ build tools are not installed (vswhere.exe not found)."; exit 1 }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { Write-Host "The C++ build tools are not installed (no Visual Studio with the x64 C++ tools)."; exit 1 }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { Write-Host "vcvars64.bat not found in $vs."; exit 1 }

$sources = @(Get-ChildItem $here -Filter *.cpp -File | ForEach-Object { "`"$($_.FullName)`"" })
if ($sources.Count -eq 0) { Write-Host "No .cpp files in $here."; exit 1 }

New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item $dll -ErrorAction SilentlyContinue   # a stale DLL must never pass for a fresh build
$obj = Join-Path ([IO.Path]::GetTempPath()) "xrshift_driver_obj"
New-Item -ItemType Directory -Force $obj | Out-Null

$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /EHsc /std:c++17 /LD /MT /I`"$here`" $($sources -join ' ') /Fo`"$obj\\`" /Fe:`"$dll`" /link /NOLOGO"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { Write-Host "Building the driver failed."; exit 1 }
Remove-Item "$out\*.exp", "$out\*.lib" -ErrorAction SilentlyContinue
if (-not (Test-Path $dll)) { Write-Host "The compiler reported success but $dll is missing."; exit 1 }
Write-Host "Built $dll"

if ($Verify) {
    $text = (cmd /c "`"$vcvars`" >nul && dumpbin /nologo /headers /exports `"$dll`"") | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host "dumpbin failed."; exit 1 }
    if ($text -notmatch '8664 machine \(x64\)') { Write-Host "Verify failed: the DLL is not x64."; exit 1 }
    if ($text -notmatch '\bHmdDriverFactory\b') { Write-Host "Verify failed: the DLL does not export HmdDriverFactory."; exit 1 }
    Write-Host "Verified: x64, exports HmdDriverFactory."
}
exit 0
