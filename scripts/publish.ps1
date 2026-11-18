# Builds the release version of XrShift into dist\XrShift, from scratch every time.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "dist\XrShift"

# A half-replaced folder is worse than none: files from two builds do not work together.
if (Test-Path $out) {
    try { Remove-Item $out -Recurse -Force -ErrorAction Stop }
    catch {
        Write-Host "Could not clear $out. If XrShift was started from there, quit it (Ctrl+Alt+Q) and run this again."
        exit 1
    }
}

# The SteamVR driver (VR mode) is C++; it is built when Microsoft's C++ build tools are installed, and VR mode
# says so plainly when it is missing.
if (Test-Path (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe")) {
    & powershell -ExecutionPolicy Bypass -File "$root\src\XrShift.SteamVR\build.ps1" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "The VR driver did not build; VR mode will not be available." }
}
dotnet publish "$root\src\XrShift.App" -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed."; exit $LASTEXITCODE }
Write-Host "Built: $out\XrShift.exe"
