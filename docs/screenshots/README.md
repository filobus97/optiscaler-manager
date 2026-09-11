# Screenshots

Rendered from the running application against a **sample game library** — synthetic
game folders containing minimal DLLs that carry real version resources, so the
versions shown are read by the same code that reads them on a real install.

They are captured by rendering the window to a bitmap under a virtual X server, so
they stay in step with the actual UI rather than being mocked up by hand. Regenerate
them after UI changes; reviewing them is also a cheap way to catch stale wording —
the "DLSS 3.7.10 shown as 3.7.1" bug and several outdated tooltips were found exactly
that way.

`swap.png` is rendered against two synthetic game folders holding real 64-bit DLLs
with version resources, one carrying a newer build than the other — so the harvest,
swap and revert shown there are the real operations on real files, not a mock-up.

`storage.png` is rendered against a **sample cache** instead: component versions,
imported DLLs and per-game backups written to a scratch config directory, so the
sizes and the live/spent split are produced by the real scan rather than invented.
