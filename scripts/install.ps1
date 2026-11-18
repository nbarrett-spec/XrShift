# Builds XrShift from this source and installs it for the current user, the same way the download does:
# its own folder, Desktop and Start menu shortcuts, and a listing in Settings > Apps. No administrator rights.
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall   (removes everything it put on the PC)
param([switch]$Uninstall)
$root = Split-Path $PSScriptRoot -Parent

if ($Uninstall) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "package\uninstall.ps1")
    exit $LASTEXITCODE
}
if (Get-Process XrShift -ErrorAction SilentlyContinue) {
    Write-Host "XrShift is running. Quit it first (close its window with X), then run this again."
    exit 1
}
& (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "package\install-app.ps1") -From (Join-Path $root "dist\XrShift")
exit $LASTEXITCODE
