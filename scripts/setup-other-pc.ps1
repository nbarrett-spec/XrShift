# Sets up one of your other PCs so its Claude Code sessions show up as bots on the PC with the glasses.
# Run it from the unzipped kit folder:   powershell -ExecutionPolicy Bypass -File setup-other-pc.ps1
# It copies the app to your own programs folder, copies network.json (the shared secret) to the app's settings
# folder, and adds the bot hooks to Claude Code's settings, keeping a backup. No administrator rights needed.
# To undo: delete the two folders it names at the end and put the settings backup back.
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$appSource = Join-Path $kit 'XrShift'
if (-not (Test-Path (Join-Path $appSource 'XrShift.exe'))) { throw "XrShift\XrShift.exe is not beside this script. Unzip the whole kit first." }

$appFolder = Join-Path $env:LOCALAPPDATA 'Programs\XrShift'
if (Get-Process XrShift -ErrorAction SilentlyContinue) { throw 'XrShift is running on this PC. Quit it (Ctrl+Alt+Q or its tray icon) and run this again.' }
New-Item -ItemType Directory -Force $appFolder | Out-Null
Copy-Item (Join-Path $appSource '*') $appFolder -Recurse -Force
$exe = (Join-Path $appFolder 'XrShift.exe') -replace '\\', '/'

$settingsFolder = Join-Path $env:LOCALAPPDATA 'XrShift'
New-Item -ItemType Directory -Force $settingsFolder | Out-Null
$network = Join-Path $kit 'network.json'
if (Test-Path $network) { Copy-Item $network (Join-Path $settingsFolder 'network.json') -Force }
else { Write-Warning "No network.json in the kit: copy it from the PC with the glasses to $settingsFolder, or this PC's bots have nowhere to report." }

# Claude Code's settings: add our command to each hook event, leaving every other hook and setting alone.
$claudeFolder = Join-Path $env:USERPROFILE '.claude'
$settingsFile = Join-Path $claudeFolder 'settings.json'
New-Item -ItemType Directory -Force $claudeFolder | Out-Null
$settings = if (Test-Path $settingsFile) {
    Copy-Item $settingsFile "$settingsFile.bak-before-xrshift-hooks" -Force
    Get-Content $settingsFile -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
if (-not $settings.PSObject.Properties['hooks']) { $settings | Add-Member hooks ([pscustomobject]@{}) }
$command = "`"$exe`" --bot-hook"
foreach ($event in 'SessionStart', 'UserPromptSubmit', 'Notification', 'PostToolUse', 'Stop', 'SessionEnd') {
    $groups = @(); if ($settings.hooks.PSObject.Properties[$event]) { $groups = @($settings.hooks.$event) }
    $already = $groups | Where-Object { $_.hooks | Where-Object { $_.command -like '*XrShift.exe* --bot-hook' } }
    if ($already) { foreach ($g in $groups) { foreach ($h in $g.hooks) { if ($h.command -like '*XrShift.exe* --bot-hook') { $h.command = $command } } } }
    else { $groups += [pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = $command; timeout = 5 }) } }
    $settings.hooks | Add-Member -NotePropertyName $event -NotePropertyValue $groups -Force
}
$json = $settings | ConvertTo-Json -Depth 30
$null = $json | ConvertFrom-Json   # must still be valid before it replaces the real file
[IO.File]::WriteAllText($settingsFile, $json, (New-Object Text.UTF8Encoding $false))

Write-Host "Done on $env:COMPUTERNAME."
Write-Host "  App:       $appFolder"
Write-Host "  Settings:  $settingsFolder"
Write-Host "  Hooks:     $settingsFile  (backup beside it, if there was a file before)"
Write-Host 'Claude Code sessions started from now on report to the PC named in network.json.'
