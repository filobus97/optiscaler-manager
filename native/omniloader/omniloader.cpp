// Upscaler Manager - omniloader
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// Stands in for one FidelityFX API DLL (amd_fidelityfx_upscaler_dx12.dll,
// amd_fidelityfx_framegeneration_dx12.dll, amd_fidelityfx_vk.dll … — they all export the
// same five functions): loads any number of real builds of it, lists every version they
// offer as one list, and sends each call to the build that owns the context. OptiScaler
// already switches live between the versions one DLL reports; with this in that DLL's
// place it switches between builds too.
//
// It also hosts AMD driver DLLs (amdxcffx64.dll): those export no ffx* functions, only
// UpdateFfxApiProvider, which hands out the driver's FSR 4 as a set of function pointers.
// Such a provider is listed and routed like any other build.
//
// It reads <its own name>.omni.ini and writes <its own name>.omni.log, so the same binary
// can stand in for any of those DLLs, side by side.

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "driver_provider.h"
#include "third_party/ffx_api.h"

namespace
{
constexpr ffxReturnCode_t kNoProvider = FFX_API_RETURN_NO_PROVIDER;

// One real upscaler DLL behind this one: either an SDK build with the five ffx* exports, or
// an AMD driver DLL whose provider functions fill the same five fields.
struct Module
{
    std::wstring path;
    std::string label;
    HMODULE handle = nullptr;
    PfnFfxCreateContext createContext = nullptr;
    PfnFfxDestroyContext destroyContext = nullptr;
    PfnFfxConfigure configure = nullptr;
    PfnFfxQuery query = nullptr;
    PfnFfxDispatch dispatch = nullptr;

    // Driver providers only. A driver offers exactly one version, so there is nothing to ask
    // it: its id and name were taken when it handed the provider out.
    bool driver = false;
    uint64_t providerId = 0;
    std::string providerName; // copied: the driver reuses the buffer it formats this into
    uint32_t structVersion = 0;
    bool (*isSupported)(const void* backend) = nullptr;
};

// A version this DLL hands out: which module offers it, and that module's own id for it.
struct Offered
{
    size_t module;
    uint64_t originalId;
};

// Ids handed to callers carry this prefix in their top half and an index into the
// table of offered versions in the bottom half. Callers treat ids as opaque, so any
// value is allowed as long as it comes back unchanged.
constexpr uint64_t kIdPrefix = 0x4F4D4E4900000000ull; // "OMNI"

std::mutex gLock;
bool gInitialised = false;
HMODULE gSelf = nullptr;
std::wstring gDir;
std::wstring gStem; // this DLL's own file name without ".dll"
std::vector<Module> gModules;
std::vector<Offered> gOffered;
std::unordered_map<ffxContext, size_t> gContexts;
std::deque<std::string> gNames; // names handed out must outlive the call
std::string gLastListing;       // the version list as last logged

FILE* gLog = nullptr;

void Log(const char* format, ...)
{
    if (!gLog) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(gLog, "[%02d:%02d:%02d.%03d] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list args;
    va_start(args, format);
    vfprintf(gLog, format, args);
    va_end(args);
    fputc('\n', gLog);
    fflush(gLog);
}

std::string Narrow(const std::wstring& wide)
{
    if (wide.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(size > 0 ? size - 1 : 0, '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, out.data(), size, nullptr, nullptr);
    return out;
}

std::wstring Widen(const std::string& narrow)
{
    if (narrow.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, narrow.c_str(), -1, nullptr, 0);
    std::wstring out(size > 0 ? size - 1 : 0, L'\0');
    if (size > 1) MultiByteToWideChar(CP_UTF8, 0, narrow.c_str(), -1, out.data(), size);
    return out;
}

std::string Trim(const std::string& s)
{
    size_t start = s.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return {};
    size_t end = s.find_last_not_of(" \t\r\n");
    return s.substr(start, end - start + 1);
}

// <name>.omni.ini, beside this DLL:
//   [Modules]
//   omni\upscaler\build-a.dll = Build A
//   omni\upscaler\build-b.dll = Build B
// One DLL per line, in order; paths are relative to this folder. The first answers
// whenever nothing picks a version. A label is added to a version's name only when two
// builds report the same name for different code.
void ReadConfig()
{
    std::wstring iniPath = gDir + gStem + L".omni.ini";
    FILE* ini = _wfopen(iniPath.c_str(), L"r");
    if (!ini)
    {
        Log("No %s; nothing to load", Narrow(iniPath).c_str());
        return;
    }

    char line[1024];
    bool inModules = false;
    while (fgets(line, sizeof line, ini))
    {
        std::string text = Trim(line);
        if (text.empty() || text[0] == ';' || text[0] == '#') continue;
        if (text[0] == '[')
        {
            inModules = _stricmp(text.c_str(), "[Modules]") == 0;
            continue;
        }
        if (!inModules) continue;

        std::string file = text, label;
        if (size_t eq = text.find('='); eq != std::string::npos)
        {
            file = Trim(text.substr(0, eq));
            label = Trim(text.substr(eq + 1));
        }
        if (file.empty()) continue;

        Module module;
        std::wstring wide = Widen(file);
        bool absolute = wide.size() > 1 && (wide[1] == L':' || wide[0] == L'\\' || wide[0] == L'/');
        module.path = absolute ? wide : gDir + wide;
        module.label = label.empty() ? file : label;
        gModules.push_back(module);
    }
    fclose(ini);
}

// ---- Hosting AMD driver DLLs ----
//
// A driver DLL hands out FSR 4 only when it finds signed FidelityFX SDK DLLs loaded in the
// process. It looks them up by name (amd_fidelityfx_*.dll) with GetModuleHandleW. In a
// process using this DLL, that name is this DLL, which is unsigned, so the driver would
// refuse. The driver is signed by the same certificate as the SDK DLLs, so it is told its
// own module is what it is looking for. That is done by replacing the driver's own import
// of GetModuleHandleW/A; nothing else in the process is touched.

constexpr size_t kMaxDrivers = 16;
HMODULE gDriverHandles[kMaxDrivers]; // read by the hook without the lock, so only appended to
std::atomic<size_t> gDriverCount { 0 };

using PfnGetModuleHandleW = HMODULE(WINAPI*)(LPCWSTR);
using PfnGetModuleHandleA = HMODULE(WINAPI*)(LPCSTR);
PfnGetModuleHandleW gRealGetModuleHandleW = nullptr; // what the driver's import held before
PfnGetModuleHandleA gRealGetModuleHandleA = nullptr;

char LowerOf(char c) { return (c >= 'A' && c <= 'Z') ? static_cast<char>(c + 32) : c; }
char LowerOf(wchar_t c) { return c < 128 ? LowerOf(static_cast<char>(c)) : '\x01'; }

template <class C> bool StartsWithNoCase(const C* text, const char* prefix)
{
    for (; *prefix; ++text, ++prefix)
        if (!*text || LowerOf(*text) != *prefix) return false;
    return true;
}

template <class C> bool EqualsNoCase(const C* text, const char* word)
{
    return StartsWithNoCase(text, word) && text[strlen(word)] == 0;
}

// The names a driver may ask for and get itself: the SDK DLLs it checks for, and its own
// name. The hosted file is renamed, so a lookup of its real name would find nothing.
template <class C> bool IsNameForDriverItself(const C* name)
{
    if (StartsWithNoCase(name, "amd_fidelityfx_")) return true;
    for (const char* own : { "amdxcffx64", "amdvkffx64" })
    {
        size_t n = strlen(own);
        if (StartsWithNoCase(name, own) && (name[n] == 0 || EqualsNoCase(name + n, ".dll"))) return true;
    }
    return false;
}

// The driver the call came from, or null when it came from anywhere else.
HMODULE CallingDriver(void* returnAddress)
{
    HMODULE caller = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(returnAddress), &caller))
        return nullptr;
    size_t count = gDriverCount.load();
    for (size_t i = 0; i < count; ++i)
        if (gDriverHandles[i] == caller) return caller;
    return nullptr;
}

// No lock here: the driver calls these from inside UpdateFfxApiProvider, which runs with
// the lock held.
HMODULE WINAPI HookGetModuleHandleW(LPCWSTR name)
{
    if (name && IsNameForDriverItself(name))
        if (HMODULE self = CallingDriver(__builtin_return_address(0))) return self;
    return gRealGetModuleHandleW ? gRealGetModuleHandleW(name) : GetModuleHandleW(name);
}

HMODULE WINAPI HookGetModuleHandleA(LPCSTR name)
{
    if (name && IsNameForDriverItself(name))
        if (HMODULE self = CallingDriver(__builtin_return_address(0))) return self;
    return gRealGetModuleHandleA ? gRealGetModuleHandleA(name) : GetModuleHandleA(name);
}

// Points the driver's GetModuleHandleW/A imports at the hooks above, whichever DLL it
// imports them from (kernel32, KERNELBASE, an api-ms-win-* set). Returns how many import
// slots it changed.
int RedirectModuleLookups(HMODULE module)
{
    auto* base = reinterpret_cast<BYTE*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return 0;

    // Where the real functions live, for a slot whose name table is missing or stripped.
    std::vector<void*> realW, realA;
    realW.push_back(reinterpret_cast<void*>(&GetModuleHandleW));
    realA.push_back(reinterpret_cast<void*>(&GetModuleHandleA));
    for (const wchar_t* dll : { L"kernel32.dll", L"kernelbase.dll" })
        if (HMODULE h = GetModuleHandleW(dll))
        {
            realW.push_back(reinterpret_cast<void*>(GetProcAddress(h, "GetModuleHandleW")));
            realA.push_back(reinterpret_cast<void*>(GetProcAddress(h, "GetModuleHandleA")));
        }
    auto isOneOf = [](const std::vector<void*>& list, void* p)
    {
        for (void* q : list)
            if (q && q == p) return true;
        return false;
    };

    int patched = 0;
    for (auto* d = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); d->Name; ++d)
    {
        auto* addresses = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + d->FirstThunk);
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        for (size_t i = 0; names[i].u1.AddressOfData; ++i)
        {
            void* current = reinterpret_cast<void*>(addresses[i].u1.Function);
            int kind = 0; // 1 = GetModuleHandleW, 2 = GetModuleHandleA
            if (d->OriginalFirstThunk && !IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal))
            {
                auto* import = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names[i].u1.AddressOfData);
                if (strcmp(import->Name, "GetModuleHandleW") == 0) kind = 1;
                else if (strcmp(import->Name, "GetModuleHandleA") == 0) kind = 2;
            }
            if (!kind && isOneOf(realW, current)) kind = 1;
            if (!kind && isOneOf(realA, current)) kind = 2;
            if (!kind) continue;

            void* hook = kind == 1 ? reinterpret_cast<void*>(&HookGetModuleHandleW)
                                   : reinterpret_cast<void*>(&HookGetModuleHandleA);
            if (current == hook) continue;
            if (kind == 1 && !gRealGetModuleHandleW) gRealGetModuleHandleW = reinterpret_cast<PfnGetModuleHandleW>(current);
            if (kind == 2 && !gRealGetModuleHandleA) gRealGetModuleHandleA = reinterpret_cast<PfnGetModuleHandleA>(current);

            DWORD old = 0;
            if (!VirtualProtect(&addresses[i], sizeof(void*), PAGE_READWRITE, &old)) continue;
            addresses[i].u1.Function = reinterpret_cast<ULONGLONG>(hook);
            DWORD ignored = 0;
            VirtualProtect(&addresses[i], sizeof(void*), old, &ignored);
            ++patched;
        }
    }
    return patched;
}

using PfnUpdateProvider = HRESULT(WINAPI*)(void*, uint32_t);

// Asks the driver for its FSR upscaler, newest struct version first: a driver that does not
// know a version answers S_OK and fills nothing.
bool TakeProvider(Module& m, PfnUpdateProvider update)
{
    for (uint32_t version : { 3u, 2u, 1u })
    {
        DriverExternalProviderData data = {};
        data.structVersion = version;
        data.descType = kDriverUpscaleDescType;
        HRESULT hr = update(&data, version >= 3 ? kDriverSizeV3 : kDriverSizeV1V2);
        const DriverProviderInterface& p = data.provider;
        Log("  UpdateFfxApiProvider struct v%u: 0x%08lx%s", version, static_cast<unsigned long>(hr),
            SUCCEEDED(hr) && !p.createContext ? " (nothing filled in)" : "");
        if (FAILED(hr) || !p.createContext) continue;
        if (!p.destroyContext || !p.configure || !p.query || !p.dispatch)
        {
            Log("  the provider is incomplete (a function is missing)");
            continue;
        }

        m.createContext = p.createContext;
        m.destroyContext = p.destroyContext;
        m.configure = p.configure;
        m.query = p.query;
        m.dispatch = p.dispatch;
        m.providerId = p.versionId;
        m.providerName = p.versionName ? p.versionName : "?"; // copied: the driver reuses that buffer
        m.structVersion = version;
        m.isSupported = version >= 3 ? data.isSupported : nullptr;
        return true;
    }
    return false;
}

// Gets a driver DLL to hand out its provider. Its lookups are redirected first, and stay
// redirected: the driver checks again whenever it is called.
bool HostDriver(Module& m, PfnUpdateProvider update)
{
    size_t slot = gDriverCount.load();
    if (slot >= kMaxDrivers)
    {
        Log("%s: too many driver DLLs; skipped", Narrow(m.path).c_str());
        return false;
    }
    gDriverHandles[slot] = m.handle;
    gDriverCount.store(slot + 1);

    int redirected = RedirectModuleLookups(m.handle);
    Log("%s is an AMD driver; redirected %d module lookup import(s)", Narrow(m.path).c_str(), redirected);
    if (redirected == 0)
        Log("  warning: it imports neither GetModuleHandleW nor GetModuleHandleA, so it may not accept this process");

    if (TakeProvider(m, update))
    {
        m.driver = true;
        return true;
    }
    Log("%s: the driver gave no FSR upscaler (it needs driver 26.10.1 or newer, or it refused this process); "
        "skipped",
        Narrow(m.path).c_str());
    gDriverCount.store(slot);
    return false;
}

void LoadModules()
{
    for (auto it = gModules.begin(); it != gModules.end();)
    {
        Module& m = *it;
        // The build's own folder is searched first for anything it depends on, so a build
        // shipped with companion DLLs works from its own subfolder.
        m.handle = LoadLibraryExW(m.path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!m.handle)
        {
            Log("Could not load %s (error %lu); skipped", Narrow(m.path).c_str(), GetLastError());
            it = gModules.erase(it);
            continue;
        }

        m.createContext = (PfnFfxCreateContext) (void*) GetProcAddress(m.handle, "ffxCreateContext");
        m.destroyContext = (PfnFfxDestroyContext) (void*) GetProcAddress(m.handle, "ffxDestroyContext");
        m.configure = (PfnFfxConfigure) (void*) GetProcAddress(m.handle, "ffxConfigure");
        m.query = (PfnFfxQuery) (void*) GetProcAddress(m.handle, "ffxQuery");
        m.dispatch = (PfnFfxDispatch) (void*) GetProcAddress(m.handle, "ffxDispatch");

        if (!m.createContext || !m.destroyContext || !m.configure || !m.query || !m.dispatch)
        {
            // Not an SDK build. An AMD driver DLL (amdxcffx64.dll) exports UpdateFfxApiProvider
            // instead, and the provider it hands out fills the same five functions.
            auto update = (PfnUpdateProvider) (void*) GetProcAddress(m.handle, "UpdateFfxApiProvider");
            if (!update)
            {
                Log("%s is not an upscaler DLL (it exports neither the ffx* functions nor UpdateFfxApiProvider); "
                    "skipped",
                    Narrow(m.path).c_str());
                FreeLibrary(m.handle);
                it = gModules.erase(it);
                continue;
            }
            if (!HostDriver(m, update))
            {
                FreeLibrary(m.handle);
                it = gModules.erase(it);
                continue;
            }
            Log("Loaded %s as '%s' (AMD driver, provider '%s' 0x%016llx, struct v%u)", Narrow(m.path).c_str(),
                m.label.c_str(), m.providerName.c_str(), static_cast<unsigned long long>(m.providerId),
                m.structVersion);
        }
        else
            Log("Loaded %s as '%s'", Narrow(m.path).c_str(), m.label.c_str());

        // OptiScaler treats DLLs by file name. On Linux it answers FSR 4's GPU check only
        // for DLLs whose name starts with "amd", so a build named otherwise drops its FSR 4
        // versions. And it takes a DLL whose name ends like AMD's upscaler for the upscaler.
        std::wstring file = m.path.substr(m.path.find_last_of(L"\\/") + 1);
        std::wstring lower;
        for (wchar_t c : file) lower += towlower(c);
        if (m.driver)
        {
            // For a driver it also spoofs the GPU vendor, but only for names starting with
            // "amdxcffx64". And OptiScaler loads its own copy as exactly amdxcffx64.dll.
            if (lower.rfind(L"amdxcffx64", 0) != 0)
                Log("  warning: its name does not start with 'amdxcffx64', so on Linux OptiScaler may not answer "
                    "its GPU check; name it amdxcffx64_<something>.dll");
            if (lower == L"amdxcffx64.dll")
                Log("  warning: it is named exactly amdxcffx64.dll, which is the copy OptiScaler loads itself; "
                    "name it amdxcffx64_<something>.dll");
        }
        else
        {
            const std::wstring amdName = L"amd_fidelityfx_upscaler_dx12.dll";
            if (lower.rfind(L"amd", 0) != 0)
                Log("  warning: its name does not start with 'amd', so on Linux it may not offer FSR 4; "
                    "name it amd_fidelityfx_upscaler_dx12_<something>.dll");
            if (lower.size() >= amdName.size() &&
                lower.compare(lower.size() - amdName.size(), amdName.size(), amdName) == 0)
                Log("  warning: its name ends like AMD's upscaler, so OptiScaler may take it for the upscaler itself; "
                    "name it amd_fidelityfx_upscaler_dx12_<something>.dll");
        }
        ++it;
    }
}

void EnsureInitialised()
{
    if (gInitialised) return;
    gInitialised = true;

    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(gSelf, path, MAX_PATH);
    std::wstring self = path;
    size_t slash = self.find_last_of(L"\\/");
    gDir = self.substr(0, slash + 1);
    gStem = self.substr(slash + 1);
    if (size_t dot = gStem.find_last_of(L'.'); dot != std::wstring::npos) gStem = gStem.substr(0, dot);

    gLog = _wfopen((gDir + gStem + L".omni.log").c_str(), L"w");
    Log("omniloader starting in %s", Narrow(gDir).c_str());

    ReadConfig();
    LoadModules();
    Log("%zu upscaler DLL(s) ready", gModules.size());
}

// The index of a version in gOffered, adding it if new.
size_t Offer(size_t module, uint64_t originalId)
{
    for (size_t i = 0; i < gOffered.size(); ++i)
        if (gOffered[i].module == module && gOffered[i].originalId == originalId) return i;
    gOffered.push_back({ module, originalId });
    return gOffered.size() - 1;
}

bool IsOurs(uint64_t id) { return (id & 0xFFFFFFFF00000000ull) == kIdPrefix && (id & 0xFFFFFFFFull) < gOffered.size(); }

const Offered& Resolve(uint64_t id) { return gOffered[id & 0xFFFFFFFFull]; }

struct Listed
{
    uint64_t id;
    const char* name;
};

// What one module offers. A driver provider has one version and is never asked; an SDK
// build is asked the way OptiScaler asks: count first, then fill.
bool ModuleVersions(Module& module, const ffxQueryDescGetVersions& request, std::vector<uint64_t>& ids,
                    std::vector<const char*>& names)
{
    if (module.driver)
    {
        ids.push_back(module.providerId);
        names.push_back(module.providerName.c_str());
        return true;
    }

    ffxQueryDescGetVersions q = request;
    uint64_t count = 0;
    q.header.pNext = nullptr;
    q.outputCount = &count;
    q.versionIds = nullptr;
    q.versionNames = nullptr;
    if (module.query(nullptr, &q.header) != FFX_API_RETURN_OK || count == 0) return false;

    ids.resize(count);
    names.resize(count);
    q.versionIds = ids.data();
    q.versionNames = names.data();
    if (module.query(nullptr, &q.header) != FFX_API_RETURN_OK) return false;
    count = *q.outputCount < count ? *q.outputCount : count;
    ids.resize(count);
    names.resize(count);
    return count > 0;
}

// Every version every build offers for this device, as one list.
//
// The first build's list is kept whole. A later build adds what is new: a version with the
// same name and id as one already listed is the same provider (each build carries AMD's
// FSR 3.1.5 and 2.3.4, and a driver-style provider OptiScaler injects shows up in every
// build's list), so it is shown once. A later version whose name is taken by different
// code keeps the build's label. And a build that would add nothing — it reports exactly
// what an earlier one does — still gets its newest version listed, labelled, so two
// builds that call themselves the same thing can both be picked.
std::vector<Listed> ListVersions(const ffxQueryDescGetVersions& request)
{
    std::vector<Listed> listed;
    std::vector<std::pair<std::string, uint64_t>> seen;

    auto nameTaken = [&](const std::string& name)
    {
        for (auto& entry : seen)
            if (entry.first == name) return true;
        return false;
    };
    auto add = [&](size_t m, uint64_t id, const char* name)
    {
        std::string text = name ? name : "?";
        const char* shown = name;
        if (nameTaken(text))
        {
            gNames.push_back(text + " [" + gModules[m].label + "]");
            shown = gNames.back().c_str();
        }
        seen.push_back({ text, id });
        listed.push_back({ kIdPrefix | Offer(m, id), shown });
    };

    for (size_t m = 0; m < gModules.size(); ++m)
    {
        std::vector<uint64_t> ids;
        std::vector<const char*> names;
        if (!ModuleVersions(gModules[m], request, ids, names)) continue;
        uint64_t count = ids.size();

        size_t before = listed.size();
        for (uint64_t i = 0; i < count; ++i)
        {
            std::string name = names[i] ? names[i] : "?";
            bool same = false;
            for (auto& entry : seen)
                if (entry.first == name && entry.second == ids[i]) same = true;
            if (!same) add(m, ids[i], names[i]);
        }

        // Builds sort their list newest first, so entry 0 is the one that makes this build itself.
        if (listed.size() == before && count > 0) add(m, ids[0], names[0]);
    }
    return listed;
}

ffxReturnCode_t AnswerVersions(ffxQueryDescGetVersions* request)
{
    if (!request->outputCount) return FFX_API_RETURN_ERROR_PARAMETER;

    auto listed = ListVersions(*request);
    uint64_t capacity = *request->outputCount;
    *request->outputCount = listed.size();

    // Count only, as the header documents for a capacity of zero or no arrays.
    if (capacity == 0 || (!request->versionIds && !request->versionNames)) return FFX_API_RETURN_OK;

    uint64_t n = capacity < listed.size() ? capacity : listed.size();
    for (uint64_t i = 0; i < n; ++i)
    {
        if (request->versionIds) request->versionIds[i] = listed[i].id;
        if (request->versionNames) request->versionNames[i] = listed[i].name;
    }
    *request->outputCount = n;

    // OptiScaler asks for the list dozens of times while it starts, so it is logged only
    // when it changes.
    std::string listing;
    for (uint64_t i = 0; i < n; ++i)
        listing += std::to_string(listed[i].id) + listed[i].name + '\n';
    if (listing != gLastListing)
    {
        gLastListing = listing;
        for (uint64_t i = 0; i < n; ++i)
            Log("  offers 0x%016llx '%s' from '%s'", (unsigned long long) listed[i].id, listed[i].name,
                gModules[Resolve(listed[i].id).module].label.c_str());
    }
    return FFX_API_RETURN_OK;
}

ffxOverrideVersion* FindOverride(ffxApiHeader* header)
{
    for (; header; header = header->pNext)
        if (header->type == FFX_API_DESC_TYPE_OVERRIDE_VERSION) return reinterpret_cast<ffxOverrideVersion*>(header);
    return nullptr;
}

// Which DLL owns this context, or the first when it is not one of ours.
Module* Owner(ffxContext* context)
{
    if (context && *context)
        if (auto it = gContexts.find(*context); it != gContexts.end()) return &gModules[it->second];
    return gModules.empty() ? nullptr : &gModules[0];
}
} // namespace

extern "C" {

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    // Only remembered here: loading other DLLs under the loader lock is not allowed, so
    // everything else waits for the first call.
    if (reason == DLL_PROCESS_ATTACH)
    {
        gSelf = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

__declspec(dllexport) ffxReturnCode_t ffxCreateContext(ffxContext* context, ffxCreateContextDescHeader* desc,
                                                       const ffxAllocationCallbacks* memCb)
{
    std::lock_guard<std::mutex> lock(gLock);
    EnsureInitialised();
    if (gModules.empty()) return FFX_API_RETURN_NO_PROVIDER;

    size_t module = 0;
    ffxOverrideVersion* chosen = FindOverride(desc);
    uint64_t asked = chosen ? chosen->versionId : 0;

    if (chosen && IsOurs(asked))
    {
        // Hand the owning DLL its own id for the version, then put the caller's back.
        module = Resolve(asked).module;
        chosen->versionId = Resolve(asked).originalId;
    }

    ffxReturnCode_t result = gModules[module].createContext(context, desc, memCb);
    if (chosen) chosen->versionId = asked;

    if (result == FFX_API_RETURN_OK && context && *context) gContexts[*context] = module;
    Log("CreateContext version 0x%016llx -> '%s': %u", (unsigned long long) asked, gModules[module].label.c_str(),
        result);
    return result;
}

__declspec(dllexport) ffxReturnCode_t ffxDestroyContext(ffxContext* context, const ffxAllocationCallbacks* memCb)
{
    std::lock_guard<std::mutex> lock(gLock);
    EnsureInitialised();
    Module* owner = Owner(context);
    if (!owner) return FFX_API_RETURN_NO_PROVIDER;

    ffxContext handle = context ? *context : nullptr;
    ffxReturnCode_t result = owner->destroyContext(context, memCb);
    if (handle) gContexts.erase(handle);
    return result;
}

__declspec(dllexport) ffxReturnCode_t ffxConfigure(ffxContext* context, const ffxConfigureDescHeader* desc)
{
    Module* owner;
    {
        std::lock_guard<std::mutex> lock(gLock);
        EnsureInitialised();
        owner = Owner(context);
    }
    return owner ? owner->configure(context, desc) : kNoProvider;
}

__declspec(dllexport) ffxReturnCode_t ffxQuery(ffxContext* context, ffxQueryDescHeader* desc)
{
    std::lock_guard<std::mutex> lock(gLock);
    EnsureInitialised();
    if (!desc) return FFX_API_RETURN_ERROR_PARAMETER;

    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_VERSIONS)
        return AnswerVersions(reinterpret_cast<ffxQueryDescGetVersions*>(desc));

    Module* owner = Owner(context);
    if (!owner) return FFX_API_RETURN_NO_PROVIDER;
    ffxReturnCode_t result = owner->query(context, desc);

    // A context's provider id comes back in the form this DLL handed out.
    if (result == FFX_API_RETURN_OK && desc->type == FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION)
    {
        auto* provider = reinterpret_cast<ffxQueryGetProviderVersion*>(desc);
        size_t module = static_cast<size_t>(owner - gModules.data());
        provider->versionId = kIdPrefix | Offer(module, provider->versionId);
    }
    return result;
}

__declspec(dllexport) ffxReturnCode_t ffxDispatch(ffxContext* context, const ffxDispatchDescHeader* desc)
{
    // The per-frame call: the lock is held only for the lookup.
    Module* owner;
    {
        std::lock_guard<std::mutex> lock(gLock);
        EnsureInitialised();
        owner = Owner(context);
    }
    return owner ? owner->dispatch(context, desc) : kNoProvider;
}

} // extern "C"
