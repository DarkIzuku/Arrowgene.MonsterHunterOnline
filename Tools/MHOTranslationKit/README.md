# MHO Translation Kit

This kit now uses Arrowgene's built-in managed Monster Hunter Online IIPS/nIFS
implementation. Tencent's `IFS2.dll` is **not required**.

## Why this is better

The server repository already contains a native C# reader/writer for MHO's
`nifs` format under:

    Arrowgene.MonsterHunterOnline.ClientTools/IIPS/

It understands the MHO-specific 0xAC header, encrypted HET/BET sections,
name hashes, archive entry flags, compression/encryption and the embedded
`(listfile)`.

That is much safer and more reproducible than patching a 2014 Tencent DLL in
memory.

## One-command audit

Open PowerShell in the extracted kit directory and run:

    powershell -ExecutionPolicy Bypass -File .\ExtractAndScan.ps1 -PatchIfs "D:\Juegos\Monster Hunter Online\Cliente\Bin\Client\IIPS\iipsdownload\eng_patch.ifs" -OutputDir "D:\MHO-Translation\eng_patch_extracted"

The script performs:

1. Managed nIFS open and HET/BET parsing.
2. Embedded `(listfile)` lookup.
3. Extraction with resolved original paths where available.
4. CJK scan of the extracted tree.
5. Creation of `remaining-cjk.csv`.

Useful outputs:

    eng_patch_extracted\_mho_listfile.txt
    eng_patch_extracted\remaining-cjk.csv

## English patch being audited

The supplied IIPS list ends with:

    [subversion]
    version=2.0.11.641
    patch=eng_patch.ifs

So `eng_patch.ifs` is loaded as the final override after the stock patch chain.

## Important

Do not publish original Tencent/Capcom assets in the repository. Keep the repo
limited to tooling, translation catalogs, metadata and patches that require the
user's own client.
