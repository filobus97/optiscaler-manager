// Upscaler Manager - omniloader tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).
//
// A DLL that is neither an SDK build nor a driver: it exports nothing the omniloader uses.
#include <windows.h>
extern "C" __declspec(dllexport) int NotAnUpscaler() { return 0; }
