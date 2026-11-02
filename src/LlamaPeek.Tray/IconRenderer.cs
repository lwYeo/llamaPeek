using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace LlamaPeek.Tray;

/// <summary>Renders "72" / "2k" / "53%" faces with an optional active-count badge
/// at bottom-right. Icons are cached by rendered content; HICON handles destroyed.</summary>
public sealed class IconRenderer : IDisposable
{
    public static readonly Color Green = Color.FromArgb(0x0d, 0x5c, 0x33);
    public static readonly Color Amber = Color.FromArgb(0x8f, 0x4d, 0x00);
    public static readonly Color Blue  = Color.FromArgb(0x14, 0x40, 0x8a);
    public static readonly Color Gray  = Color.FromArgb(0x55, 0x5b, 0x66);
    public static readonly Color Red   = Color.FromArgb(0xa0, 0x1f, 0x1f);

    private const int Size = 64;
    // Bounded: rate text changes every paint, so an unbounded cache leaks icons
    // over days of running. Dictionary insertion order = eviction order.
    private const int CacheCap = 300;
    private readonly Dictionary<string, Icon> _cache = new();

    // Icons currently assigned to the NotifyIcons. Evicting one of these
    // disposes the live tray icon and the next UpdateIcon throws
    // ObjectDisposedException — they are skipped.
    private readonly HashSet<Icon> _live = new();
    public void SetLive(Icon? a, Icon? b, Icon? c)
    {
        _live.Clear();
        if (a is not null) _live.Add(a);
        if (b is not null) _live.Add(b);
        if (c is not null) _live.Add(c);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public Icon Render(string text, string badge, Color bg, string? label = null, bool alert = false)
    {
        var key = $"{text}|{badge}|{bg.ToArgb()}|{label}|{alert}";
        if (_cache.TryGetValue(key, out var hit)) return hit;

        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var face = Rounded(new Rectangle(1, 1, Size - 3, Size - 3), 12);
            using (var faceBrush = new SolidBrush(bg))
                g.FillPath(faceBrush, face);
            DrawCentered(g, text, Brushes.White);
            if (label is not null)
            {
                using var lf = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString(label, lf, Brushes.White, 5, Size - 19);
            }
            if (badge.Length > 0)
            {
                // Aligned with the bottom-left label (label center ~y 52).
                var r = new Rectangle(Size - 26, Size - 24, 24, 24);
                g.FillEllipse(Brushes.Black, r);
                using var bf = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString(badge, bf);
                g.DrawString(badge, bf, Brushes.White,
                    r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
            }
            if (alert)
            {
                // Disconnect indicator: red dot, top-right, white outline.
                var r = new Rectangle(Size - 24, 0, 22, 22);
                g.FillEllipse(Brushes.Red, r);
                g.DrawEllipse(Pens.White, r);
            }
        }
        var hicon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hicon).Clone();
        DestroyIcon(hicon);
        _cache[key] = icon;
        while (_cache.Count > CacheCap)
        {
            // Evict the oldest non-live entry; if everything is live,
            // allow the cache to sit briefly over the cap.
            var evict = _cache.Keys
                .FirstOrDefault(k => !_live.Contains(_cache[k]));
            if (evict is null) break;
            // Retire, don't dispose: the icon may be read by UpdateIcon on
            // this very paint (assigned mid-paint, not yet in _live). The
            // graveyard drains at the next Render — a full paint later.
            _graveyard.Add(_cache[evict]);
            _cache.Remove(evict);
        }
        // Drain icons retired at least one paint ago.
        if (_graveyard.Count > 0)
        {
            foreach (var dead in _graveyard) dead.Dispose();
            _graveyard.Clear();
        }
        return icon;
    }

    private readonly List<Icon> _graveyard = [];

    private static void DrawCentered(Graphics g, string text, Brush brush)
    {
        // Value sits centered in the UPPER part of the face so the badge
        // (bottom-right) and label (bottom-left) never overlap it.
        float size = 40f;
        using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        var f = font;
        var sz = g.MeasureString(text, f);
        while (sz.Width > Size - 4 && size > 22f)
        {
            size -= 1f;
            f.Dispose();
            f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            sz = g.MeasureString(text, f);
        }
        g.DrawString(text, f, brush, (Size - sz.Width) / 2f, 24f - sz.Height / 2f);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values) icon.Dispose();
        _cache.Clear();
        foreach (var icon in _graveyard) icon.Dispose();
        _graveyard.Clear();
    }
}
