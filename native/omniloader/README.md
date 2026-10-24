# omniloader

One FidelityFX DLL that holds several others. OptiScaler loads it in place of
`amd_fidelityfx_upscaler_dx12.dll`, and the omniloader loads every build you list. Their
versions all show up in one list in OptiScaler's overlay, under **FFX Upscaler**. You can
switch between them in-game, with no restart, the same way OptiScaler already switches
between the versions inside a single DLL.

Any upscaler DLL that has the five FidelityFX API functions can go in the list: AMD's
official builds, OptiScaler Client extras (4.1.1b, the INT8 builds), the FSR 4 shader
overrides (exact or lossy), or anything else built the same way.

AMD driver files can go in the list too: `amdxcffx64.dll` from AMD driver 26.10.1 or
newer. It carries every FSR 4 model, including the RX 6000 and Lite models. See
[Driver files](#driver-files).

## With the app

On the install screen, under **Advanced → Files to install**, pick **Several FSR builds,
switchable in-game** and tick the builds you want. The list holds every build the
sources switched on in Settings offer: OptiScaler's own, the faster FSR 4.1.1 builds
your GPU gets, each OptiScaler Client extras release, and the DLLs you imported. The app
puts the omniloader, the builds and the list in place, and **Remove** takes them away.
An AMD driver file you tick (imported in Settings) goes in the same folder as
`amdxcffx64_<label>.dll`. The Vulkan driver file cannot go behind the omniloader; the
install screen places it next to OptiScaler instead.

## Try it by hand

1. Find the folder OptiScaler loads the upscaler from. That is the game folder, or its
   `OptiScaler` subfolder on OptiScaler 0.9.5 and later.
2. Make a folder there, such as `omni\upscaler`, and put each build in it, renamed so
   its name **starts with `amd_fidelityfx_upscaler_dx12_`**:
   ```
   omni\upscaler\amd_fidelityfx_upscaler_dx12_official.dll
   omni\upscaler\amd_fidelityfx_upscaler_dx12_shader-overrides-exact.dll
   ```
   OptiScaler treats DLLs by name. On Linux it answers FSR 4's GPU check only for DLLs
   whose name starts with `amd`; a build named otherwise decides the GPU cannot run
   FSR 4 and offers none of its FSR 4 versions. And a DLL whose name *ends* like AMD's
   (`…\amd_fidelityfx_upscaler_dx12.dll`) is taken for the upscaler itself. The log
   warns about either.
3. Put `omniloader.dll` in place of `amd_fidelityfx_upscaler_dx12.dll`, renamed to that.
4. Beside it, write `amd_fidelityfx_upscaler_dx12.omni.ini`:
   ```ini
   ; The first build answers whenever nothing picks a version.
   [Modules]
   omni\upscaler\amd_fidelityfx_upscaler_dx12_official.dll = Official
   omni\upscaler\amd_fidelityfx_upscaler_dx12_shader-overrides-exact.dll = Shader overrides
   ```
   Paths are relative to the omniloader's folder; full paths work too. The text after
   `=` is a label. A version name only gets its label added when two builds report the
   same name, for example `4.1.1 [Shader overrides]`.
5. Start the game, open OptiScaler's overlay, and pick a version under FFX Upscaler.

The omniloader reads the ini named after its own file. You can put a second copy in
place of another FidelityFX DLL, such as `amd_fidelityfx_framegeneration_dx12.dll`, and
give it its own `.omni.ini`.

## Driver files

An AMD driver DLL has no FidelityFX API functions. It exports `UpdateFfxApiProvider`, which
hands out its FSR 4 as a set of functions. The omniloader asks for that and lists the one
version it gets, then routes calls to it like any other build.

Rename the file `amdxcffx64_<label>.dll` (for example `amdxcffx64_driver-26.10.1.dll`),
put it in the same folder as the builds, and list it like them:

```ini
omni\upscaler\amdxcffx64_driver-26.10.1.dll = Driver 26.10.1
```

Keep the name starting with `amdxcffx64_`, and not exactly `amdxcffx64.dll`: OptiScaler
loads its own copy under that name, and on Linux it only answers the driver's GPU check
for names starting with `amdxcffx64`. The log warns about both.

The driver hands out FSR 4 only when it sees signed FidelityFX DLLs in the process, and it
looks for them by name. In a game using the omniloader, that name is the omniloader, which
is unsigned. So the omniloader changes the driver's own lookups of `amd_fidelityfx_*` (and
of its own name) to answer with the driver's file, which carries the same signature. No
other DLL in the process is touched.

Only the DirectX 12 file (`amdxcffx64`) can be hosted, because the omniloader stands in for
the DirectX 12 upscaler. `amdvkffx64` (Vulkan) stays beside OptiScaler.

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

- **Driver files are untested on hardware.** Hosting them is only tried against a mock
  driver under Wine. Whether a real `amdxcffx64.dll` accepts the process this way, and
  what it does after, is not known yet. If it gives no FSR 4, the log says so and the file
  is skipped.
- **Nvidia spoofing can hide the GPU from a hosted driver.** OptiScaler pauses its own
  GPU spoofing while it asks its driver file for FSR 4; the omniloader cannot. With
  OptiScaler's Nvidia override on, a hosted driver may see a GPU that is not AMD's and
  give no FSR 4. The app leaves the override off unless asked.
- **FSR 4 model selection does not reach driver builds.** OptiScaler's `Fsr4Model` setting
  has no effect behind the omniloader, and the driver picks the model for the GPU itself.
- **OptiScaler's FSR 4 preset fixes** find their place in the upscaler by scanning the DLL
  it loaded. Behind the omniloader they scan the omniloader instead, find nothing, and do
  nothing. The upscalers still run with their own defaults.
- **Tested in one game so far.** Star Wars Outlaws on Linux (Proton), with eight builds
  behind it: OptiScaler's own, both faster 4.1.1 builds, and five OptiScaler Client
  extras releases. Each switch in the overlay landed on the build picked, as FSR 4's
  watermark showed, and frame rates matched the same build installed alone.

## Still to do

- **FSR 4 model override.** OptiScaler's FSR 4 model override (forcing a model, or
  correcting the preset a game sends) does not apply behind the omniloader, because it
  scans only the DLL OptiScaler loaded. Fixing it means OptiScaler also scanning the
  builds the omniloader loaded. Planned for a future release.

## Build

Needs MinGW-w64. Wine is needed for the tests.

```sh
./build.sh        # out/omniloader.dll
./build.sh test   # also builds the stand-in builds and runs the tests under Wine
./build.sh kit    # also packs out/omniloader-test-kit.zip
```

`third_party/ffx_api.h` is AMD's FidelityFX API header (MIT), from FidelityFX SDK 2.3.0.
