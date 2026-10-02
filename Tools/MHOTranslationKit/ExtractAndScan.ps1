param(
    [Parameter(Mandatory = $true)]
    [string]$PatchIfs,

    [Parameter(Mandatory = $true)]
    [string]$IFS2,

    [string]$OutputDir = "",

    [string]$Csv = ""
)

$ErrorActionPreference = "Stop"

$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$extractor = Join-Path $kit "MHOIFSExtractor.exe"
$translator = Join-Path $kit "MHOTranslator.exe"

if (-not (Test-Path $extractor)) {
    throw "MHOIFSExtractor.exe was not found beside this script."
}

if (-not (Test-Path $translator)) {
    throw "MHOTranslator.exe was not found beside this script."
}

$PatchIfs = (Resolve-Path $PatchIfs).Path
$IFS2 = (Resolve-Path $IFS2).Path

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($PatchIfs)
    $OutputDir = Join-Path (Split-Path -Parent $PatchIfs) ($base + "_extracted")
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if ([string]::IsNullOrWhiteSpace($Csv)) {
    $Csv = Join-Path $OutputDir "remaining-cjk.csv"
}

Write-Host ""
Write-Host "=== Monster Hunter Online Translation Pipeline ==="
Write-Host "Patch:  $PatchIfs"
Write-Host "IFS2:   $IFS2"
Write-Host "Output: $OutputDir"
Write-Host ""

Write-Host "[1/3] Testing archive and reading internal (listfile)..."
& $extractor list $PatchIfs --ifs2 $IFS2
if ($LASTEXITCODE -ne 0) {
    throw "IFS listfile test failed with exit code $LASTEXITCODE. Do not continue until IFS2 compatibility is resolved."
}

Write-Host ""
Write-Host "[2/3] Extracting archive with original paths..."
& $extractor extract $PatchIfs --ifs2 $IFS2 --out $OutputDir
$extractExit = $LASTEXITCODE

if ($extractExit -ne 0 -and $extractExit -ne 10) {
    throw "IFS extraction failed with exit code $extractExit."
}

if ($extractExit -eq 10) {
    Write-Warning "Extraction completed with one or more failed entries. The scan will continue so the successful files can still be audited."
}

Write-Host ""
Write-Host "[3/3] Scanning extracted tree for remaining Chinese/Japanese text..."
& $translator scan $OutputDir --all --max-mb 64 --output $Csv
if ($LASTEXITCODE -ne 0) {
    throw "Translation scan failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "=== Complete ==="
Write-Host "Extracted tree: $OutputDir"
Write-Host "CJK catalog:    $Csv"
Write-Host "Internal list:  $(Join-Path $OutputDir '_mho_listfile.txt')"
