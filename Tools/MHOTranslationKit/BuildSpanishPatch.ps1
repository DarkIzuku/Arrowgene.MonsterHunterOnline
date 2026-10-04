param(
    [Parameter(Mandatory = $true)]
    [string]$BaseIfs,

    [Parameter(Mandatory = $true)]
    [string]$ExtractedDir,

    [string]$OutputIfs = "",

    [string]$WorkDir = "",

    [string]$CatalogPath = "",

    [switch]$UiOnly,

    [switch]$UiSmoke,

    [string]$UiSmokeSource = "",

    [string]$UiSmokeReplacement = "",

    [switch]$UiSmokeInPlaceEqual
)

$ErrorActionPreference = "Stop"

$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$translator = Join-Path $kit "MHOTranslator.exe"
if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $catalogDir = Join-Path $kit "Translation\es-ES"
    if (-not (Test-Path $catalogDir)) {
        # Backward compatibility with kits produced before the canonical Translation\es-ES layout.
        $catalogDir = Join-Path $kit "es-ES"
    }
} else {
    $catalogDir = [System.IO.Path]::GetFullPath($CatalogPath)
}

if (-not (Test-Path $translator)) {
    throw "MHOTranslator.exe was not found beside this script."
}

if (-not (Test-Path $catalogDir)) {
    throw "The Spanish catalog directory was not found. Expected Translation\es-ES (or legacy es-ES) beside this script."
}

$BaseIfs = (Resolve-Path $BaseIfs).Path
$ExtractedDir = (Resolve-Path $ExtractedDir).Path

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path (Split-Path -Parent $BaseIfs) "mho_es_work"
}

$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
$patchedDir = Join-Path $WorkDir "patched"

if ([string]::IsNullOrWhiteSpace($OutputIfs)) {
    $OutputIfs = Join-Path (Split-Path -Parent $BaseIfs) "eng_patch_es.ifs"
}

$OutputIfs = [System.IO.Path]::GetFullPath($OutputIfs)

if (Test-Path $patchedDir) {
    Remove-Item $patchedDir -Recurse -Force
}
New-Item -ItemType Directory -Force $patchedDir | Out-Null

Write-Host ""
Write-Host "=== MHO Spanish Patch Builder ==="
Write-Host "Base IFS:      $BaseIfs"
Write-Host "Extracted:     $ExtractedDir"
Write-Host "Catalogs:      $catalogDir"
Write-Host "Patched files: $patchedDir"
Write-Host "Output IFS:    $OutputIfs"
Write-Host "Mode:          $(if ($UiSmoke) { 'single SWF smoke test (mhui.swf)' } elseif ($UiOnly) { 'SWF/UI only' } else { 'all supported resources' })"
Write-Host ""

Write-Host "[1/2] Applying approved Spanish translations..."
if ($UiSmoke) {
    if ([string]::IsNullOrWhiteSpace($UiSmokeSource)) {
        & $translator apply $ExtractedDir $catalogDir --out $patchedDir --only-swf --only-path "libs/ui/mhui.swf"
    } elseif ($UiSmokeInPlaceEqual) {
        if ([string]::IsNullOrEmpty($UiSmokeReplacement)) {
            throw "UiSmokeInPlaceEqual requires -UiSmokeReplacement."
        }
        & $translator apply $ExtractedDir $catalogDir --out $patchedDir --only-swf --only-path "libs/ui/mhui.swf" --only-source $UiSmokeSource --override-translation $UiSmokeReplacement --swf-inplace-equal
    } elseif (-not [string]::IsNullOrEmpty($UiSmokeReplacement)) {
        & $translator apply $ExtractedDir $catalogDir --out $patchedDir --only-swf --only-path "libs/ui/mhui.swf" --only-source $UiSmokeSource --override-translation $UiSmokeReplacement
    } else {
        & $translator apply $ExtractedDir $catalogDir --out $patchedDir --only-swf --only-path "libs/ui/mhui.swf" --only-source $UiSmokeSource
    }
} elseif ($UiOnly) {
    & $translator apply $ExtractedDir $catalogDir --out $patchedDir --only-swf
} else {
    & $translator apply $ExtractedDir $catalogDir --out $patchedDir
}
if ($LASTEXITCODE -ne 0) {
    throw "Translation apply failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "[2/2] Rebuilding and verifying nIFS archive..."
& $translator build-ifs $BaseIfs $patchedDir --out $OutputIfs
if ($LASTEXITCODE -ne 0) {
    throw "IFS rebuild failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "=== Spanish patch complete ==="
Write-Host "Output: $OutputIfs"
Write-Host ""
Write-Host "The original archive was not modified."
