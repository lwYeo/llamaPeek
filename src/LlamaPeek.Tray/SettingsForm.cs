using LlamaPeek.Core;

namespace LlamaPeek.Tray;

/// <summary>Configuration dialog: servers grid, poll cadence, smoothing,
/// API key (masked, copy disabled), launch at startup. OK saves to the
/// registry; the tray host restarts the monitor afterwards.</summary>
public sealed class SettingsForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly NumericUpDown _pollActive = Num();
    private readonly NumericUpDown _pollIdle = Num();
    private readonly NumericUpDown _grace = Num(min: 0);
    private readonly NumericUpDown _refresh = Num(max: 5_000);
    private readonly NumericUpDown _linger = Num(min: 0, max: 60_000);
    private readonly NumericUpDown _smoothing = new()
    { DecimalPlaces = 2, Increment = 0.05m, Minimum = 0, Maximum = 1, Width = 90 };
    private readonly NoCopyTextBox _apiKey = new()
    { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox _startup = new()
    { Text = "Launch at startup", AutoSize = true, Margin = new Padding(4, 8, 0, 4) };

    private readonly TableLayoutPanel _gridPanel = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        Margin = new Padding(0),
    };

    public SettingsForm(AppSettings s, string? apiKey)
    {
        Text = "llamaPeek configuration";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        // DPI scaling: baseline is 96dpi (assigned at the END of the ctor,
        // after all controls exist), so every size, margin and column below
        // is multiplied by DeviceDpi/96 exactly once. AutoScaleMode.Font
        // cannot be used: its baseline is captured at the *current* DPI,
        // giving factor 1.0 — fonts scaled, layout stayed at design pixels.
        AutoScaleMode = AutoScaleMode.Dpi;
        // Design size at 96dpi, sized to content: the 12 AutoSize rows need
        // ~405px, so 440 leaves a modest grid expansion instead of a huge
        // percent-row balloon at high DPI.
        ClientSize = new Size(760, 440);
        // No AutoScroll: the dialog is FixedDialog and content-sized, and
        // AutoScroll clips the layout (grid flush to the frame) when the
        // content minimum width exceeds the client by a few pixels.
        _existingKey = apiKey ?? "";
        _apiKey.Text = _existingKey;

        BuildGrid(s.Servers);
        _pollActive.Value = s.PollActiveMs;
        _pollIdle.Value = s.PollIdleMs;
        _grace.Value = s.GraceMs;
        _refresh.Value = s.RefreshMs;
        _linger.Value = s.LingerMs;
        _smoothing.Value = (decimal)s.Smoothing;
        _startup.Checked = s.LaunchAtStartup;

        // Table layout — scales with DPI, no clipped controls.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 4),
            ColumnCount = 2,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Row styles are indexed by row number — declare every row explicitly.
        for (int i = 0; i < 12; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int row = 0;

        var head = Head("Servers:");
        head.Margin = new Padding(0);   // sit flush under the title bar
        root.SetColumnSpan(head, 2);
        root.Controls.Add(head, 0, row++);

        // Grid + its buttons share one row: the grid's MinimumSize
        // shrinks the grid inside this panel, never clipping the buttons.
        _gridPanel.MinimumSize = new Size(0, _gridRowsHeight + 34);  // grid rows + button strip
        _gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _gridPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _grid.Margin = new Padding(4, 2, 4, 2);
        _gridPanel.Controls.Add(_grid, 0, 0);

        var gridButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
        gridButtons.Controls.Add(MakeButton("Add server",
            () => _grid.Rows.Add("name", "http://127.0.0.1:8080", "auto", false)));
        gridButtons.Controls.Add(MakeButton("Remove selected", () =>
        {
            if (_grid.CurrentRow is not null && !_grid.CurrentRow.IsNewRow)
                _grid.Rows.Remove(_grid.CurrentRow);
        }));
        _gridPanel.Controls.Add(gridButtons, 0, 1);

        root.SetColumnSpan(_gridPanel, 2);
        root.Controls.Add(_gridPanel, 0, row++);

        void AddNumRow(string label, NumericUpDown num)
        {
            root.Controls.Add(Head(label), 0, row);
            num.Margin = new Padding(4, 3, 0, 3);
            root.Controls.Add(num, 1, row++);
        }

        AddNumRow("Poll active (ms):", _pollActive);
        AddNumRow("Poll idle (ms):", _pollIdle);
        AddNumRow("Grace (ms):", _grace);
        AddNumRow("Refresh (ms):", _refresh);
        AddNumRow("Linger (ms):", _linger);
        AddNumRow("Smoothing:", _smoothing);

        // Grace may never sit below Linger: raise its floor live, which
        // auto-clamps Grace upward the moment Linger exceeds it.
        _linger.ValueChanged += (_, _) => _grace.Minimum = _linger.Value;

        root.Controls.Add(Head("Server API key:"), 0, row);
        _apiKey.Margin = new Padding(4, 6, 40, 4);
        root.Controls.Add(_apiKey, 1, row++);

        root.SetColumnSpan(_startup, 2);
        root.Controls.Add(_startup, 0, row++);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 4, 0, 0),
            AutoSize = true,
        };
        var cancel = MakeButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        var ok = MakeButton("OK", OnOk);
        ok.Width = 85; cancel.Width = 85;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.SetColumnSpan(buttons, 2);
        root.Controls.Add(buttons, 0, row);

        Controls.Add(root);
        CancelButton = cancel;
        AcceptButton = ok;

        // Scale the finished tree once (96dpi baseline), THEN size the form:
        // PreferredSize is now measured in runtime pixels on the scaled tree,
        // so no division is needed and nothing can double-scale.
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(
            (int)Math.Round(640F * DeviceDpi / 96F),   // 640 design px
            root.PreferredSize.Height);
    }

    /// <summary>
    /// Self-correcting width: after the form is shown, the live-DPI layout is
    /// final (handles created, fonts measured). If the grid's right edge sits
    /// closer than 16px to the client edge — the Fill columns could not shrink
    /// enough at this DPI — widen the form so every column, including
    /// "Required", is fully readable at any Windows scaling (100–250%).
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        float f = DeviceDpi / 96f;

        // The grid (Dock=Fill) kept a stale 1104px width from Fill mode,
        // overflowing its 936px panel and the form. Un-dock and set explicit
        // bounds: panel width minus margins.
        _grid.Dock = DockStyle.None;
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _grid.SetBounds(_grid.Left, _grid.Top,
            _gridPanel.ClientSize.Width - 12, _grid.Height);

        // Columns Fill the grid width (no grey band on the right; Fill can
        // shrink to MinimumWidth so no clipping). Rows/headers get compact
        // DPI-scaled heights so cadence matches the numeric rows.
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowTemplate.Height = (int)Math.Round(22 * f);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = (int)Math.Round(22 * f);
        foreach (DataGridViewRow r in _grid.Rows) r.Height = _grid.RowTemplate.Height;
        _gridPanel.PerformLayout();

        // Belt: if the grid's right edge still exceeds the client (e.g. a
        // font renders wider), widen the form so nothing is clipped.
        var gridRect = _grid.RectangleToScreen(_grid.ClientRectangle);
        int need = RectangleToClient(gridRect).Right + 16;
        if (need > ClientSize.Width)
            ClientSize = new Size(need, ClientSize.Height);
    }

    private void BuildGrid(List<ServerConfig> servers)
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;                 // no leading selector column
        _grid.ScrollBars = ScrollBars.Vertical;           // explicit widths: no h-scroll
        // Explicit DPI-scaled widths (set in OnShown) instead of Fill mode:
        // Fill's preferred width is unstable and pushed the form past its
        // design width. Design widths sum to 466px → fits the 640px dialog.
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        // Typed value in the Kind combo (e.g. "xyz") raises DataError; the
        // default handler shows a raw exception dialog. Swallow and revert.
        _grid.DataError += (_, e) => e.ThrowException = false;
        // Fill mode can only shrink columns to their MinimumWidth (default
        // 22+, combo columns more). Sum of minima exceeded the grid width at
        // high DPI, so with no h-scroll the Required column clipped. Low
        // minima let Fill always fit every column.
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 12, MinimumWidth = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "URL", FillWeight = 45, MinimumWidth = 60 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        { HeaderText = "Kind", FillWeight = 20, MinimumWidth = 50, Items = { "auto", "llamacpp", "strata" } });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        { HeaderText = "Required", FillWeight = 14, MinimumWidth = 40 });
        foreach (var s in servers)
            _grid.Rows.Add(s.Name, s.Url, s.Kind.ToString().ToLowerInvariant(), s.RequiresModel);

        // Height that fits the rows exactly, in DESIGN pixels (the grid's
        // handle is not created in the ctor, so row/header template heights
        // are the design 23px; the form's AutoScale pass scales this
        // MinimumSize by DeviceDpi/96 along with everything else).
        _gridRowsHeight = 23 + 23 * (_grid.Rows.Count + 1) + 6;
    }

    private int _gridRowsHeight = 120;
    private readonly string _existingKey;   // key as loaded at dialog open

    private static Label Head(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(0, 5, 0, 0) };

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static NumericUpDown Num(int min = 100, int max = 60_000) =>
        new() { Minimum = min, Maximum = max, Width = 90 };

    private void OnOk()
    {
        // Commit any in-progress cell edit so the last typed value is read.
        _grid.CurrentCell = null;

        var servers = new List<ServerConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            var name = row.Cells[0].Value?.ToString()?.Trim() ?? "";
            var url = row.Cells[1].Value?.ToString()?.Trim() ?? "";
            if (name.Length == 0 || url.Length == 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                MessageBox.Show(this, $"Server '{name}': URL must be http:// or https://",
                    "llamaPeek", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var kind = Enum.TryParse<ServerKind>(row.Cells[2].Value?.ToString(), true, out var k)
                ? k : ServerKind.Auto;
            var requires = row.Cells[3].Value is true;
            servers.Add(new ServerConfig(name, url.TrimEnd('/'), kind, requires));
        }

        // Duplicate names collide in the aggregator's "server|slot" state keys
        // and silently mix slot readings between servers.
        var dup = servers.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                         .FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            MessageBox.Show(this, $"Server names must be unique — '{dup.Key}' is used twice.",
                "llamaPeek", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = new AppSettings
        {
            Servers = servers,
            PollActiveMs = (int)_pollActive.Value,
            PollIdleMs = (int)_pollIdle.Value,
            GraceMs = (int)_grace.Value,
            RefreshMs = (int)_refresh.Value,
            LingerMs = (int)_linger.Value,
            Smoothing = (double)_smoothing.Value,
            LaunchAtStartup = _startup.Checked,
        };

        // Grace must cover the linger window (live guard sets Grace.Minimum =
        // Linger; this catches loads where the stored pair is inconsistent).
        if (_grace.Value < _linger.Value)
        {
            MessageBox.Show(this, "Grace must be at least the Linger value — " +
                "polling would drop to idle cadence while icons still show lingering values.",
                "llamaPeek", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Warn when the bearer token would travel in cleartext.
        // Warn when the bearer token would travel in cleartext.
        var key = _apiKey.Text.Trim();
        if (key.Length > 0 && servers.Any(s => s.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            MessageBox.Show(this,
                "An API key is set and at least one server uses http:// — the token will be sent unencrypted. " +
                "This is safe on Tailscale/loopback; use https:// on shared networks.",
                "llamaPeek", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // Set results BEFORE closing: Apply() reads these after ShowDialog returns.
        _pending = settings;
        _pendingKey = key.Length == 0 ? null : key;
        DialogResult = DialogResult.OK;
        Close();
    }

    private AppSettings? _pending;
    private string? _pendingKey;

    /// <summary>Persist to registry + Run key. Call after OK closes the dialog.</summary>
    public void Apply()
    {
        if (_pending is null) return;
        RegistryStore.Save(_pending);
        RegistryStore.SaveApiKey(_pendingKey);
        RegistryStore.SetLaunchAtStartup(_pending.LaunchAtStartup,
            Environment.ProcessPath ?? Application.ExecutablePath);
    }
}

/// <summary>Password textbox with no copy path: WM_COPY/WM_CUT and Ctrl+C/X are
/// swallowed, and the right-click menu is suppressed (its Copy item bypasses
/// WndProc on some Windows builds). Paste-in stays enabled for entering a key.</summary>
public sealed class NoCopyTextBox : TextBox
{
    private const int WM_COPY = 0x0301, WM_CUT = 0x0300, WM_CONTEXTMENU = 0x007B;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_COPY or WM_CUT or WM_CONTEXTMENU) return;
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        keyData is (Keys.Control | Keys.C) or (Keys.Control | Keys.X)
            ? true
            : base.ProcessCmdKey(ref msg, keyData);
}
