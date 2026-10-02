# MHOIFSExtractor

Win32 helper for extracting Monster Hunter Online `.ifs` archives through the
client's own `IFS2.dll`.

## Why use IFS2.dll?

MHO's IFS archives contain HET/BET metadata that generic archive tools do not
always decode correctly. The original Tencent tooling exposes functions for
opening the archive, reading its internal `(listfile)`, and extracting files.
Using the client's own DLL gives us the real filenames and directory structure.

This repository does **not** include Tencent's `IFS2.dll`. Use the copy from
your own MHO client.

## Build

The GitHub Actions workflow builds Win32/x86 because the MHO client and
`IFS2.dll` are 32-bit.

## Usage

Put `MHOIFSExtractor.exe` next to the game's `IFS2.dll`, or pass the DLL path.

First test only the listfile:

    MHOIFSExtractor.exe list "D:\MHO\IIPS\iipsdownload\eng_patch.ifs" --ifs2 "D:\MHO\Bin\Client\Bin32\IFS2.dll"

If that succeeds, extract:

    MHOIFSExtractor.exe extract "D:\MHO\IIPS\iipsdownload\eng_patch.ifs" --ifs2 "D:\MHO\Bin\Client\Bin32\IFS2.dll" --out "D:\MHO\eng_patch_extracted"

The extractor writes `_mho_listfile.txt` into the output directory so we can
audit the real archive layout.

## Compatibility

The helper first looks for named IFS2 exports. If they are unavailable, it uses
a known Tencent IFS compatibility profile. The offsets can be overridden from
the command line if the MHO version differs.

The tool fails with diagnostics instead of modifying the archive.

## Translation workflow

After extraction:

    MHOTranslator.exe scan "D:\MHO\eng_patch_extracted" --all --output "D:\MHO\eng_patch_cjk.csv"

That gives us a translation catalog with actual source paths instead of raw
archive offsets.
