// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// Loads the omniloader the way OptiScaler does — by name, five exports — in front of mock
// builds, and checks the merged version list and where every call goes. Run under Wine
// by run-tests.sh; prints PASS/FAIL per check and exits non-zero on any failure.

#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../third_party/ffx_api.h"
#include "mock_protocol.h"

namespace
{
int gFailures = 0, gPasses = 0;

void Check(const char* what, bool ok, const std::string& detail = "")
{
    if (ok) { ++gPasses; printf("  PASS  %s\n", what); }
    else { ++gFailures; printf("  FAIL  %s %s\n", what, detail.c_str()); }
}

struct Api
{
    HMODULE dll;
    PfnFfxCreateContext create;
    PfnFfxDestroyContext destroy;
    PfnFfxConfigure configure;
    PfnFfxQuery query;
    PfnFfxDispatch dispatch;
};

Api Load(const char* path)
{
    Api a {};
    a.dll = LoadLibraryA(path);
    if (!a.dll) return a;
    a.create = (PfnFfxCreateContext) (void*) GetProcAddress(a.dll, "ffxCreateContext");
    a.destroy = (PfnFfxDestroyContext) (void*) GetProcAddress(a.dll, "ffxDestroyContext");
    a.configure = (PfnFfxConfigure) (void*) GetProcAddress(a.dll, "ffxConfigure");
    a.query = (PfnFfxQuery) (void*) GetProcAddress(a.dll, "ffxQuery");
    a.dispatch = (PfnFfxDispatch) (void*) GetProcAddress(a.dll, "ffxDispatch");
    return a;
}

struct Versions
{
    std::vector<uint64_t> ids;
    std::vector<std::string> names;
};

// The two-step protocol OptiScaler uses: count first, then fill.
Versions List(const Api& api)
{
    Versions v;
    ffxQueryDescGetVersions q {};
    q.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    q.createDescType = 0x00010000; // upscale
    uint64_t count = 0;
    q.outputCount = &count;
    if (api.query(nullptr, &q.header) != FFX_API_RETURN_OK) return v;

    std::vector<uint64_t> ids(count);
    std::vector<const char*> names(count);
    q.versionIds = ids.data();
    q.versionNames = names.data();
    if (api.query(nullptr, &q.header) != FFX_API_RETURN_OK) return v;
    for (uint64_t i = 0; i < count; ++i)
    {
        v.ids.push_back(ids[i]);
        v.names.push_back(names[i]);
    }
    return v;
}

std::string Join(const std::vector<std::string>& parts)
{
    std::string out;
    for (auto& p : parts) out += (out.empty() ? "" : " | ") + p;
    return out;
}

// Creates a context the way OptiScaler does: context desc → backend desc → override.
ffxContext Create(const Api& api, uint64_t versionId, bool withOverride, ffxReturnCode_t* result,
                  uint64_t* overrideAfter = nullptr)
{
    ffxApiHeader contextDesc { 0x00010000, nullptr };
    ffxApiHeader backendDesc { 0x0000002, nullptr };
    ffxOverrideVersion override {};
    override.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
    override.versionId = versionId;
    contextDesc.pNext = &backendDesc;
    if (withOverride) backendDesc.pNext = &override.header;

    ffxContext context = nullptr;
    *result = api.create(&context, &contextDesc, nullptr);
    if (overrideAfter) *overrideAfter = override.versionId;
    return context;
}

MockContext Who(const Api& api, ffxContext context)
{
    MockContext seen {};
    MockWhoQuery q {};
    q.header.type = kMockWhoQuery;
    q.context = &seen;
    api.query(&context, &q.header);
    return seen;
}
} // namespace

int main(int argc, char** argv)
{
    const char* dir = argc > 1 ? argv[1] : ".";
    SetCurrentDirectoryA(dir);

    printf("--- one stand-in, four builds, one driver-style DLL, one missing file ---\n");
    Api api = Load("amd_fidelityfx_upscaler_dx12.dll");
    Check("loads under the upscaler's own name, with all five functions",
          api.dll && api.create && api.destroy && api.configure && api.query && api.dispatch);
    if (!api.dll) return 1;

    Versions v = List(api);
    std::string joined = Join(v.names);
    Check("lists every build's versions as one list, shared ones once",
          joined == "4.1.1 | 3.1.5 | 2.3.4 | 4.1.1-fast | 4.1.1 [Build C] | 4.1.1 [Build D]", joined);

    bool unique = true;
    for (size_t i = 0; i < v.ids.size(); ++i)
        for (size_t j = i + 1; j < v.ids.size(); ++j)
            if (v.ids[i] == v.ids[j]) unique = false;
    Check("  every entry has its own id, though builds share theirs", unique);

    Versions again = List(api);
    Check("  and asking again gives the same ids", again.ids == v.ids);

    // Pick each entry the way OptiScaler's overlay does, and see which build answers.
    const char* expectTag[] = { "A", "A", "A", "B", "C", "D" };
    const uint64_t expectId[] = { 0x4011, 0x3015, 0x2034, 0x4011, 0x4011, 0x4012 };
    for (size_t i = 0; i < v.ids.size() && i < 6; ++i)
    {
        ffxReturnCode_t result;
        uint64_t after = 0;
        ffxContext c = Create(api, v.ids[i], true, &result, &after);
        MockContext who = c ? Who(api, c) : MockContext {};
        char what[160];
        snprintf(what, sizeof what, "picking '%s' creates it in build %s with that build's own id", v.names[i].c_str(),
                 expectTag[i]);
        Check(what, result == FFX_API_RETURN_OK && strcmp(who.tag, expectTag[i]) == 0 && who.versionId == expectId[i],
              std::string("got ") + who.tag);
        Check("  and leaves the caller's override as it was", after == v.ids[i]);

        api.configure(&c, nullptr);
        api.dispatch(&c, nullptr);
        api.dispatch(&c, nullptr);
        who = Who(api, c);
        Check("  configure and dispatch reach the same build", who.configured == 1 && who.dispatched == 2);

        ffxQueryGetProviderVersion pv {};
        pv.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        api.query(&c, &pv.header);
        Check("  its provider id comes back as the id that was picked", pv.versionId == v.ids[i]);

        Check("  and destroying it goes to that build", api.destroy(&c, nullptr) == FFX_API_RETURN_OK && c == nullptr);
    }

    // Two contexts alive at once in different builds: each keeps its own route.
    {
        ffxReturnCode_t r1, r2;
        ffxContext a = Create(api, v.ids[0], true, &r1);
        ffxContext b = Create(api, v.ids[3], true, &r2);
        api.dispatch(&b, nullptr);
        Check("two contexts in two builds at once keep their own routes",
              strcmp(Who(api, a).tag, "A") == 0 && strcmp(Who(api, b).tag, "B") == 0 && Who(api, b).dispatched == 1 &&
                  Who(api, a).dispatched == 0);
        api.destroy(&a, nullptr);
        api.destroy(&b, nullptr);
    }

    {
        ffxReturnCode_t result;
        ffxContext c = Create(api, 0, false, &result);
        Check("without an override the first build answers", result == FFX_API_RETURN_OK && strcmp(Who(api, c).tag, "A") == 0);
        api.destroy(&c, nullptr);
    }

    {
        ffxQueryDescGetVersions q {};
        q.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
        uint64_t capacity = 2;
        uint64_t ids[2];
        const char* names[2];
        q.outputCount = &capacity;
        q.versionIds = ids;
        q.versionNames = names;
        api.query(nullptr, &q.header);
        Check("a short buffer gets as many as fit, and says how many",
              capacity == 2 && ids[0] == v.ids[0] && ids[1] == v.ids[1]);
    }

    printf("--- the same binary under another FidelityFX DLL's name ---\n");
    Api fg = Load("amd_fidelityfx_framegeneration_dx12.dll");
    Versions fgv = fg.dll ? List(fg) : Versions {};
    Check("reads its own ini, so one binary can stand in for several DLLs side by side",
          Join(fgv.names) == "4.1.1-fast | 3.1.5 | 2.3.4", Join(fgv.names));

    printf("%s (%d passed, %d failed)\n", gFailures ? "FAILED" : "ALL PASS", gPasses, gFailures);
    return gFailures ? 1 : 0;
}
