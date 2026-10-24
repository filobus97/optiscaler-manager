// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// A stand-in AMD driver DLL (amdxcffx64.dll): no ffx* exports, only UpdateFfxApiProvider.
// It behaves the way the real one is described to: it hands out its provider only when the
// signed-SDK lookups answer with its own module, it knows only struct version 2, and the
// version name it hands out lives in a buffer that every later call overwrites.

#include <windows.h>
#include <cstring>
#include "../driver_provider.h"
#include "mock_protocol.h"

namespace
{
constexpr uint64_t kProviderId = 0x80004110; // bit 31 set, like real external providers
HMODULE gSelf = nullptr;
char gNameBuffer[32] = "";
int gGetVersionsCalls = 0;

// Every call scribbles over the name buffer, so a caller that kept the pointer shows it.
void Scribble() { strcpy(gNameBuffer, "XXXX"); }

// The signature check: the SDK DLLs must be found, and they must be this very module.
// In the test process the real lookup finds the omniloader, so this fails without the
// import redirection.
bool LooksSigned()
{
    return GetModuleHandleW(L"amd_fidelityfx_upscaler_dx12.dll") == gSelf &&
           GetModuleHandleA("amd_fidelityfx_dx12.dll") == gSelf && GetModuleHandleW(L"AMD_FidelityFX_Loader_DX12") == gSelf &&
           // The file is renamed on disk; the driver still finds itself by its real name.
           GetModuleHandleW(L"amdxcffx64.dll") == gSelf && GetModuleHandleW(L"amdxcffx64") == gSelf &&
           // Everything else is left alone.
           GetModuleHandleW(L"kernel32.dll") != nullptr && GetModuleHandleW(L"kernel32.dll") != gSelf &&
           GetModuleHandleW(nullptr) != gSelf;
}

bool CanProvide(uint64_t descType) { return descType == kDriverUpscaleDescType; }

ffxReturnCode_t Create(ffxContext* context, ffxCreateContextDescHeader* desc, const ffxAllocationCallbacks*)
{
    Scribble();
    uint64_t want = kProviderId;
    for (auto* h = desc; h; h = h->pNext)
        if (h->type == FFX_API_DESC_TYPE_OVERRIDE_VERSION) want = reinterpret_cast<ffxOverrideVersion*>(h)->versionId;
    if (want != kProviderId) return FFX_API_RETURN_ERROR_PARAMETER;

    auto* c = new MockContext {};
    strcpy(c->tag, "DRV");
    c->versionId = want;
    *context = c;
    return FFX_API_RETURN_OK;
}

ffxReturnCode_t Destroy(ffxContext* context, const ffxAllocationCallbacks*)
{
    Scribble();
    delete static_cast<MockContext*>(*context);
    *context = nullptr;
    return FFX_API_RETURN_OK;
}

ffxReturnCode_t Configure(ffxContext* context, const ffxConfigureDescHeader*)
{
    Scribble();
    static_cast<MockContext*>(*context)->configured++;
    return FFX_API_RETURN_OK;
}

ffxReturnCode_t Query(ffxContext* context, ffxQueryDescHeader* desc)
{
    Scribble();
    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_VERSIONS)
    {
        ++gGetVersionsCalls; // the omniloader must never forward this
        return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
    }
    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION)
    {
        auto* q = reinterpret_cast<ffxQueryGetProviderVersion*>(desc);
        q->versionId = static_cast<MockContext*>(*context)->versionId;
        q->versionName = "4.1.2-driver";
        return FFX_API_RETURN_OK;
    }
    if (desc->type == kMockWhoQuery)
    {
        *reinterpret_cast<MockWhoQuery*>(desc)->context = *static_cast<MockContext*>(*context);
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t Dispatch(ffxContext* context, const ffxDispatchDescHeader*)
{
    Scribble();
    static_cast<MockContext*>(*context)->dispatched++;
    return FFX_API_RETURN_OK;
}
} // namespace

extern "C" {

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) gSelf = instance;
    return TRUE;
}

// How often the omniloader forwarded a version query, which it must not.
__declspec(dllexport) int MockDriverGetVersionsCalls() { return gGetVersionsCalls; }

__declspec(dllexport) HRESULT WINAPI UpdateFfxApiProvider(void* data, uint32_t size)
{
    Scribble();
    if (!data) return E_INVALIDARG;
    if (!LooksSigned()) return E_ACCESSDENIED;

    auto* d = static_cast<DriverExternalProviderData*>(data);
    if (d->structVersion == 3 && size != kDriverSizeV3) return E_INVALIDARG;
    if (d->structVersion <= 2 && size != kDriverSizeV1V2) return E_INVALIDARG;
    if (d->descType != kDriverUpscaleDescType) return S_OK; // not something it provides
    if (d->structVersion != 2) return d->structVersion > 2 ? S_OK : E_NOINTERFACE; // newer than it knows: nothing filled

    strcpy(gNameBuffer, "4.1.2-driver");
    d->provider.versionId = kProviderId;
    d->provider.versionName = gNameBuffer;
    d->provider.canProvide = CanProvide;
    d->provider.createContext = Create;
    d->provider.destroyContext = Destroy;
    d->provider.configure = Configure;
    d->provider.query = Query;
    d->provider.dispatch = Dispatch;
    return S_OK;
}
}
