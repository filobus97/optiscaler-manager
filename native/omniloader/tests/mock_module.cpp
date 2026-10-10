// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// A stand-in FidelityFX DLL: it reports a fixed version list and records which build and
// version a context was created with, so the tests can see where each call went.
// Built several times with a different MOCK_VARIANT.

#include <windows.h>
#include <cstring>
#include "../third_party/ffx_api.h"
#include "mock_protocol.h"

namespace
{
struct Version { uint64_t id; const char* name; };

#if MOCK_VARIANT == 1   // AMD-like: FSR 4 plus the FSR 3 and 2 every build carries
const char* kTag = "A";
const Version kVersions[] = { { 0x4011, "4.1.1" }, { 0x3015, "3.1.5" }, { 0x2034, "2.3.4" } };
#elif MOCK_VARIANT == 2 // a modified build: its own name for the same FSR 4 id
const char* kTag = "B";
const Version kVersions[] = { { 0x4011, "4.1.1-fast" }, { 0x3015, "3.1.5" }, { 0x2034, "2.3.4" } };
#elif MOCK_VARIANT == 3 // a different build that calls itself exactly what A does
const char* kTag = "C";
const Version kVersions[] = { { 0x4011, "4.1.1" }, { 0x3015, "3.1.5" }, { 0x2034, "2.3.4" } };
#else                   // same name as A's FSR 4, different id
const char* kTag = "D";
const Version kVersions[] = { { 0x4012, "4.1.1" } };
#endif
constexpr uint64_t kCount = sizeof kVersions / sizeof kVersions[0];
} // namespace

extern "C" {

__declspec(dllexport) ffxReturnCode_t ffxCreateContext(ffxContext* context, ffxCreateContextDescHeader* desc,
                                                       const ffxAllocationCallbacks*)
{
    uint64_t want = kVersions[0].id; // no override: the newest, as AMD's builds do
    for (auto* h = desc; h; h = h->pNext)
        if (h->type == FFX_API_DESC_TYPE_OVERRIDE_VERSION) want = reinterpret_cast<ffxOverrideVersion*>(h)->versionId;

    for (uint64_t i = 0; i < kCount; ++i)
        if (kVersions[i].id == want)
        {
            auto* c = new MockContext {};
            strcpy(c->tag, kTag);
            c->versionId = want;
            *context = c;
            return FFX_API_RETURN_OK;
        }
    return FFX_API_RETURN_ERROR_PARAMETER; // an id this build never offered
}

__declspec(dllexport) ffxReturnCode_t ffxDestroyContext(ffxContext* context, const ffxAllocationCallbacks*)
{
    delete static_cast<MockContext*>(*context);
    *context = nullptr;
    return FFX_API_RETURN_OK;
}

__declspec(dllexport) ffxReturnCode_t ffxConfigure(ffxContext* context, const ffxConfigureDescHeader*)
{
    static_cast<MockContext*>(*context)->configured++;
    return FFX_API_RETURN_OK;
}

__declspec(dllexport) ffxReturnCode_t ffxQuery(ffxContext* context, ffxQueryDescHeader* desc)
{
    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_VERSIONS)
    {
        auto* q = reinterpret_cast<ffxQueryDescGetVersions*>(desc);
        uint64_t capacity = *q->outputCount;
        if (capacity == 0 || (!q->versionIds && !q->versionNames)) { *q->outputCount = kCount; return FFX_API_RETURN_OK; }
        uint64_t n = capacity < kCount ? capacity : kCount;
        for (uint64_t i = 0; i < n; ++i)
        {
            if (q->versionIds) q->versionIds[i] = kVersions[i].id;
            if (q->versionNames) q->versionNames[i] = kVersions[i].name;
        }
        *q->outputCount = n;
        return FFX_API_RETURN_OK;
    }
    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION)
    {
        auto* q = reinterpret_cast<ffxQueryGetProviderVersion*>(desc);
        q->versionId = static_cast<MockContext*>(*context)->versionId;
        q->versionName = "mock";
        return FFX_API_RETURN_OK;
    }
    if (desc->type == kMockWhoQuery)
    {
        auto* q = reinterpret_cast<MockWhoQuery*>(desc);
        *q->context = *static_cast<MockContext*>(*context);
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

__declspec(dllexport) ffxReturnCode_t ffxDispatch(ffxContext* context, const ffxDispatchDescHeader*)
{
    static_cast<MockContext*>(*context)->dispatched++;
    return FFX_API_RETURN_OK;
}
}
