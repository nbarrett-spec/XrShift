# Removes XrShift and everything it put on this PC, so nothing is left behind:
#   the app and its shortcuts, its listing in Settings > Apps, its settings and logs, the hand tracker it set up,
#   "start with Windows", and its SteamVR driver registration.
# It asks before removing the extra-monitor driver (Parsec's), which XrShift only set up when asked.
# Run from Settings > Apps > XrShift > Uninstall, or:  powershell -ExecutionPolicy Bypass -File uninstall.ps1
$home_ = Join-Path $env:LOCALAPPDATA "Programs\XrShift"
$data = Join-Path $env:LOCALAPPDATA "XrShift"

Write-Host "Removing XrShift..."
if (Get-Process XrShift -ErrorAction SilentlyContinue) {
    Write-Host "XrShift is running. Quit it first (close its window with X), then run this again."
    Read-Host "Press Enter to close"
    exit 1
}

# SteamVR: forget XrShift's driver (only if SteamVR is installed).
$driver = Join-Path $home_ "steamvr\xrshift"
$steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
if ($steam) {
    $vrpathreg = Join-Path $steam "steamapps\common\SteamVR\bin\win64\vrpathreg.exe"
    if (Test-Path $vrpathreg) {
        & $vrpathreg removedriver $driver 2>$null | Out-Null
        Write-Host "  SteamVR: XrShift's driver unregistered."
    }
}

# Start with Windows, the Settings > Apps listing, the shortcuts.
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "XrShift" -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XrShift" -Recurse -ErrorAction SilentlyContinue
foreach ($link in @(
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "XrShift.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift - reset screens.lnk"))) {
    if (Test-Path $link) { Remove-Item $link -Force }
}
Write-Host "  Shortcuts and start with Windows removed."

# Settings, logs, and the hand tracker (Google's MediaPipe in its own folder).
if (Test-Path $data) {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Settings, logs and the hand tracker removed."
}

# The extra-monitor driver: Parsec's, set up from XrShift's Add monitor. Other apps may use it too, so ask.
$parsec = Get-PnpDevice -FriendlyName "Parsec Virtual Display Adapter" -ErrorAction SilentlyContinue
if ($parsec) {
    $answer = Read-Host "Also remove the extra-monitor driver (Parsec Virtual Display Driver)? Keep it if another app uses it. [y/N]"
    if ($answer -match '^[yY]') {
        winget uninstall --id Parsec.ParsecVDD -e --silent --accept-source-agreements
        Write-Host "  Extra-monitor driver removed (Windows may ask for permission)."
    }
}

# The app itself last (this script may be running from inside it; PowerShell has already read it).
if (Test-Path $home_) { Remove-Item $home_ -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ""
Write-Host "XrShift has been removed."
Read-Host "Press Enter to close"
