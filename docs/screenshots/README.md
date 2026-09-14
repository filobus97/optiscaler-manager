# Screenshots

Rendered from the running application against a **sample game library** — synthetic
game folders holding minimal DLLs that carry real version resources, so the versions
shown are read by the same code that reads them on a real install.

The window is rendered to a bitmap under a virtual X server by the UI harness, so these
stay in step with the actual UI rather than being mocked up by hand. Regenerate them
after UI changes and read them: the "DLSS 3.7.10 shown as 3.7.1" bug, a clipped card
row, a dead ini key promised by the install preview and several stale tooltips were all
found exactly that way.

- `swap.png`, `swap-guard.png` and `swap-patched.png` run against real 64-bit DLLs in synthetic game
  folders, one carrying a newer build than the other, and against the live archive
  index — so the builds, the harvest, the refused cross-generation swap and the way out
  of a swap the game has patched are the real operations, not a mock-up.
- `storage.png` runs against a sample cache instead: component versions, imported DLLs
  and per-game backups written to a scratch config directory, so the sizes and the
  live/spent split come from the real scan.
