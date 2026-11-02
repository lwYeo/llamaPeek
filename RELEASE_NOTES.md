# llamaPeek 0.1.1

Bug-fix release: the configuration dialog now renders correctly at every
Windows display scaling (100–250%).

## Fixed

- **Config dialog DPI scaling** — the dialog layout no longer distorts at
  high scaling (fonts scaled while the layout stayed at design pixels);
  scaling is now applied once from a fixed 96dpi baseline.
- **Server grid width** — the four columns fill the full grid width (no
  grey band on the right) and the "Required" column is never clipped at
  any scaling; a post-layout guard widens the dialog if a font renders
  wider than the design.
- **Grid row heights** scale with DPI and match the settings rows' cadence.
- **Dialog size** — content-sized (no dead space at the bottom), width
  trimmed to 640px.
- **Unit tests no longer touch the live registry** — the registry
  round-trip tests previously overwrote `HKCU\Software\llamaPeek` (and
  deleted a stored API key) on every `dotnet test` run; they now use a
  sandboxed key (`HKCU\Software\llamaPeek-test`).

# llamaPeek 0.1.0

First release. Tray-only Windows monitor for llama.cpp-compatible inference
servers (llama.cpp, Strata, and compatible routers).

## What's new

- **Three live tray icons** — combined decode speed (TG, t/s), combined
  prefill speed (PP, t/s), and prefill progress (%). Faces refresh every
  500 ms; hover for max values and current/total tokens.
- **Multi-server aggregation** — add any number of servers; each is polled
  independently with auto-detection of llama.cpp (`/slots`) vs Strata
  (`/metrics`) and exponential backoff when unreachable.
- **Configuration dialog** — servers grid, poll cadence (active/idle),
  grace, refresh, linger, smoothing, and launch-at-startup. Saving applies
  immediately via an internal restart.
- **API key security** — bearer token stored as a DPAPI (current-user) blob
  in the registry; the dialog field is masked, copy/cut disabled, and the
  right-click menu suppressed. Paste-in allowed.
- **Disconnect indicator** — a red dot appears on all icons when a
  *Required* server is unreachable.
- **Menu** — About, Configuration, Restart, Exit, and Exit and delete
  configuration (destructive, confirmation required, No is the default
  button).

## Download

Grab `llamaPeek.exe` (single file, ~47 MB, no .NET runtime needed) from the
assets below. Verify the download:

```powershell
Get-FileHash .\llamaPeek.exe -Algorithm SHA256
```

Compare against `SHA256SUMS.txt`.

## Known limitations

- Windows tray icons are 16 px (scaled by DPI); the faces are as large as
  Windows allows.
- Tray tooltips are a snapshot at pop-up time — move the mouse away and
  re-hover for fresh values.
- Short prefills (<8k tokens) on Strata report prompt progress only at
  prefill end, so PP may show `--` for brief prompts.
- No prompt text is ever read, logged, or stored — counters only.
