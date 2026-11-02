# llamaPeek 0.2.1

## Fixed

- **Launch at startup could be silently deleted by test runs** — the
  registry tests sandboxed the app key but not the launch-at-startup Run
  value, so running the test suite removed the real autostart entry
  (the config dialog still showed the box checked). The Run value is now
  sandboxed in tests; a test run cannot touch it.
- **Remaining tray-icon crash (`ObjectDisposedException: 'Icon'`)** —
  evicted cache icons are no longer disposed synchronously (they are
  retired and disposed one paint later, so nothing is disposed while
  being assigned/read), and any residual disposed-object race now skips
  a repaint instead of showing the crash dialog.

# llamaPeek 0.2.0

Changes since 0.1.1.

## Changed

- **The "Required" checkbox was split into two columns** — it previously did
  double duty (alert dot + router `?model=` mode). The servers grid now has:
  - **Model** — router builds whose `/slots` needs `?model=<loaded model>`
    (auto-discovered). Opt-in, no alert side-effect.
  - **Required** — unreachable server lights the red dot on all icons.
  **Migration:** existing configs with the old checkbox ticked keep their
  alert behavior (the flag maps to **Required**; router mode becomes
  opt-in). The migration runs once on first load and is marked in the
  registry immediately, so an explicit Model tick is never re-migrated.
- **Server names must not contain `|`** — it is the internal state-key
  separator; such names collided with other servers' slot state.
- **Baseline reset on poll failure (intended behavior):** a server whose
  poll fails drops its slot baselines, so after recovery PP/TG rates
  re-warm from zero. This is the trade that guarantees a slot dying
  mid-task can never linger in the icons; expect a brief ramp-up after
  timeouts.

## Fixed

- **Tray-icon crash (`ObjectDisposedException: 'Icon'`)** — the icon cache
  eviction could dispose the icon a tray icon was currently displaying;
  live icons are now excluded from eviction.
- **Phantom rates after a server dies mid-task** — slot state is now dropped
  when a poll fails or a slot disappears from a response, so PP/TG icons and
  the active-count badge no longer keep a dead slot's last value forever.
- **Frozen PP/TG after a llama.cpp restart (builds without `id_task`)** —
  counter regression (counters zeroed) now resets baselines immediately
  instead of waiting for counters to climb past the old high-water.
- **About dialog stacking** — rapid menu clicks queued one dialog box per
  click; only one About can be open at a time now.
- First snapshot paint no longer dropped at startup; Restart clears stale
  linger values; minor GDI object lifetime fixes.

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
