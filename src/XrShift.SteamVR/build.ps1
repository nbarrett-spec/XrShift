# Builds XrShift's SteamVR driver into xrshift\bin\win64\driver_xrshift.dll.
# Needs Microsoft's C++ build tools (Visual Studio Build Tools with "Desktop development with C++").
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Write-Host "The C++ build tools are not installed."; exit 1 }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { Write-Host "The C++ build tools are not installed."; exit 1 }
$here = $PSScriptRoot
$out = Join-Path $here "xrshift\bin\win64"
New-Item -ItemType Directory -Force $out | Out-Null
$obj = Join-Path $env:TEMP "xrshift_driver.obj"
$cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /O2 /EHsc /std:c++17 /LD /MT `"$here\driver.cpp`" /Fe:`"$out\driver_xrshift.dll`" /Fo:`"$obj`" /link /NOLOGO"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { Write-Host "Building the driver failed."; exit 1 }
Remove-Item "$out\*.exp", "$out\*.lib" -ErrorAction SilentlyContinue
Write-Host "Built $out\driver_xrshift.dll"
