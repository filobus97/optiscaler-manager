# omniloader (experimental)

One FidelityFX DLL that holds several others. OptiScaler loads it in place of
`amd_fidelityfx_upscaler_dx12.dll`, and the omniloader loads every build you list. Their
versions all show up in one list in OptiScaler's overlay, under **FFX Upscaler**. You can
switch between them in-game, with no restart, the same way OptiScaler already switches
between the versions inside a single DLL.

Any upscaler DLL that has the five FidelityFX API functions can go in the list: AMD's
official builds, OptiScaler Client extras (4.1.1b, the INT8 builds), the FSR 4 shader
overrides (exact or lossy), or anything else built the same way.

## Try it by hand

1. Find the folder OptiScaler loads the upscaler from. That is the game folder, or its
   `OptiScaler` subfolder on OptiScaler 0.9.5 and later.
2. Make a folder there, such as `omni\upscaler`, and put each build in it. Give each one
   its own file name, or its own subfolder with the name unchanged:
   ```
   omni\upscaler\fsr-official.dll
   omni\upscaler\fsr-4.1.1b.dll
   omni\shader-overrides\amd_fidelityfx_upscaler_dx12.dll
   ```
3. Put `omniloader.dll` in place of `amd_fidelityfx_upscaler_dx12.dll`, renamed to that.
4. Beside it, write `amd_fidelityfx_upscaler_dx12.omni.ini`:
   ```ini
   ; The first build answers whenever nothing picks a version.
   [Modules]
   omni\upscaler\fsr-official.dll = Official
   omni\upscaler\fsr-4.1.1b.dll = 4.1.1b
   omni\shader-overrides\amd_fidelityfx_upscaler_dx12.dll = Shader overrides
   ```
   Paths are relative to the omniloader's folder; full paths work too. The text after
   `=` is a label. A version name only gets its label added when two builds report the
   same name, for example `4.1.1 [4.1.1b]`.
5. Start the game, open OptiScaler's overlay, and pick a version under FFX Upscaler.

The omniloader reads the ini named after its own file. You can put a second copy in
place of another FidelityFX DLL, such as `amd_fidelityfx_framegeneration_dx12.dll`, and
give it its own `.omni.ini`.

## What it writes

`amd_fidelityfx_upscaler_dx12.omni.log`, beside the DLL. It lists each build it loaded or
skipped, and why, every version it offered, and which build each upscaler was created in.
Send it along with any report.

## How the list is built

- The first build's versions are listed as they are.
- Each later build adds the versions the list does not already have.
- When a later build reports a name that is already in the list, but with a different
  version id, it is listed again with its label added.
- A build that would add nothing (it has the same names and ids as one before it) still
  gets the first version it reports listed with its label, so every build can be picked.

Each entry gets its own id. When OptiScaler creates the upscaler with one of them, the
omniloader hands the call to the build that offered it, with that build's own id, and sends
every later call for that upscaler to the same build.

## What does not work, or is untested

- **Driver-style builds** (`amdxcffx64.dll`, which export `UpdateFfxApiProvider`) are not
  hosted. They are skipped, with a note in the log. OptiScaler loads one of those itself,
  and lists it beside the omniloader's list.
- **OptiScaler's FSR 4 preset fixes** find their place in the upscaler by scanning the DLL
  it loaded. Behind the omniloader they scan the omniloader instead, find nothing, and do
  nothing. The upscalers still run with their own defaults.
- **Not yet run on a GPU.** The routing is tested under Wine with stand-in builds
  (`./build.sh test`). Real builds are not, and one that looks itself up by its file name
  could behave differently.

## Build

Needs MinGW-w64. Wine is needed for the tests.

```sh
./build.sh        # out/omniloader.dll
./build.sh test   # also builds the stand-in builds and runs the tests under Wine
```

`third_party/ffx_api.h` is AMD's FidelityFX API header (MIT), from FidelityFX SDK 2.3.0.
