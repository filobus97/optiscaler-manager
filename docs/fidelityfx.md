# AMD's FidelityFX runtime, as this app has to see it

Everything here was verified against AMD's own documentation and signed binaries. It
lives in `docs/` rather than in comment headers because it is the reasoning behind
several decisions, not a description of any one member — but a decision made without it
looks arbitrary and will get "corrected".

## The SDK 2 split, and why one filename means two things

Before FidelityFX SDK 2.0.0, `amd_fidelityfx_dx12.dll` was a monolith holding every
effect, upscaling included. From 2.0.0 the effects were split into a module each behind
a loader, and AMD's documentation is explicit:

> Starting with AMD FidelityFX™ SDK 2.0.0 the effects, previously combined in
> amd_fidelityfx_dx12.dll, are split into multiple DLLs based on effect type… A small
> loader DLL, not containing any effect code… It is interface- and behavior-compatible
> with amd_fidelityfx_dx12.dll. Applications previously loading amd_fidelityfx_dx12.dll
> must now load amd_fidelityfx_loader_dx12.dll instead.

Because the loader is deliberately compatible with the old name, a game migrating to
SDK 2 can keep shipping the old filename — and in the wild, they do. So that one name
covers two incompatible generations, and telling them apart is not optional: SDK 1
effects are deprecated to SDK 1, so dropping a monolith over a loader leaves SDK 1 code
trying to drive SDK 2 modules, and the reverse leaves a game calling into a loader with
no modules beside it. Either way the game ends up with no upscaler.

Both sources this app offers for that filename — the DLSS Swapper archive and another
game's own files — can hold either generation, which is why
`FidelityFxLayout.Identify` reads the evidence out of the file rather than trusting the
name, and `DllSwapService.CrossGenerationReason` refuses the mismatch inside the swap
itself.

## What the real binaries say

Read out of AMD's signed builds in `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` at tag
`v2.3.0`, whose release notes state it contains FSR Upscaling 4.1.1:

| File | File version | Provider table |
| --- | --- | --- |
| `amd_fidelityfx_loader_dx12.dll` | `2.3.0.2740` — the SDK version | empty (26 KB) |
| `amd_fidelityfx_upscaler_dx12.dll` | `4.1.1.2740` | 4.1.1 / 3.1.5 / 2.3.4 |
| `amd_fidelityfx_framegeneration_dx12.dll` | `4.0.1.2740` | 4.0.1 / 3.1.7 / 3.1.6 |
| `amd_fidelityfx_denoiser_dx12.dll` | `1.2.0.2740` | — |
| `amd_fidelityfx_radiancecache_dx12.dll` | `0.9.0.2740` | — |

Across the SDK 2 tags: v2.0.0 loader `1.0.2.44888` with upscaler 4.0.2; v2.1.0 and
v2.1.1 loader `2.1.0.604` with upscaler 4.0.3; v2.2.0 loader `2.2.0.0` with upscaler
4.1.0; v2.3.0 as above.

Two conclusions shape the code. For an SDK 2 module the file version **is** the effect
version, so nothing needs translating. For the loader the file version is the SDK
version and the FSR version is not in that file at all — showing it as though it were an
FSR version is exactly the confusion the labelling exists to prevent.

## Reading the FSR version without executing anything

For every other swappable DLL the version resource is the version a player recognises:
`nvngx_dlss.dll` reporting `310.9.1.0` means DLSS 310.9.1. The SDK 1 FidelityFX
runtimes do not work that way — AMD stamps them with an SDK build number, so the library
providing **FSR 3.1.2** reports itself as `1.0.1.38338`, a number that appears nowhere
in AMD's documentation and looks like a downgrade next to a game's FSR 3 library.

DLSS Swapper solves this by loading the DLL and calling `ffxQuery` for the provider
version table. That is not available here: these are Windows PEs and this app's primary
platform is Linux, so there is nothing to load them into. Two routes work without
executing anything:

1. The archive index records the FSR version for every build it holds, keyed by MD5.
   That is AMD's own label, and it is what `DllRepositoryService` supplies.
2. Failing that, the provider table is a run of NUL-terminated ASCII strings in the
   binary's data and can be read out directly — see `FidelityFxVersion.FromProviderTable`
   for the constraints that make that safe.

Both paths were checked against every FidelityFX build the archive holds — nine DX12 and
seven Vulkan — and all sixteen resolved exactly.

## Which provider runs, and why only "newest" is exact

`[FSR] UpscalerIndex` selects which provider the FidelityFX upscaler uses. AMD's
`GetProviderVersions` (in `Kits/FidelityFX/api/internal/ffx_provider.h`) ends with an
insertion sort commented *"Sort the returned versions by version ids so newest version
is at beginning of array"*, so **index 0 is always the newest and is exact**. Any other
index cannot be computed offline: the same function filters by `IsSupported(device)`,
and on DX12 the driver injects a provider of its own, so the list is built at run time
from hardware this app cannot see.

`[FSR] Fsr4Update`, which older versions of this app wrote unconditionally, no longer
exists in OptiScaler — zero references anywhere in its source. It is written only when
the release being installed still documents the key in its own ini.

## Why FSR 4 is not reachable by swapping

FSR 4 lives in `amd_fidelityfx_upscaler_dx12.dll`. Games older than SDK 2 do not ship
that file, and a swap can only replace a file a game already has. The archive holds no
FSR 4 either. OptiScaler brings its own upscaler instead, which is why FSR 4 is the
install route's job and the swapper does not pretend to compete.
