using Arrowgene.MonsterHunterOnline.ClientTools.IIPS;
using Arrowgene.MonsterHunterOnline.ClientTools.Flash;
using Arrowgene.MonsterHunterOnline.ClientTools.Dat;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

return args.Length == 0 ? ShowHelp() : args[0].ToLowerInvariant() switch
{
    "extract-ifs" => ExtractIfsCommand(args.Skip(1).ToArray()),
    "scan" => ScanCommand(args.Skip(1).ToArray()),
    "apply" => TranslationPatch.ApplyCommand(args.Skip(1).ToArray()),
    "build-ifs" => TranslationPatch.BuildIfsCommand(args.Skip(1).ToArray()),
    "clone-ifs" => TranslationPatch.CloneIfsCommand(args.Skip(1).ToArray()),
    "inspect-ifs" => TranslationPatch.InspectIfsCommand(args.Skip(1).ToArray()),
    "inspect-entry" => InspectEntryCommand(args.Skip(1).ToArray()),
    "trace-path" => TracePathCommand(args.Skip(1).ToArray()),
    "diff-swf-strings" => DiffSwfStringsCommand(args.Skip(1).ToArray()),
    "compare" => CompareCommand(args.Skip(1).ToArray()),
    "help" or "--help" or "-h" => ShowHelp(),
    _ => UnknownCommand(args[0])
};

static int ShowHelp()
{
    Console.WriteLine("""
MHOTranslator - helper tooling for Monster Hunter Online fan translation work.

Commands:

  extract-ifs <archive.ifs> [--out <dir>] [--no-checksums] [--no-listfile]
      Opens MHO nIFS archives with Arrowgene's managed IIPS implementation and
      extracts entries without using Tencent IFS2.dll.

  scan <input-dir> [--output <csv>] [--all] [--max-mb <n>]
      Scans extracted game/patch files for Chinese/Japanese text and exports
      a translation-ready CSV.

  apply <input-dir> <catalog.csv> [--out <patched-dir>]
      Applies only catalog rows marked status=translate. DAT files are
      decrypted/re-encrypted with verification; supported SWF strings are
      rebuilt structurally instead of binary-patched.

  build-ifs <base.ifs> <patched-dir> [--out <output.ifs>]
      Replaces translated resources in a copy of the base nIFS archive,
      saves it, reopens it and verifies modified entries byte-for-byte.

  clone-ifs <base.ifs> [--out <output.ifs>]
      Rebuilds the archive without modifying any entry, then verifies all
      extracted contents. Used to test compatibility with Tencent IFS2.dll.

  inspect-ifs <archive.ifs>
      Dumps the raw Tencent nIFS header, integrity-table layout and recomputed
      MD5 values needed for a client-compatible writer.

  inspect-entry <archive.ifs> <archive-path>
      Dumps one entry's BET metadata and compares the stored BET digest against
      MD5 of the extracted resource and raw stored payload. For sector-based
      files it also prints the sector table and compression markers.

  trace-path <IIPSFileList.lst> <archive-path> [--out <dir>]
      Walks the active IIPS load order and reports every archive containing the
      requested path. Optionally extracts every version for byte/SWF comparison.

  diff-swf-strings <before.swf> <after.swf> [--output <csv>]
      Compares DoABC string-pool entries by tag ordinal and pool index.
      Intended to discover exactly which strings the working English patch
      changed relative to the original Chinese SWF.

  compare <original-dir> <patched-dir> [--output <csv>]
      Compares two extracted trees and reports which files the existing
      English patch changes/adds/removes.

Examples:

  MHOTranslator.exe extract-ifs "D:\\MHO\\eng_patch.ifs" --out "D:\\MHO\\eng_patch_extracted"
  MHOTranslator.exe scan "D:\\MHO\\extracted"
  MHOTranslator.exe scan "D:\\MHO\\extracted" --all --output "D:\\MHO\\cjk.csv"
  MHOTranslator.exe compare "D:\\MHO\\original" "D:\\MHO\\english" --output patch-diff.csv
""");
    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return ShowHelp();
}


static int DiffSwfStringsCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("diff-swf-strings requires <before.swf> <after.swf> [--output <csv>].");
        return 2;
    }

    string beforePath = Path.GetFullPath(args[0]);
    string afterPath = Path.GetFullPath(args[1]);
    string output = Path.GetFullPath(
        GetOption(args, "--output") ??
        Path.Combine(Environment.CurrentDirectory, "swf-string-diff.csv"));

    if (!File.Exists(beforePath) || !File.Exists(afterPath))
    {
        Console.Error.WriteLine("Both SWF files must exist.");
        return 2;
    }

    try
    {
        List<List<string>> beforePools = ExtractDoAbcStringPools(File.ReadAllBytes(beforePath), beforePath);
        List<List<string>> afterPools = ExtractDoAbcStringPools(File.ReadAllBytes(afterPath), afterPath);

        int tagCount = Math.Max(beforePools.Count, afterPools.Count);
        int changed = 0;
        int same = 0;
        int structuralMismatches = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using StreamWriter writer = new(output, false, new UTF8Encoding(false));
        writer.WriteLine("abc_tag,string_index,before,after,status,notes");

        for (int tag = 0; tag < tagCount; tag++)
        {
            if (tag >= beforePools.Count || tag >= afterPools.Count)
            {
                structuralMismatches++;
                writer.WriteLine($"{tag},0,,,review,\"DoABC tag missing on one side\"");
                continue;
            }

            List<string> a = beforePools[tag];
            List<string> z = afterPools[tag];
            int count = Math.Max(a.Count, z.Count);
            if (a.Count != z.Count)
            {
                structuralMismatches++;
            }

            for (int i = 1; i < count; i++)
            {
                string before = i < a.Count ? a[i] : string.Empty;
                string after = i < z.Count ? z[i] : string.Empty;
                if (before == after)
                {
                    same++;
                    continue;
                }

                changed++;
                string status = string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after)
                    ? "review"
                    : "english_patch_changed";
                string notes = a.Count == z.Count
                    ? "same_pool_index"
                    : "pool_count_mismatch";

                writer.WriteLine(
                    $"{tag},{i},{CsvCell(before)},{CsvCell(after)},{status},{CsvCell(notes)}");
            }
        }

        Console.WriteLine($"Before:                {beforePath}");
        Console.WriteLine($"After:                 {afterPath}");
        Console.WriteLine($"DoABC tags before:     {beforePools.Count}");
        Console.WriteLine($"DoABC tags after:      {afterPools.Count}");
        Console.WriteLine($"Changed pool entries:  {changed}");
        Console.WriteLine($"Unchanged pool entries:{same}");
        Console.WriteLine($"Structural mismatches: {structuralMismatches}");
        Console.WriteLine($"Output:                {output}");

        return structuralMismatches == 0 ? 0 : 5;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"diff-swf-strings failed: {ex}");
        return 7;
    }
}

static List<List<string>> ExtractDoAbcStringPools(byte[] bytes, string name)
{
    SwfFile swf = SwfFile.Open(bytes, name);
    List<List<string>> result = new();

    foreach (SwfTag tag in swf.Tags)
    {
        if (tag.Code != 82)
        {
            continue;
        }

        ReadOnlySpan<byte> tagData = tag.Data.Span;
        if (tagData.Length < 5)
        {
            continue;
        }

        int offset = 4;
        while (offset < tagData.Length && tagData[offset] != 0)
        {
            offset++;
        }
        if (offset >= tagData.Length)
        {
            continue;
        }

        offset++;
        ReadOnlySpan<byte> abc = tagData.Slice(offset);
        if (abc.Length < 4)
        {
            continue;
        }

        int p = 4;

        uint intCount = ReadAbcU30(abc, ref p);
        for (uint i = 1; i < intCount; i++)
        {
            SkipAbcU32(abc, ref p);
        }

        uint uintCount = ReadAbcU30(abc, ref p);
        for (uint i = 1; i < uintCount; i++)
        {
            SkipAbcU32(abc, ref p);
        }

        uint doubleCount = ReadAbcU30(abc, ref p);
        if (doubleCount > 0)
        {
            int bytesToSkip = checked((int)((doubleCount - 1) * 8));
            if (bytesToSkip > abc.Length - p)
            {
                throw new InvalidDataException("ABC double pool exceeds DoABC tag.");
            }
            p += bytesToSkip;
        }

        uint stringCount = ReadAbcU30(abc, ref p);
        List<string> strings = new(checked((int)stringCount)) { string.Empty };
        for (uint i = 1; i < stringCount; i++)
        {
            uint length = ReadAbcU30(abc, ref p);
            int len = checked((int)length);
            if (len > abc.Length - p)
            {
                throw new InvalidDataException("ABC string exceeds DoABC tag.");
            }

            ReadOnlySpan<byte> raw = abc.Slice(p, len);
            p += len;
            try
            {
                strings.Add(new UTF8Encoding(false, true).GetString(raw));
            }
            catch (DecoderFallbackException)
            {
                strings.Add(Convert.ToHexString(raw));
            }
        }

        result.Add(strings);
    }

    return result;
}

static uint ReadAbcU30(ReadOnlySpan<byte> data, ref int offset)
{
    uint value = 0;
    for (int i = 0; i < 5; i++)
    {
        if (offset >= data.Length)
        {
            throw new InvalidDataException("Unexpected end of ABC U30.");
        }

        byte b = data[offset++];
        value |= (uint)(b & 0x7F) << (7 * i);
        if ((b & 0x80) == 0)
        {
            return value & 0x3FFFFFFF;
        }
    }

    throw new InvalidDataException("Invalid ABC U30.");
}

static void SkipAbcU32(ReadOnlySpan<byte> data, ref int offset)
{
    for (int i = 0; i < 5; i++)
    {
        if (offset >= data.Length)
        {
            throw new InvalidDataException("Unexpected end of ABC U32.");
        }

        byte b = data[offset++];
        if ((b & 0x80) == 0)
        {
            return;
        }
    }

    throw new InvalidDataException("Invalid ABC U32.");
}

static string CsvCell(string value)
{
    if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    return value;
}

static int ExtractIfsCommand(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("extract-ifs requires <archive.ifs>.");
        return 2;
    }

    string archivePath = Path.GetFullPath(args[0]);
    if (!File.Exists(archivePath))
    {
        Console.Error.WriteLine($"Archive does not exist: {archivePath}");
        return 2;
    }

    string output = GetOption(args, "--out")
                    ?? Path.Combine(
                        Path.GetDirectoryName(archivePath)!,
                        Path.GetFileNameWithoutExtension(archivePath) + "_extracted");
    output = Path.GetFullPath(output);

    bool verifyChecksums = !args.Any(x => x.Equals("--no-checksums", StringComparison.OrdinalIgnoreCase));
    bool loadListFile = !args.Any(x => x.Equals("--no-listfile", StringComparison.OrdinalIgnoreCase));

    try
    {
        Console.WriteLine($"Opening nIFS archive: {archivePath}");
        using IIPSArchive archive = IIPSArchive.Open(
            archivePath,
            new IIPSArchiveOpenOptions
            {
                VerifyChecksums = verifyChecksums,
                LoadListFile = loadListFile,
                FileShare = FileShare.ReadWrite | FileShare.Delete,
            });

        Console.WriteLine($"FormatVersion:   {archive.Metadata.FormatVersion}");
        Console.WriteLine($"SectorSize:      {archive.Metadata.SectorSize}");
        Console.WriteLine($"Header MD5:      {archive.Metadata.HeaderMd5}");
        Console.WriteLine($"HET MD5:         {archive.Metadata.HetMd5}");
        Console.WriteLine($"BET MD5:         {archive.Metadata.BetMd5}");
        Console.WriteLine($"Entries:         {archive.Entries.Count}");
        Console.WriteLine($"Resolved paths:  {archive.ArchivePaths.Count}");

        Directory.CreateDirectory(output);
        string unnamed = Path.Combine(output, "_unnamed");
        int extracted = 0;
        int failed = 0;
        int skipped = 0;

        foreach (IIPSArchiveEntry entry in archive.Entries)
        {
            if (!entry.Exists || entry.Length == 0 || entry.IsDirectory)
            {
                skipped++;
                continue;
            }

            string target;
            if (!string.IsNullOrWhiteSpace(entry.ArchivePath))
            {
                string normalized = entry.ArchivePath!
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

                string fullTarget = Path.GetFullPath(Path.Combine(output, normalized));
                string outputRoot = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullTarget.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"[SKIP] Unsafe archive path: {entry.ArchivePath}");
                    failed++;
                    continue;
                }

                target = fullTarget;
            }
            else
            {
                Directory.CreateDirectory(unnamed);
                target = Path.Combine(unnamed, $"{entry.Index:D6}.bin");
            }

            try
            {
                string? directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(target, entry.ReadAllBytes());
                extracted++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] entry {entry.Index} {entry.ArchivePath ?? "(unnamed)"}: {ex.Message}");
            }

            if ((extracted + failed) % 100 == 0)
            {
                Console.WriteLine($"Progress: extracted={extracted} failed={failed} skipped={skipped}");
            }
        }

        string listFile = Path.Combine(output, "_mho_listfile.txt");
        File.WriteAllLines(listFile, archive.ArchivePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));

        Console.WriteLine($"Output:          {output}");
        Console.WriteLine($"Extracted:       {extracted}");
        Console.WriteLine($"Failed:          {failed}");
        Console.WriteLine($"Skipped:         {skipped}");
        Console.WriteLine($"Path list:       {listFile}");

        return failed == 0 ? 0 : 10;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"IIPS open/extract failed: {ex}");
        return 7;
    }
}


static int InspectEntryCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("inspect-entry requires <archive.ifs> <archive-path>.");
        return 2;
    }

    string archivePath = Path.GetFullPath(args[0]);
    string entryPath = args[1].Replace('/', '\\');
    if (!File.Exists(archivePath))
    {
        Console.Error.WriteLine($"Archive does not exist: {archivePath}");
        return 2;
    }

    try
    {
        using IIPSArchive archive = IIPSArchive.Open(
            archivePath,
            new IIPSArchiveOpenOptions
            {
                VerifyChecksums = false,
                LoadListFile = false,
                FileShare = FileShare.ReadWrite | FileShare.Delete,
            });

        IIPSArchiveEntry entry = archive.GetEntry(entryPath);
        byte[] plain = entry.ReadAllBytes();
        byte[] stored = entry.ReadStoredBytes();

        static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
        static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        Console.WriteLine($"Archive:          {archivePath}");
        Console.WriteLine($"Path:             {entryPath}");
        Console.WriteLine($"Index:            {entry.Index}");
        Console.WriteLine($"HET index:        {entry.HetIndex}");
        Console.WriteLine($"Name hash:        0x{entry.NameHash:X16}");
        Console.WriteLine($"Offset:           0x{entry.FileOffset:X}");
        Console.WriteLine($"File size:        {entry.Length}");
        Console.WriteLine($"Compressed size:  {entry.CompressedSize}");
        Console.WriteLine($"Stored length:    {entry.StoredLength}");
        Console.WriteLine($"Flags:            0x{(uint)entry.Flags:X8} ({entry.Flags})");
        Console.WriteLine($"Storage mode:     {entry.StorageMode}");
        Console.WriteLine($"Extra:            0x{entry.Extra:X16}");
        Console.WriteLine($"BET 128-bit:      {entry.Md5}");
        Console.WriteLine($"MD5 extracted:    {Md5Hex(plain)}");
        Console.WriteLine($"MD5 stored:       {Md5Hex(stored)}");
        Console.WriteLine($"SHA256 extracted: {Sha256Hex(plain)}");
        Console.WriteLine($"SHA256 stored:    {Sha256Hex(stored)}");
        Console.WriteLine($"Stored first 32:  {Convert.ToHexString(stored.AsSpan(0, Math.Min(32, stored.Length)))}");

        if (!entry.IsSingleUnit && stored.Length >= 8)
        {
            int sectorSize = checked((int)archive.Metadata.SectorSize);
            int sectorCount = (plain.Length + sectorSize - 1) / sectorSize;
            int tableCount = sectorCount + 1;
            if ((entry.Flags & IIPSArchiveEntryFlags.HasSectorCrc) != 0)
            {
                tableCount += sectorCount;
            }

            int tableBytes = checked(tableCount * 4);
            Console.WriteLine();
            Console.WriteLine($"Sector size:       {sectorSize}");
            Console.WriteLine($"Sector count:      {sectorCount}");
            Console.WriteLine($"Sector tbl count:  {tableCount}");

            if (entry.IsEncrypted)
            {
                Console.WriteLine("Sector table:      encrypted (raw markers omitted)");
            }
            else if (stored.Length >= tableBytes)
            {
                uint[] offsets = new uint[tableCount];
                for (int i = 0; i < tableCount; i++)
                {
                    offsets[i] = BitConverter.ToUInt32(stored, i * 4);
                }

                Console.WriteLine($"Sector table[0]:   0x{offsets[0]:X}");
                Console.WriteLine($"Sector table[last]:0x{offsets[Math.Min(sectorCount, offsets.Length - 1)]:X}");

                int shown = Math.Min(sectorCount, 16);
                for (int i = 0; i < shown; i++)
                {
                    uint start = offsets[i];
                    uint end = offsets[i + 1];
                    int rawSize = Math.Min(sectorSize, plain.Length - i * sectorSize);
                    string marker = start < stored.Length ? $"0x{stored[start]:X2}" : "(out)";
                    Console.WriteLine(
                        $"  sector[{i:D3}] 0x{start:X}-0x{end:X} len={end - start} raw={rawSize} marker={marker}");
                }
                if (sectorCount > shown)
                {
                    Console.WriteLine($"  ... {sectorCount - shown} more sectors");
                }
            }
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"inspect-entry failed: {ex}");
        return 7;
    }
}

static int TracePathCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("trace-path requires <IIPSFileList.lst> <archive-path> [--out <dir>].");
        return 2;
    }

    string listPath = Path.GetFullPath(args[0]);
    string entryPath = args[1].Replace('/', '\\');
    string? output = GetOption(args, "--out");
    if (output != null)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
    }

    if (!File.Exists(listPath))
    {
        Console.Error.WriteLine($"IIPS file list does not exist: {listPath}");
        return 2;
    }

    try
    {
        IIPSFileList fileList = IIPSFileList.Parse(listPath);
        IIPSFileListResolvedState state = fileList.Resolve();
        string root = Path.GetDirectoryName(listPath)!;
        string[] searchDirs = [root, Path.Combine(root, "iipsdownload")];
        List<string> loadOrder = state.AllFilesInOrder.ToList();

        Console.WriteLine($"IIPS list:        {listPath}");
        Console.WriteLine($"Version:          {state.Version}");
        Console.WriteLine($"Path:             {entryPath}");
        Console.WriteLine($"Active archives:  {loadOrder.Count}");
        Console.WriteLine();

        int foundCount = 0;
        for (int order = 0; order < loadOrder.Count; order++)
        {
            string fileName = loadOrder[order];
            string? foundArchive = searchDirs
                .Select(dir => Path.Combine(dir, fileName))
                .FirstOrDefault(File.Exists);
            if (foundArchive == null)
            {
                continue;
            }

            try
            {
                using IIPSArchive archive = IIPSArchive.Open(
                    foundArchive,
                    new IIPSArchiveOpenOptions
                    {
                        VerifyChecksums = false,
                        LoadListFile = false,
                        FileShare = FileShare.ReadWrite | FileShare.Delete,
                    });

                if (!archive.TryGetEntry(entryPath, out IIPSArchiveEntry? entry) || entry == null || !entry.Exists)
                {
                    continue;
                }

                foundCount++;
                byte[] data = entry.ReadAllBytes();
                string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                Console.WriteLine(
                    $"[{order:D3}] {fileName} index={entry.Index} offset=0x{entry.FileOffset:X} " +
                    $"size={entry.Length} stored={entry.StoredLength} flags=0x{(uint)entry.Flags:X8} " +
                    $"bet128={entry.Md5} sha256={sha}");

                if (output != null)
                {
                    string safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    string target = Path.Combine(output, $"{order:D3}_{safe}_{Path.GetFileName(entryPath)}");
                    File.WriteAllBytes(target, data);
                    Console.WriteLine($"      extracted -> {target}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{order:D3}] {fileName} open failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Versions containing path: {foundCount}");
        if (foundCount > 0)
        {
            Console.WriteLine("Later archives override earlier ones; the last hit is the runtime-visible resource.");
        }
        return foundCount > 0 ? 0 : 4;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"trace-path failed: {ex}");
        return 7;
    }
}

static int ScanCommand(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("scan requires <input-dir>.");
        return 2;
    }

    string root = Path.GetFullPath(args[0]);
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Input directory does not exist: {root}");
        return 2;
    }

    string output = Path.GetFullPath(
        GetOption(args, "--output")
        ?? Path.Combine(Environment.CurrentDirectory, "mho-cjk-strings.csv"));
    string priorityOutput = Path.Combine(
        Path.GetDirectoryName(output)!,
        Path.GetFileNameWithoutExtension(output) + "-priority.csv");

    HashSet<string> generatedOutputs = new(StringComparer.OrdinalIgnoreCase)
    {
        output,
        Path.GetFullPath(priorityOutput),
    };

    bool scanAll = args.Any(x => x.Equals("--all", StringComparison.OrdinalIgnoreCase));
    double maxMb = double.TryParse(GetOption(args, "--max-mb"), out double parsedMb) ? parsedMb : 16;
    long maxBytes = (long)(Math.Max(0.1, maxMb) * 1024 * 1024);

    HashSet<string> textExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".lua", ".txt", ".cfg", ".ini", ".csv", ".json", ".lst",
        ".html", ".htm", ".js", ".css", ".properties", ".loc", ".lang"
    };

    List<ScanRow> rows = new();
    int examined = 0;
    int textFiles = 0;
    int swfFiles = 0;
    int binaryUtfFiles = 0;
    int skippedLarge = 0;
    int skippedBinary = 0;
    int skippedGenerated = 0;
    int unreadable = 0;

    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string fullFile = Path.GetFullPath(file);
        if (generatedOutputs.Contains(fullFile))
        {
            skippedGenerated++;
            continue;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(file);
        }
        catch
        {
            unreadable++;
            continue;
        }

        if (info.Length > maxBytes)
        {
            skippedLarge++;
            continue;
        }

        if (!scanAll && !textExtensions.Contains(info.Extension) &&
            !info.Extension.Equals(".swf", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        examined++;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch
        {
            unreadable++;
            continue;
        }

        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

        if (SwfFile.IsSwf(file))
        {
            try
            {
                swfFiles++;
                ScanSwfStructured(rows, relative, file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SWF WARN] {relative}: {ex.Message}");
            }
            continue;
        }

        if (info.Extension.Equals(".dat", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                DatFile dat = new();
                dat.Open(bytes);

                if (dat.ContentType == DatFile.DatContentType.TSV && dat.Sheets.Count > 0)
                {
                    textFiles++;
                    ScanDatSheets(rows, relative, dat);
                }
                else
                {
                    string content = dat.Content ?? "";
                    if (ContainsCjk(content))
                    {
                        textFiles++;
                        ScanDatPlainContent(rows, relative, content);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DAT WARN] {relative}: {ex.Message}");
            }
            continue;
        }

        if (TryReadText(bytes, out string text, out string encodingName))
        {
            textFiles++;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!ContainsCjk(line))
                {
                    continue;
                }

                string source = NormalizeCandidate(line);
                if (source.Length == 0)
                {
                    continue;
                }

                rows.Add(new ScanRow(relative, $"line:{i + 1}", "text", "unknown", "normal", encodingName, source, 1, "", ""));
            }

            continue;
        }

        if (scanAll)
        {
            int before = rows.Count;
            AddBinaryCjkHits(rows, relative, bytes, "binary");
            if (rows.Count > before)
            {
                binaryUtfFiles++;
                continue;
            }
        }

        skippedBinary++;
    }

    // Collapse duplicate strings from the same source file/kind. SWFs often
    // contain the same ActionScript/UI string multiple times.
    List<ScanRow> collapsed = rows
        .GroupBy(r => new { r.Path, r.SourceKind, r.FieldRole, r.Priority, r.Encoding, r.Source })
        .Select(g =>
        {
            ScanRow first = g.First();
            int occurrences = g.Sum(x => x.Occurrences);
            string locations = string.Join(
                ";",
                g.Select(x => x.Location)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .Take(12));
            return first with { Location = locations, Occurrences = occurrences };
        })
        .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(r => r.Occurrences)
        .ThenBy(r => r.Source, StringComparer.Ordinal)
        .ToList();

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using (StreamWriter writer = new(output, false, new UTF8Encoding(true)))
    {
        writer.WriteLine("path,location,source_kind,field_role,priority,encoding,source,occurrences,translation,notes");
        foreach (ScanRow row in collapsed)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.Path),
                Csv(row.Location),
                Csv(row.SourceKind),
                Csv(row.FieldRole),
                Csv(row.Priority),
                Csv(row.Encoding),
                Csv(row.Source),
                row.Occurrences.ToString(),
                Csv(row.Translation),
                Csv(row.Notes)));
        }
    }

    Console.WriteLine($"Root:              {root}");
    Console.WriteLine($"Files examined:    {examined}");
    Console.WriteLine($"Text files:        {textFiles}");
    Console.WriteLine($"SWF files:         {swfFiles}");
    Console.WriteLine($"Other binary hits: {binaryUtfFiles}");
    Console.WriteLine($"CJK catalog rows:  {collapsed.Count}");
    Console.WriteLine($"CJK occurrences:   {collapsed.Sum(x => x.Occurrences)}");
    Console.WriteLine($"Skipped large:     {skippedLarge}");
    Console.WriteLine($"Skipped binary:    {skippedBinary}");
    Console.WriteLine($"Skipped generated: {skippedGenerated}");
    Console.WriteLine($"Unreadable:        {unreadable}");
    Console.WriteLine($"Output:            {output}");

    var topFiles = collapsed.GroupBy(r => r.Path)
        .Select(g => new { Path = g.Key, Count = g.Count(), Occurrences = g.Sum(x => x.Occurrences) })
        .OrderByDescending(x => x.Count)
        .Take(20)
        .ToList();

    if (topFiles.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Top files by remaining CJK catalog rows:");
        foreach (var item in topFiles)
        {
            Console.WriteLine($"{item.Count,6} rows  {item.Occurrences,6} hits  {item.Path}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Rows by translation priority:");
    foreach (var group in collapsed.GroupBy(x => x.Priority).OrderBy(x => x.Key))
    {
        Console.WriteLine($"{group.Key,-8} {group.Count(),8}");
    }

    var priorityGroups = collapsed
        .Where(x => x.Priority is "high" or "medium")
        .GroupBy(x => new { x.Source, x.FieldRole, x.Priority })
        .Select(g => new
        {
            g.Key.Source,
            g.Key.FieldRole,
            g.Key.Priority,
            Occurrences = g.Sum(x => x.Occurrences),
            First = g.First(),
        })
        .OrderBy(x => x.Priority == "high" ? 0 : 1)
        .ThenByDescending(x => x.Occurrences)
        .ThenBy(x => x.Source, StringComparer.Ordinal)
        .ToList();

    using (StreamWriter writer = new(priorityOutput, false, new UTF8Encoding(true)))
    {
        writer.WriteLine("source,field_role,priority,occurrences,example_path,example_location,translation,notes");
        foreach (var group in priorityGroups)
        {
            writer.WriteLine(string.Join(",",
                Csv(group.Source),
                Csv(group.FieldRole),
                Csv(group.Priority),
                group.Occurrences.ToString(),
                Csv(group.First.Path),
                Csv(group.First.Location),
                "",
                ""));
        }
    }

    Console.WriteLine($"Priority unique:    {priorityGroups.Count}");
    Console.WriteLine($"Priority catalog:   {priorityOutput}");

    return 0;
}

static void ScanDatSheets(List<ScanRow> rows, string relative, DatFile dat)
{
    foreach (TsvSheet sheet in dat.Sheets)
    {
        if (sheet.TableHead == null)
            continue;

        string sheetName = string.IsNullOrWhiteSpace(sheet.Name) ? "(unnamed)" : sheet.Name;

        // Headers are useful for reverse engineering but are normally metadata,
        // not player-facing strings.
        for (int c = 0; c < sheet.TableHead.Length; c++)
        {
            string header = NormalizeCandidate(sheet.TableHead[c] ?? "");
            if (header.Length > 0 && ContainsCjk(header))
            {
                rows.Add(new ScanRow(
                    relative,
                    $"sheet:{sheetName}/header:{c + 1}",
                    "dat-header",
                    "schema",
                    "low",
                    "utf-8",
                    header,
                    1,
                    "",
                    ""));
            }
        }

        if (sheet.Table == null)
            continue;

        for (int r = 0; r < sheet.Table.Length; r++)
        {
            string[] row = sheet.Table[r] ?? Array.Empty<string>();
            for (int col = 0; col < row.Length; col++)
            {
                string source = NormalizeCandidate(row[col] ?? "");
                if (source.Length == 0 || !ContainsCjk(source))
                    continue;

                string header = col < sheet.TableHead.Length
                    ? NormalizeCandidate(sheet.TableHead[col] ?? "")
                    : $"col{col + 1}";

                (string role, string priority) = ClassifyDatField(header);

                rows.Add(new ScanRow(
                    relative,
                    $"sheet:{sheetName}/row:{r + 1}/col:{col + 1}:{header}",
                    "dat-cell",
                    role,
                    priority,
                    "utf-8",
                    source,
                    1,
                    "",
                    ""));
            }
        }
    }
}

static void ScanDatPlainContent(List<ScanRow> rows, string relative, string content)
{
    string trimmed = content.TrimEnd('\0').Trim();

    if (trimmed.StartsWith("<", StringComparison.Ordinal))
    {
        try
        {
            System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(trimmed);
            int index = 0;
            foreach (System.Xml.Linq.XElement element in doc.Descendants())
            {
                if (element.HasElements)
                    continue;

                string source = NormalizeCandidate(element.Value);
                if (source.Length == 0 || !ContainsCjk(source))
                    continue;

                string field = element.Name.LocalName;
                (string role, string priority) = ClassifyDatField(field);

                rows.Add(new ScanRow(
                    relative,
                    $"xml:{BuildXmlPath(element)}/value:{index++}",
                    "dat-xml",
                    role,
                    priority,
                    "utf-8",
                    source,
                    1,
                    "",
                    ""));
            }
            return;
        }
        catch
        {
            // Fall through to line scan if this decrypted content is not valid XML.
        }
    }

    string[] lines = trimmed.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        string source = NormalizeCandidate(lines[i]);
        if (source.Length == 0 || !ContainsCjk(source))
            continue;

        rows.Add(new ScanRow(
            relative,
            $"dat-line:{i + 1}",
            "dat-decrypted",
            "unknown",
            "normal",
            "utf-8",
            source,
            1,
            "",
            ""));
    }
}

static string BuildXmlPath(System.Xml.Linq.XElement element)
{
    Stack<string> parts = new();
    System.Xml.Linq.XElement? current = element;
    while (current != null)
    {
        string id = current.Attribute("Id")?.Value ?? "";
        parts.Push(string.IsNullOrEmpty(id) ? current.Name.LocalName : $"{current.Name.LocalName}[{id}]");
        current = current.Parent;
    }
    return string.Join("/", parts);
}

static (string Role, string Priority) ClassifyDatField(string header)
{
    string h = header.Trim();

    string[] highName =
    [
        "Name", "名字", "名称", "物品名称", "配方名称", "怪物名称", "LevelName",
        "NPC名称", "技能名称", "任务名称", "标题", "Title"
    ];
    string[] highDescription =
    [
        "Description", "说明", "描述", "Note", "CompleteNote", "备注", "文本",
        "Text", "Message", "消息", "对白", "对话", "内容"
    ];
    string[] medium =
    [
        "提示", "Tip", "Help", "帮助", "奖励名称", "Buff名称", "技能说明",
        "效果说明", "猎团", "系统"
    ];

    if (highName.Any(x => h.Equals(x, StringComparison.OrdinalIgnoreCase) || h.Contains(x, StringComparison.OrdinalIgnoreCase)))
        return ("name-title", "high");

    if (highDescription.Any(x => h.Equals(x, StringComparison.OrdinalIgnoreCase) || h.Contains(x, StringComparison.OrdinalIgnoreCase)))
        return ("description-message", "high");

    if (medium.Any(x => h.Equals(x, StringComparison.OrdinalIgnoreCase) || h.Contains(x, StringComparison.OrdinalIgnoreCase)))
        return ("player-facing", "medium");

    // Numeric/config columns sometimes have Chinese headers, but CJK in their
    // cell values can still be enums or developer metadata. Keep them, lower priority.
    return ("unknown", "normal");
}

static void ScanSwfStructured(List<ScanRow> rows, string relative, string file)
{
    SwfFile swf = SwfFile.Open(file);

    foreach (SwfTag tag in swf.Tags)
    {
        if (tag.Code == 82) // DoABC
        {
            ScanDoAbcTag(rows, relative, tag);
            continue;
        }

        // These tags contain explicit UTF-8 strings in the SWF format.
        if (tag.Code is 43 or 56 or 76 or 77 or 88)
        {
            AddNullTerminatedUtf8Strings(rows, relative, tag.Data.Span, $"swf-{tag.Name}", $"tag:{tag.Index}");
        }
    }
}

static void ScanDoAbcTag(List<ScanRow> rows, string relative, SwfTag tag)
{
    ReadOnlySpan<byte> data = tag.Data.Span;
    if (data.Length < 5)
        return;

    int offset = 4; // DoABC flags
    string abcName = ReadNullTerminatedUtf8(data, ref offset);
    if (offset >= data.Length)
        return;

    ReadOnlySpan<byte> abc = data.Slice(offset);
    if (abc.Length < 4)
        return;

    int p = 4; // minor_version + major_version

    // int pool
    uint intCount = ReadU30(abc, ref p);
    for (uint i = 1; i < intCount; i++) SkipU32(abc, ref p);

    // uint pool
    uint uintCount = ReadU30(abc, ref p);
    for (uint i = 1; i < uintCount; i++) SkipU32(abc, ref p);

    // double pool
    uint doubleCount = ReadU30(abc, ref p);
    if (doubleCount > 0)
    {
        long bytes = (long)(doubleCount - 1) * 8;
        if (bytes > abc.Length - p)
            throw new InvalidDataException("ABC double pool exceeds tag length.");
        p += (int)bytes;
    }

    // string pool
    uint stringCount = ReadU30(abc, ref p);
    for (uint i = 1; i < stringCount; i++)
    {
        uint len = ReadU30(abc, ref p);
        if (len > (uint)(abc.Length - p))
            throw new InvalidDataException("ABC string exceeds tag length.");

        ReadOnlySpan<byte> raw = abc.Slice(p, checked((int)len));
        p += checked((int)len);

        string value;
        try
        {
            value = new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            continue;
        }

        value = NormalizeCandidate(value);
        if (value.Length == 0 || value.Length > 1024 || !ContainsCjk(value))
            continue;

        rows.Add(new ScanRow(
            relative,
            $"tag:{tag.Index}/abc:{abcName}/str:{i}",
            "swf-abc",
            "ui-string",
            "high",
            "utf-8",
            value,
            1,
            "",
            ""));
    }
}

static uint ReadU30(ReadOnlySpan<byte> data, ref int offset)
{
    uint value = 0;
    int shift = 0;

    for (int i = 0; i < 5; i++)
    {
        if (offset >= data.Length)
            throw new EndOfStreamException("Unexpected EOF in ABC U30.");

        byte b = data[offset++];
        value |= (uint)(b & 0x7F) << shift;
        if ((b & 0x80) == 0)
            return value & 0x3FFFFFFF;

        shift += 7;
    }

    return value & 0x3FFFFFFF;
}

static void SkipU32(ReadOnlySpan<byte> data, ref int offset)
{
    for (int i = 0; i < 5; i++)
    {
        if (offset >= data.Length)
            throw new EndOfStreamException("Unexpected EOF in ABC integer pool.");

        byte b = data[offset++];
        if ((b & 0x80) == 0)
            return;
    }
}

static void AddNullTerminatedUtf8Strings(
    List<ScanRow> rows,
    string relative,
    ReadOnlySpan<byte> data,
    string sourceKind,
    string locationPrefix)
{
    int start = 0;
    int index = 0;

    while (start < data.Length)
    {
        int end = start;
        while (end < data.Length && data[end] != 0)
            end++;

        if (end > start)
        {
            try
            {
                string value = new UTF8Encoding(false, true).GetString(data.Slice(start, end - start));
                value = NormalizeCandidate(value);
                if (value.Length > 0 && value.Length <= 1024 && ContainsCjk(value) && LooksLikeUsefulBinaryString(value))
                {
                    rows.Add(new ScanRow(relative, $"{locationPrefix}/str:{index}", sourceKind, "ui-string", "high", "utf-8", value, 1, "", ""));
                }
            }
            catch (DecoderFallbackException)
            {
            }
        }

        index++;
        start = end + 1;
    }
}

static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> data, ref int offset)
{
    int start = offset;
    while (offset < data.Length && data[offset] != 0)
        offset++;

    string result = Encoding.UTF8.GetString(data.Slice(start, offset - start));
    if (offset < data.Length)
        offset++;
    return result;
}

static bool TryDecodeSwf(byte[] bytes, out byte[] payload, out string kind)
{
    payload = Array.Empty<byte>();
    kind = "";

    if (bytes.Length < 8)
    {
        return false;
    }

    if (bytes[0] == (byte)'F' && bytes[1] == (byte)'W' && bytes[2] == (byte)'S')
    {
        payload = bytes;
        kind = "fws";
        return true;
    }

    if (bytes[0] == (byte)'C' && bytes[1] == (byte)'W' && bytes[2] == (byte)'S')
    {
        try
        {
            using MemoryStream input = new(bytes, 8, bytes.Length - 8, writable: false);
            using ZLibStream zlib = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();

            // Keep a synthetic uncompressed SWF header so diagnostics and
            // downstream tooling can still recognize the payload shape.
            output.WriteByte((byte)'F');
            output.WriteByte((byte)'W');
            output.WriteByte((byte)'S');
            output.WriteByte(bytes[3]);
            output.Write(bytes, 4, 4);
            zlib.CopyTo(output);

            payload = output.ToArray();
            kind = "cws";
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ZWS uses LZMA. We identify it so it is not mistaken for arbitrary text,
    // but .NET does not provide an in-box LZMA decoder.
    if (bytes[0] == (byte)'Z' && bytes[1] == (byte)'W' && bytes[2] == (byte)'S')
    {
        kind = "zws";
        return false;
    }

    return false;
}

static void AddBinaryCjkHits(List<ScanRow> rows, string relative, byte[] bytes, string sourceKind)
{
    // Generic binary fallback is deliberately UTF-8 only. Treating arbitrary
    // byte pairs as UTF-16 produced millions of false CJK positives.
    AddDecodedBinaryHits(rows, relative, bytes, sourceKind, new UTF8Encoding(false, true), "utf-8");
}

static void AddDecodedBinaryHits(
    List<ScanRow> rows,
    string relative,
    byte[] bytes,
    string sourceKind,
    Encoding encoding,
    string encodingName)
{
    string decoded;
    try
    {
        decoded = encoding.GetString(bytes);
    }
    catch (DecoderFallbackException)
    {
        return;
    }
    catch
    {
        return;
    }

    int start = 0;
    for (int i = 0; i <= decoded.Length; i++)
    {
        bool boundary = i == decoded.Length || IsBinaryStringBoundary(decoded[i]);
        if (!boundary)
        {
            continue;
        }

        int length = i - start;
        if (length > 0)
        {
            string candidate = NormalizeCandidate(decoded.Substring(start, length));
            if (candidate.Length >= 1 &&
                candidate.Length <= 512 &&
                ContainsCjk(candidate) &&
                LooksLikeUsefulBinaryString(candidate))
            {
                rows.Add(new ScanRow(
                    relative,
                    $"char:{start}",
                    sourceKind,
                    "binary-string",
                    "low",
                    encodingName,
                    candidate,
                    1,
                    "",
                    ""));
            }
        }

        start = i + 1;
    }
}

static bool IsBinaryStringBoundary(char c)
{
    return c == '\0' ||
           c == '\uFFFD' ||
           (char.IsControl(c) && c is not '\t') ||
           char.IsSurrogate(c);
}

static bool LooksLikeUsefulBinaryString(string value)
{
    if (value.Length == 0)
    {
        return false;
    }

    int visible = 0;
    int weird = 0;

    foreach (Rune rune in value.EnumerateRunes())
    {
        int v = rune.Value;
        if (Rune.IsControl(rune))
        {
            weird += 3;
        }
        else if ((v >= 0x20 && v <= 0x7E) || ContainsCjkRune(v) || Rune.IsLetterOrDigit(rune) || Rune.IsPunctuation(rune) || Rune.IsWhiteSpace(rune))
        {
            visible++;
        }
        else
        {
            weird++;
        }
    }

    return visible > 0 && weird <= Math.Max(2, visible / 3);
}

static string NormalizeCandidate(string value)
{
    StringBuilder sb = new(value.Length);
    bool previousSpace = false;

    foreach (char c in value.Trim())
    {
        if (char.IsWhiteSpace(c))
        {
            if (!previousSpace)
            {
                sb.Append(' ');
                previousSpace = true;
            }
            continue;
        }

        previousSpace = false;
        sb.Append(c);
    }

    return sb.ToString().Trim();
}

static int CompareCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("compare requires <original-dir> <patched-dir>.");
        return 2;
    }

    string originalRoot = Path.GetFullPath(args[0]);
    string patchedRoot = Path.GetFullPath(args[1]);
    if (!Directory.Exists(originalRoot) || !Directory.Exists(patchedRoot))
    {
        Console.Error.WriteLine("Both compare directories must exist.");
        return 2;
    }

    string output = GetOption(args, "--output")
                    ?? Path.Combine(Environment.CurrentDirectory, "mho-patch-diff.csv");

    Dictionary<string, string> original = BuildFileMap(originalRoot);
    Dictionary<string, string> patched = BuildFileMap(patchedRoot);
    SortedSet<string> all = new(original.Keys, StringComparer.OrdinalIgnoreCase);
    all.UnionWith(patched.Keys);

    List<CompareRow> rows = new();
    foreach (string relative in all)
    {
        bool inOriginal = original.TryGetValue(relative, out string? originalPath);
        bool inPatched = patched.TryGetValue(relative, out string? patchedPath);

        string state;
        string originalSha = "";
        string patchedSha = "";

        if (!inOriginal)
        {
            state = "added-by-patch";
            patchedSha = Sha256(patchedPath!);
        }
        else if (!inPatched)
        {
            state = "missing-from-patch-tree";
            originalSha = Sha256(originalPath!);
        }
        else
        {
            originalSha = Sha256(originalPath!);
            patchedSha = Sha256(patchedPath!);
            state = originalSha == patchedSha ? "unchanged" : "changed";
        }

        rows.Add(new CompareRow(relative, state, originalSha, patchedSha));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using (StreamWriter writer = new(output, false, new UTF8Encoding(true)))
    {
        writer.WriteLine("path,state,original_sha256,patched_sha256");
        foreach (CompareRow row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.Path),
                Csv(row.State),
                Csv(row.OriginalSha256),
                Csv(row.PatchedSha256)));
        }
    }

    foreach (var group in rows.GroupBy(r => r.State).OrderBy(g => g.Key))
    {
        Console.WriteLine($"{group.Key,-24} {group.Count(),8}");
    }

    Console.WriteLine($"Output: {Path.GetFullPath(output)}");
    return 0;
}

static Dictionary<string, string> BuildFileMap(string root)
{
    Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        map[relative] = file;
    }

    return map;
}

static string Sha256(string file)
{
    using FileStream stream = File.OpenRead(file);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static bool TryReadText(byte[] bytes, out string text, out string encodingName)
{
    text = "";
    encodingName = "";

    if (bytes.Length == 0)
    {
        text = "";
        encodingName = "empty";
        return true;
    }

    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
    {
        text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        encodingName = "utf-8-bom";
        return true;
    }

    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
    {
        text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        encodingName = "utf-16le";
        return true;
    }

    if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
    {
        text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        encodingName = "utf-16be";
        return true;
    }

    try
    {
        UTF8Encoding strictUtf8 = new(false, true);
        text = strictUtf8.GetString(bytes);
        if (LooksLikeText(text))
        {
            encodingName = "utf-8";
            return true;
        }
    }
    catch (DecoderFallbackException)
    {
    }

    try
    {
        Encoding gb18030 = Encoding.GetEncoding(
            "GB18030",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        text = gb18030.GetString(bytes);
        if (LooksLikeText(text))
        {
            encodingName = "gb18030";
            return true;
        }
    }
    catch
    {
    }

    text = "";
    encodingName = "";
    return false;
}

static bool LooksLikeText(string value)
{
    if (value.Length == 0)
    {
        return true;
    }

    int controls = 0;
    foreach (char c in value)
    {
        if (c == '\0')
        {
            controls += 4;
            continue;
        }

        if (char.IsControl(c) && c is not '\r' and not '\n' and not '\t' and not '\f')
        {
            controls++;
        }
    }

    return (double)controls / value.Length < 0.02;
}

static bool ContainsCjk(string value)
{
    foreach (Rune rune in value.EnumerateRunes())
    {
        if (ContainsCjkRune(rune.Value))
        {
            return true;
        }
    }

    return false;
}

static bool ContainsCjkRune(int v)
{
    return (v >= 0x3400 && v <= 0x4DBF) ||
           (v >= 0x4E00 && v <= 0x9FFF) ||
           (v >= 0xF900 && v <= 0xFAFF) ||
           (v >= 0x3040 && v <= 0x309F) ||
           (v >= 0x30A0 && v <= 0x30FF) ||
           (v >= 0xFF66 && v <= 0xFF9D) ||
           (v >= 0x20000 && v <= 0x2FA1F);
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static string Csv(string value)
{
    if (value.Contains('"'))
    {
        value = value.Replace(new string('"', 1), new string('"', 2));
    }

    return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? string.Concat('"', value, '"') : value;
}

readonly record struct ScanRow(
    string Path,
    string Location,
    string SourceKind,
    string FieldRole,
    string Priority,
    string Encoding,
    string Source,
    int Occurrences,
    string Translation,
    string Notes);
readonly record struct CompareRow(string Path, string State, string OriginalSha256, string PatchedSha256);
