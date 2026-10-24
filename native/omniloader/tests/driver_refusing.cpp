// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// A driver DLL that gives no upscaler: it answers every struct version with S_OK and fills
// nothing, like a driver older than 26.10.1 or one that refused the process.
#include <windows.h>
extern "C" __declspec(dllexport) HRESULT WINAPI UpdateFfxApiProvider(void*, unsigned) { return S_OK; }
