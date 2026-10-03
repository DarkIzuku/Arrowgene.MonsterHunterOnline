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


## Build the Spanish patch

The kit includes the approved Spanish catalogs under:

    Translation\es-ES\

Only rows explicitly marked with:

    status=translate

are applied. Rows marked \`preserve\`, \`review\` or \`ignore\` are never changed
automatically. This is important for Monster Hunter proper names, Tencent/QQ/WeGame
branding, internal ActionScript symbols and ambiguous MHO-exclusive terminology.

After extracting \`eng_patch.ifs\` once, build the current Spanish patch with:

    powershell -ExecutionPolicy Bypass -File .\BuildSpanishPatch.ps1 -BaseIfs "D:\Juegos\Monster Hunter Online\Cliente\Bin\Client\IIPS\iipsdownload\eng_patch.ifs" -ExtractedDir "D:\MHO-Translation\eng_patch_extracted"

The builder:

1. merges every translation CSV inside \`Translation\\es-ES\`;
2. rejects conflicting translations for the same source string;
3. patches DAT cells only when the entire value matches an approved source;
4. decrypts/re-encrypts encrypted DAT resources and verifies the AES round trip;
5. rebuilds supported SWF/ActionScript string pools structurally;
6. replaces only modified resources inside a new nIFS archive;
7. reopens the resulting archive and verifies every modified entry byte-for-byte.

Default output:

    ...\iipsdownload\eng_patch_es.ifs

The original \`eng_patch.ifs\` is not modified.

### Translation philosophy

The goal is maximum Spanish coverage without unnatural literal translations.
Use official Spanish Monster Hunter terminology where available. Preserve proper
names and brands when translating them would make the game less correct. Ambiguous
MHO-exclusive names remain in \`review\` until their context is known.
