using System.Drawing.Drawing2D;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Riquadro riassuntivo di un disco: indicatore circolare della temperatura,
/// pillola dello stato di salute, modello e barra di occupazione.
/// </summary>
public sealed class DiskCard : Control
{
    private const int GaugeSize = 74;
    private const float GaugeStart = 135f;
    private const float GaugeSweep = 270f;
    private const int TempRangeMin = 20;
    private const int TempRangeMax = 85;

    private readonly AppSettings _settings;
    private bool _selected;
    private bool _hover;

    // Pulsante di copia in alto a destra
    private bool _copyHover;
    private bool _copied;
    private System.Windows.Forms.Timer? _copyFeedback;
    private readonly ToolTip _tip = new();

    /// <summary>Richiesta di copiare negli appunti il riepilogo di questo disco.</summary>
    public event EventHandler? CopyRequested;

    public DiskInfo Disk { get; private set; }

    private Rectangle CopyButton => new(Width - 34, 9, 24, 24);

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    public DiskCard(DiskInfo disk, AppSettings settings)
    {
        Disk = disk;
        _settings = settings;
        Size = new Size(364, 128);
        Margin = new Padding(0, 0, 12, 0);
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Update(DiskInfo disk)
    {
        Disk = disk;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _copyHover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool onCopy = CopyButton.Contains(e.Location);
        if (onCopy != _copyHover)
        {
            _copyHover = onCopy;
            // Il suggerimento va mostrato a mano: riguarda una zona della scheda,
            // non l'intero controllo.
            if (onCopy) _tip.Show("Copia il riepilogo negli appunti", this,
                                  CopyButton.Left - 170, CopyButton.Bottom + 4, 4000);
            else _tip.Hide(this);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && CopyButton.Contains(e.Location))
        {
            CopyRequested?.Invoke(this, EventArgs.Empty);
            ShowCopyFeedback();
        }
        base.OnMouseDown(e);
    }

    /// <summary>Per un paio di secondi l'icona diventa una spunta, come conferma.</summary>
    private void ShowCopyFeedback()
    {
        _copied = true;
        Invalidate();

        _copyFeedback ??= new System.Windows.Forms.Timer { Interval = 1800 };
        _copyFeedback.Stop();
        _copyFeedback.Tick -= OnCopyFeedbackElapsed;
        _copyFeedback.Tick += OnCopyFeedbackElapsed;
        _copyFeedback.Start();
    }

    private void OnCopyFeedbackElapsed(object? sender, EventArgs e)
    {
        _copyFeedback?.Stop();
        _copied = false;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _copyFeedback?.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        var surface = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var fill = _selected ? (Theme.IsDark ? Theme.SurfaceAlt : Theme.Surface)
                 : _hover ? Theme.SurfaceHover
                 : Theme.Surface;
        var border = _selected ? Theme.Accent : Theme.Border;

        Theme.DrawSurface(g, surface, 10, fill, border, _selected ? 1.6f : 1f);

        // Filo verticale colorato con lo stato di salute
        var healthColor = HealthColor(Disk.Health);
        using (var clip = Theme.RoundedRect(surface, 10))
        {
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Intersect);
            using (var hb = new SolidBrush(healthColor))
                g.FillRectangle(hb, new RectangleF(0, 0, 4, Height));
            g.Clip = saved;
        }

        int left = 20;
        int right = Width - GaugeSize - 24;
        float textWidth = right - left - 8;

        // Modello
        using (var b = new SolidBrush(Theme.TextPrimary))
        {
            string model = Disk.Model.Length > 0 ? Disk.Model : $"Disco {Disk.Index}";
            g.DrawString(Theme.Ellipsize(g, model, Theme.BodyStrong, textWidth), Theme.BodyStrong, b, left, 16);
        }

        // Interfaccia · tipo · lettere
        using (var b = new SolidBrush(Theme.TextMuted))
        {
            string line = $"{Disk.InterfaceDisplay} · {Disk.KindDisplay}";
            g.DrawString(Theme.Ellipsize(g, line, Theme.Small, textWidth), Theme.Small, b, left, 35);
        }

        // Pillola di salute (i font del tema sono condivisi: non vanno liberati qui)
        string healthText = HealthText(Disk.Health);
        var pillSize = g.MeasureString(healthText, Theme.SmallStrong);
        var pillRect = new RectangleF(left, 57, pillSize.Width + 18, 20);
        Theme.DrawPill(g, pillRect, healthText, healthColor,
                       Theme.Alpha(healthColor, Theme.IsDark ? 46 : 28), Theme.SmallStrong);

        if (Disk.LifePercent is int lp)
        {
            using var lb = new SolidBrush(Theme.TextSecondary);
            g.DrawString($"vita {lp}%", Theme.Small, lb, pillRect.Right + 8, 59);
        }

        DrawCapacityBar(g, left, 92, textWidth);
        DrawGauge(g, new RectangleF(Width - GaugeSize - 18, (Height - GaugeSize) / 2f, GaugeSize, GaugeSize));
        DrawCopyButton(g);
    }

    private void DrawCopyButton(Graphics g)
    {
        var r = CopyButton;

        if (_copyHover || _copied)
        {
            using var path = Theme.RoundedRect(r, 6);
            using var b = new SolidBrush(_copied ? Theme.Alpha(Theme.Good, 46) : Theme.SurfaceHover);
            g.FillPath(b, path);
        }

        var tint = _copied ? Theme.Good : _copyHover ? Theme.Accent : Theme.TextMuted;
        using var pen = new Pen(tint, 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        if (_copied)
        {
            // Spunta di conferma
            g.DrawLines(pen,
            [
                new PointF(r.X + 6f, r.Y + 12f),
                new PointF(r.X + 10f, r.Y + 16f),
                new PointF(r.X + 18f, r.Y + 8f),
            ]);
            return;
        }

        // Due fogli sovrapposti: l'icona classica di "copia"
        var back = new RectangleF(r.X + 5f, r.Y + 5f, 10f, 12f);
        var front = new RectangleF(r.X + 9f, r.Y + 8f, 10f, 12f);

        using (var backPath = Theme.RoundedRect(back, 2f))
            g.DrawPath(pen, backPath);

        using (var frontPath = Theme.RoundedRect(front, 2f))
        {
            using var fill = new SolidBrush(_selected ? (Theme.IsDark ? Theme.SurfaceAlt : Theme.Surface)
                                                      : _hover ? Theme.SurfaceHover : Theme.Surface);
            g.FillPath(fill, frontPath);
            g.DrawPath(pen, frontPath);
        }
    }

    private void DrawCapacityBar(Graphics g, float x, float y, float width)
    {
        string caption = Disk.SizeDisplay;
        double? used = Disk.UsedFraction;

        if (used is double f)
        {
            var track = new RectangleF(x, y, width, 5);
            using (var path = Theme.RoundedRect(track, 2.5f))
            using (var b = new SolidBrush(Theme.Track))
                g.FillPath(b, path);

            float w = Math.Max(3f, (float)(width * f));
            var barColor = f > 0.92 ? Theme.Bad : f > 0.8 ? Theme.Warn : Theme.Accent;
            using (var path = Theme.RoundedRect(new RectangleF(x, y, w, 5), 2.5f))
            using (var b = new SolidBrush(barColor))
                g.FillPath(b, path);

            // Il confronto va fatto con lo spazio dei volumi, non con la capacità grezza.
            caption = $"{DiskInfo.FormatBytes(Disk.VolumeUsedBytes)} di {DiskInfo.FormatBytes(Disk.VolumeTotalBytes)} usati";
            if (Disk.DriveLetters.Count > 0) caption += $"  ·  {Disk.LettersDisplay}";
        }
        else if (Disk.DriveLetters.Count > 0)
        {
            caption += $"  ·  {Disk.LettersDisplay}";
        }

        using var cb = new SolidBrush(Theme.TextMuted);
        g.DrawString(Theme.Ellipsize(g, caption, Theme.Micro, width), Theme.Micro, cb, x, y + 10);
    }

    private void DrawGauge(Graphics g, RectangleF area)
    {
        var color = Theme.OnSurface(IconRenderer.ColorForTemp(Disk.TemperatureC, _settings));
        float thickness = 7f;
        var arc = RectangleF.Inflate(area, -thickness / 2f, -thickness / 2f);

        using (var trackPen = new Pen(Theme.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(trackPen, arc, GaugeStart, GaugeSweep);

        if (Disk.TemperatureC is int c)
        {
            float t = Math.Clamp((c - TempRangeMin) / (float)(TempRangeMax - TempRangeMin), 0.02f, 1f);
            using var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, arc, GaugeStart, GaugeSweep * t);
        }

        // Valore al centro
        string value = Disk.TemperatureC is int v
            ? Math.Round(_settings.ToDisplay(v)).ToString("0")
            : "—";

        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(Disk.TemperatureC is null ? Theme.TextMuted : color))
        using (var font = new Font(Theme.Display.FontFamily, 17f, FontStyle.Bold, GraphicsUnit.Point))
            g.DrawString(value, font, b, new RectangleF(area.X, area.Y - 5, area.Width, area.Height), fmt);

        using (var ub = new SolidBrush(Theme.TextMuted))
            g.DrawString(_settings.UnitSuffix, Theme.Micro, ub,
                new RectangleF(area.X, area.Y + area.Height / 2f + 8, area.Width, 14), fmt);
    }

    public static Color HealthColor(HealthState s) => s switch
    {
        HealthState.Good => Theme.Good,
        HealthState.Caution => Theme.Warn,
        HealthState.Bad => Theme.Bad,
        _ => Theme.Neutral,
    };

    public static string HealthText(HealthState s) => s switch
    {
        HealthState.Good => "Buono",
        HealthState.Caution => "Attenzione",
        HealthState.Bad => "Critico",
        _ => "Sconosciuto",
    };
}
