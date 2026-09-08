# Screenshots

Rendered from the running application against a **sample game library** — synthetic
game folders containing minimal DLLs that carry real version resources, so the
versions shown are read by the same code that reads them on a real install.

They are captured by rendering the window to a bitmap under a virtual X server, so
they stay in step with the actual UI rather than being mocked up by hand. Regenerate
them after UI changes; reviewing them is also a cheap way to catch stale wording —
the "DLSS 3.7.10 shown as 3.7.1" bug and several outdated tooltips were found exactly
that way.
