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
// It reads <its own name>.omni.ini and writes <its own name>.omni.log, so the same binary
// can stand in for any of those DLLs, side by side.

#include <windows.h>

#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "third_party/ffx_api.h"

namespace
{
constexpr ffxReturnCode_t kNoProvider = FFX_API_RETURN_NO_PROVIDER;

// One real upscaler DLL behind this one.
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
            // A driver-style build (amdxcffx64.dll) exports UpdateFfxApiProvider instead.
            // OptiScaler loads those itself and lists them beside whatever this DLL lists.
            bool driverStyle = GetProcAddress(m.handle, "UpdateFfxApiProvider") != nullptr;
            Log("%s is not an upscaler DLL%s; skipped", Narrow(m.path).c_str(),
                driverStyle ? " (it is driver-style: install it as amdxcffx64.dll beside OptiScaler)" : "");
            FreeLibrary(m.handle);
            it = gModules.erase(it);
            continue;
        }

        Log("Loaded %s as '%s'", Narrow(m.path).c_str(), m.label.c_str());
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
        ffxQueryDescGetVersions q = request;
        uint64_t count = 0;
        q.header.pNext = nullptr;
        q.outputCount = &count;
        q.versionIds = nullptr;
        q.versionNames = nullptr;
        if (gModules[m].query(nullptr, &q.header) != FFX_API_RETURN_OK || count == 0) continue;

        std::vector<uint64_t> ids(count);
        std::vector<const char*> names(count);
        q.versionIds = ids.data();
        q.versionNames = names.data();
        if (gModules[m].query(nullptr, &q.header) != FFX_API_RETURN_OK) continue;
        count = *q.outputCount < count ? *q.outputCount : count;

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

    for (uint64_t i = 0; i < n; ++i)
        Log("  offers 0x%016llx '%s' from '%s'", (unsigned long long) listed[i].id, listed[i].name,
            gModules[Resolve(listed[i].id).module].label.c_str());
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
