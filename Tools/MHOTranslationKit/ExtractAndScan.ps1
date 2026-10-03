param(
    [Parameter(Mandatory = $true)]
    [string]$PatchIfs,

    [string]$OutputDir = "",

    [string]$Csv = ""
)

$ErrorActionPreference = "Stop"

$kit = Split-Path -Parent $MyInvocation.MyCommand.Path
$translator = Join-Path $kit "MHOTranslator.exe"

if (-not (Test-Path $translator)) {
    throw "MHOTranslator.exe was not found beside this script."
}

$PatchIfs = (Resolve-Path $PatchIfs).Path

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
Write-Host "Output: $OutputDir"
Write-Host ""

Write-Host "[1/2] Opening and extracting nIFS with Arrowgene managed parser..."
& $translator extract-ifs $PatchIfs --out $OutputDir
$extractExit = $LASTEXITCODE

if ($extractExit -ne 0 -and $extractExit -ne 10) {
    throw "Managed IIPS extraction failed with exit code $extractExit."
}

if ($extractExit -eq 10) {
    Write-Warning "Extraction completed with one or more failed entries. The scan will continue."
}

Write-Host ""
Write-Host "[2/2] Scanning extracted tree for remaining Chinese/Japanese text..."
& $translator scan $OutputDir --all --max-mb 64 --output $Csv
if ($LASTEXITCODE -ne 0) {
    throw "Translation scan failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "=== Complete ==="
Write-Host "Extracted tree: $OutputDir"
Write-Host "CJK catalog:    $Csv"
Write-Host "Path list:      $(Join-Path $OutputDir '_mho_listfile.txt')"
