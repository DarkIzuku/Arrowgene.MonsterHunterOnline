param(
    [Parameter(Mandatory = $true)]
    [string]$BaseIfs,

    [Parameter(Mandatory = $true)]
    [string]$ExtractedDir,

    [string]$OutputIfs = "D:\\MHO-Translation\\eng_patch_enhanced.ifs",

    [string]$WorkDir = "D:\\MHO-Translation\\english_cleanup_work",

    [string]$CatalogPath = ""
)

$ErrorActionPreference = "Stop"
$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$translator = Join-Path $kit "MHOTranslator.exe"

if (-not (Test-Path $translator)) {
    throw "MHOTranslator.exe was not found next to this script."
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $kit "Translation\en-US\english-cleanup-safe-v1.csv"
}

$patchedDir = Join-Path $WorkDir "patched"
if (Test-Path $patchedDir) {
    Remove-Item $patchedDir -Recurse -Force
}
New-Item -ItemType Directory -Force $patchedDir | Out-Null

Write-Host ""
Write-Host "=== MHO English Patch Cleanup ==="
Write-Host "Base IFS:   $BaseIfs"
Write-Host "Extracted:  $ExtractedDir"
Write-Host "Catalog:    $CatalogPath"
Write-Host "Patched:    $patchedDir"
Write-Host "Output IFS: $OutputIfs"
Write-Host ""

Write-Host "[1/2] Applying conservative English cleanup..."
& $translator apply-english-cleanup $ExtractedDir $CatalogPath --out $patchedDir --quiet
if ($LASTEXITCODE -ne 0) {
    throw "English cleanup failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "[2/2] Rebuilding and verifying nIFS archive..."
& $translator build-ifs $BaseIfs $patchedDir --out $OutputIfs --skip-oversize
if ($LASTEXITCODE -ne 0) {
    throw "IFS rebuild failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "=== English cleanup complete ==="
Write-Host "Output: $OutputIfs"
Write-Host ""
Write-Host "The original English patch was not modified."
