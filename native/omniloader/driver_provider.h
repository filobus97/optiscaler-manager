// Upscaler Manager - omniloader
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// What AMD's driver DLLs (amdxcffx64.dll, amdvkffx64.dll) hand out through
// UpdateFfxApiProvider, as OptiScaler declares it. x64 layout; the asserts keep it honest.
#pragma once

#include <cstddef>
#include <cstdint>

#include "third_party/ffx_api.h"

// The upscaler's create-context description type, which the driver is asked about.
constexpr uint64_t kDriverUpscaleDescType = 0x00010000;

struct DriverProviderInterface
{
    uint64_t versionId;
    const char* versionName; // the driver formats this into a buffer later calls overwrite
    bool (*canProvide)(uint64_t descType);
    PfnFfxCreateContext createContext;
    PfnFfxDestroyContext destroyContext;
    PfnFfxConfigure configure;
    PfnFfxQuery query;
    PfnFfxDispatch dispatch;
};

struct DriverExternalProviderData
{
    uint32_t structVersion;
    uint64_t descType;
    DriverProviderInterface provider;
    bool (*isSupported)(const void* backend); // only struct version 3 and up
    void* reserved;
};

static_assert(sizeof(void*) == 8, "the driver structs are x64");
static_assert(offsetof(DriverExternalProviderData, provider) == 0x10, "provider offset");
static_assert(sizeof(DriverProviderInterface) == 0x40, "provider size");
static_assert(offsetof(DriverExternalProviderData, isSupported) == 0x50, "isSupported offset");
static_assert(sizeof(DriverExternalProviderData) == 0x60, "struct size");

// Struct versions 1 and 2 end where isSupported starts.
constexpr uint32_t kDriverSizeV1V2 = offsetof(DriverExternalProviderData, isSupported);
constexpr uint32_t kDriverSizeV3 = sizeof(DriverExternalProviderData);
