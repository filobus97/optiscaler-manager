# Screenshots

Rendered from the running application against a **sample game library** — synthetic
game folders holding minimal DLLs that carry real version resources, so the versions
shown are read by the same code that reads them on a real install.

`games.png`, `details.png`, `install.png`, `install-faster-fsr4.png` and `settings.png`
come from `tests/UpscalerManager.UiHarness/run.sh`, which renders the real window
headlessly (no display needed) against a sample Steam library in a throwaway HOME — real
app IDs, so the covers are Steam's, and one game shipping Easy Anti-Cheat. Regenerate
them after UI changes and read them: the "DLSS 3.7.10 shown as 3.7.1" bug, a clipped
card row, a dead ini key promised by the install preview and several stale tooltips
were all found exactly that way.

The rest were made by an earlier harness that lived outside the repository and was lost;
their screens have not changed since.

- `swap.png`, `swap-guard.png` and `swap-patched.png` run against real 64-bit DLLs in synthetic game
  folders, one carrying a newer build than the other, and against the live archive
  index — so the builds, the harvest, the refused cross-generation swap and the way out
  of a swap the game has patched are the real operations, not a mock-up.
- `storage.png` runs against a sample cache instead: component versions, imported DLLs
  and per-game backups written to a scratch config directory, so the sizes and the
  live/spent split come from the real scan.
