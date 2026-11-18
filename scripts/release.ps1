# Makes the download for GitHub: XrShift with its own .NET inside (nothing else to install), an installer and an
# uninstaller, zipped as dist\XrShift-<version>-win-x64.zip.
#   powershell -ExecutionPolicy Bypass -File scripts\release.ps1
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot "publish.ps1") -SelfContained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$version = (Get-Item (Join-Path $root "dist\XrShift\XrShift.exe")).VersionInfo.ProductVersion -replace '\+.*$', ''
$name = "XrShift-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage "setup") -Force | Out-Null
Copy-Item (Join-Path $root "dist\XrShift") (Join-Path $stage "XrShift") -Recurse
Copy-Item (Join-Path $PSScriptRoot "package\install-app.ps1"), (Join-Path $PSScriptRoot "package\uninstall.ps1") (Join-Path $stage "setup")
Copy-Item (Join-Path $PSScriptRoot "package\Install XrShift.cmd") $stage
Copy-Item (Join-Path $root "LICENSE") $stage
$zip = Join-Path $root "dist\$name-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "Release: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
