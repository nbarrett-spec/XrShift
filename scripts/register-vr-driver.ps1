# Tells SteamVR about XrShift's VR driver (or forgets it again) by editing the "external_drivers" list in
#   %LOCALAPPDATA%\openvr\openvrpaths.vrpath
# Nothing else in that file is touched, a backup is made before any change, and running it twice does no harm.
#   powershell -ExecutionPolicy Bypass -File register-vr-driver.ps1 -Status       (the default)
#   powershell -ExecutionPolicy Bypass -File register-vr-driver.ps1 -Register
#   powershell -ExecutionPolicy Bypass -File register-vr-driver.ps1 -Unregister
# -DriverFolder: the xrshift driver folder; found automatically (installed app, then the kit, then the repository).
# -VrPath:       the vrpath file; only for testing against a copy.
param(
    [switch]$Register,
    [switch]$Unregister,
    [switch]$Status,
    [string]$DriverFolder,
    [string]$VrPath = (Join-Path $env:LOCALAPPDATA 'openvr\openvrpaths.vrpath')
)
$ErrorActionPreference = 'Stop'
if ([int]$Register.IsPresent + [int]$Unregister.IsPresent + [int]$Status.IsPresent -gt 1) { throw 'Give only one of -Register, -Unregister, -Status.' }

function Normalize([string]$p) { ($p -replace '/', '\').TrimEnd('\') }

if (-not $DriverFolder) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\XrShift\steamvr\xrshift'),
        (Join-Path $PSScriptRoot 'XrShift\steamvr\xrshift'),
        (Join-Path $PSScriptRoot '..\src\XrShift.SteamVR\xrshift'))
    $DriverFolder = $candidates | Where-Object { Test-Path (Join-Path $_ 'bin\win64\driver_xrshift.dll') } | Select-Object -First 1
    if (-not $DriverFolder) { $DriverFolder = $candidates[0] }
}
$DriverFolder = Normalize ([IO.Path]::GetFullPath($DriverFolder))
$dllPresent = Test-Path (Join-Path $DriverFolder 'bin\win64\driver_xrshift.dll')

# Steam's libraries (the default one plus any in libraryfolders.vdf), looking for SteamVR (Steam app 250820).
function Find-SteamVr {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '^\s*"path"\s+"(.+)"\s*$') { $libs += ($Matches[1] -replace '\\\\', '\') }
        }
    }
    foreach ($l in $libs) {
        $p = Join-Path $l 'steamapps\common\SteamVR'
        if ((Test-Path (Join-Path $l 'steamapps\appmanifest_250820.acf')) -or (Test-Path (Join-Path $p 'bin\win64\vrserver.exe'))) { return $p }
    }
    return $null
}

# Reads the vrpath file; says why and returns $null if it is missing or not JSON.
function Read-VrPath {
    if (-not (Test-Path $VrPath)) {
        Write-Host "SteamVR has not been run on this PC yet: $VrPath does not exist. Start SteamVR once, close it, and run this again."
        return $null
    }
    try { return (Get-Content $VrPath -Raw | ConvertFrom-Json) }
    catch { Write-Host "$VrPath is not valid JSON ($($_.Exception.Message)). Nothing was changed."; return $null }
}

function Get-Drivers($cfg) {
    if ($cfg.PSObject.Properties['external_drivers'] -and $null -ne $cfg.external_drivers) { return @($cfg.external_drivers) }
    return @()
}

if ($Register -or $Unregister) {
    $cfg = Read-VrPath
    if ($null -eq $cfg) { exit 1 }
    if ($Register -and -not $dllPresent) {
        Write-Host "The driver is not built: $DriverFolder\bin\win64\driver_xrshift.dll is missing. Nothing was changed."
        exit 1
    }
    $drivers = Get-Drivers $cfg
    $isMine = { param($d) (Normalize "$d") -ieq $DriverFolder }
    $has = [bool]($drivers | Where-Object { & $isMine $_ })
    if ($Register -and $has) { Write-Host "Already registered: $DriverFolder"; exit 0 }
    if ($Unregister -and -not $has) { Write-Host "Not registered, nothing to do."; exit 0 }

    $new = if ($Register) { @($drivers) + $DriverFolder } else { @($drivers | Where-Object { -not (& $isMine $_) }) }
    if ($cfg.PSObject.Properties['external_drivers']) { $cfg.external_drivers = $new }
    else { $cfg | Add-Member -NotePropertyName external_drivers -NotePropertyValue $new }

    $json = $cfg | ConvertTo-Json -Depth 20
    if (-not $new) { $json = $json -replace '("external_drivers":\s*)null', '$1[]' }   # Windows PowerShell writes an empty list as null
    $check = $json | ConvertFrom-Json   # must still be valid, and say what we meant, before it replaces the real file
    if ((@(Get-Drivers $check) | Where-Object { & $isMine $_ }).Count -ne [int]$Register.IsPresent) { throw 'Internal check failed; nothing was changed.' }

    $backup = "$VrPath.bak-before-xrshift"
    Copy-Item $VrPath $backup -Force
    $tmp = "$VrPath.xrshift-new"
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding $false))
    [IO.File]::Replace($tmp, $VrPath, [NullString]::Value)   # atomic swap on the same volume
    if (Get-Process vrserver, vrmonitor -ErrorAction SilentlyContinue) { Write-Host 'SteamVR is running: restart it for this to take effect.' }
    Write-Host "$(if ($Register) { 'Registered' } else { 'Unregistered' }): $DriverFolder"
    Write-Host "Backup of the old file: $backup"
    exit 0
}

# -Status (also when no switch is given)
$steamVr = Find-SteamVr
Write-Host "SteamVR installed:   $(if ($steamVr) { "yes ($steamVr)" } else { 'no (install it from Steam)' })"
Write-Host "Driver built:        $(if ($dllPresent) { "yes ($DriverFolder)" } else { "no ($DriverFolder\bin\win64\driver_xrshift.dll is missing)" })"
if (-not (Test-Path $VrPath)) {
    Write-Host "Driver registered:   unknown (SteamVR has not been run yet: $VrPath does not exist)"
}
else {
    $cfg = Read-VrPath
    if ($null -ne $cfg) {
        $reg = [bool](Get-Drivers $cfg | Where-Object { (Normalize "$_") -ieq $DriverFolder })
        Write-Host "Driver registered:   $(if ($reg) { 'yes' } else { 'no' })"
    }
}
exit 0
