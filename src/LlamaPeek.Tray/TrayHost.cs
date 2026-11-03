using LlamaPeek.Core;

namespace LlamaPeek.Tray;

/// <summary>Three tray icons + the shared right-click menu. No main window.</summary>
public sealed class TrayHost : ApplicationContext
{
    private readonly NotifyIcon _tg = new(), _pp = new(), _pf = new();
    private readonly ContextMenuStrip[] _menus = [new(), new(), new()];
    private readonly IconRenderer _renderer = new();
    private readonly Form _hidden = new();
    private readonly StatsMonitor _monitor;
    private AppSettings _settings;

    public TrayHost()
    {
        _hidden.ShowInTaskbar = false;
        _hidden.WindowState = FormWindowState.Minimized;
        _hidden.Load += (_, _) => _hidden.Hide();

        // One menu PER icon: a single ContextMenuStrip shared across NotifyIcons
        // deadlocks when opened from the 2nd/3rd icon (menu "hangs").
        foreach (var m in _menus)
        {
            m.Items.Add("About", null, OnAbout);
            m.Items.Add("Configuration", null, OnConfig);
            m.Items.Add("Restart", null, OnRestart);
            m.Items.Add("Exit", null, OnExit);
            m.Items.Add("Exit and delete configuration", null, OnExitAndPurge);
        }

        foreach (var (icon, tip) in new[] { (_tg, "llamaPeek TG"), (_pp, "llamaPeek PP"), (_pf, "llamaPeek Prefill") })
        {
            icon.Text = tip;
            icon.Visible = true;
            icon.Icon = _renderer.Render("--", "", IconRenderer.Gray);
        }
        // Same menu contents on all three icons.
        _tg.ContextMenuStrip = _menus[0];
        _pp.ContextMenuStrip = _menus[1];
        _pf.ContextMenuStrip = _menus[2];

        var settings = RegistryStore.Load();
        _settings = settings;
        _monitor = new StatsMonitor(settings, RegistryStore.LoadApiKey);
        _monitor.SnapshotUpdated += snap =>
        {
            // Marshal to UI thread; a snapshot racing with shutdown must not
            // crash the pool thread with ObjectDisposedException.
            try { _hidden.BeginInvoke(() => Paint(snap)); }
            catch { /* form disposed mid-flight — exiting */ }
        };

        _hidden.FormClosed += (_, _) =>
        {
            // Icons vanish immediately (no ghost slots), UI thread never waits
            // on an in-flight HTTP poll: cancellation is requested and the
            // loops finish as the process exits.
            _stopping = true;
            _tg.Visible = _pp.Visible = _pf.Visible = false;
            _tg.Dispose(); _pp.Dispose(); _pf.Dispose();
            foreach (var m in _menus) m.Dispose(); _renderer.Dispose();
            _ = _monitor.DisposeAsync().AsTask().ContinueWith(
                _ => ExitThread(), TaskScheduler.Default);
        };
        _hidden.Show();
        // Start after Show: BeginInvoke on a not-yet-created handle throws,
        // and the first snapshot's paint would be silently dropped.
        _monitor.Start();
    }

    private bool _stopping;

    private void Paint(Snapshot s)
    {
        // A snapshot marshaled just before exit can run after FormClosed
        // disposed the icons — skip it (ObjectDisposedException on Icon).
        if (_stopping) return;

        // Idle faces are the phase color greyed out (50% blend), not red;
        // unreachability stays visible in the tooltips.
        var tgColor = s.TgActive > 0 ? IconRenderer.Green : Blend(IconRenderer.Green, IconRenderer.Gray);
        var ppColor = s.PpActive > 0 ? IconRenderer.Amber : Blend(IconRenderer.Amber, IconRenderer.Gray);

        // Prefill retains its last value on idle (stays blue); PP/TG values
        // linger LingerMs past idle so the handover is readable.
        var (tg, pp) = _linger.Apply(s, TimeSpan.FromMilliseconds(_settings.LingerMs), DateTime.UtcNow);

        try
        {
            _tg.Icon = _renderer.Render(CapText(tg), Badge(s.TgActive), tgColor, "TG", s.RequiredUnreachable);
            _pp.Icon = _renderer.Render(CapText(pp), Badge(s.PpActive), ppColor, "PP", s.RequiredUnreachable);
            _pf.Icon = _renderer.Render(PctText(_linger.Pct), "%", IconRenderer.Blue, "Prefill", s.RequiredUnreachable);
        }
        catch (ObjectDisposedException)
        {
            // A disposed icon must never reach the crash dialog. Skip THIS
            // frame only — do not touch _stopping (that would freeze the
            // icons permanently; it belongs to FormClosed alone).
            return;
        }
        _tg.Text = $"TG {(tg is { } t ? $"{t:N1}" : "--")} t/s — max {(s.TgMax is { } tm ? $"{tm:N1}" : "--")} t/s";
        _pp.Text = $"PP {(pp is { } p ? $"{p:N1}" : "--")} t/s — max {(s.PpMax is { } pm ? $"{pm:N1}" : "--")} t/s";
        _pf.Text = _linger.Pct is { } pct
            ? $"Prefill {pct:F0}% — {_linger.Processed:N0}/{_linger.Total:N0} tok"
            : "Prefill —";
    }

    private readonly LingerState _linger = new();

    private static Color Blend(Color a, Color b) =>
        Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);

    private static string RateText(double? r) => r switch
    {
        null => "--",
        >= 10000 => $"{r / 1000:F0}k",
        >= 1000 => $"{r / 1000:F1}k",
        _ => $"{r:F0}",
    };

    private static string Badge(int n) => n > 0 ? n.ToString() : "";

    // PP/TG faces: max 3 characters — "1.2k" trims to "1.2" (k suffix dropped).
    private static string CapText(double? r)
    {
        var t = RateText(r);
        return t.Length > 3 ? t.TrimEnd('k') : t;
    }

    private static string PctText(double? pct) => pct is null ? "--" : $"{pct:F0}";

    private SettingsForm? _configForm;

    private bool _aboutOpen;

    private void OnAbout(object? _, EventArgs __) =>
        _hidden.BeginInvoke(() =>
        {
            // Modal box runs on the UI thread; queued clicks must not stack
            // a new box per click — one About at a time.
            if (_aboutOpen) return;
            _aboutOpen = true;
            try
            {
                MessageBox.Show(   // no owner: _hidden is minimized, owner-centering would corner it
                    $"llamaPeek {Program.Version}\n\n" +
                    "Tray monitor for llama.cpp-compatible inference servers.\n" +
                    "Author: lwYeo — https://github.com/lwYeo/llamaPeek\n\n" +
                    "Reads counters only (health, metrics, slots, models); prompts and\n" +
                    "completions are never read, logged, or stored. MIT licensed.",
                    "About llamaPeek", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { _aboutOpen = false; }
        });

    private void OnConfig(object? _, EventArgs __)
    {
        // Defer past the menu's own modal message loop: ShowDialog called
        // directly from a menu click nests loops and freezes the tray.
        _hidden.BeginInvoke(() => ShowConfig());
    }

    private void ShowConfig()
    {
        if (_configForm is not null)
        {
            _configForm.BringToFront();
            _configForm.Activate();
            return;
        }
        _configForm = new SettingsForm(RegistryStore.Load(), RegistryStore.LoadApiKey());
        try
        {
            if (_configForm.ShowDialog() == DialogResult.OK)
            {
                _configForm.Apply();            // registry + Run key
                Restart();                      // saving config restarts the monitor
            }
        }
        finally
        {
            _configForm.Dispose();
            _configForm = null;
        }
    }

    private void OnRestart(object? _, EventArgs __) => Restart();

    private readonly SemaphoreSlim _restartGate = new(1, 1);

    private void Restart()
    {
        // Linger state belongs to the old aggregator — drop it so the icons
        // don't show the previous session's values for one tick.
        _linger.Reset();

        // Serialize: two overlapping restarts would interleave stop/build/
        // start and spawn duplicate poll loops. The gate holds the order;
        // failures are observed (never unobserved Task exceptions).
        _ = Task.Run(async () =>
        {
            await _restartGate.WaitAsync();
            try
            {
                _settings = RegistryStore.Load();
                await _monitor.RestartAsync(_settings);
                try { _hidden.BeginInvoke(() => Paint(_monitor.Current())); }
                catch { /* form disposed mid-flight — exiting */ }
            }
            catch
            {
                /* restart failed: icons keep the last snapshot; user can retry */
            }
            finally { _restartGate.Release(); }
        });
    }

    private void OnExit(object? _, EventArgs __) =>
        _hidden.BeginInvoke(() => _hidden.Close());   // out of the menu loop first

    private void OnExitAndPurge(object? _, EventArgs __) =>
        _hidden.BeginInvoke(() =>
        {
            // No is the DEFAULT button: Enter/Esc are safe; deletion requires an
            // explicit Yes click.
            var answer = MessageBox.Show(
                "Permanently DELETE all llamaPeek configuration?\n\n" +
                "This removes the server list, poll settings, stored API key, and the\n" +
                "launch-at-startup entry. This cannot be undone.\n" +
                "The program file itself is left in place.\n\n" +
                "Click Yes to delete and exit, No to keep everything.",
                "llamaPeek — confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            RegistryStore.DeleteAll();
            _hidden.Close();
        });
}
