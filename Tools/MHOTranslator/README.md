# MHOTranslator

Early tooling for the DarkIzuku Monster Hunter Online translation effort.

The goal is to avoid translating the client blindly. We first extract the original
game archives and the existing English patch, measure what the patch already changes,
and then inventory every remaining Chinese/Japanese string.

## Why this exists

MHO Revival documents that MHO client content is stored in .ifs archives containing
resources such as Lua scripts, models, textures and other game content. The original
English translation team's public project describes its tools as the applications used
to extract, translate, import and build the MHO English patch.

References:

- https://gitlab.com/kenma9123/mho-project
- https://github.com/MHO-Revival/Docs/blob/main/IFS%20Files.md
- https://github.com/MHO-Revival/Docs/blob/main/Local%20server%20Installation%20Guide.md

## Phase 1

This tool currently does two deliberately safe things. It does not modify game files yet.

### Scan extracted files for CJK text

PowerShell:

    MHOTranslator.exe scan "D:\MHO\extracted"

Output defaults to:

    mho-cjk-strings.csv

Columns:

    path,line,encoding,source,translation

The scanner currently understands BOM-marked UTF-8/UTF-16, strict UTF-8 and GB18030,
which is useful for legacy Simplified Chinese Windows assets.

To try every file under the size limit:

    MHOTranslator.exe scan "D:\MHO\extracted" --all --max-mb 32 --output "D:\MHO\all-cjk.csv"

### Compare original vs English-patched extracted trees

    MHOTranslator.exe compare "D:\MHO\original-extracted" "D:\MHO\english-extracted" --output "D:\MHO\patch-diff.csv"

States:

- unchanged
- changed
- added-by-patch
- missing-from-patch-tree

This tells us which assets the existing English patch actually touches before we spend
time reverse engineering unrelated resources.

## Planned next steps

1. Recover/build the original Kenma IFS extraction/build tools.
2. Extract the stock client and English patch into parallel trees.
3. Run compare.
4. Run scan against the effective patched tree.
5. Identify the exact localization formats and placeholders.
6. Add placeholder/tag protection.
7. Export categorized translation catalogs for UI, items, quests, NPCs, etc.
8. Add an importer that preserves the original encoding and file format.
9. Rebuild a separate Spanish .ifs patch without overwriting stock assets.

## Important

Do not redistribute original Tencent/Capcom game assets in this repository. Keep
translation source data to string catalogs, tooling, metadata and patches that require
the user's own client files.
