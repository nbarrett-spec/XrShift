# Installs XrShift for the current user from a ready-built copy: puts it in its own folder, adds Desktop and
# Start menu shortcuts, and lists it in Windows' Settings > Apps so it can be removed from there like any app.
# No administrator rights needed. Used by "Install XrShift.cmd" in the download, and by scripts\install.ps1.
param([Parameter(Mandatory = $true)][string]$From)

$ErrorActionPreference = "Stop"
$home_ = Join-Path $env:LOCALAPPDATA "Programs\XrShift"
$exe = Join-Path $home_ "XrShift.exe"
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "XrShift.lnk"
$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift.lnk"
$reset = Join-Path ([Environment]::GetFolderPath("Programs")) "XrShift - reset screens.lnk"

if (Get-Process XrShift -ErrorAction SilentlyContinue) {
    Write-Host "XrShift is running. Quit it first (close its window with X), then run this again."
    exit 1
}
if (-not (Test-Path (Join-Path $From "XrShift.exe"))) {
    Write-Host "No XrShift.exe in $From."
    exit 1
}

# A folder holding files from two versions does not work: start clean.
if (Test-Path $home_) { Remove-Item $home_ -Recurse -Force }
New-Item -ItemType Directory -Path $home_ -Force | Out-Null
Copy-Item (Join-Path $From "*") $home_ -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $home_ -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($path in @($desktop, $startMenu)) {
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
$link.Description = "Removes XrShift's extra monitors: the PC goes back to its own screens"
$link.Save()

# Settings > Apps: name, version, icon, size, and the way to remove it.
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$sizeKb = [int]((Get-ChildItem $home_ -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XrShift"
New-Item -Path $key -Force | Out-Null
$values = @{
    DisplayName = "XrShift"
    DisplayVersion = $version
    Publisher = "XrShift (open source)"
    DisplayIcon = "$exe,0"
    InstallLocation = $home_
    URLInfoAbout = "https://github.com/skinger3/XrShift"
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$home_\uninstall.ps1`""
}
foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $key -Name EstimatedSize -Value $sizeKb -Type DWord

Write-Host ""
Write-Host "XrShift $version is installed. Start it from the Desktop icon or the Start menu."
Write-Host "To remove it later: Settings > Apps > XrShift > Uninstall."
