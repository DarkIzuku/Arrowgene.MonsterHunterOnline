# MHO Translation Kit

This kit combines:

- **MHOIFSExtractor.exe** — Win32/x86 helper that asks the MHO client's own
  `IFS2.dll` to read `(listfile)` and extract an IFS archive with its real paths.
- **MHOTranslator.exe** — inventories Chinese/Japanese text and exports CSV.
- **ExtractAndScan.ps1** — runs the complete audit pipeline.

Tencent/Capcom files are not included. Use your own client.

## Your English patch

The MHO Revival patch supplied for this project ends the IIPS load list with:

    [subversion]
    version=2.0.11.641
    patch=eng_patch.ifs

That means `eng_patch.ifs` is loaded as a final override after the stock MHO
2.0.11.641 patch chain.

## One-command audit

Open PowerShell in this kit directory:

    .\ExtractAndScan.ps1 `
      -PatchIfs "D:\Juegos\Monster Hunter Online\Cliente\Bin\Client\IIPS\iipsdownload\eng_patch.ifs" `
      -IFS2 "D:\Juegos\Monster Hunter Online\Cliente\Bin\Client\Bin32\IFS2.dll"

Optional:

    -OutputDir "D:\MHO-Translation\eng_patch_extracted"
    -Csv "D:\MHO-Translation\remaining-cjk.csv"

The script first tests the archive by reading its internal `(listfile)`. It
will not attempt extraction if that compatibility check fails.

## What to send back for analysis

After a successful run, the most useful files are:

1. `_mho_listfile.txt`
2. `remaining-cjk.csv`

For deeper format work, a ZIP of the extracted tree is useful too, but do not
publish original game assets in the GitHub repository.
