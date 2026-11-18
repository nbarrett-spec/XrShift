# Installs XrShift for the current user: builds it, copies it to its own folder, and puts
# shortcuts with its icon on the Desktop and in the Start menu. No administrator rights needed.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
# Remove it again:    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall
param([switch]$Uninstall)

$root = Split-Path $PSScriptRoot -Parent
$home_ = Join-Path $env:LOCALAPPDATA "Programs\XrShift"
$exe = Join-Path $home_ "XrShift.exe"
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "XrShift.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift.lnk")
)
# Always there, even when the app is not running: takes every virtual monitor away (back to a plain PC).
$reset = Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift - reset screens.lnk"

if (Get-Process XrShift -ErrorAction SilentlyContinue) {
    Write-Host "XrShift is running. Quit it first (Ctrl+Alt+Q, or Quit in its tray icon; there may be more than one icon), then run this again."
    exit 1
}

if ($Uninstall) {
    @($shortcuts + $reset) | ForEach-Object { if (Test-Path $_) { Remove-Item $_ -Force } }
    if (Test-Path $home_) { Remove-Item $home_ -Recurse -Force }
    Write-Host "XrShift removed. Your settings in $env:LOCALAPPDATA\XrShift were left alone."
    exit 0
}

& (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $home_) { Remove-Item $home_ -Recurse -Force }
New-Item -ItemType Directory -Path $home_ -Force | Out-Null
Copy-Item (Join-Path $root "dist\XrShift\*") $home_ -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($path in $shortcuts) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $home_
    $link.IconLocation = "$exe,0"
    $link.Description = "Floating screens for XR glasses"
    $link.Save()
}
$link = $shell.CreateShortcut($reset)
$link.TargetPath = $exe
$link.Arguments = "--reset"
$link.WorkingDirectory = $home_
$link.IconLocation = "$exe,0"
$link.Description = "Removes XrShift's virtual monitors: the PC goes back to its own screens"
$link.Save()

Write-Host ""
Write-Host "XrShift is installed. Start it from the Desktop icon or the Start menu."
