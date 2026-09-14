# Swapping, and where it differs from DLSS Swapper

The swap route exists because OptiScaler is overkill for a great many games: if a game
already ships DLSS, dropping a newer `nvngx_dlss.dll` beside its executable upgrades it
with nothing hooked, nothing injected and nothing to configure.

[DLSS Swapper](https://github.com/beeradmoore/dlss-swapper) is the reference
implementation and has no Linux equivalent, which is the whole reason this route is
here. No code was copied. What follows is what was taken, and where this deliberately
differs.

## Taken, because it was simply right

- **A swap writes to every copy of the DLL in the game, not the first one found.** Its
  `UpdateDllAsync` loops over all assets of a type. This originally did not, and on a
  game carrying two copies — common in Unreal titles, which ship `nvngx_dlss.dll` both
  beside the executable and under `Engine/Binaries/ThirdParty` — the swap was a coin
  toss that half the time appeared to do nothing.
- **Hash before extract.** An archived download is checked against the hashes the index
  publishes before anything is installed, and the extracted DLL is checked again.
- **The archive itself.** 229 builds collected out of shipped games and vendor SDK zips
  over years — the part that could not be reimplemented. This app links to it and never
  mirrors it; see the README's download policy.
- **Import accepts zips as well as loose DLLs, and several files at once**, because
  every download in this space arrives as a zip.

## Deliberately different

- **The original goes into this app's external per-game backup store, not a sibling
  `.dlsss` file in the game folder.** A game verifying its files removes a stray
  sibling; the store survives that, and survives the game being reinstalled.
- **Filenames match case-insensitively.** DLSS Swapper compares exact case and relies on
  NTFS to paper over it — with the comment "the case of these files should never change,
  right?" above it. On the ext4 filesystems this app's primary platform uses, a game
  shipping `NvNgx_Dlss.dll` would simply never be seen. (Its zip extraction *does* use an
  ignore-case comparison, so the inconsistency is theirs rather than a blanket rule.)
- **A swap is refused outright where OptiScaler owns the same file**, rather than letting
  the two silently overwrite each other's backups. `libxess.dll` in particular is on both
  lists.
- **A running game is refused**, because a mapped DLL either fails to be replaced or is
  ignored until restart — either way the player is told they swapped something that did
  not change.
- **Cross-generation FidelityFX swaps are refused.** DLSS Swapper does not guard this;
  it is the one place where doing less than the reference would be worse than doing
  nothing. See [`fidelityfx.md`](fidelityfx.md).

## Not attempted

`NVAPIHelper` — 1136 lines of Windows-only driver DLSS preset control — is not ported.
There is no equivalent on Linux, and a setting that silently does nothing is worse than
its absence.
