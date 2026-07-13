# OptiScaler Manager

A deliberately **simple, AMD-focused** desktop frontend for the
[OptiScaler](https://github.com/optiscaler/OptiScaler) mod. It does one job well:
**get FSR 4 working in your games**, with almost no decisions to make.

OptiScaler Manager is a sibling to
[**OptiScaler Client Next**](https://github.com/filobus97/Optiscaler-Client)
(a fork of OptiScaler Client by [Agustinm28](https://github.com/Agustinm28)). It
**reuses that project's proven service layer** — the download/import, install and
backup engines — but wraps it in a much smaller UI: one screen, one primary
action per game, advanced options tucked away.

> **Primary platform: Linux.** Windows is fully supported as the secondary
> target; macOS at least runs the UI. Windows-only install paths are guarded.

---

## What it is (and what it deliberately isn't)

- **One primary screen.** A detected-GPU banner and your game list. Per game, a
  single **Enable FSR 4** button that applies AMD-sane defaults (latest
  OptiScaler + your imported custom DLL/SDK if present).
- **Transparent — no black boxes.** Before anything is written, a
  **"What will happen"** preview lists the *exact files* that will be placed next
  to your game and the *exact `OptiScaler.ini` keys* that will change, so you can
  verify it or reproduce it by hand.
- **Tooltip-rich.** Every control explains what it does.
- **Reversible.** *Revert* restores backed-up files from an external per-game
  backup store and reverts the ini keys.

### It never ships proprietary AMD binaries

OptiScaler Manager **never downloads, bundles, or links to** proprietary AMD
binaries (`amdxcffx64.dll`, `amd_fidelityfx_*.dll`). Custom DLLs are strictly
**bring-your-own**: you supply a local file/folder/archive you already possess,
and the app copies it into a private cache for installs. See
[Importing your own DLLs](#importing-your-own-dlls).

---

## How it works under the hood

The interesting design choice is that **components are modelled as data**, not as
per-screen glue. Each component (OptiScaler core, the FSR 4 INT8 backend, your
custom `amdxcffx64.dll`, your custom FSR SDK, fakenvapi, Nukem frame-gen,
OptiPatcher) declares its **id, target files, ini keys, and conflicts** in a
small [component registry](src/OptiscalerManager.Core/Components/ComponentRegistry.cs).

Both of these are *derived* from that registry, with no bespoke logic:

- **Mutual exclusion** — e.g. the FSR 4 INT8 "Extras" backend and a custom FSR
  SDK both write `amd_fidelityfx_upscaler_dx12.dll`, so they're automatically
  recognised as incompatible.
- **The "What will happen" preview** — the file and ini-key lists you see are the
  exact data the installer acts on.

### Project layout

| Project | What it is |
| --- | --- |
| `src/OptiscalerManager.Core` | UI-agnostic service layer ported from OptiScaler Client + the component registry. No Avalonia dependency. |
| `src/OptiscalerManager.App` | The Avalonia UI: one screen, the preview dialog, the import settings. |
| `tests/OptiscalerManager.Core.Tests` | xUnit tests for the pure logic (PE inspection, ini editing, version gate, FSR SDK scan) and the registry. |

The Core layer was decoupled from the source project's two UI touchpoints:

- `DebugWindow.Log` → an injected [`ILog`](src/OptiscalerManager.Core/Logging/ILog.cs)
  via a small static `Log` facade.
- the in-service NukemFG file dialog → an
  [`IManualComponentProvider`](src/OptiscalerManager.Core/Prompts/IManualComponentProvider.cs)
  callback the host implements.

---

## Building & running

Requires the **.NET 10 SDK**.

```bash
# Build everything
dotnet build OptiscalerManager.slnx -c Release

# Run the tests
dotnet test tests/OptiscalerManager.Core.Tests/OptiscalerManager.Core.Tests.csproj -c Release

# Run the app (framework-dependent, for development)
dotnet run --project src/OptiscalerManager.App/OptiscalerManager.App.csproj
```

To produce a self-contained single-file build for your platform:

```bash
dotnet publish src/OptiscalerManager.App/OptiscalerManager.App.csproj \
  -c Release -r linux-x64 --self-contained true -o publish
# RIDs: linux-x64 (primary), win-x64, osx-x64, osx-arm64
```

---

## Importing your own DLLs

Open **Settings / Import DLLs** and import either:

- **A single `amdxcffx64.dll`** (the FSR 4.x INT8 runtime). It is validated as a
  64-bit Windows PE and copied into the cache.
- **An FSR SDK package**, either from a **`.zip`/`.7z` archive** *or* an
  **already-extracted folder**. The importer recursively collects the DLL set —
  the upscaler (`amd_fidelityfx_upscaler_dx12.dll`, **required**) plus frame
  generation and companions — **skipping 32-bit copies** and **preferring
  'signed' builds**.

Once imported, **Enable FSR 4** installs your component instead of OptiScaler's
built-in FSR path. If nothing is imported, Enable FSR 4 installs the latest
OptiScaler and switches it to the FSR 4 backend (`[FSR] UpscalerIndex = 0`,
`[FSR] Fsr4Update = true`).

You always see the exact file and ini changes in the preview first.

---

## Cutting a release

CI (`.github/workflows/ci.yml`) builds and tests on `linux-x64` and `win-x64` for
every push/PR to `main`. Releases (`.github/workflows/release.yml`) publish
self-contained single-file builds for **`linux-x64`, `win-x64`, `osx-x64`,
`osx-arm64`**, zip each as `OptiscalerManager-<version>-<rid>.zip`, and attach
them to an auto-created GitHub Release.

A release can be cut three ways:

1. **Push a tag** `v<version>` (e.g. `v0.1.0`).
2. **Push to `main` with `[release]`** in the commit message — the version is
   read from `src/OptiscalerManager.App/OptiscalerManager.App.csproj`, and the
   tag is created for you. (Useful when the environment blocks direct tag pushes.)
3. **Run the *Release* workflow manually** (`workflow_dispatch`) and pass the
   version.

---

## Attribution & license

**License: [GPL-3.0-or-later](LICENSE).**

OptiScaler Manager is built on the work of others and preserves their attribution:

- The reused service layer comes from **OptiScaler Client** by
  **[Agustín Montaña (Agustinm28)](https://github.com/Agustinm28)**, via the
  **OptiScaler Client Next** fork
  ([filobus97/Optiscaler-Client](https://github.com/filobus97/Optiscaler-Client)).
- The mod this app configures is **OptiScaler**, by the
  [upstream OptiScaler team](https://github.com/optiscaler/OptiScaler).

This program is distributed in the hope that it will be useful, but **WITHOUT ANY
WARRANTY**; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.
