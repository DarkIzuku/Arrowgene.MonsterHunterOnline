#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>
#include <algorithm>
#include <cstdint>

namespace fs = std::filesystem;

struct Offsets
{
    uintptr_t SFileOpenArchive = 0x163E0;
    uintptr_t SFileExtractFile = 0x25DF0;
    uintptr_t SFileCloseFile = 0x210A0;
    uintptr_t SFileReadFile = 0x22570;
    uintptr_t NIFSOpenFileEx = 0x1FED0;
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

static bool InstallDebugShim(HMODULE module)
{
#if defined(_M_IX86)
    // The original Tencent/React IFS tool always installs a tiny stand-in for
    // ifsdebug.dll before opening archives. IFS2.dll can dereference this
    // interface during archive open, so leaving it null may cause 0xC0000005.
    constexpr uintptr_t DebugInterfaceOffset = 0x8CBE0;

    auto base = reinterpret_cast<uint8_t*>(module);
    auto slot = reinterpret_cast<DWORD*>(base + DebugInterfaceOffset);

    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(slot, &mbi, sizeof(mbi)) != sizeof(mbi) || mbi.State != MEM_COMMIT)
    {
        std::cerr << "[IFS2] Debug shim slot is not mapped.\n";
        return false;
    }

    static DWORD debugFunction = reinterpret_cast<DWORD>(&OutputDebugStringA);
    static DWORD debugFunctionPtr = reinterpret_cast<DWORD>(&debugFunction);

    DWORD oldProtect = 0;
    if (!VirtualProtect(slot, sizeof(DWORD), PAGE_READWRITE, &oldProtect))
    {
        std::cerr << "[IFS2] VirtualProtect failed for debug shim slot. Win32="
                  << GetLastError() << "\n";
        return false;
    }

    *slot = reinterpret_cast<DWORD>(&debugFunctionPtr);

    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(DWORD), oldProtect, &ignored);

    std::cout << "[IFS2] Installed ifsdebug compatibility shim at RVA 0x"
              << std::hex << DebugInterfaceOffset << std::dec << "\n";
    return true;
#else
    return false;
#endif
}

static HANDLE OpenArchiveSafe(const std::string& archivePath)
{
#if defined(_MSC_VER) && defined(_M_IX86)
    HANDLE result = nullptr;
    __try
    {
        std::cout << "[IFS2] Calling SFileOpenArchive_w...\n";
        result = gOpenArchive(archivePath.c_str(), 0);
        std::cout << "[IFS2] SFileOpenArchive_w returned " << result << "\n";
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        DWORD code = GetExceptionCode();
        std::cerr << "[IFS2] SFileOpenArchive_w raised SEH exception 0x"
                  << std::hex << code << std::dec << "\n";
        return nullptr;
    }
    return result;
#else
    return gOpenArchive(archivePath.c_str(), 0);
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

    return gOpenArchive && gExtractFile && gCloseFile && gReadFile && gOpenFile;
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

The built-in compatibility offsets come from the public Tencent IFS tooling
interface and are used only if named exports are unavailable.

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

    std::string archiveUtf8 = WideToUtf8(archivePath.wstring());
    HANDLE archive = OpenArchiveSafe(archiveUtf8);
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
