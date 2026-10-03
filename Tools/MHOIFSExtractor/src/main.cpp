#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>
#include <algorithm>
#include <cstdint>
#include <cstring>

namespace fs = std::filesystem;

struct Offsets
{
    // Profile recovered from the user's Monster Hunter Online IFS2.dll:
    // SHA-256 69d1a8fa9df64149779c42fa19d7194f1917ba968e91efe4af2d535e21663d25
    // PE TimeDateStamp 0x533A60DB, SizeOfImage 0x95000.
    //
    // These are wrapper entry points. The older public IFS Tool offsets land
    // inside different functions for this binary and cause 0xC0000005.
    uintptr_t SFileOpenArchive = 0x16370;
    uintptr_t SFileExtractFile = 0x25940;
    uintptr_t SFileCloseFile = 0x20FB0;
    uintptr_t SFileReadFile = 0x220C0;
    uintptr_t NIFSOpenFileEx = 0x1FA20;
    uintptr_t OpenArchiveCore = 0x176A0;
};

using SFileOpenArchive_t = HANDLE(__stdcall*)(const char*, int);
using SFileExtractFile_t = bool(__stdcall*)(const char*, const char*);
using SFileCloseFile_t = bool(__stdcall*)();
using SFileReadFile_t = void(__stdcall*)(DWORD, LPDWORD, int);
using NIFSOpenFileEx_t = bool(__stdcall*)(HANDLE, const char*, DWORD, HANDLE*, DWORD);

static SFileOpenArchive_t gOpenArchive = nullptr;
static SFileExtractFile_t gExtractFile = nullptr;
static SFileCloseFile_t gCloseFile = nullptr;
static SFileReadFile_t gReadFile = nullptr;
static NIFSOpenFileEx_t gOpenFile = nullptr;
static void* gOpenArchiveCore = nullptr;

static std::string WideToUtf8(const std::wstring& value)
{
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                   nullptr, 0, nullptr, nullptr);
    std::string out(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                        out.data(), size, nullptr, nullptr);
    return out;
}

static std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                   nullptr, 0);
    std::wstring out(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                        out.data(), size);
    return out;
}

static uintptr_t ParseHex(const std::wstring& value)
{
    std::wstring s = value;
    if (s.rfind(L"0x", 0) == 0 || s.rfind(L"0X", 0) == 0)
        s = s.substr(2);
    return static_cast<uintptr_t>(std::stoull(s, nullptr, 16));
}

static bool IsExecutableOffset(HMODULE module, uintptr_t offset)
{
    auto base = reinterpret_cast<const uint8_t*>(module);
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
        return false;

    auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return false;

    const IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section)
    {
        uintptr_t start = section->VirtualAddress;
        uintptr_t size = std::max<uintptr_t>(section->Misc.VirtualSize, section->SizeOfRawData);
        uintptr_t end = start + size;

        if (offset >= start && offset < end)
            return (section->Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
    }

    return false;
}

static void WINAPI IFS2DebugSink(const char* message)
{
    if (!message) return;
    std::cout << "[IFS2DBG] " << message;
    size_t len = std::strlen(message);
    if (len == 0 || message[len - 1] != '\n')
        std::cout << "\n";
    std::cout.flush();
}

static bool InstallDebugShim(HMODULE module)
{
#if defined(_M_IX86)
    // Verified against the user's MHO IFS2.dll profile:
    // SHA-256 69d1a8fa9df64149779c42fa19d7194f1917ba968e91efe4af2d535e21663d25
    //
    // The historical public extractor used RVA 0x8CBE0 for another build.
    // This MHO build keeps the debug interface at 0x8BBD8 and the debug
    // enable byte at 0x8BBBF.
    constexpr uintptr_t DebugInterfaceRva = 0x8BBD8;
    constexpr uintptr_t DebugEnabledRva = 0x8BBBF;

    auto base = reinterpret_cast<uint8_t*>(module);
    auto interfaceSlot = reinterpret_cast<DWORD*>(base + DebugInterfaceRva);
    auto enabledSlot = reinterpret_cast<BYTE*>(base + DebugEnabledRva);

    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(interfaceSlot, &mbi, sizeof(mbi)) != sizeof(mbi) ||
        mbi.State != MEM_COMMIT)
    {
        std::cerr << "[IFS2] Debug interface slot is not mapped.\n";
        return false;
    }

    static DWORD debugFunction = reinterpret_cast<DWORD>(&IFS2DebugSink);
    static DWORD debugVtable = reinterpret_cast<DWORD>(&debugFunction);

    DWORD oldProtectInterface = 0;
    if (!VirtualProtect(interfaceSlot, sizeof(DWORD), PAGE_READWRITE, &oldProtectInterface))
    {
        std::cerr << "[IFS2] VirtualProtect failed for debug interface. Win32="
                  << GetLastError() << "\n";
        return false;
    }

    *interfaceSlot = reinterpret_cast<DWORD>(&debugVtable);

    DWORD ignored = 0;
    VirtualProtect(interfaceSlot, sizeof(DWORD), oldProtectInterface, &ignored);

    DWORD oldProtectEnabled = 0;
    if (!VirtualProtect(enabledSlot, sizeof(BYTE), PAGE_READWRITE, &oldProtectEnabled))
    {
        std::cerr << "[IFS2] VirtualProtect failed for debug flag. Win32="
                  << GetLastError() << "\n";
        return false;
    }

    *enabledSlot = 1;
    VirtualProtect(enabledSlot, sizeof(BYTE), oldProtectEnabled, &ignored);

    std::cout << "[IFS2] Installed verified debug interface at RVA 0x"
              << std::hex << DebugInterfaceRva
              << " and enabled logging at RVA 0x" << DebugEnabledRva
              << std::dec << "\n";
    return true;
#else
    return false;
#endif
}

static bool OpenArchiveCoreCompat(
    const char* archivePath,
    DWORD flags,
    DWORD priority,
    HANDLE* outArchive)
{
#if defined(_MSC_VER) && defined(_M_IX86)
    bool result = false;
    auto fn = gOpenArchiveCore;

    __try
    {
        __asm
        {
            mov ecx, archivePath
            mov edx, outArchive
            push priority
            push flags
            call fn
            mov result, al
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        DWORD code = GetExceptionCode();
        std::cerr << "[IFS2] OpenArchiveCore raised SEH exception 0x"
                  << std::hex << code << std::dec << "\n";
        return false;
    }

    return result;
#else
    return false;
#endif
}

static HANDLE OpenArchiveSafe(const std::string& archivePath, DWORD flags)
{
#if defined(_MSC_VER) && defined(_M_IX86)
    HANDLE result = nullptr;
    SetLastError(ERROR_SUCCESS);

    std::cout << "[IFS2] Calling OpenArchiveCore with flags=0x"
              << std::hex << flags << std::dec << "...\n";

    bool ok = OpenArchiveCoreCompat(
        archivePath.c_str(),
        flags,
        0,
        &result);

    DWORD lastError = GetLastError();
    std::cout << "[IFS2] OpenArchiveCore returned "
              << (ok ? "true" : "false")
              << " handle=" << result
              << " GetLastError=" << lastError
              << " (0x" << std::hex << lastError << std::dec << ")\n";

    return ok ? result : nullptr;
#else
    return nullptr;
#endif
}

static bool InstallProtectedArchiveBypass(HMODULE module)
{
#if defined(_M_IX86)
    constexpr uintptr_t GateRva = 0x17A52;
    constexpr uintptr_t ConditionalJumpRva = 0x17A5B;

    static const uint8_t expected[] = {
        0x8B, 0xF7,
        0xE8, 0xE7, 0xE3, 0x00, 0x00,
        0x84, 0xC0,
        0x74, 0x0D,
        0xC7, 0x44, 0x24, 0x10,
        0x08, 0x94, 0x35, 0x77
    };

    auto base = reinterpret_cast<uint8_t*>(module);
    auto gate = base + GateRva;

    if (std::memcmp(gate, expected, sizeof(expected)) != 0)
    {
        std::cerr << "[IFS2] Protected-archive gate signature mismatch; "
                     "refusing to patch unknown code.\n";
        return false;
    }

    auto jump = base + ConditionalJumpRva;
    DWORD oldProtect = 0;
    if (!VirtualProtect(jump, 1, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        std::cerr << "[IFS2] VirtualProtect failed for protected-archive gate. Win32="
                  << GetLastError() << "\n";
        return false;
    }

    *jump = 0xEB;
    FlushInstructionCache(GetCurrentProcess(), jump, 1);

    DWORD ignored = 0;
    VirtualProtect(jump, 1, oldProtect, &ignored);

    std::cout << "[IFS2] Installed verified in-memory bypass for custom error "
                 "2000000008 at RVA 0x"
              << std::hex << ConditionalJumpRva << std::dec << "\n";
    return true;
#else
    return false;
#endif
}

static void PrintPeDiagnostics(HMODULE module)
{
    auto base = reinterpret_cast<const uint8_t*>(module);
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        std::cout << "[IFS2] PE diagnostics unavailable.\n";
        return;
    }

    auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        std::cout << "[IFS2] Invalid PE signature.\n";
        return;
    }

    std::cout << "[IFS2] Machine=0x" << std::hex << nt->FileHeader.Machine
              << " TimeDateStamp=0x" << nt->FileHeader.TimeDateStamp
              << " SizeOfImage=0x" << nt->OptionalHeader.SizeOfImage
              << " EntryPointRVA=0x" << nt->OptionalHeader.AddressOfEntryPoint
              << std::dec << "\n";

    if (nt->FileHeader.Machine != IMAGE_FILE_MACHINE_I386)
        std::cout << "[WARN] IFS2.dll is not an x86/I386 PE image.\n";
}

static bool ResolveFunctions(HMODULE dll, const Offsets& offsets)
{
    auto base = reinterpret_cast<uintptr_t>(dll);

    auto resolve = [&](const char* exportName, uintptr_t offset) -> FARPROC
    {
        FARPROC byName = GetProcAddress(dll, exportName);
        if (byName)
        {
            std::cout << "[IFS2] Resolved export " << exportName << "\n";
            return byName;
        }

        if (!IsExecutableOffset(dll, offset))
        {
            std::cerr << "[IFS2] Refusing compatibility offset 0x"
                      << std::hex << offset << std::dec
                      << " because it is not inside an executable PE section.\n";
            return nullptr;
        }

        std::cout << "[IFS2] Using compatibility offset for " << exportName
                  << ": 0x" << std::hex << offset << std::dec << "\n";
        return reinterpret_cast<FARPROC>(base + offset);
    };

    gOpenArchive = reinterpret_cast<SFileOpenArchive_t>(
        resolve("SFileOpenArchive_w", offsets.SFileOpenArchive));
    gExtractFile = reinterpret_cast<SFileExtractFile_t>(
        resolve("SFileExtractFile_w", offsets.SFileExtractFile));
    gCloseFile = reinterpret_cast<SFileCloseFile_t>(
        resolve("SFileCloseFile", offsets.SFileCloseFile));
    gReadFile = reinterpret_cast<SFileReadFile_t>(
        resolve("SFileReadFile", offsets.SFileReadFile));
    gOpenFile = reinterpret_cast<NIFSOpenFileEx_t>(
        resolve("NIFSOpenFileEx", offsets.NIFSOpenFileEx));

    if (!IsExecutableOffset(dll, offsets.OpenArchiveCore))
    {
        std::cerr << "[IFS2] OpenArchiveCore RVA 0x" << std::hex
                  << offsets.OpenArchiveCore << std::dec
                  << " is not executable.\n";
        return false;
    }

    gOpenArchiveCore = reinterpret_cast<void*>(
        reinterpret_cast<uintptr_t>(dll) + offsets.OpenArchiveCore);

    std::cout << "[IFS2] Using verified OpenArchiveCore RVA 0x"
              << std::hex << offsets.OpenArchiveCore << std::dec << "\n";

    return gOpenArchive && gExtractFile && gCloseFile && gReadFile &&
           gOpenFile && gOpenArchiveCore;
}

static bool ExtractCompat(HANDLE archive, const char* input, const char* output)
{
#if defined(_M_IX86)
    bool result = false;
    auto fn = gExtractFile;
    __asm
    {
        push output
        push input
        mov ecx, archive
        call fn
        mov result, al
    }
    return result;
#else
#error MHOIFSExtractor must be built for Win32/x86 because Tencent IFS2.dll is 32-bit.
#endif
}

static void ReadCompat(HANDLE fileHandle, void* buffer, DWORD capacity, DWORD* bytesRead)
{
#if defined(_M_IX86)
    auto fn = gReadFile;
    __asm
    {
        push 1
        push bytesRead
        push capacity
        mov edx, buffer
        mov ecx, fileHandle
        call fn
    }
#else
#error MHOIFSExtractor must be built for Win32/x86 because Tencent IFS2.dll is 32-bit.
#endif
}

static void CloseCompat(HANDLE handle)
{
#if defined(_M_IX86)
    auto fn = gCloseFile;
    __asm
    {
        mov esi, handle
        call fn
    }
#else
#error MHOIFSExtractor must be built for Win32/x86 because Tencent IFS2.dll is 32-bit.
#endif
}

static std::vector<std::string> ParseList(const std::vector<char>& bytes, DWORD length)
{
    std::vector<std::string> result;
    std::string current;

    for (DWORD i = 0; i < length; ++i)
    {
        char c = bytes[i];
        if (c == '\r' || c == '\n' || c == '\0')
        {
            if (!current.empty())
            {
                while (!current.empty() && (current.back() == ' ' || current.back() == '\t'))
                    current.pop_back();
                size_t first = current.find_first_not_of(" \t");
                if (first != std::string::npos)
                    current.erase(0, first);

                if (!current.empty())
                    result.push_back(current);
                current.clear();
            }
        }
        else
        {
            current.push_back(c);
        }
    }

    if (!current.empty())
        result.push_back(current);

    std::sort(result.begin(), result.end());
    result.erase(std::unique(result.begin(), result.end()), result.end());
    return result;
}

static bool ReadListFile(HANDLE archive, std::vector<std::string>& entries)
{
    HANDLE listHandle = nullptr;
    if (!gOpenFile(archive, "(listfile)", 1, &listHandle, 0) || !listHandle)
    {
        std::cerr << "[ERROR] IFS2.dll could not open internal (listfile).\n";
        return false;
    }

    // MHO patch listfiles are expected to be small. Start at 8 MiB to avoid
    // truncating large archives while keeping the helper simple.
    std::vector<char> buffer(8 * 1024 * 1024);
    DWORD read = 0;
    ReadCompat(listHandle, buffer.data(), static_cast<DWORD>(buffer.size()), &read);
    CloseCompat(listHandle);

    if (read == 0 || read >= buffer.size())
    {
        std::cerr << "[ERROR] Failed to read (listfile), or buffer was too small. bytes="
                  << read << "\n";
        return false;
    }

    entries = ParseList(buffer, read);
    std::cout << "[OK] (listfile) contains " << entries.size() << " unique paths.\n";
    return !entries.empty();
}

static bool WriteList(const fs::path& output, const std::vector<std::string>& entries)
{
    std::ofstream file(output, std::ios::binary);
    if (!file) return false;
    for (const auto& entry : entries)
        file << entry << "\n";
    return true;
}

static void PrintUsage()
{
    std::cout <<
R"(MHOIFSExtractor - Monster Hunter Online IFS extraction helper

Usage:
  MHOIFSExtractor.exe extract <archive.ifs> [options]
  MHOIFSExtractor.exe list    <archive.ifs> [options]

Options:
  --ifs2 <path>               Path to the client's 32-bit IFS2.dll.
                              Default: IFS2.dll beside this executable.
  --out <dir>                 Extraction directory.
                              Default: <archive-name>_extracted
  --open-offset <hex>         Compatibility override for SFileOpenArchive_w.
  --extract-offset <hex>      Compatibility override for SFileExtractFile_w.
  --close-offset <hex>        Compatibility override for SFileCloseFile.
  --read-offset <hex>         Compatibility override for SFileReadFile.
  --openfile-offset <hex>     Compatibility override for NIFSOpenFileEx.
  --open-flags <hex>          Archive open flags. Default: 0x100 (read-only).

The built-in compatibility offsets are the verified wrapper entry points for
the MHO IFS2.dll profile identified by PE TimeDateStamp 0x533A60DB and
SizeOfImage 0x95000. Command-line overrides remain available for other builds.

Examples:
  MHOIFSExtractor.exe list eng_patch.ifs --ifs2 "D:\MHO\Bin\Client\Bin32\IFS2.dll"
  MHOIFSExtractor.exe extract eng_patch.ifs --ifs2 .\IFS2.dll --out .\eng_patch_extracted
)";
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3)
    {
        PrintUsage();
        return 2;
    }

    std::wstring command = argv[1];
    fs::path archivePath = fs::absolute(argv[2]);
    fs::path ifs2Path = fs::absolute(fs::path(argv[0]).parent_path() / L"IFS2.dll");
    fs::path outDir = archivePath.parent_path() / (archivePath.stem().wstring() + L"_extracted");
    Offsets offsets;
    DWORD openFlags = 0x100; // MPQ_OPEN_READ_ONLY in the StormLib base used by IFS2.

    for (int i = 3; i < argc; ++i)
    {
        std::wstring arg = argv[i];
        auto needValue = [&](const wchar_t* name) -> std::wstring
        {
            if (i + 1 >= argc)
            {
                std::wcerr << L"[ERROR] " << name << L" requires a value.\n";
                std::exit(2);
            }
            return argv[++i];
        };

        if (arg == L"--ifs2")
            ifs2Path = fs::absolute(needValue(L"--ifs2"));
        else if (arg == L"--out")
            outDir = fs::absolute(needValue(L"--out"));
        else if (arg == L"--open-offset")
            offsets.SFileOpenArchive = ParseHex(needValue(L"--open-offset"));
        else if (arg == L"--extract-offset")
            offsets.SFileExtractFile = ParseHex(needValue(L"--extract-offset"));
        else if (arg == L"--close-offset")
            offsets.SFileCloseFile = ParseHex(needValue(L"--close-offset"));
        else if (arg == L"--read-offset")
            offsets.SFileReadFile = ParseHex(needValue(L"--read-offset"));
        else if (arg == L"--openfile-offset")
            offsets.NIFSOpenFileEx = ParseHex(needValue(L"--openfile-offset"));
        else if (arg == L"--open-flags")
            openFlags = static_cast<DWORD>(ParseHex(needValue(L"--open-flags")));
        else
        {
            std::wcerr << L"[ERROR] Unknown option: " << arg << L"\n";
            return 2;
        }
    }

    if (!fs::exists(archivePath))
    {
        std::wcerr << L"[ERROR] Archive not found: " << archivePath << L"\n";
        return 3;
    }

    if (!fs::exists(ifs2Path))
    {
        std::wcerr << L"[ERROR] IFS2.dll not found: " << ifs2Path << L"\n";
        std::wcerr << L"Copy this extractor beside the game's IFS2.dll, or use --ifs2.\n";
        return 4;
    }

    std::wcout << L"[INFO] Archive: " << archivePath << L"\n";
    std::wcout << L"[INFO] IFS2.dll: " << ifs2Path << L"\n";

    SetDllDirectoryW(ifs2Path.parent_path().c_str());
    HMODULE dll = LoadLibraryW(ifs2Path.c_str());
    if (!dll)
    {
        std::wcerr << L"[ERROR] LoadLibraryW(IFS2.dll) failed. Win32 error "
                   << GetLastError() << L". Make sure this is the game's 32-bit DLL.\n";
        return 5;
    }

    PrintPeDiagnostics(dll);

    if (!InstallDebugShim(dll))
    {
        std::cerr << "[ERROR] Failed to install the IFS2 debug compatibility shim.\n";
        FreeLibrary(dll);
        return 6;
    }

    if (!ResolveFunctions(dll, offsets))
    {
        std::cerr << "[ERROR] Failed to resolve the IFS2 interface.\n";
        FreeLibrary(dll);
        return 6;
    }

    if (!InstallProtectedArchiveBypass(dll))
    {
        std::cerr << "[ERROR] Refusing to continue without a verified protected-archive bypass.\n";
        FreeLibrary(dll);
        return 6;
    }

    std::string archiveUtf8 = WideToUtf8(archivePath.wstring());
    HANDLE archive = OpenArchiveSafe(archiveUtf8, openFlags);
    if (!archive)
    {
        std::cerr << "[ERROR] IFS2.dll failed to open the archive.\n";
        std::cerr << "[HINT] If this IFS2.dll version differs from the known profile, "
                     "we will need its function offsets/signatures.\n";
        FreeLibrary(dll);
        return 7;
    }

    std::vector<std::string> entries;
    if (!ReadListFile(archive, entries))
    {
        CloseCompat(archive);
        FreeLibrary(dll);
        return 8;
    }

    if (command == L"list")
    {
        fs::path listOut = archivePath.parent_path() / (archivePath.stem().wstring() + L"_listfile.txt");
        WriteList(listOut, entries);
        std::wcout << L"[OK] Wrote: " << listOut << L"\n";
        CloseCompat(archive);
        FreeLibrary(dll);
        return 0;
    }

    if (command != L"extract")
    {
        std::wcerr << L"[ERROR] Unknown command: " << command << L"\n";
        CloseCompat(archive);
        FreeLibrary(dll);
        return 2;
    }

    fs::create_directories(outDir);
    fs::path previous = fs::current_path();
    fs::current_path(outDir);

    size_t ok = 0;
    size_t failed = 0;
    for (size_t index = 0; index < entries.size(); ++index)
    {
        const std::string& name = entries[index];

        // The internal list can contain its own metadata entries.
        if (name == "(listfile)")
            continue;

        fs::path relative = Utf8ToWide(name);
        if (relative.is_absolute() || name.find("..") != std::string::npos)
        {
            std::cerr << "[SKIP] Unsafe path: " << name << "\n";
            ++failed;
            continue;
        }

        if (ExtractCompat(archive, name.c_str(), name.c_str()))
        {
            ++ok;
        }
        else
        {
            ++failed;
            std::cerr << "[FAIL] " << name << "\n";
        }

        if ((index + 1) % 100 == 0 || index + 1 == entries.size())
            std::cout << "[PROGRESS] " << (index + 1) << "/" << entries.size()
                      << " extracted=" << ok << " failed=" << failed << "\n";
    }

    fs::current_path(previous);
    WriteList(outDir / "_mho_listfile.txt", entries);

    CloseCompat(archive);
    FreeLibrary(dll);

    std::wcout << L"[DONE] Output: " << outDir << L"\n";
    std::cout << "[DONE] Extracted: " << ok << "  Failed: " << failed << "\n";
    return failed == 0 ? 0 : 10;
}
