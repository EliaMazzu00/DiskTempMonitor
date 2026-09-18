using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Riquadro compatto sempre in primo piano con le sole misure, da tenere in un angolo
/// dello schermo mentre si lavora.
/// <para>
/// Non ha intestazione fissa: i comandi compaiono solo al passaggio del mouse, così a
/// riposo resta una striscia di soli dati. Ogni riga mostra il pallino di stato, la
/// sigla, l'andamento recente in miniatura e il valore. Oltre ai dischi può mostrare
/// temperatura e carico del processore, la griglia dei core e la scheda video.
/// </para>
/// </summary>
public sealed class MiniWindow : Form
{
    private const int RowHeight = 34;
    private const int Inset = 12;
    private const int WidthFull = 244;
    private const int WidthCompact = 164;
    private const int SparkSamples = 44;
    private const int CoreCell = 22;
    private const int CoreGap = 3;

    private readonly AppSettings _settings;
    private readonly ContextMenuStrip _menu = new();
    private IReadOnlyList<DiskInfo> _disks = [];
    private SystemMetrics? _metrics;
    private List<MiniRow> _rows = [];
    private int _coresBlockHeight;

    private bool _dragging;
    private Point _dragOrigin;
    private bool _hover;
    private bool _closeHover;

    public event EventHandler? ShowMainRequested;
    public event EventHandler? CloseRequested;

    public MiniWindow(AppSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        Opacity = Math.Clamp(_settings.MiniWindowOpacity, 40, 100) / 100.0;

        // Senza questo resta il grigio chiaro predefinito di Windows, che si vede come
        // un contorno chiaro lungo il ritaglio degli angoli arrotondati.
        BackColor = Theme.IsDark ? Theme.SurfaceAlt : Theme.Surface;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        BuildMenu();
        ApplySize();
        RestorePosition();
    }

    private bool Compact => _settings.MiniWindowCompact;

    private Rectangle CloseButton => new(Width - 22, 5, 16, 16);

    // ------------------------------------------------------------------ dati

    /// <summary>Una riga del riquadro, indipendente da cosa la misura rappresenti.</summary>
    private sealed class MiniRow
    {
        public string Label = "";
        public string Value = "—";
        public string Unit = "";
        public Color Accent;
        public Color Dot;
        public int[] History = [];
        /// <summary>Riempimento 0-1 per le righe che mostrano una percentuale.</summary>
        public float? Fraction;
    }

    public void Update(IReadOnlyList<DiskInfo> disks, SystemMetrics? metrics = null)
    {
        _disks = disks;
        _metrics = metrics;
        _rows = BuildRows();
        ApplySize();
        Invalidate();
    }

    private List<MiniRow> BuildRows()
    {
        var rows = new List<MiniRow>();

        if (_settings.MiniShowDisks)
        {
            foreach (var disk in DiskInfo.InDisplayOrder(_disks))
            {
                var color = Theme.OnSurface(IconRenderer.ColorForTemp(disk.TemperatureC, _settings));
                rows.Add(new MiniRow
                {
                    Label = disk.DriveLetters.Count > 0
                        ? string.Join(" ", disk.DriveLetters)
                        : $"D{disk.Index}",
                    Value = Format(disk.TemperatureC),
                    Unit = _settings.UnitSuffix,
                    Accent = color,
                    Dot = DiskCard.HealthColor(disk.Health),
                    History = disk.History.ToArray(),
                });
            }
        }

        var cpu = _metrics?.Cpu;

        if (_settings.MiniShowCpu)
        {
            int? temp = _metrics?.CpuTemperature(_settings.MiniCpuMode);
            var color = Theme.OnSurface(IconRenderer.ColorForCpu(temp, _settings));
            rows.Add(new MiniRow
            {
                Label = "CPU",
                Value = Format(temp),
                Unit = _settings.UnitSuffix,
                Accent = color,
                Dot = color,
                History = _metrics?.CpuHistory.ToArray() ?? [],
            });
        }

        if (_settings.MiniShowGpu)
        {
            int? temp = _metrics?.GpuTemperature;
            var color = Theme.OnSurface(IconRenderer.ColorForGpu(temp, _settings));
            rows.Add(new MiniRow
            {
                Label = "GPU",
                Value = Format(temp),
                Unit = _settings.UnitSuffix,
                Accent = color,
                Dot = color,
                History = _metrics?.GpuHistory.ToArray() ?? [],
            });
        }

        // I carichi chiudono la fila: prima tutte le temperature, che sono il motivo per
        // cui il riquadro esiste, poi le percentuali.
        if (_settings.MiniShowCpuLoad) rows.Add(CaricoRow("CPU %", cpu?.AverageLoadPercent));
        if (_settings.MiniShowGpuLoad) rows.Add(CaricoRow("GPU %", _metrics?.PrimaryGpu?.CoreLoadPercent));
        if (_settings.MiniShowGpuVram) rows.Add(CaricoRow("VRAM %", _metrics?.PrimaryGpu?.VramUsedPercent));
        if (_settings.MiniShowMemory) rows.Add(CaricoRow("RAM %", _metrics?.MemoryUsedPercent));

        return rows;
    }

    /// <summary>Riga di percentuale: al posto del micro-grafico mostra una barra.</summary>
    private static MiniRow CaricoRow(string label, float? percent) => new()
    {
        Label = label,
        Value = percent is float p ? $"{p:0}" : "—",
        Unit = "%",
        Accent = Theme.Accent,
        Dot = Theme.Accent,
        Fraction = percent is float f ? Math.Clamp(f / 100f, 0f, 1f) : null,
    };

    private string Format(int? celsius) =>
        celsius is int c ? Math.Round(_settings.ToDisplay(c)).ToString("0") : "—";

    private IReadOnlyList<CoreSample> Cores =>
        _settings.MiniShowCpuCores ? _metrics?.Cpu?.Cores ?? [] : [];

    private void ApplySize()
    {
        int width = Compact ? WidthCompact : WidthFull;

        int columns = Math.Max(1, (width - Inset * 2 + CoreGap) / (CoreCell + CoreGap));
        int coreCount = Cores.Count;
        _coresBlockHeight = coreCount == 0
            ? 0
            : (int)Math.Ceiling(coreCount / (double)columns) * (CoreCell + CoreGap) + 8;

        int height = Inset * 2 + Math.Max(1, _rows.Count) * RowHeight + _coresBlockHeight;

        if (Width != width || Height != height) Size = new Size(width, height);
    }

    // ------------------------------------------------------------------ menu

    private void BuildMenu()
    {
        _menu.Renderer = new ThemedMenuRenderer();
        _menu.ShowImageMargin = false;

        var content = new ToolStripMenuItem("Cosa mostrare");
        AddToggle(content, "Dischi", () => _settings.MiniShowDisks, v => _settings.MiniShowDisks = v);
        AddToggle(content, "Temperatura processore", () => _settings.MiniShowCpu, v => _settings.MiniShowCpu = v);
        AddToggle(content, "Carico processore", () => _settings.MiniShowCpuLoad, v => _settings.MiniShowCpuLoad = v);
        AddToggle(content, "Griglia dei core", () => _settings.MiniShowCpuCores, v => _settings.MiniShowCpuCores = v);
        AddToggle(content, "Temperatura scheda video", () => _settings.MiniShowGpu, v => _settings.MiniShowGpu = v);
        AddToggle(content, "Carico scheda video", () => _settings.MiniShowGpuLoad, v => _settings.MiniShowGpuLoad = v);
        AddToggle(content, "Memoria occupata", () => _settings.MiniShowMemory, v => _settings.MiniShowMemory = v);
        Themed(content.DropDown);

        var cpuMode = new ToolStripMenuItem("Temperatura processore");
        foreach (var (mode, label) in new[]
        {
            (CpuTempMode.Hottest, "Core più caldo"),
            (CpuTempMode.Average, "Media dei core"),
            (CpuTempMode.Package, "Package"),
        })
        {
            var m = mode;
            var item = new ToolStripMenuItem(label) { Tag = m };
            item.Click += (_, _) =>
            {
                _settings.MiniCpuMode = m;
                _settings.Save();
                Refresh();
            };
            cpuMode.DropDownItems.Add(item);
        }
        Themed(cpuMode.DropDown);

        var compact = new ToolStripMenuItem("Vista compatta");
        compact.Click += (_, _) =>
        {
            _settings.MiniWindowCompact = !_settings.MiniWindowCompact;
            _settings.Save();
            ApplySize();
            KeepOnScreen();
            Invalidate();
        };

        var opacity = new ToolStripMenuItem("Trasparenza");
        foreach (int value in new[] { 100, 94, 85, 70, 55 })
        {
            int v = value;
            var item = new ToolStripMenuItem($"{v}%");
            item.Click += (_, _) =>
            {
                _settings.MiniWindowOpacity = v;
                _settings.Save();
                Opacity = v / 100.0;
                UpdateMenuChecks();
            };
            opacity.DropDownItems.Add(item);
        }
        Themed(opacity.DropDown);

        var show = new ToolStripMenuItem("Mostra finestra intera");
        show.Click += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        var close = new ToolStripMenuItem("Chiudi riquadro");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.AddRange([content, cpuMode, new ToolStripSeparator(),
                              compact, opacity, new ToolStripSeparator(), show, close]);
    }

    private void AddToggle(ToolStripMenuItem parent, string label, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(label) { Tag = get };
        item.Click += (_, _) =>
        {
            set(!get());
            _settings.Save();
            // Ricostruisce righe e altezza con la nuova scelta.
            Update(_disks, _metrics);
            KeepOnScreen();
        };
        parent.DropDownItems.Add(item);
    }

    private static void Themed(ToolStripDropDown drop)
    {
        drop.Renderer = new ThemedMenuRenderer();
        drop.BackColor = Theme.SurfaceAlt;
        drop.ForeColor = Theme.TextPrimary;
        drop.Font = Theme.Body;
        if (drop is ToolStripDropDownMenu dm) dm.ShowImageMargin = false;
    }

    private void UpdateMenuChecks()
    {
        _menu.BackColor = Theme.SurfaceAlt;
        _menu.ForeColor = Theme.TextPrimary;
        _menu.Font = Theme.Body;

        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is not ToolStripMenuItem mi) continue;
            Themed(mi.DropDown);

            switch (mi.Text)
            {
                case "Vista compatta":
                    mi.Checked = Compact;
                    break;

                case "Cosa mostrare":
                    foreach (ToolStripItem sub in mi.DropDownItems)
                        if (sub is ToolStripMenuItem s && s.Tag is Func<bool> get) s.Checked = get();
                    break;

                case "Temperatura processore":
                    foreach (ToolStripItem sub in mi.DropDownItems)
                        if (sub is ToolStripMenuItem s && s.Tag is CpuTempMode mode)
                            s.Checked = mode == _settings.MiniCpuMode;
                    break;

                case "Trasparenza":
                    foreach (ToolStripItem sub in mi.DropDownItems)
                        if (sub is ToolStripMenuItem s) s.Checked = s.Text == $"{_settings.MiniWindowOpacity}%";
                    break;
            }
        }
    }

    // ------------------------------------------------------------- posizione

    private void RestorePosition()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int x = _settings.MiniWindowX;
        int y = _settings.MiniWindowY;

        // Prima apertura, o posizione fuori dagli schermi collegati adesso:
        // si riparte dall'angolo in basso a destra.
        bool valid = x > int.MinValue && Screen.AllScreens.Any(s =>
            s.WorkingArea.IntersectsWith(new Rectangle(x, y, Width, Height)));

        Location = valid ? new Point(x, y)
                         : new Point(screen.Right - Width - 24, screen.Bottom - Height - 24);
    }

    /// <summary>Dopo un cambio di dimensione il riquadro non deve finire fuori schermo.</summary>
    private void KeepOnScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        int x = Math.Min(Location.X, area.Right - Width - 4);
        int y = Math.Min(Location.Y, area.Bottom - Height - 4);
        Location = new Point(Math.Max(area.Left + 4, x), Math.Max(area.Top + 4, y));
        SavePosition();
    }

    private void SavePosition()
    {
        _settings.MiniWindowX = Location.X;
        _settings.MiniWindowY = Location.Y;
        _settings.Save();
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _closeHover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            UpdateMenuChecks();
            _menu.Show(this, e.Location);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            if (CloseButton.Contains(e.Location))
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            _dragging = true;
            _dragOrigin = e.Location;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);
        }
        else
        {
            bool onClose = CloseButton.Contains(e.Location);
            if (onClose != _closeHover) { _closeHover = onClose; Invalidate(); }
            Cursor = onClose ? Cursors.Hand : Cursors.SizeAll;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            SavePosition();
        }
        base.OnMouseUp(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        ShowMainRequested?.Invoke(this, EventArgs.Empty);
        base.OnDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) CloseRequested?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }

    // ---------------------------------------------------------------- disegno

    // ------------------------------------- angoli arrotondati dal compositore

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private bool _nativeCorners;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeCorners();
    }

    /// <summary>
    /// Su Windows 11 gli angoli li arrotonda il compositore, con l'antialiasing che una
    /// <see cref="Region"/> non può avere: il ritaglio manuale lascia il bordo scalettato
    /// e scopre il colore di fondo della finestra. Se l'attributo non è disponibile si
    /// torna alla regione ritagliata.
    /// </summary>
    private void ApplyNativeCorners()
    {
        try
        {
            int preference = DWMWCP_ROUND;
            _nativeCorners = DwmSetWindowAttribute(
                Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0;

            if (_nativeCorners)
            {
                Region?.Dispose();
                Region = null;                       // niente ritaglio: ci pensa il compositore

                int border = ToColorRef(Theme.Border);
                DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            else
            {
                ApplyRegion();
            }
        }
        catch
        {
            _nativeCorners = false;
            ApplyRegion();
        }
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private void ApplyRegion()
    {
        using var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 12);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_nativeCorners && IsHandleCreated) ApplyRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawBackground(g);

        if (_rows.Count == 0 && Cores.Count == 0)
        {
            using var mb = new SolidBrush(Theme.TextMuted);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString("Nessuna misura", Theme.Small, mb,
                new RectangleF(Inset, 0, Width - Inset * 2, Height), fmt);
            return;
        }

        float y = Inset;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i > 0)
            {
                using var pen = new Pen(Theme.Alpha(Theme.Divider, 140), 1f);
                g.DrawLine(pen, Inset, y, Width - Inset, y);
            }

            DrawRow(g, _rows[i], y);
            y += RowHeight;
        }

        if (_coresBlockHeight > 0) DrawCores(g, y);

        if (_hover) DrawCloseButton(g);
    }

    private void DrawBackground(Graphics g)
    {
        // Leggera sfumatura verticale: dà profondità senza rubare leggibilità
        using var fill = new LinearGradientBrush(
            new RectangleF(0, 0, Width, Height + 1),
            Theme.IsDark ? Theme.SurfaceAlt : Theme.Surface,
            Theme.IsDark ? Theme.Surface : Theme.SurfaceAlt,
            90f);

        if (_nativeCorners)
        {
            // Angoli e bordo li disegna il compositore: qui basta riempire tutto,
            // senza lasciare pixel scoperti lungo il perimetro.
            g.FillRectangle(fill, new RectangleF(-1, -1, Width + 2, Height + 2));
            return;
        }

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Theme.RoundedRect(r, 12);
        g.FillPath(fill, path);

        using var border = new Pen(Theme.Alpha(Theme.Border, 200), 1.2f);
        g.DrawPath(border, path);
    }

    private void DrawRow(Graphics g, MiniRow row, float top)
    {
        float centre = top + RowHeight / 2f;

        // Pallino dello stato
        using (var glow = new SolidBrush(Theme.Alpha(row.Dot, 60)))
            g.FillEllipse(glow, Inset - 2, centre - 5, 10, 10);
        using (var dot = new SolidBrush(row.Dot))
            g.FillEllipse(dot, Inset, centre - 3, 6, 6);

        using (var b = new SolidBrush(Theme.TextSecondary))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(Theme.Ellipsize(g, row.Label, Theme.SmallStrong, 46), Theme.SmallStrong, b,
                         new RectangleF(Inset + 14, top, 46, RowHeight), fmt);

        using var big = new Font(Theme.Body.FontFamily, 14.5f, FontStyle.Bold, GraphicsUnit.Point);
        float unitWidth = 16;
        float tempRight = Width - Inset - unitWidth;

        using (var b = new SolidBrush(row.Accent))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far })
            g.DrawString(row.Value, big, b, new RectangleF(tempRight - 46, top, 46, RowHeight), fmt);

        using (var b = new SolidBrush(Theme.Alpha(row.Accent, 170)))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(row.Unit, Theme.Micro, b,
                         new RectangleF(tempRight + 1, top + 1, unitWidth, RowHeight), fmt);

        if (Compact) return;

        float graphLeft = Inset + 64;
        float graphRight = tempRight - 52;
        if (graphRight - graphLeft <= 24) return;

        var area = new RectangleF(graphLeft, top + 7, graphRight - graphLeft, RowHeight - 14);

        if (row.Fraction is float fraction) DrawBar(g, fraction, row.Accent, area);
        else DrawSparkline(g, row.History, row.Accent, area);
    }

    /// <summary>Barra orizzontale, per le righe che mostrano una percentuale.</summary>
    private static void DrawBar(Graphics g, float fraction, Color color, RectangleF area)
    {
        var track = new RectangleF(area.X, area.Y + area.Height / 2 - 3, area.Width, 6);
        using (var path = Theme.RoundedRect(track, 3))
        using (var b = new SolidBrush(Theme.Track))
            g.FillPath(b, path);

        float w = Math.Max(3f, track.Width * fraction);
        using (var path = Theme.RoundedRect(new RectangleF(track.X, track.Y, w, track.Height), 3))
        using (var b = new SolidBrush(color))
            g.FillPath(b, path);
    }

    /// <summary>Una casella per core, colorata sulla temperatura e riempita sul carico.</summary>
    private void DrawCores(Graphics g, float top)
    {
        var cores = Cores;
        if (cores.Count == 0) return;

        using (var pen = new Pen(Theme.Alpha(Theme.Divider, 140), 1f))
            g.DrawLine(pen, Inset, top, Width - Inset, top);

        float y = top + 6;
        int columns = Math.Max(1, (Width - Inset * 2 + CoreGap) / (CoreCell + CoreGap));

        for (int i = 0; i < cores.Count; i++)
        {
            int column = i % columns;
            int rowIndex = i / columns;
            var cell = new RectangleF(
                Inset + column * (CoreCell + CoreGap),
                y + rowIndex * (CoreCell + CoreGap),
                CoreCell, CoreCell);

            DrawCoreCell(g, cores[i], cell);
        }
    }

    private void DrawCoreCell(Graphics g, CoreSample core, RectangleF cell)
    {
        int? temp = core.TemperatureC is float t ? (int)Math.Round(t) : null;
        var color = Theme.OnSurface(IconRenderer.ColorForCpu(temp, _settings));

        using (var path = Theme.RoundedRect(cell, 4))
        using (var b = new SolidBrush(Theme.Track))
            g.FillPath(b, path);

        // Il carico riempie la casella dal basso: si legge a colpo d'occhio.
        if (core.LoadPercent is float load)
        {
            float h = cell.Height * Math.Clamp(load / 100f, 0f, 1f);
            using var clip = Theme.RoundedRect(cell, 4);
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Intersect);
            using (var b = new SolidBrush(Theme.Alpha(color, 90)))
                g.FillRectangle(b, cell.X, cell.Bottom - h, cell.Width, h);
            g.Clip = saved;
        }

        using (var path = Theme.RoundedRect(cell, 4))
        using (var pen = new Pen(Theme.Alpha(color, 200), 1.2f))
            g.DrawPath(pen, path);

        string text = temp is int c ? Math.Round(_settings.ToDisplay(c)).ToString("0") : "—";
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, Theme.Micro, brush, cell, fmt);
    }

    private static void DrawSparkline(Graphics g, IReadOnlyList<int> history, Color color, RectangleF area)
    {
        var samples = history.Count > SparkSamples
            ? history.Skip(history.Count - SparkSamples).ToArray()
            : history.ToArray();

        if (samples.Length < 2)
        {
            using var flat = new Pen(Theme.Alpha(color, 70), 1.5f);
            float mid = area.Y + area.Height / 2;
            g.DrawLine(flat, area.Left, mid, area.Right, mid);
            return;
        }

        int min = samples.Min(), max = samples.Max();
        int span = Math.Max(6, max - min);          // evita picchi finti su serie piatte
        float mid2 = (min + max) / 2f;
        float lo = mid2 - span / 2f;

        var points = new PointF[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float x = area.Left + i * (area.Width / (samples.Length - 1));
            float y = area.Bottom - (samples[i] - lo) / span * area.Height;
            points[i] = new PointF(x, Math.Clamp(y, area.Top, area.Bottom));
        }

        // Area sfumata sotto la curva
        var polygon = new PointF[points.Length + 2];
        Array.Copy(points, polygon, points.Length);
        polygon[^2] = new PointF(points[^1].X, area.Bottom);
        polygon[^1] = new PointF(points[0].X, area.Bottom);

        using (var gradient = new LinearGradientBrush(
                   new RectangleF(area.X, area.Y, area.Width, area.Height + 1),
                   Theme.Alpha(color, 80), Theme.Alpha(color, 0), 90f))
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(polygon);
            g.FillPath(gradient, path);
        }

        using (var pen = new Pen(Theme.Alpha(color, 220), 1.4f)
        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, points);

        using var dot = new SolidBrush(color);
        g.FillEllipse(dot, points[^1].X - 2, points[^1].Y - 2, 4, 4);
    }

    private void DrawCloseButton(Graphics g)
    {
        var r = CloseButton;
        if (_closeHover)
        {
            using var path = Theme.RoundedRect(r, 4);
            using var b = new SolidBrush(Theme.Alpha(Theme.Bad, 70));
            g.FillPath(b, path);
        }

        using var pen = new Pen(_closeHover ? Theme.Bad : Theme.Alpha(Theme.TextMuted, 190), 1.4f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawLine(pen, r.X + 5, r.Y + 5, r.Right - 5, r.Bottom - 5);
        g.DrawLine(pen, r.Right - 5, r.Y + 5, r.X + 5, r.Bottom - 5);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePosition();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Non deve rubare il fuoco quando compare.</summary>
    protected override bool ShowWithoutActivation => true;
}
