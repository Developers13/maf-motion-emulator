# Assemble the portable Windows GUI runtime package.
#
# Layout (everything relative to the zip root):
#   maf-motion-emulator.exe
#   tools/                 e2e.py, run_task.py, setup_runtime.ps1, ...
#   agent/                 pipeline custom actions/recognitions
#   assets/                pipeline, templates, OCR model (if present)
#   gui/scripts/           mumu.ps1, adb-prep.ps1, root-recover.ps1
#   gui/tools/nemu/        nemu.cs (and nemu.exe if built locally)
#   README.md, docs/
#
# The GUI locates the project root by walking up from the exe directory until it
# finds tools/e2e.py, so this layout works without any ProjectRoot setting.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/package_gui_win.ps1 `
#     -ExePath gui/src-tauri/target/release/maf-motion-emulator.exe
#
#   ... -OutDir dist -NoZip    # staging folder only (CI artifact upload)
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$OutDir = 'dist',
    [switch]$NoZip
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$bundleName = 'MAF-motion-emulator-gui-win-x64'

$exe = (Resolve-Path -LiteralPath $ExePath).Path
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Output "[package] ERROR exe not found: $ExePath"
    exit 1
}
if (-not [IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $projectRoot $OutDir
}
$stage = Join-Path $OutDir $bundleName
$zip = Join-Path $OutDir "$bundleName.zip"

$required = @(
    (Join-Path $projectRoot 'tools'),
    (Join-Path $projectRoot 'agent'),
    (Join-Path $projectRoot 'assets'),
    (Join-Path $projectRoot 'gui/scripts')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output "[package] ERROR required runtime path missing: $path"
        exit 1
    }
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'maf-motion-emulator.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools') -Destination (Join-Path $stage 'tools') -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'agent') -Destination (Join-Path $stage 'agent') -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets') -Destination (Join-Path $stage 'assets') -Recurse

$guiStage = Join-Path $stage 'gui'
New-Item -ItemType Directory -Force -Path $guiStage | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'gui/scripts') -Destination (Join-Path $guiStage 'scripts') -Recurse

$nemuDir = Join-Path $projectRoot 'gui/tools/nemu'
if (Test-Path -LiteralPath $nemuDir) {
    $nemuStage = Join-Path $guiStage 'tools/nemu'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $nemuStage) | Out-Null
    Copy-Item -LiteralPath $nemuDir -Destination $nemuStage -Recurse
    if (-not (Test-Path -LiteralPath (Join-Path $nemuStage 'nemu.exe'))) {
        Write-Output '[package] WARN gui/tools/nemu/nemu.exe is missing; root recovery nemu fallback needs a local build of nemu.cs'
    }
}
else {
    Write-Output '[package] WARN gui/tools/nemu not found'
}

$readme = Join-Path $projectRoot 'README.md'
if (Test-Path -LiteralPath $readme) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $stage 'README.md')
}
$agents = Join-Path $projectRoot 'AGENTS.md'
if (Test-Path -LiteralPath $agents) {
    Copy-Item -LiteralPath $agents -Destination (Join-Path $stage 'AGENTS.md')
}
$docs = Join-Path $projectRoot 'docs'
if (Test-Path -LiteralPath $docs) {
    Copy-Item -LiteralPath $docs -Destination (Join-Path $stage 'docs') -Recurse
}

# Local bytecode caches and machine-specific MAA config must not ship.
foreach ($relative in @('agent/__pycache__', 'tools/__pycache__', 'assets/config')) {
    $local = Join-Path $stage $relative
    if (Test-Path -LiteralPath $local) {
        Remove-Item -LiteralPath $local -Recurse -Force
    }
}

$ocrDir = Join-Path $stage 'assets/resource/model/ocr'
$ocrCount = 0
if (Test-Path -LiteralPath $ocrDir) {
    $ocrCount = (Get-ChildItem -LiteralPath $ocrDir -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
}
if ($ocrCount -lt 1) {
    Write-Output '[package] WARN OCR model is missing from assets/resource/model/ocr; run tools/setup_runtime.ps1 or download ppocr_v6-small.zip'
}
else {
    Write-Output "[package] OCR model: $ocrCount file(s)"
}

$fileCount = (Get-ChildItem -LiteralPath $stage -File -Recurse | Measure-Object).Count
Write-Output "[package] staged $fileCount file(s) at $stage"

if (-not $NoZip) {
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    $zipBytes = (Get-Item -LiteralPath $zip).Length
    Write-Output "[package] wrote $zip ($([math]::Round($zipBytes / 1MB, 2)) MB)"
}

Write-Output '[package] done.'
