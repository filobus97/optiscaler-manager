# OptiScaler Manager

A deliberately **simple, AMD-focused** desktop frontend for the
[OptiScaler](https://github.com/optiscaler/OptiScaler) mod. It does one job well:
**install OptiScaler and get FSR 4 working in your games**, with almost no
decisions to make.

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
  single **Install OptiScaler** button downloads and installs the *real OptiScaler
  release from source* (`optiscaler/OptiScaler` on GitHub) — **latest by default,
  or any older release from the version selector** at the top of the dialog. The
  install dialog then decouples the independent choices:

  **Step 1 — Backend (which files to install):**
    - *Default — OptiScaler's own files* (**recommended**): OptiScaler's release already
      bundles a working **FSR 4.1.1** upscaler (INT8-capable since OptiScaler 0.9.4),
      so this alone enables FSR 4;
    - *Latest FSR SDK from AMD* — AMD's official open-source FidelityFX SDK (GPUOpen),
      **swapped in place**: only OptiScaler's own FSR DLLs already present in the game
      folder are replaced with the SDK's same-name equivalents — nothing new is added,
      and game-owned files (e.g. the game's own `amd_ags_x64.dll`) are never touched.
      Right after an OptiScaler release this is usually identical to *Default*; its
      value is picking up AMD's **newer** signedbin revisions before OptiScaler bundles
      them;
    - *FSR 4 INT8 (community build)* — a community INT8 build from the OptiScaler-Extras
      repo, at a **version you pick** (upstream still recommends **4.0.2c** for RDNA2
      on Windows);
    - *Custom DLLs + latest AMD SDK (merged)* — the latest AMD `signedbin` set as the
      base, with **your imported custom DLLs merged on top**: same-name DLLs overwrite
      AMD's, unknown names (e.g. `amdxcffx64.dll`) are **added alongside**. Everything
      is manifest-tracked, so *Revert* removes it all.

  **Step 2 — FSR 4 selection:** the Manager **always forces the flags that make FSR 4
  *available*** (`[FSR] Fsr4Update=true`); you then choose whether it also **selects**
  FSR 4 for you (`UpscalerIndex=0`) or leaves it **auto** so you pick it in OptiScaler's
  in-game overlay. Two optional toggles cover FSR 4.1.1's new GPU validation:
  **Force INT8 on unsupported GPUs** (`Fsr4ForceEnableInt8=true`, for RDNA2 / mobile
  RDNA3 / Intel / Nvidia — it can't help GPUs without INT8 support) and **Show the FSR4
  watermark** (`Fsr4EnableWatermark=true`) to verify on screen whether you're really
  getting FSR4 / FSR4-i8 or the silent FSR3 fallback.

  **Step 3 — Add-ons & extras:**
    - *fakenvapi* — `nvapi64.dll` + `fakenvapi.ini` from the
      [optiscaler/fakenvapi](https://github.com/optiscaler/fakenvapi) releases
      (auto-downloaded). Translates Nvidia Reflex into **AMD Anti-Lag 2 / LatencyFlex**;
    - *Nukem DLSSG-to-FSR3* — frame generation for games with DLSS-G
      (`dlssg_to_fsr3_amd_is_better.dll`, **bring-your-own** — import it once in
      Settings). Selecting it sets `[FrameGen] FGInput=nukems` and pulls fakenvapi in;
    - *Nvidia override* — for games that hide DLSS options on AMD/Intel. **Per game
      only** (chosen in this dialog on each install; no global setting), with a
      **method selector**: *Default* uses OptiScaler's built-in DXGI spoofing
      (`[Spoofing] Dxgi=true`, adapter reports as an RTX 4090); *OptiPatcher*
      installs the `plugins/OptiPatcher.asi` plugin (`[Plugins] LoadAsiPlugins=true`),
      which patches the game's vendor checks in memory instead.

  Plus the **`OptiScaler.ini`** to use — OptiScaler's default, or one of your saved
  profiles. When you pick a custom `.ini`, the options above overwrite **only the keys
  they affect** (`Fsr4Update`, `UpscalerIndex`, the optional toggles above, and the menu
  key); the rest of your `.ini` is left exactly as you wrote it.
- **Transparent — no black boxes.** Before anything is written, a live
  **"What will happen"** preview lists the *exact files* that will be placed next
  to your game and the *exact `OptiScaler.ini` keys* that will change (updating as
  you change the options), so you can verify it or reproduce it by hand.
- **Tooltip-rich.** Every control explains what it does.
- **Reversible.** *Revert* restores backed-up files from an external per-game
  backup store and reverts the ini keys.

### On AMD binaries

OptiScaler Manager can download AMD's **open-source (MIT) FidelityFX SDK** from AMD's
official GPUOpen repository, and community FSR 4 INT8 builds from the OptiScaler-Extras
repository — both are openly distributed. It **never downloads, bundles, or links to
the proprietary FSR 4 driver runtime `amdxcffx64.dll`**: that one is strictly
**bring-your-own**, supplied from a local file/folder/archive you already possess and
copied into a private cache. See [Importing your own DLLs](#importing-your-own-dlls-and-ini-profiles).

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

## Importing your own DLLs and `.ini` profiles

Open **Settings** to import:

- **Custom DLLs (one or more).** Pick individual `.dll` files (multi-select), a
  folder (searched recursively), or a `.zip`/`.7z`/`.rar` archive — every valid
  **64-bit** DLL is imported into a flat library (largest copy wins when a name is
  duplicated; re-importing a name replaces it; entries are individually deletable).
  At install time (the *Custom DLLs + latest AMD SDK* backend) they are **merged on
  top of the latest AMD `signedbin` set**: same names overwrite AMD's file, new
  names (e.g. `amdxcffx64.dll`) are added alongside. Legacy imports from older
  versions are migrated automatically.
- **Nukem's DLSSG-to-FSR3 DLL.** The frame-gen mod cannot be auto-downloaded — import
  `dlssg_to_fsr3_amd_is_better.dll` (or the mod archive) once, then tick the add-on
  per install. fakenvapi needs no import: it is downloaded from the
  optiscaler/fakenvapi releases when selected.
- **`OptiScaler.ini` profiles.** Import any `OptiScaler.ini`, tag it with a name,
  and it becomes selectable in the Install dialog. Collect as many as you like;
  delete them from Settings.
- **Overlay / menu key.** OptiScaler's in-game overlay opens with **Insert** by
  default, but not every keyboard has that key. Pick another (Home, End, F1–F12, …)
  in **Settings** and it is forced as `[Menu] ShortcutKey` on **every** install,
  including the default `.ini`.

When you click **Install OptiScaler**, the dialog lets you pick the backend
(Default / latest AMD SDK / INT8 community / custom-merged) and which `.ini`
profile to write. The Manager always sets `[FSR] Fsr4Update = true` and
`UpscalerIndex` per your Step-2 choice (these win over the chosen profile,
matching what is written to disk). You always see the exact file and ini changes
in the live preview first.

## Updating in place

The app **checks for new releases at launch** (and on demand from *Settings → About &
updates*): when a newer version exists, a dismissable banner offers the releases page
and reminds you of the in-place updater. The check is best-effort — offline it stays
silent.

Each release ships a self-contained updater next to the executable that pulls the
latest GitHub release and replaces the program **without touching your data** — all
settings, imported DLLs, `.ini` profiles, backups and the download cache live in
your OS config directory (`%APPDATA%\OptiscalerManager` on Windows,
`~/.config/OptiscalerManager` on Linux, `~/Library/Application Support/OptiscalerManager`
on macOS), *outside* the install folder.

Close the app first, then from the install folder run:

```bash
# Linux / macOS
sh update.sh                 # --force to reinstall, --dir <path> to target another install

# Windows (PowerShell)
powershell -ExecutionPolicy Bypass -File update.ps1   # -Force / -Dir <path>
```

The updater detects your platform, compares the bundled `VERSION` with the latest
release, downloads the matching `OptiscalerManager-<version>-<rid>.zip`, and swaps
the files in place. The scripts also live in [`scripts/`](scripts/) if you want to
run them standalone.

## Native Wayland (Linux)

The app targets **Avalonia 12.1** and uses its **native Wayland backend** when run
under a Wayland session (detected via `WAYLAND_DISPLAY`), falling back to X11
otherwise. The Wayland backend is officially *experimental* upstream; if you hit a
rendering issue, force X11 by unsetting `WAYLAND_DISPLAY` (e.g. run through
XWayland).

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
