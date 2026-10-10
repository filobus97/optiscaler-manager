// Upscaler Manager - omniloader tests
// A DLL shaped like amdxcffx64.dll: no FFX API, only the driver-provider entry point.
#include <windows.h>
extern "C" __declspec(dllexport) HRESULT UpdateFfxApiProvider(void*, unsigned) { return E_NOINTERFACE; }
