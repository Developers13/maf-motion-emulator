# Runtime bootstrap for local development.
#
# Installs the MaaFW Python bindings and downloads the OCR model required by
# the pipeline. Both steps are idempotent: an existing install/model is kept
# unless -Force is passed.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup_runtime.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup_runtime.ps1 -Force
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup_runtime.ps1 -PythonLauncher python
#
# Notes:
#   * MuMu Player 12 and its Android instance are external prerequisites; this
#     script does not install or launch them.
#   * user-list.json is local credentials and is never created or modified here.
param(
    [string]$PythonLauncher = 'py',
    [switch]$Force,
    [switch]$Pre
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$ocrDir = Join-Path $projectRoot 'assets/resource/model/ocr'
$ocrUrl = 'https://download.maafw.xyz/MaaCommonAssets/OCR/ppocr_v6/ppocr_v6-small.zip'

function Write-Step([string]$message) {
    Write-Output "[setup] $message"
}

# 1. Python bindings: maafw.
if (-not (Get-Command $PythonLauncher -ErrorAction SilentlyContinue)) {
    if (Get-Command python -ErrorAction SilentlyContinue) {
        $PythonLauncher = 'python'
    }
    else {
        Write-Output "[setup] ERROR Python launcher '$PythonLauncher' not found; install Python 3.10+ or pass -PythonLauncher"
        exit 1
    }
}

$maaOk = $false
try {
    & $PythonLauncher -c "import maa" *> $null
    $maaOk = ($LASTEXITCODE -eq 0)
}
catch {
    $maaOk = $false
}

if ($maaOk -and -not $Force) {
    Write-Step "maafw already installed for $PythonLauncher (skip; use -Force to upgrade)"
}
else {
    $pipArgs = @('-m', 'pip', 'install', '--upgrade', 'maafw')
    if ($Pre) {
        $pipArgs += '--pre'
    }
    Write-Step "installing/upgrading maafw via $PythonLauncher -m pip ..."
    & $PythonLauncher @pipArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Output '[setup] ERROR pip install maafw failed'
        exit 1
    }
}

# 2. OCR model: ppocr_v6-small into assets/resource/model/ocr/.
$haveOcr = $false
if (Test-Path -LiteralPath $ocrDir) {
    $haveOcr = ((Get-ChildItem -LiteralPath $ocrDir -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
}

if ($haveOcr -and -not $Force) {
    Write-Step "OCR model already present at $ocrDir (skip; use -Force to re-download)"
}
else {
    Write-Step "downloading OCR model: $ocrUrl"
    $tmpZip = Join-Path ([IO.Path]::GetTempPath()) ("ppocr_v6-small-{0}.zip" -f [Guid]::NewGuid().ToString('N'))
    try {
        if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
            & curl.exe -fL --retry 3 --connect-timeout 30 -o $tmpZip $ocrUrl
            if ($LASTEXITCODE -ne 0) {
                throw "curl failed with exit code $LASTEXITCODE"
            }
        }
        else {
            Invoke-WebRequest -Uri $ocrUrl -OutFile $tmpZip -UseBasicParsing
        }

        if (Test-Path -LiteralPath $ocrDir) {
            Remove-Item -LiteralPath $ocrDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $ocrDir -Force | Out-Null
        Expand-Archive -LiteralPath $tmpZip -DestinationPath $ocrDir -Force

        $count = (Get-ChildItem -LiteralPath $ocrDir -File -Recurse | Measure-Object).Count
        if ($count -lt 1) {
            throw 'the archive extracted no files'
        }
        Write-Step "OCR model ready: $count file(s) in $ocrDir"
    }
    catch {
        Write-Output "[setup] ERROR OCR model download/extract failed: $($_.Exception.Message)"
        exit 1
    }
    finally {
        if (Test-Path -LiteralPath $tmpZip) {
            Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue
        }
    }
}

# 3. Friendly checks for optional local tools (never fatal).
$nemu = Join-Path $projectRoot 'gui/tools/nemu/nemu.exe'
if (-not (Test-Path -LiteralPath $nemu)) {
    Write-Output "[setup] WARN $nemu is missing; build gui/tools/nemu/nemu.cs if root recovery needs the nemu fallback"
}

Write-Step 'done. MaaFW bindings and OCR model are ready.'
