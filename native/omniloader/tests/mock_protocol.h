// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
#pragma once
#include <cstdint>
#include "../third_party/ffx_api.h"

// What a mock context remembers, read back through a query type only the mocks know.
struct MockContext
{
    char tag[4];
    uint64_t versionId;
    int configured;
    int dispatched;
};

constexpr uint64_t kMockWhoQuery = 0x7E570001;

struct MockWhoQuery
{
    ffxQueryDescHeader header;
    MockContext* context;
};
