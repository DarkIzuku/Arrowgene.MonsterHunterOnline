using Arrowgene.MonsterHunterOnline.ClientTools.IIPS;
using System.Security.Cryptography;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

return args.Length == 0 ? ShowHelp() : args[0].ToLowerInvariant() switch
{
    "extract-ifs" => ExtractIfsCommand(args.Skip(1).ToArray()),
    "scan" => ScanCommand(args.Skip(1).ToArray()),
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

    string output = GetOption(args, "--output")
                    ?? Path.Combine(Environment.CurrentDirectory, "mho-cjk-strings.csv");
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
    int skippedLarge = 0;
    int skippedBinary = 0;
    int unreadable = 0;

    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
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

        if (!scanAll && !textExtensions.Contains(info.Extension))
        {
            continue;
        }

        examined++;

        if (!TryReadText(file, out string text, out string encodingName))
        {
            skippedBinary++;
            continue;
        }

        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!ContainsCjk(line))
            {
                continue;
            }

            rows.Add(new ScanRow(relative, i + 1, encodingName, line.Trim(), ""));
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using (StreamWriter writer = new(output, false, new UTF8Encoding(true)))
    {
        writer.WriteLine("path,line,encoding,source,translation");
        foreach (ScanRow row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.Path),
                row.Line.ToString(),
                Csv(row.Encoding),
                Csv(row.Source),
                Csv(row.Translation)));
        }
    }

    Console.WriteLine($"Root:           {root}");
    Console.WriteLine($"Files examined: {examined}");
    Console.WriteLine($"CJK strings:    {rows.Count}");
    Console.WriteLine($"Skipped large:  {skippedLarge}");
    Console.WriteLine($"Skipped binary: {skippedBinary}");
    Console.WriteLine($"Unreadable:     {unreadable}");
    Console.WriteLine($"Output:         {Path.GetFullPath(output)}");

    var topFiles = rows.GroupBy(r => r.Path)
        .OrderByDescending(g => g.Count())
        .Take(15)
        .ToList();

    if (topFiles.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Top files by untranslated CJK lines:");
        foreach (var group in topFiles)
        {
            Console.WriteLine($"{group.Count(),6}  {group.Key}");
        }
    }

    return 0;
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

static bool TryReadText(string file, out string text, out string encodingName)
{
    text = "";
    encodingName = "";

    byte[] bytes;
    try
    {
        bytes = File.ReadAllBytes(file);
    }
    catch
    {
        return false;
    }

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
        int v = rune.Value;
        if ((v >= 0x3400 && v <= 0x4DBF) ||
            (v >= 0x4E00 && v <= 0x9FFF) ||
            (v >= 0xF900 && v <= 0xFAFF) ||
            (v >= 0x3040 && v <= 0x309F) ||
            (v >= 0x30A0 && v <= 0x30FF) ||
            (v >= 0xFF66 && v <= 0xFF9D) ||
            (v >= 0x20000 && v <= 0x2FA1F))
        {
            return true;
        }
    }

    return false;
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

readonly record struct ScanRow(string Path, int Line, string Encoding, string Source, string Translation);
readonly record struct CompareRow(string Path, string State, string OriginalSha256, string PatchedSha256);
