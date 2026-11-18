# Sets up hand tracking for XrShift's gesture mode: a private Python environment with Google's MediaPipe
# (Apache 2.0) and its hand model, all in one folder of its own. Deleting that folder removes it completely.
#   powershell -ExecutionPolicy Bypass -File scripts\hands\setup.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\hands\setup.ps1 -Remove
param([switch]$Remove)

$home_ = Join-Path $env:LOCALAPPDATA "XrShift\hands"
$venv = Join-Path $home_ "venv"
$model = Join-Path $home_ "hand_landmarker.task"
$modelUrl = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task"

if ($Remove) {
    if (Test-Path $home_) { Remove-Item $home_ -Recurse -Force }
    Write-Host "Hand tracking removed."
    exit 0
}

New-Item -ItemType Directory -Path $home_ -Force | Out-Null
if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) {
    # The hand tracker runs on Python. Most PCs do not have it: offer to install it (for this user only).
    $hasPy = Get-Command py -ErrorAction SilentlyContinue
    $hasPython = (Get-Command python -ErrorAction SilentlyContinue) -and -not ((Get-Command python).Source -like "*WindowsApps*")
    if (-not $hasPy -and -not $hasPython) {
        $answer = Read-Host "Hand gestures need Python 3.13 (free, about 100 MB). Install it now for your user? [y/N]"
        if ($answer -notmatch '^[yY]') { Write-Host "Hand gestures were not set up."; exit 1 }
        winget install --id Python.Python.3.13 -e --scope user --silent --accept-package-agreements --accept-source-agreements
        $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine")
    }
    py -3.13 -m venv $venv
    if ($LASTEXITCODE -ne 0) { python -m venv $venv }
    if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) { Write-Host "Could not make the Python environment."; exit 1 }
}
$python = Join-Path $venv "Scripts\python.exe"
& $python -m pip install --disable-pip-version-check --quiet "mediapipe==1.2.3"
if ($LASTEXITCODE -ne 0) { Write-Host "Installing MediaPipe failed."; exit 1 }

if (-not (Test-Path $model)) {
    Invoke-WebRequest -Uri $modelUrl -OutFile $model -UseBasicParsing
}
& $python -c "import mediapipe, numpy, cv2; print('mediapipe', mediapipe.__version__, 'ready')"
Write-Host "Hand tracking is set up in $home_"
