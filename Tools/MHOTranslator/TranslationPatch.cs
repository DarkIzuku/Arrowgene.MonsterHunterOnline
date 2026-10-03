using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Arrowgene.MonsterHunterOnline.ClientTools.Dat;
using Arrowgene.MonsterHunterOnline.ClientTools.Flash;
using Arrowgene.MonsterHunterOnline.ClientTools.IIPS;

internal static class TranslationPatch
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static int ApplyCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("apply requires <input-dir> <catalog.csv> [--out <patched-dir>].");
            return 2;
        }

        string inputRoot = Path.GetFullPath(args[0]);
        string catalogPath = Path.GetFullPath(args[1]);
        string outputRoot = Path.GetFullPath(
            GetOption(args, "--out") ??
            Path.Combine(Environment.CurrentDirectory, "mho-es-patched"));
        bool onlySwf = args.Any(x => x.Equals("--only-swf", StringComparison.OrdinalIgnoreCase));
        string? onlyPath = GetOption(args, "--only-path");
        string? onlySource = GetOption(args, "--only-source");
        string? overrideTranslation = GetOption(args, "--override-translation");
        bool swfInPlaceEqual = args.Any(x => x.Equals("--swf-inplace-equal", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(onlyPath))
        {
            onlyPath = onlyPath.Replace('\\', '/').TrimStart('/');
        }

        if (!Directory.Exists(inputRoot))
        {
            Console.Error.WriteLine($"Input directory does not exist: {inputRoot}");
            return 2;
        }

        if (!File.Exists(catalogPath) && !Directory.Exists(catalogPath))
        {
            Console.Error.WriteLine($"Catalog file/directory does not exist: {catalogPath}");
            return 2;
        }

        Dictionary<string, string> translations;
        try
        {
            translations = LoadTranslations(catalogPath);
            if (!string.IsNullOrWhiteSpace(onlySource))
            {
                if (!translations.TryGetValue(onlySource, out string? selectedTranslation))
                {
                    Console.Error.WriteLine($"Requested --only-source string is not present as status=translate: {onlySource}");
                    return 3;
                }

                translations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [onlySource] = string.IsNullOrEmpty(overrideTranslation) ? selectedTranslation : overrideTranslation,
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load translation catalog: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"Input:        {inputRoot}");
        Console.WriteLine($"Catalog:      {catalogPath}");
        Console.WriteLine($"Translations: {translations.Count}");
        Console.WriteLine($"Output:       {outputRoot}");
        Console.WriteLine($"Mode:         {(onlySwf ? "SWF/UI only" : "all supported resources")}");
        if (!string.IsNullOrWhiteSpace(onlyPath))
        {
            Console.WriteLine($"Only path:    {onlyPath}");
        }
        if (!string.IsNullOrWhiteSpace(onlySource))
        {
            Console.WriteLine($"Only source:  {EscapeForLog(onlySource)}");
        }
        if (!string.IsNullOrEmpty(overrideTranslation))
        {
            Console.WriteLine($"Override:     {EscapeForLog(overrideTranslation)}");
        }
        if (swfInPlaceEqual)
        {
            Console.WriteLine("SWF mode:     equal-length in-place ABC patch");
        }

        Directory.CreateDirectory(outputRoot);

        int examined = 0;
        int modifiedFiles = 0;
        int replacements = 0;
        int datFiles = 0;
        int swfFiles = 0;
        int textFiles = 0;
        int failures = 0;

        foreach (string file in Directory.EnumerateFiles(inputRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(inputRoot, file).Replace('\\', '/');
            string extension = Path.GetExtension(file).ToLowerInvariant();

            if (onlySwf && extension != ".swf")
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(onlyPath) &&
                !relative.Equals(onlyPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            examined++;

            try
            {
                byte[] original = File.ReadAllBytes(file);
                byte[]? patched = null;
                int fileReplacements = 0;

                if (extension == ".dat")
                {
                    patched = PatchDat(original, translations, out fileReplacements);
                    datFiles++;
                }
                else if (extension == ".swf" && SwfFile.IsSwf(file))
                {
                    patched = swfInPlaceEqual
                        ? PatchSwfInPlaceEqualLength(original, relative, translations, out fileReplacements)
                        : PatchSwf(original, relative, translations, out fileReplacements);
                    swfFiles++;
                }
                else if (IsTextExtension(extension))
                {
                    patched = PatchTextFile(original, translations, out fileReplacements);
                    textFiles++;
                }

                if (patched == null || fileReplacements == 0)
                {
                    continue;
                }

                string target = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, patched);

                modifiedFiles++;
                replacements += fileReplacements;
                string sizeInfo = extension == ".swf"
                    ? $", bytes {original.Length} -> {patched.Length} ({patched.Length - original.Length:+#;-#;0})"
                    : string.Empty;
                Console.WriteLine($"[PATCH] {relative} ({fileReplacements} replacements{sizeInfo})");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[FAIL] {relative}: {ex.Message}");
            }
        }

        string manifest = Path.Combine(outputRoot, "_translation-manifest.txt");
        File.WriteAllText(
            manifest,
            $"catalog={catalogPath}{Environment.NewLine}" +
            $"translations={translations.Count}{Environment.NewLine}" +
            $"examined={examined}{Environment.NewLine}" +
            $"modified_files={modifiedFiles}{Environment.NewLine}" +
            $"replacements={replacements}{Environment.NewLine}" +
            $"dat_files={datFiles}{Environment.NewLine}" +
            $"swf_files={swfFiles}{Environment.NewLine}" +
            $"text_files={textFiles}{Environment.NewLine}" +
            $"failures={failures}{Environment.NewLine}",
            Utf8NoBom);

        Console.WriteLine();
        Console.WriteLine($"Examined:       {examined}");
        Console.WriteLine($"Modified files: {modifiedFiles}");
        Console.WriteLine($"Replacements:   {replacements}");
        Console.WriteLine($"Failures:       {failures}");
        Console.WriteLine($"Manifest:       {manifest}");

        return failures == 0 ? 0 : 10;
    }

    public static int InspectIfsCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("inspect-ifs requires <archive.ifs>.");
            return 2;
        }

        string path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Archive does not exist: {path}");
            return 2;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0xAC)
            {
                Console.Error.WriteLine("Archive is smaller than the 0xAC nIFS header.");
                return 3;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x00, 4));
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x04, 4));
            ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x08, 2));
            ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x0A, 2));
            ulong archiveSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x0C, 8));
            ulong betPos = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x14, 8));
            ulong hetPos = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x1C, 8));
            ulong md5TablePos = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x24, 8));
            ulong bitmapPos = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x2C, 8));
            ulong hetSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x34, 8));
            ulong betSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x3C, 8));
            ulong md5TableSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x44, 8));
            ulong bitmapSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x4C, 8));
            uint md5PieceSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x54, 4));
            uint rawChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x58, 4));

            string rawHash0 = Convert.ToHexString(bytes.AsSpan(0x5C, 16)).ToLowerInvariant();
            string rawHash1 = Convert.ToHexString(bytes.AsSpan(0x6C, 16)).ToLowerInvariant();
            string betHash = Convert.ToHexString(bytes.AsSpan(0x7C, 16)).ToLowerInvariant();
            string hetHash = Convert.ToHexString(bytes.AsSpan(0x8C, 16)).ToLowerInvariant();
            string headerHash = Convert.ToHexString(bytes.AsSpan(0x9C, 16)).ToLowerInvariant();

            string Calc(ulong offset, ulong length)
            {
                if (length == 0)
                    return "(empty)";
                if (offset > (ulong)bytes.LongLength || length > (ulong)bytes.LongLength - offset)
                    return "(out-of-range)";
                return Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(
                        bytes.AsSpan(checked((int)offset), checked((int)length))))
                    .ToLowerInvariant();
            }

            string computedHeader = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(bytes.AsSpan(0, 0x9C)))
                .ToLowerInvariant();

            Console.WriteLine($"Path:             {path}");
            Console.WriteLine($"File length:      {bytes.LongLength}");
            Console.WriteLine($"Magic:            0x{magic:X8}");
            Console.WriteLine($"Header size:      0x{headerSize:X} ({headerSize})");
            Console.WriteLine($"Format version:   {formatVersion}");
            Console.WriteLine($"Sector shift:     {sectorShift}");
            Console.WriteLine($"Archive size:     {archiveSize}");
            Console.WriteLine($"HET:              pos=0x{hetPos:X} size={hetSize}");
            Console.WriteLine($"BET:              pos=0x{betPos:X} size={betSize}");
            Console.WriteLine($"MD5 table:        pos=0x{md5TablePos:X} size={md5TableSize}");
            Console.WriteLine($"Bitmap:           pos=0x{bitmapPos:X} size={bitmapSize}");
            Console.WriteLine($"MD5 piece size:   {md5PieceSize}");
            Console.WriteLine($"Raw chunk size:   {rawChunkSize}");
            Console.WriteLine();
            Console.WriteLine("Stored 0xAC header hashes:");
            Console.WriteLine($"  hash[0] @5C:    {rawHash0}");
            Console.WriteLine($"  hash[1] @6C:    {rawHash1}");
            Console.WriteLine($"  BET     @7C:    {betHash}");
            Console.WriteLine($"  HET     @8C:    {hetHash}");
            Console.WriteLine($"  Header  @9C:    {headerHash}");
            Console.WriteLine();
            Console.WriteLine("Recomputed raw-region MD5:");
            Console.WriteLine($"  MD5 table:      {Calc(md5TablePos, md5TableSize)}");
            Console.WriteLine($"  Bitmap:         {Calc(bitmapPos, bitmapSize)}");
            Console.WriteLine($"  BET:            {Calc(betPos, betSize)}");
            Console.WriteLine($"  HET:            {Calc(hetPos, hetSize)}");
            Console.WriteLine($"  Header[0..9B]:  {computedHeader}");

            if (md5PieceSize != 0)
            {
                ulong chunkCount = (archiveSize + md5PieceSize - 1) / md5PieceSize;
                Console.WriteLine();
                Console.WriteLine($"Archive chunks:   {chunkCount}");
                Console.WriteLine($"chunks*16:        {chunkCount * 16}");
                Console.WriteLine($"(chunks+1)*16:    {(chunkCount + 1) * 16}");
            }

            if (bitmapSize != 0 && bitmapPos + bitmapSize <= (ulong)bytes.LongLength)
            {
                ReadOnlySpan<byte> bitmap = bytes.AsSpan(checked((int)bitmapPos), checked((int)bitmapSize));
                long nonZero = 0;
                long ff = 0;
                foreach (byte b in bitmap)
                {
                    if (b != 0) nonZero++;
                    if (b == 0xFF) ff++;
                }
                Console.WriteLine($"Bitmap nonzero:   {nonZero}/{bitmap.Length}");
                Console.WriteLine($"Bitmap 0xFF:      {ff}/{bitmap.Length}");
                Console.WriteLine($"Bitmap first 32:  {Convert.ToHexString(bitmap.Slice(0, Math.Min(32, bitmap.Length)))}");
            }

            if (md5TableSize != 0 && md5TablePos + md5TableSize <= (ulong)bytes.LongLength)
            {
                ReadOnlySpan<byte> table = bytes.AsSpan(checked((int)md5TablePos), checked((int)md5TableSize));
                Console.WriteLine($"MD5 first 32:     {Convert.ToHexString(table.Slice(0, Math.Min(32, table.Length)))}");
                if (table.Length >= 32)
                {
                    Console.WriteLine($"MD5 last 32:      {Convert.ToHexString(table.Slice(table.Length - 32, 32))}");
                }

                if (md5PieceSize > 0 && table.Length % 16 == 0)
                {
                    int tableEntries = table.Length / 16;
                    int chunkCount = checked((int)((archiveSize + md5PieceSize - 1) / md5PieceSize));
                    int directMatches = 0;
                    int plusOneMatches = 0;
                    int minusOneMatches = 0;

                    byte[]? firstChunkHash = null;
                    byte[]? lastChunkHash = null;

                    for (int i = 0; i < chunkCount; i++)
                    {
                        ulong chunkOffset = (ulong)i * md5PieceSize;
                        int chunkLength = checked((int)Math.Min((ulong)md5PieceSize, archiveSize - chunkOffset));
                        byte[] chunkHash = System.Security.Cryptography.MD5.HashData(
                            bytes.AsSpan(checked((int)chunkOffset), chunkLength));

                        if (i == 0) firstChunkHash = chunkHash;
                        if (i == chunkCount - 1) lastChunkHash = chunkHash;

                        if (i < tableEntries &&
                            table.Slice(i * 16, 16).SequenceEqual(chunkHash))
                        {
                            directMatches++;
                        }

                        if (i + 1 < tableEntries &&
                            table.Slice((i + 1) * 16, 16).SequenceEqual(chunkHash))
                        {
                            plusOneMatches++;
                        }

                        if (i > 0 && i - 1 < tableEntries &&
                            table.Slice((i - 1) * 16, 16).SequenceEqual(chunkHash))
                        {
                            minusOneMatches++;
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("MD5 table mapping test:");
                    Console.WriteLine($"  table entries:       {tableEntries}");
                    Console.WriteLine($"  archive chunks:      {chunkCount}");
                    Console.WriteLine($"  table[i] == chunk[i]:     {directMatches}/{chunkCount}");
                    Console.WriteLine($"  table[i+1] == chunk[i]:   {plusOneMatches}/{chunkCount}");
                    Console.WriteLine($"  table[i-1] == chunk[i]:   {minusOneMatches}/{Math.Max(0, chunkCount - 1)}");

                    if (firstChunkHash != null)
                        Console.WriteLine($"  chunk[0] MD5:        {Convert.ToHexString(firstChunkHash)}");
                    if (lastChunkHash != null)
                        Console.WriteLine($"  chunk[last] MD5:     {Convert.ToHexString(lastChunkHash)}");

                    byte[] mainArchiveMd5 = System.Security.Cryptography.MD5.HashData(
                        bytes.AsSpan(0, checked((int)archiveSize)));
                    Console.WriteLine($"  main archive MD5:    {Convert.ToHexString(mainArchiveMd5)}");

                    byte[] tableMd5 = System.Security.Cryptography.MD5.HashData(table);
                    Console.WriteLine($"  MD5 table MD5:       {Convert.ToHexString(tableMd5)}");

                    if (tableEntries > chunkCount)
                    {
                        Console.WriteLine($"  extra first entry:   {Convert.ToHexString(table.Slice(0, 16))}");
                        Console.WriteLine($"  extra last entry:    {Convert.ToHexString(table.Slice((tableEntries - 1) * 16, 16))}");

                        byte[] tableWithoutFirstMd5 = System.Security.Cryptography.MD5.HashData(table.Slice(16));
                        byte[] tableWithoutLastMd5 = System.Security.Cryptography.MD5.HashData(table.Slice(0, table.Length - 16));
                        Console.WriteLine($"  MD5(table[1..]):     {Convert.ToHexString(tableWithoutFirstMd5)}");
                        Console.WriteLine($"  MD5(table[..last]):  {Convert.ToHexString(tableWithoutLastMd5)}");

                        if (bitmapSize != 0 && bitmapPos + bitmapSize <= (ulong)bytes.LongLength)
                        {
                            ReadOnlySpan<byte> bitmap2 = bytes.AsSpan(checked((int)bitmapPos), checked((int)bitmapSize));
                            byte[] bitmapHash = System.Security.Cryptography.MD5.HashData(bitmap2);
                            Console.WriteLine($"  bitmap MD5:          {Convert.ToHexString(bitmapHash)}");
                        }
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IFS inspection failed: {ex}");
            return 7;
        }
    }

    public static int CloneIfsCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("clone-ifs requires <base.ifs> [--out <output.ifs>].");
            return 2;
        }

        string baseIfs = Path.GetFullPath(args[0]);
        string outputIfs = Path.GetFullPath(
            GetOption(args, "--out") ??
            Path.Combine(
                Path.GetDirectoryName(baseIfs)!,
                Path.GetFileNameWithoutExtension(baseIfs) + "_clone.ifs"));

        if (!File.Exists(baseIfs))
        {
            Console.Error.WriteLine($"Base IFS does not exist: {baseIfs}");
            return 2;
        }

        Console.WriteLine($"Base IFS:   {baseIfs}");
        Console.WriteLine($"Output IFS: {outputIfs}");
        Console.WriteLine("Mode:       no-op archive rebuild (zero modified entries)");

        try
        {
            Dictionary<string, byte[]> originalEntries = new(StringComparer.OrdinalIgnoreCase);

            using (IIPSArchive archive = IIPSArchive.Open(
                       baseIfs,
                       new IIPSArchiveOpenOptions
                       {
                           VerifyChecksums = true,
                           LoadListFile = true,
                           FileShare = FileShare.ReadWrite | FileShare.Delete,
                       }))
            {
                foreach (IIPSArchiveEntry entry in archive.Entries)
                {
                    if (!entry.Exists || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.ArchivePath))
                    {
                        continue;
                    }

                    originalEntries[entry.ArchivePath!] = entry.ReadAllBytes();
                }

                archive.Save(
                    outputIfs,
                    new IIPSArchiveSaveOptions
                    {
                        // Preserve every original entry and raw archive layout.
                        IncludeListFile = false,
                        PreserveUnchangedEntries = true,
                        PreserveOriginalLayout = true,
                    });
            }

            int verified = 0;
            int mismatched = 0;

            using (IIPSArchive verify = IIPSArchive.Open(
                       outputIfs,
                       new IIPSArchiveOpenOptions
                       {
                           VerifyChecksums = true,
                           LoadListFile = true,
                           FileShare = FileShare.Read,
                       }))
            {
                foreach ((string path, byte[] expected) in originalEntries)
                {
                    if (!verify.TryGetEntry(path, out IIPSArchiveEntry? entry) || entry == null)
                    {
                        Console.Error.WriteLine($"[MISSING] {path}");
                        mismatched++;
                        continue;
                    }

                    byte[] actual = entry.ReadAllBytes();
                    if (!actual.AsSpan().SequenceEqual(expected))
                    {
                        Console.Error.WriteLine($"[MISMATCH] {path}");
                        mismatched++;
                        continue;
                    }

                    verified++;
                }
            }

            string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baseIfs)));
            string cloneHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(outputIfs)));

            Console.WriteLine($"Verified entries: {verified}");
            Console.WriteLine($"Mismatches:       {mismatched}");
            Console.WriteLine($"Original SHA256:  {originalHash}");
            Console.WriteLine($"Clone SHA256:     {cloneHash}");
            Console.WriteLine($"Byte-identical:   {string.Equals(originalHash, cloneHash, StringComparison.OrdinalIgnoreCase)}");
            Console.WriteLine($"Built:            {outputIfs}");

            return mismatched == 0 ? 0 : 10;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IFS clone failed: {ex}");
            return 7;
        }
    }

    public static int BuildIfsCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("build-ifs requires <base.ifs> <patched-dir> [--out <output.ifs>].");
            return 2;
        }

        string baseIfs = Path.GetFullPath(args[0]);
        string patchedRoot = Path.GetFullPath(args[1]);
        string outputIfs = Path.GetFullPath(
            GetOption(args, "--out") ??
            Path.Combine(
                Path.GetDirectoryName(baseIfs)!,
                Path.GetFileNameWithoutExtension(baseIfs) + "_es.ifs"));

        if (!File.Exists(baseIfs))
        {
            Console.Error.WriteLine($"Base IFS does not exist: {baseIfs}");
            return 2;
        }

        if (!Directory.Exists(patchedRoot))
        {
            Console.Error.WriteLine($"Patched directory does not exist: {patchedRoot}");
            return 2;
        }

        List<(string Path, byte[] Data)> changes = [];
        foreach (string file in Directory.EnumerateFiles(patchedRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(patchedRoot, file).Replace('/', '\\');
            if (relative.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add((relative, File.ReadAllBytes(file)));
        }

        if (changes.Count == 0)
        {
            Console.Error.WriteLine("No patched files were found.");
            return 4;
        }

        Console.WriteLine($"Base IFS:      {baseIfs}");
        Console.WriteLine($"Patched files: {changes.Count}");
        Console.WriteLine($"Output IFS:    {outputIfs}");

        try
        {
            using (IIPSArchive archive = IIPSArchive.Open(
                       baseIfs,
                       new IIPSArchiveOpenOptions
                       {
                           VerifyChecksums = true,
                           LoadListFile = true,
                           FileShare = FileShare.ReadWrite | FileShare.Delete,
                       }))
            {
                int replaced = 0;
                int missing = 0;

                foreach ((string archivePath, byte[] data) in changes)
                {
                    if (!archive.TryGetEntry(archivePath, out IIPSArchiveEntry? entry) || entry == null)
                    {
                        Console.Error.WriteLine($"[MISS] {archivePath}");
                        missing++;
                        continue;
                    }

                    archive.Replace(entry, data);
                    replaced++;
                }

                if (replaced == 0)
                {
                    Console.Error.WriteLine("None of the patched files exist in the base archive.");
                    return 5;
                }

                archive.Save(
                    outputIfs,
                    new IIPSArchiveSaveOptions
                    {
                        IncludeListFile = false,
                        PreserveUnchangedEntries = true,
                        PreserveOriginalLayout = true,
                    });

                Console.WriteLine($"Replaced:      {replaced}");
                Console.WriteLine($"Missing:       {missing}");
            }

            // Reopen and verify every modified entry byte-for-byte.
            using (IIPSArchive verify = IIPSArchive.Open(
                       outputIfs,
                       new IIPSArchiveOpenOptions
                       {
                           VerifyChecksums = true,
                           LoadListFile = true,
                           FileShare = FileShare.Read,
                       }))
            {
                int verified = 0;
                foreach ((string archivePath, byte[] expected) in changes)
                {
                    if (!verify.TryGetEntry(archivePath, out IIPSArchiveEntry? entry) || entry == null)
                    {
                        continue;
                    }

                    byte[] actual = entry.ReadAllBytes();
                    if (!actual.AsSpan().SequenceEqual(expected))
                    {
                        throw new InvalidDataException($"Verification mismatch after rebuild: {archivePath}");
                    }

                    string expectedMd5 = Convert.ToHexString(MD5.HashData(expected)).ToLowerInvariant();
                    if (!string.Equals(entry.Md5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"BET entry digest mismatch after rebuild: {archivePath}; " +
                            $"stored={entry.Md5}, expected={expectedMd5}.");
                    }

                    verified++;
                }

                Console.WriteLine($"Verified:      {verified}");
            }

            Console.WriteLine($"Built:         {outputIfs}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IFS rebuild failed: {ex}");
            return 7;
        }
    }

    private static Dictionary<string, string> LoadTranslations(string path)
    {
        List<string> catalogs = File.Exists(path)
            ? [path]
            : Directory.EnumerateFiles(path, "*.csv", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (catalogs.Count == 0)
        {
            throw new InvalidDataException("No CSV translation catalogs were found.");
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (string catalog in catalogs)
        {
            List<string[]> rows = ParseCsv(File.ReadAllText(catalog));
            if (rows.Count == 0)
            {
                continue;
            }

            string[] header = rows[0];
            int sourceCol = FindColumn(header, "source");
            int translationCol = FindColumn(header, "translation");
            int statusCol = FindColumn(header, "status");

            // Ignore analytical CSVs such as remaining-cjk.csv. A translation
            // catalog must explicitly expose source + translation columns.
            if (sourceCol < 0 || translationCol < 0)
            {
                continue;
            }

            for (int i = 1; i < rows.Count; i++)
            {
                string[] row = rows[i];
                if (sourceCol >= row.Length || translationCol >= row.Length)
                {
                    continue;
                }

                string source = row[sourceCol];
                string translation = row[translationCol];
                string status = statusCol >= 0 && statusCol < row.Length ? row[statusCol] : "translate";

                if (!status.Equals("translate", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(source) ||
                    string.IsNullOrEmpty(translation) ||
                    source == translation)
                {
                    continue;
                }

                if (result.TryGetValue(source, out string? existing) && existing != translation)
                {
                    throw new InvalidDataException(
                        $"Conflicting translations for '{source}': '{existing}' vs '{translation}' (catalog {catalog}).");
                }

                result[source] = translation;
            }
        }

        return result;
    }

    private static byte[]? PatchDat(
        byte[] original,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        replacements = 0;
        if (original.Length < 4)
        {
            return null;
        }

        bool plainTsv = original.AsSpan(0, 4).SequenceEqual("#TSV"u8);
        byte[] plain;
        byte[]? header = null;
        byte[]? originalCipher = null;

        if (plainTsv)
        {
            plain = original;
        }
        else
        {
            if (original.Length < DatFile.DatHeaderLength ||
                (original.Length - DatFile.DatHeaderLength) % 16 != 0)
            {
                return null;
            }

            header = original.AsSpan(0, (int)DatFile.DatHeaderLength).ToArray();
            originalCipher = original.AsSpan((int)DatFile.DatHeaderLength).ToArray();
            plain = DatFile.DecryptDat(originalCipher);

            // Never write a format we cannot round-trip exactly.
            byte[] roundTrip = DatFile.EncryptDat(plain);
            if (!roundTrip.AsSpan().SequenceEqual(originalCipher))
            {
                throw new InvalidDataException("DAT crypto round-trip verification failed.");
            }
        }

        int contentLength = plain.Length;
        while (contentLength > 0 && plain[contentLength - 1] == 0)
        {
            contentLength--;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(plain, 0, contentLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        string patchedText;
        if (text.StartsWith("#TSV", StringComparison.Ordinal))
        {
            patchedText = PatchTabSeparatedText(text, translations, out replacements);
        }
        else if (text.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            patchedText = PatchXmlLeafText(text, translations, out replacements);
        }
        else
        {
            patchedText = PatchPhysicalLines(text, translations, out replacements);
        }

        if (replacements == 0)
        {
            return null;
        }

        byte[] patchedPlain = Utf8NoBom.GetBytes(patchedText);
        if (plainTsv)
        {
            return patchedPlain;
        }

        int paddedLength = (patchedPlain.Length + 15) & ~15;
        byte[] padded = new byte[paddedLength];
        patchedPlain.CopyTo(padded, 0);
        byte[] cipher = DatFile.EncryptDat(padded);

        byte[] result = new byte[header!.Length + cipher.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(cipher, 0, result, header.Length, cipher.Length);

        // The third DAT header field behaves like an AES-block count in the
        // files where it matches the payload. Update only when that relation is
        // already true; otherwise preserve the unknown original value.
        uint oldChunkCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint oldBlockCount = (uint)(originalCipher!.Length / 16);
        if (oldChunkCount == oldBlockCount)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), (uint)(cipher.Length / 16));
        }

        // Verify the newly produced payload before returning it.
        byte[] verifyPlain = DatFile.DecryptDat(cipher);
        if (!verifyPlain.AsSpan(0, patchedPlain.Length).SequenceEqual(patchedPlain))
        {
            throw new InvalidDataException("DAT write verification failed.");
        }

        return result;
    }

    private static byte[]? PatchSwfInPlaceEqualLength(
        byte[] original,
        string name,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        replacements = 0;
        SwfFile swf = SwfFile.Open(original, name);
        byte[] uncompressed = swf.GetUncompressedBytes();
        byte[] patchedFws = (byte[])uncompressed.Clone();

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
            int abcOffset = offset;
            ReadOnlySpan<byte> abc = tagData.Slice(abcOffset);
            if (abc.Length < 4)
            {
                continue;
            }

            int p = 4;
            uint intCount = ReadU30(abc, ref p);
            for (uint i = 1; i < intCount; i++) SkipU32(abc, ref p);

            uint uintCount = ReadU30(abc, ref p);
            for (uint i = 1; i < uintCount; i++) SkipU32(abc, ref p);

            uint doubleCount = ReadU30(abc, ref p);
            if (doubleCount > 0)
            {
                int doubleBytes = checked((int)((doubleCount - 1) * 8));
                if (doubleBytes > abc.Length - p)
                {
                    throw new InvalidDataException("ABC double pool exceeds tag.");
                }
                p += doubleBytes;
            }

            uint stringCount = ReadU30(abc, ref p);
            for (uint i = 1; i < stringCount; i++)
            {
                uint length = ReadU30(abc, ref p);
                int stringLength = checked((int)length);
                if (stringLength > abc.Length - p)
                {
                    throw new InvalidDataException("ABC string exceeds tag.");
                }

                ReadOnlySpan<byte> raw = abc.Slice(p, stringLength);
                try
                {
                    string source = StrictUtf8.GetString(raw);
                    if (translations.TryGetValue(source, out string? translation))
                    {
                        byte[] replacement = Utf8NoBom.GetBytes(translation);
                        if (replacement.Length != stringLength)
                        {
                            throw new InvalidDataException(
                                $"Equal-length SWF mode requires identical UTF-8 byte length: " +
                                $"source={EscapeForLog(source)} ({stringLength}), " +
                                $"replacement={EscapeForLog(translation)} ({replacement.Length}).");
                        }

                        int absolute = checked(tag.Offset + tag.HeaderLength + abcOffset + p);
                        replacement.CopyTo(patchedFws.AsSpan(absolute, stringLength));
                        replacements++;
                        Console.WriteLine(
                            $"[SWF-INPLACE] tag=82 source={EscapeForLog(source)} " +
                            $"replacement={EscapeForLog(translation)} bytes={stringLength} offset=0x{absolute:X}");
                    }
                }
                catch (DecoderFallbackException)
                {
                }

                p += stringLength;
            }
        }

        if (replacements == 0)
        {
            return null;
        }

        // The decompressed SWF structure is byte-for-byte identical except for the
        // selected equal-length string bytes. No tag length, U30 or ABC offset changes.
        if (swf.Compression == SwfCompression.Uncompressed)
        {
            _ = SwfFile.Open(patchedFws, name + ":verify-inplace");
            return patchedFws;
        }

        if (swf.Compression != SwfCompression.Zlib)
        {
            throw new NotSupportedException($"Cannot rebuild {swf.Compression} SWF: {name}");
        }

        using MemoryStream compressed = new();
        compressed.WriteByte((byte)'C');
        compressed.WriteByte((byte)'W');
        compressed.WriteByte((byte)'S');
        compressed.WriteByte(patchedFws[3]);
        compressed.Write(patchedFws, 4, 4);
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(patchedFws, 8, patchedFws.Length - 8);
        }

        byte[] result = compressed.ToArray();
        _ = SwfFile.Open(result, name + ":verify-inplace");
        return result;
    }

    private static byte[]? PatchSwf(
        byte[] original,
        string name,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        replacements = 0;
        SwfFile swf = SwfFile.Open(original, name);
        byte[] uncompressed = swf.GetUncompressedBytes();
        if (swf.Tags.Count == 0)
        {
            return null;
        }

        int firstTagOffset = swf.Tags[0].Offset;
        using MemoryStream rebuilt = new();
        rebuilt.Write(uncompressed, 0, firstTagOffset);

        int lastOriginalEnd = firstTagOffset;
        foreach (SwfTag tag in swf.Tags)
        {
            if (tag.Offset > lastOriginalEnd)
            {
                rebuilt.Write(uncompressed, lastOriginalEnd, tag.Offset - lastOriginalEnd);
            }

            byte[] data = tag.Data.ToArray();
            byte[] patchedData = PatchSwfTagData(tag.Code, data, translations, out int tagReplacements, out List<string> tagSources);
            foreach (string source in tagSources)
            {
                Console.WriteLine($"[SWF-MATCH] tag={tag.Code} source={EscapeForLog(source)}");
            }
            replacements += tagReplacements;

            int originalTagLength = tag.HeaderLength + checked((int)tag.Length);
            if (tagReplacements == 0)
            {
                // Keep untouched tags byte-for-byte, including their original short/long
                // header representation. Old Scaleform builds can be stricter than our parser.
                rebuilt.Write(uncompressed, tag.Offset, originalTagLength);
            }
            else
            {
                WriteSwfTag(rebuilt, tag.Code, patchedData);
            }

            lastOriginalEnd = tag.Offset + originalTagLength;
        }

        if (lastOriginalEnd < uncompressed.Length)
        {
            rebuilt.Write(uncompressed, lastOriginalEnd, uncompressed.Length - lastOriginalEnd);
        }

        if (replacements == 0)
        {
            return null;
        }

        byte[] rebuiltFws = rebuilt.ToArray();
        rebuiltFws[0] = (byte)'F';
        rebuiltFws[1] = (byte)'W';
        rebuiltFws[2] = (byte)'S';
        BinaryPrimitives.WriteUInt32LittleEndian(rebuiltFws.AsSpan(4, 4), (uint)rebuiltFws.Length);

        if (swf.Compression == SwfCompression.Uncompressed)
        {
            // Parse once more as a structural verification.
            _ = SwfFile.Open(rebuiltFws, name + ":verify");
            return rebuiltFws;
        }

        if (swf.Compression != SwfCompression.Zlib)
        {
            throw new NotSupportedException($"Cannot rebuild {swf.Compression} SWF: {name}");
        }

        using MemoryStream compressed = new();
        compressed.WriteByte((byte)'C');
        compressed.WriteByte((byte)'W');
        compressed.WriteByte((byte)'S');
        compressed.WriteByte(rebuiltFws[3]);
        compressed.Write(rebuiltFws, 4, 4);

        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(rebuiltFws, 8, rebuiltFws.Length - 8);
        }

        byte[] result = compressed.ToArray();
        _ = SwfFile.Open(result, name + ":verify");
        return result;
    }

    private static byte[] PatchSwfTagData(
        ushort code,
        byte[] data,
        IReadOnlyDictionary<string, string> translations,
        out int replacements,
        out List<string> matchedSources)
    {
        matchedSources = new List<string>();
        byte[] result = code switch
        {
            82 => PatchDoAbc(data, translations, out replacements, matchedSources),
            43 or 77 => PatchSingleSwfStringTag(data, 0, translations, out replacements, matchedSources),
            56 or 76 => PatchSymbolStringMap(data, translations, out replacements, matchedSources),
            88 => PatchFontNameTag(data, translations, out replacements, matchedSources),
            _ => ReturnUnchanged(data, out replacements),
        };
        return result;
    }

    private static byte[] ReturnUnchanged(byte[] data, out int replacements)
    {
        replacements = 0;
        return data;
    }

    private static byte[] PatchDoAbc(
        byte[] tagData,
        IReadOnlyDictionary<string, string> translations,
        out int replacements,
        List<string> matchedSources)
    {
        replacements = 0;
        if (tagData.Length < 5)
        {
            return tagData;
        }

        int offset = 4;
        while (offset < tagData.Length && tagData[offset] != 0)
        {
            offset++;
        }

        if (offset >= tagData.Length)
        {
            return tagData;
        }

        offset++; // terminating null after ABC name
        int abcOffset = offset;
        ReadOnlySpan<byte> abc = tagData.AsSpan(abcOffset);
        if (abc.Length < 4)
        {
            return tagData;
        }

        int p = 4;
        uint intCount = ReadU30(abc, ref p);
        for (uint i = 1; i < intCount; i++) SkipU32(abc, ref p);

        uint uintCount = ReadU30(abc, ref p);
        for (uint i = 1; i < uintCount; i++) SkipU32(abc, ref p);

        uint doubleCount = ReadU30(abc, ref p);
        if (doubleCount > 0)
        {
            int doubleBytes = checked((int)((doubleCount - 1) * 8));
            if (doubleBytes > abc.Length - p)
            {
                throw new InvalidDataException("ABC double pool exceeds tag.");
            }
            p += doubleBytes;
        }

        uint stringCount = ReadU30(abc, ref p);
        int stringsStart = p;

        List<byte[]> strings = new();
        for (uint i = 1; i < stringCount; i++)
        {
            uint length = ReadU30(abc, ref p);
            if (length > (uint)(abc.Length - p))
            {
                throw new InvalidDataException("ABC string exceeds tag.");
            }

            byte[] raw = abc.Slice(p, checked((int)length)).ToArray();
            p += checked((int)length);

            byte[] output = raw;
            try
            {
                string source = StrictUtf8.GetString(raw);
                if (translations.TryGetValue(source, out string? translation))
                {
                    output = Utf8NoBom.GetBytes(translation);
                    replacements++;
                    matchedSources.Add(source);
                }
            }
            catch (DecoderFallbackException)
            {
            }

            strings.Add(output);
        }

        if (replacements == 0)
        {
            return tagData;
        }

        int stringsEnd = p;
        using MemoryStream abcOut = new();
        abcOut.Write(abc.Slice(0, stringsStart));

        foreach (byte[] str in strings)
        {
            WriteU30(abcOut, (uint)str.Length);
            abcOut.Write(str);
        }

        abcOut.Write(abc.Slice(stringsEnd));

        using MemoryStream tagOut = new();
        tagOut.Write(tagData, 0, abcOffset);
        abcOut.Position = 0;
        abcOut.CopyTo(tagOut);
        return tagOut.ToArray();
    }

    private static byte[] PatchSingleSwfStringTag(
        byte[] data,
        int stringOffset,
        IReadOnlyDictionary<string, string> translations,
        out int replacements,
        List<string> matchedSources)
    {
        replacements = 0;
        if (!TryReadNullString(data, stringOffset, out string source, out int after))
        {
            return data;
        }

        if (!translations.TryGetValue(source, out string? translation))
        {
            return data;
        }

        replacements = 1;
        matchedSources.Add(source);
        using MemoryStream output = new();
        output.Write(data, 0, stringOffset);
        byte[] translated = Utf8NoBom.GetBytes(translation);
        output.Write(translated);
        output.WriteByte(0);
        output.Write(data, after, data.Length - after);
        return output.ToArray();
    }

    private static byte[] PatchSymbolStringMap(
        byte[] data,
        IReadOnlyDictionary<string, string> translations,
        out int replacements,
        List<string> matchedSources)
    {
        replacements = 0;
        if (data.Length < 2)
        {
            return data;
        }

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        int p = 2;
        using MemoryStream output = new();
        output.Write(data, 0, 2);

        for (int i = 0; i < count; i++)
        {
            if (p + 2 > data.Length)
            {
                return data;
            }

            output.Write(data, p, 2);
            p += 2;

            if (!TryReadNullString(data, p, out string source, out int after))
            {
                return data;
            }

            string value = translations.TryGetValue(source, out string? translation)
                ? translation
                : source;

            if (value != source)
            {
                replacements++;
                matchedSources.Add(source);
            }

            byte[] encoded = Utf8NoBom.GetBytes(value);
            output.Write(encoded);
            output.WriteByte(0);
            p = after;
        }

        if (p < data.Length)
        {
            output.Write(data, p, data.Length - p);
        }

        return replacements == 0 ? data : output.ToArray();
    }

    private static byte[] PatchFontNameTag(
        byte[] data,
        IReadOnlyDictionary<string, string> translations,
        out int replacements,
        List<string> matchedSources)
    {
        replacements = 0;
        if (data.Length < 3)
        {
            return data;
        }

        // DefineFontName: UI16 FontID, STRING FontName, STRING Copyright.
        int p = 2;
        using MemoryStream output = new();
        output.Write(data, 0, 2);

        for (int field = 0; field < 2 && p < data.Length; field++)
        {
            if (!TryReadNullString(data, p, out string source, out int after))
            {
                return data;
            }

            string value = translations.TryGetValue(source, out string? translation)
                ? translation
                : source;

            if (value != source)
            {
                replacements++;
                matchedSources.Add(source);
            }

            byte[] encoded = Utf8NoBom.GetBytes(value);
            output.Write(encoded);
            output.WriteByte(0);
            p = after;
        }

        if (p < data.Length)
        {
            output.Write(data, p, data.Length - p);
        }

        return replacements == 0 ? data : output.ToArray();
    }

    private static byte[]? PatchTextFile(
        byte[] original,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        replacements = 0;
        string text;
        try
        {
            text = StrictUtf8.GetString(original);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        string patched = text.TrimStart().StartsWith("<", StringComparison.Ordinal)
            ? PatchXmlLeafText(text, translations, out replacements)
            : PatchPhysicalLines(text, translations, out replacements);

        return replacements == 0 ? null : Utf8NoBom.GetBytes(patched);
    }

    private static string PatchTabSeparatedText(
        string text,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        int count = 0;
        string result = TransformPhysicalLines(text, line =>
        {
            if (!line.Contains('\t'))
            {
                return line;
            }

            string[] cells = line.Split('\t');
            bool changed = false;
            for (int i = 0; i < cells.Length; i++)
            {
                if (TryTranslateExact(cells[i], translations, out string translated))
                {
                    cells[i] = translated;
                    count++;
                    changed = true;
                }
            }

            return changed ? string.Join('\t', cells) : line;
        });
        replacements = count;
        return result;
    }

    private static string PatchPhysicalLines(
        string text,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        int count = 0;
        string result = TransformPhysicalLines(text, line =>
        {
            if (!TryTranslateExact(line, translations, out string translated))
            {
                return line;
            }

            count++;
            return translated;
        });
        replacements = count;
        return result;
    }

    private static string PatchXmlLeafText(
        string text,
        IReadOnlyDictionary<string, string> translations,
        out int replacements)
    {
        // Preserve exact file formatting whenever possible by replacing only
        // complete text-node values between markup boundaries.
        replacements = 0;
        StringBuilder output = new(text.Length);
        int p = 0;

        while (p < text.Length)
        {
            int open = text.IndexOf('>', p);
            if (open < 0)
            {
                output.Append(text, p, text.Length - p);
                break;
            }

            output.Append(text, p, open - p + 1);
            int nextTag = text.IndexOf('<', open + 1);
            if (nextTag < 0)
            {
                output.Append(text, open + 1, text.Length - open - 1);
                break;
            }

            string value = text.Substring(open + 1, nextTag - open - 1);
            if (TryTranslateExact(value, translations, out string translated))
            {
                output.Append(translated);
                replacements++;
            }
            else
            {
                output.Append(value);
            }

            p = nextTag;
        }

        return output.ToString();
    }

    private static bool TryTranslateExact(
        string value,
        IReadOnlyDictionary<string, string> translations,
        out string translated)
    {
        if (translations.TryGetValue(value, out string? exact))
        {
            translated = exact;
            return true;
        }

        int left = 0;
        while (left < value.Length && char.IsWhiteSpace(value[left]))
        {
            left++;
        }

        int right = value.Length;
        while (right > left && char.IsWhiteSpace(value[right - 1]))
        {
            right--;
        }

        if (left == 0 && right == value.Length)
        {
            translated = value;
            return false;
        }

        string core = value.Substring(left, right - left);
        if (!translations.TryGetValue(core, out string? replacement))
        {
            translated = value;
            return false;
        }

        translated = value.Substring(0, left) + replacement + value.Substring(right);
        return true;
    }

    private static string TransformPhysicalLines(string text, Func<string, string> transform)
    {
        StringBuilder output = new(text.Length);
        int start = 0;

        while (start < text.Length)
        {
            int p = start;
            while (p < text.Length && text[p] != '\r' && text[p] != '\n')
            {
                p++;
            }

            output.Append(transform(text.Substring(start, p - start)));

            if (p >= text.Length)
            {
                break;
            }

            if (text[p] == '\r' && p + 1 < text.Length && text[p + 1] == '\n')
            {
                output.Append("\r\n");
                start = p + 2;
            }
            else
            {
                output.Append(text[p]);
                start = p + 1;
            }
        }

        if (text.Length == 0)
        {
            return text;
        }

        return output.ToString();
    }

    private static string EscapeForLog(string value)
    {
        StringBuilder output = new();
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value >= 0x20 && rune.Value <= 0x7E)
            {
                output.Append((char)rune.Value);
            }
            else if (rune.Value <= 0xFFFF)
            {
                output.Append($"\\u{rune.Value:X4}");
            }
            else
            {
                output.Append($"\\U{rune.Value:X8}");
            }
        }
        return output.ToString();
    }

    private static bool TryReadNullString(byte[] data, int offset, out string value, out int after)
    {
        value = "";
        after = offset;
        int end = offset;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        if (end >= data.Length)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(data, offset, end - offset);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        after = end + 1;
        return true;
    }

    private static void WriteSwfTag(Stream output, ushort code, byte[] data)
    {
        if (data.Length < 0x3F)
        {
            ushort header = (ushort)((code << 6) | data.Length);
            Span<byte> raw = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(raw, header);
            output.Write(raw);
        }
        else
        {
            ushort header = (ushort)((code << 6) | 0x3F);
            Span<byte> raw = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(raw.Slice(0, 2), header);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.Slice(2, 4), (uint)data.Length);
            output.Write(raw);
        }

        output.Write(data);
    }

    private static uint ReadU30(ReadOnlySpan<byte> data, ref int offset)
    {
        uint value = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            if (offset >= data.Length)
            {
                throw new EndOfStreamException("Unexpected EOF in U30.");
            }

            byte b = data[offset++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value & 0x3FFFFFFF;
            }

            shift += 7;
        }

        return value & 0x3FFFFFFF;
    }

    private static void SkipU32(ReadOnlySpan<byte> data, ref int offset)
    {
        for (int i = 0; i < 5; i++)
        {
            if (offset >= data.Length)
            {
                throw new EndOfStreamException("Unexpected EOF in U32.");
            }

            if ((data[offset++] & 0x80) == 0)
            {
                return;
            }
        }
    }

    private static void WriteU30(Stream output, uint value)
    {
        value &= 0x3FFFFFFF;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            output.WriteByte(b);
        } while (value != 0);
    }

    private static int FindColumn(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string[]> ParseCsv(string text)
    {
        List<string[]> rows = [];
        List<string> row = [];
        StringBuilder field = new();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static bool IsTextExtension(string extension)
    {
        return extension is ".xml" or ".lua" or ".txt" or ".cfg" or ".ini" or ".csv" or ".json" or ".lst" or ".html" or ".htm" or ".js" or ".css" or ".properties" or ".loc" or ".lang";
    }

    private static string? GetOption(string[] args, string name)
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
}
