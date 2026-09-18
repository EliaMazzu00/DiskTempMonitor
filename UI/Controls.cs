using System.Drawing.Drawing2D;

namespace DiskTempMonitor.UI;

// I contenitori standard di WinForms non hanno il doppio buffer: durante gli
// aggiornamenti periodici questo produce sfarfallio. Queste varianti lo attivano.

public sealed class BufferedPanel : Panel
{
    public BufferedPanel() => DoubleBuffered = true;
}

public sealed class BufferedTable : TableLayoutPanel
{
    public BufferedTable() => DoubleBuffered = true;
}

public sealed class BufferedFlow : FlowLayoutPanel
{
    public BufferedFlow() => DoubleBuffered = true;
}

/// <summary>
/// Griglia degli attributi.
/// <para>
/// Volutamente <b>senza</b> <c>DoubleBuffered</c>: attivarlo su un DataGridView (si può
/// solo per sottoclasse, dato che la proprietà è protetta) ne rompe il ridisegno
/// incrementale, che ridipinge solo le celle "sporche" dando per scontato di disegnare
/// direttamente sullo schermo. Il risultato sono i residui di bordi verticali che
/// restano dopo ridimensionamenti, scorrimenti e cambi di selezione.
/// </para>
/// <para>
/// Lo sfarfallio che il doppio buffer doveva evitare non si presenta più da quando le
/// celle vengono aggiornate sul posto invece di ricostruire le righe a ogni ciclo.
/// </para>
/// </summary>
public sealed class BufferedGrid : DataGridView
{
    public BufferedGrid() => SetStyle(ControlStyles.ResizeRedraw, true);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ScheduleFullRedraw();
    }

    protected override void OnColumnWidthChanged(DataGridViewColumnEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        ScheduleFullRedraw();
    }

    /// <summary>
    /// Quando la colonna elastica cambia larghezza di pochi pixel, il DataGridView
    /// ricicla i pixel già disegnati (blit) e ridipinge solo la striscia nuova: i
    /// separatori finiscono così copiati in posizioni sbagliate. Invalidare subito non
    /// basta, perché il blit avviene dopo; il ridisegno va rimandato a layout concluso.
    /// </summary>
    private void ScheduleFullRedraw()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                Invalidate();
                Update();
            });
        }
        catch (ObjectDisposedException) { /* controllo in chiusura */ }
        catch (InvalidOperationException) { /* handle in ricreazione */ }
    }
}

/// <summary>
/// Pannello con superficie arrotondata e titolo facoltativo. Fa da contenitore alle
/// varie sezioni della finestra.
/// </summary>
public sealed class CardPanel : Panel
{
    private const int HeaderHeight = 38;

    private string _title = "";
    private string _hint = "";
    private bool _collapsed;
    private bool _headerHover;

    public CardPanel(string title = "")
    {
        _title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(16, title.Length > 0 ? 42 : 16, 16, 14);
    }

    /// <summary>Se attivo, un clic sull'intestazione riduce o espande la scheda.</summary>
    public bool Collapsible { get; set; }

    /// <summary>Testo di un pulsante facoltativo in fondo all'intestazione.</summary>
    public string? ActionText { get; set; }

    public event EventHandler? ActionClicked;
    public event EventHandler? CollapsedChanged;

    private RectangleF _actionRect = RectangleF.Empty;
    private bool _actionHover;

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            foreach (Control c in Controls) c.Visible = !value;
            Invalidate();
            CollapsedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool onAction = !_actionRect.IsEmpty && !_collapsed && _actionRect.Contains(e.X, e.Y);
        bool onHeader = Collapsible && !onAction && (_collapsed || e.Y < HeaderHeight);

        if (onAction != _actionHover) { _actionHover = onAction; Invalidate(); }
        if (onHeader != _headerHover) { _headerHover = onHeader; Invalidate(); }

        Cursor = onAction || onHeader ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_headerHover || _actionHover) { _headerHover = false; _actionHover = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        if (!_actionRect.IsEmpty && !_collapsed && _actionRect.Contains(e.X, e.Y))
        {
            ActionClicked?.Invoke(this, EventArgs.Empty);
            base.OnMouseDown(e);
            return;                                   // il pulsante non riduce la scheda
        }

        if (Collapsible && (_collapsed || e.Y < HeaderHeight))
            Collapsed = !_collapsed;

        base.OnMouseDown(e);
    }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Invalidate(); } }
    }

    /// <summary>Testo secondario allineato a destra nell'intestazione.</summary>
    public string Hint
    {
        get => _hint;
        set { if (_hint != value) { _hint = value; Invalidate(); } }
    }

    public int CornerRadius { get; set; } = 10;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(Theme.Background);
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.DrawSurface(g, r, CornerRadius);

        if (_title.Length == 0) return;

        if (_collapsed) { PaintCollapsed(g); return; }

        if (_headerHover)
        {
            using var hover = Theme.RoundedRect(new RectangleF(4, 4, Width - 8, HeaderHeight - 6), 6);
            using var hb = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(hb, hover);
        }

        float textLeft = 16;
        if (Collapsible)
        {
            DrawChevron(g, new PointF(20, HeaderHeight / 2f), pointingRight: false);
            textLeft = 34;
        }

        using (var b = new SolidBrush(Theme.TextPrimary))
            g.DrawString(_title, Theme.Section, b, textLeft, 13);

        float rightEdge = Width - 16;

        if (!string.IsNullOrEmpty(ActionText))
        {
            var textSize = g.MeasureString(ActionText, Theme.SmallStrong);
            float w = textSize.Width + 22;
            _actionRect = new RectangleF(Width - 14 - w, 8, w, 22);

            using (var path = Theme.RoundedRect(_actionRect, 6))
            {
                if (_actionHover)
                {
                    using var fill = new SolidBrush(Theme.SurfaceHover);
                    g.FillPath(fill, path);
                }
                using var p = new Pen(_actionHover ? Theme.Accent : Theme.Border, 1f);
                g.DrawPath(p, path);
            }

            using var tb = new SolidBrush(_actionHover ? Theme.Accent : Theme.TextSecondary);
            using var af = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(ActionText, Theme.SmallStrong, tb, _actionRect, af);

            rightEdge = _actionRect.Left - 10;
        }
        else
        {
            _actionRect = RectangleF.Empty;
        }

        if (_hint.Length > 0)
        {
            using var hb = new SolidBrush(Theme.TextMuted);
            var size = g.MeasureString(_hint, Theme.Small);
            g.DrawString(_hint, Theme.Small, hb, rightEdge - size.Width, 15);
        }

        using var pen = new Pen(Theme.Divider, 1f);
        g.DrawLine(pen, 14, 36, Width - 14, 36);
    }

    /// <summary>Da ridotta la scheda diventa una linguetta con il titolo in verticale.</summary>
    private void PaintCollapsed(Graphics g)
    {
        if (_headerHover)
        {
            using var hover = Theme.RoundedRect(new RectangleF(3, 3, Width - 6, Height - 6), CornerRadius - 2);
            using var hb = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(hb, hover);
        }

        DrawChevron(g, new PointF(Width / 2f, 20), pointingRight: true);

        var state = g.Save();
        g.TranslateTransform(Width / 2f + 6, 44);
        g.RotateTransform(90);
        using (var b = new SolidBrush(Theme.TextSecondary))
            g.DrawString(_title, Theme.Section, b, 0, 0);
        g.Restore(state);
    }

    private void DrawChevron(Graphics g, PointF center, bool pointingRight)
    {
        using var pen = new Pen(_headerHover ? Theme.Accent : Theme.TextSecondary, 1.7f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        float s = 4f;
        PointF[] points = pointingRight
            ? [new(center.X - s / 2, center.Y - s), new(center.X + s / 2, center.Y), new(center.X - s / 2, center.Y + s)]
            : [new(center.X - s, center.Y - s / 2), new(center.X, center.Y + s / 2), new(center.X + s, center.Y - s / 2)];

        g.DrawLines(pen, points);
    }
}

/// <summary>Pulsante piatto della barra strumenti, con glifo Segoe Fluent e testo.</summary>
public sealed class ToolButton : Control
{
    private bool _hover;
    private bool _pressed;

    public string Glyph { get; set; } = "";
    public bool Primary { get; set; }

    public ToolButton(string glyph, string text, EventHandler onClick)
    {
        Glyph = glyph;
        Text = text;
        Height = 32;
        Cursor = Cursors.Hand;
        Click += onClick;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Margin = new Padding(0, 0, 6, 0);
    }

    /// <summary>Larghezza necessaria per glifo e testo, misurata alla creazione.</summary>
    public void AutoWidth()
    {
        using var g = CreateGraphics();
        float w = 18;
        if (Glyph.Length > 0) w += g.MeasureString(Glyph, GlyphFont).Width + 4;
        if (!string.IsNullOrEmpty(Text)) w += g.MeasureString(Text, Theme.Body).Width + 6;
        Width = (int)Math.Ceiling(w);
    }

    private static Font GlyphFont => _glyphFont ??= BuildGlyphFont();
    private static Font? _glyphFont;

    private static Font BuildGlyphFont()
    {
        string family = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";
        return new Font(family, 10.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        Color fill, fore;
        if (Primary)
        {
            fill = _pressed ? Theme.Mix(Theme.Accent, Theme.Background, 0.25f)
                 : _hover ? Theme.Mix(Theme.Accent, Color.White, Theme.IsDark ? 0.12f : 0.10f)
                 : Theme.Accent;
            fore = Theme.IsDark ? Theme.FromHex("#0B1A25") : Color.White;
        }
        else
        {
            fill = _pressed ? Theme.SurfaceHover
                 : _hover ? Theme.SurfaceAlt
                 : Color.Transparent;
            fore = Theme.TextPrimary;
        }

        if (fill != Color.Transparent)
        {
            using var path = Theme.RoundedRect(r, 6);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        float x = 9;
        using var brush = new SolidBrush(fore);
        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        if (Glyph.Length > 0)
        {
            g.DrawString(Glyph, GlyphFont, brush, new RectangleF(x, 0, 20, Height), fmt);
            x += g.MeasureString(Glyph, GlyphFont).Width + 2;
        }

        if (!string.IsNullOrEmpty(Text))
            g.DrawString(Text, Theme.Body, brush, new RectangleF(x, 0, Width - x, Height), fmt);
    }
}

/// <summary>
/// Casella di spunta disegnata da noi. Quella di sistema, con FlatStyle.Flat e sfondo
/// trasparente, risulta praticamente invisibile sul fondo scuro.
/// </summary>
public sealed class ThemedCheckBox : CheckBox
{
    private const int BoxSize = 17;
    private bool _hover;

    public ThemedCheckBox(string text)
    {
        Text = text;
        AutoSize = false;
        Height = 24;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Adatta la larghezza al testo.</summary>
    public void AutoWidth()
    {
        using var g = CreateGraphics();
        Width = (int)Math.Ceiling(BoxSize + 8 + g.MeasureString(Text, Theme.Body).Width) + 6;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        var box = new RectangleF(1f, (Height - BoxSize) / 2f, BoxSize, BoxSize);
        using (var path = Theme.RoundedRect(box, 4))
        {
            if (Checked)
            {
                using var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Neutral);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(_hover ? Theme.SurfaceHover : Theme.SurfaceAlt);
                g.FillPath(b, path);
                using var p = new Pen(_hover ? Theme.Accent : Theme.Border, 1.3f);
                g.DrawPath(p, path);
            }
        }

        if (Checked)
        {
            // Segno di spunta disegnato a mano: resta nitido a ogni scala.
            var check = Theme.IsDark ? Theme.FromHex("#0B1A25") : Color.White;
            using var pen = new Pen(check, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = box.X, y = box.Y, s = box.Width;
            g.DrawLines(pen,
            [
                new PointF(x + s * 0.24f, y + s * 0.52f),
                new PointF(x + s * 0.43f, y + s * 0.71f),
                new PointF(x + s * 0.77f, y + s * 0.30f),
            ]);
        }

        using var brush = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted);
        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(Text, Theme.Body, brush,
            new RectangleF(box.Right + 8, 0, Width - box.Right - 8, Height), fmt);
    }
}

/// <summary>Barra di schede disegnata a mano, in tinta col resto dell'interfaccia.</summary>
public sealed class TabBar : Control
{
    private readonly List<string> _items = [];
    private int _selected;
    private int _hovered = -1;

    public event EventHandler? SelectedChanged;

    public TabBar()
    {
        Height = 38;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetItems(params string[] items)
    {
        _items.Clear();
        _items.AddRange(items);
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, _items.Count - 1));
            if (clamped == _selected) return;
            _selected = clamped;
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private RectangleF TabBounds(Graphics g, int index)
    {
        float x = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            float w = g.MeasureString(_items[i], Theme.Section).Width + 34;
            if (i == index) return new RectangleF(x, 0, w, Height);
            x += w;
        }
        return RectangleF.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        using var g = CreateGraphics();
        int found = -1;
        for (int i = 0; i < _items.Count; i++)
            if (TabBounds(g, i).Contains(e.X, e.Y)) { found = i; break; }

        if (found != _hovered) { _hovered = found; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hovered >= 0) { _hovered = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        using var g = CreateGraphics();
        for (int i = 0; i < _items.Count; i++)
            if (TabBounds(g, i).Contains(e.X, e.Y)) { SelectedIndex = i; break; }

        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        // Linea di base sotto tutte le schede
        using (var pen = new Pen(Theme.Border, 1f))
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        for (int i = 0; i < _items.Count; i++)
        {
            var r = TabBounds(g, i);
            bool active = i == _selected;

            if (_hovered == i && !active)
            {
                using var path = Theme.RoundedRect(new RectangleF(r.X + 3, 4, r.Width - 6, Height - 10), 6);
                using var b = new SolidBrush(Theme.SurfaceHover);
                g.FillPath(b, path);
            }

            using (var b = new SolidBrush(active ? Theme.TextPrimary : Theme.TextMuted))
                g.DrawString(_items[i], active ? Theme.Section : Theme.Body, b, r, fmt);

            if (active)
            {
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, r.X + 10, Height - 3, r.Width - 20, 3);
            }
        }
    }
}

/// <summary>Tavolozza per i menu a discesa, coerente col tema.</summary>
internal sealed class ThemedColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.SurfaceAlt;
    public override Color ImageMarginGradientBegin => Theme.SurfaceAlt;
    public override Color ImageMarginGradientMiddle => Theme.SurfaceAlt;
    public override Color ImageMarginGradientEnd => Theme.SurfaceAlt;
    public override Color MenuItemSelected => Theme.SurfaceHover;
    public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
    public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
    public override Color MenuItemBorder => Theme.Accent;
    public override Color MenuBorder => Theme.Border;
    public override Color SeparatorDark => Theme.Divider;
    public override Color SeparatorLight => Theme.Divider;
}

public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new ThemedColorTable()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item!.Enabled ? Theme.TextPrimary : Theme.TextMuted;
        base.OnRenderItemText(e);
    }
}

/// <summary>
/// Elenco a discesa disegnato da noi. La ComboBox di sistema in stile DropDownList
/// ignora BackColor, quindi resta bianca anche nel tema scuro.
/// </summary>
public sealed class ThemedCombo : Control
{
    private readonly List<string> _items = [];
    private ContextMenuStrip? _menu;
    private bool _menuDirty = true;
    private int _selectedIndex = -1;
    private bool _hover;
    private bool _open;

    public event EventHandler? SelectedIndexChanged;

    public ThemedCombo()
    {
        Height = 28;
        Width = 200;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public IReadOnlyList<string> Items => _items;

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count - 1;
        _menuDirty = true;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectedText => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : "";

    public int IndexOf(string value) =>
        _items.FindIndex(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        ShowList();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down: SelectedIndex = _selectedIndex + 1; e.Handled = true; break;
            case Keys.Up: SelectedIndex = _selectedIndex - 1; e.Handled = true; break;
            case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
            case Keys.End: SelectedIndex = _items.Count - 1; e.Handled = true; break;
            case Keys.Space or Keys.Enter: ShowList(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Il menu viene creato una volta sola e riusato. Liberarlo alla chiusura (evento
    /// Closed) manda in eccezione WinForms, che continua a usarlo subito dopo il clic
    /// su una voce: è la causa dei blocchi quando si sceglieva un valore.
    /// </summary>
    private ContextMenuStrip EnsureMenu()
    {
        _menu ??= new ContextMenuStrip
        {
            Renderer = new ThemedMenuRenderer(),
            ShowImageMargin = false,
            // Elenchi lunghi (i font installati) devono restare navigabili.
            MaximumSize = new Size(Math.Max(Width, 320), 420),
        };

        _menu.BackColor = Theme.SurfaceAlt;
        _menu.ForeColor = Theme.TextPrimary;
        _menu.Font = Theme.Body;
        _menu.MinimumSize = new Size(Width, 0);

        if (_menuDirty)
        {
            foreach (ToolStripItem old in _menu.Items) old.Dispose();
            _menu.Items.Clear();

            for (int i = 0; i < _items.Count; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(_items[i]);
                item.Click += (_, _) => SelectedIndex = index;
                _menu.Items.Add(item);
            }
            _menuDirty = false;
        }

        for (int i = 0; i < _menu.Items.Count; i++)
            if (_menu.Items[i] is ToolStripMenuItem mi) mi.Checked = i == _selectedIndex;

        return _menu;
    }

    private void ShowList()
    {
        if (_items.Count == 0 || _open) return;

        var menu = EnsureMenu();
        _open = true;
        menu.Closed -= OnMenuClosed;
        menu.Closed += OnMenuClosed;
        menu.Show(this, new Point(0, Height + 2));
        Invalidate();
    }

    private void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        _open = false;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        bool active = _hover || Focused || _open;
        Theme.DrawSurface(g, r, 6, Theme.SurfaceAlt, active ? Theme.Accent : Theme.Border, active ? 1.4f : 1f);

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using (var b = new SolidBrush(Theme.TextPrimary))
            g.DrawString(SelectedText, Theme.Body, b, new RectangleF(10, 0, Width - 34, Height), fmt);

        // Freccia disegnata a mano: nessuna dipendenza dai font di icone
        using var pen = new Pen(Theme.TextSecondary, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - 15, cy = Height / 2f - 1;
        g.DrawLines(pen, [new PointF(cx - 4, cy - 1.5f), new PointF(cx, cy + 2.5f), new PointF(cx + 4, cy - 1.5f)]);
    }
}

/// <summary>
/// Campo numerico disegnato da noi. Oltre al tema risolve un fastidio del
/// NumericUpDown di sistema: cambia valore alla rotellina anche senza il fuoco,
/// per cui scorrendo una pagina si alterano i campi che si attraversano.
/// </summary>
public sealed class ThemedSpin : Control
{
    private int _value;
    private bool _hover;
    private int _hotButton;          // 0 nessuno, 1 su, -1 giù
    private string _typed = "";

    public event EventHandler? ValueChanged;

    public int Minimum { get; }
    public int Maximum { get; }

    public ThemedSpin(int minimum, int maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
        _value = minimum;
        Height = 28;
        Width = 92;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle UpButton => new(Width - 20, 3, 16, (Height - 6) / 2);
    private Rectangle DownButton => new(Width - 20, Height / 2, 16, (Height - 6) / 2);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _hotButton = 0; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hot = UpButton.Contains(e.Location) ? 1 : DownButton.Contains(e.Location) ? -1 : 0;
        if (hot != _hotButton) { _hotButton = hot; Invalidate(); }
        Cursor = hot != 0 ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _typed = "";
        if (UpButton.Contains(e.Location)) Value++;
        else if (DownButton.Contains(e.Location)) Value--;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Solo con il fuoco: così lo scorrimento della pagina non tocca i valori.
        if (Focused) Value += Math.Sign(e.Delta);
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Value++; _typed = ""; e.Handled = true; break;
            case Keys.Down: Value--; _typed = ""; e.Handled = true; break;
            case Keys.Home: Value = Minimum; _typed = ""; e.Handled = true; break;
            case Keys.End: Value = Maximum; _typed = ""; e.Handled = true; break;
            case Keys.Back when _typed.Length > 0:
                _typed = _typed[..^1];
                if (_typed.Length > 0 && int.TryParse(_typed, out int back)) Value = back;
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (char.IsDigit(e.KeyChar))
        {
            // Digitazione diretta: si accumulano le cifre finché restano nel campo.
            string candidate = _typed + e.KeyChar;
            if (candidate.Length <= Maximum.ToString().Length && int.TryParse(candidate, out int v))
            {
                _typed = candidate;
                Value = v;
            }
            e.Handled = true;
        }
        base.OnKeyPress(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _typed = ""; Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        bool active = _hover || Focused;
        Theme.DrawSurface(g, r, 6, Theme.SurfaceAlt, active ? Theme.Accent : Theme.Border, active ? 1.4f : 1f);

        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using (var b = new SolidBrush(Theme.TextPrimary))
            g.DrawString(_value.ToString(), Theme.Body, b, new RectangleF(10, 0, Width - 32, Height), fmt);

        DrawArrow(g, UpButton, up: true, _hotButton == 1);
        DrawArrow(g, DownButton, up: false, _hotButton == -1);
    }

    private static void DrawArrow(Graphics g, Rectangle area, bool up, bool hot)
    {
        using var pen = new Pen(hot ? Theme.Accent : Theme.TextSecondary, 1.6f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round };

        float cx = area.X + area.Width / 2f;
        float cy = area.Y + area.Height / 2f;
        float dy = up ? 2f : -2f;

        g.DrawLines(pen,
        [
            new PointF(cx - 3.5f, cy + dy / 2),
            new PointF(cx, cy - dy / 2),
            new PointF(cx + 3.5f, cy + dy / 2),
        ]);
    }
}

/// <summary>Riga chiave/valore del pannello dettagli, con copia facoltativa.</summary>
public sealed class DetailRow : Control
{
    private readonly bool _copyable;
    private bool _hoverCopy;
    private string _value;

    public string Key { get; }

    public DetailRow(string key, string value, bool copyable = false)
    {
        Key = key;
        _value = value;
        _copyable = copyable;
        Height = 26;
        Dock = DockStyle.Top;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        if (copyable)
        {
            Cursor = Cursors.Hand;
            Click += (_, _) => CopyValue();
        }
    }

    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; Invalidate(); } }
    }

    public Color? ValueColor { get; set; }
    public Font? ValueFont { get; set; }
    public int KeyWidth { get; set; } = 150;

    private void CopyValue()
    {
        if (_value.Length == 0 || _value == "—") return;
        try { Clipboard.SetText(_value); } catch { /* clipboard occupata */ }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = _copyable && e.X > KeyWidth;
        if (over != _hoverCopy) { _hoverCopy = over; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverCopy = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        using (var kb = new SolidBrush(Theme.TextMuted))
            g.DrawString(Key, Theme.Small, kb, new RectangleF(0, 0, KeyWidth - 8, Height), fmt);

        var font = ValueFont ?? Theme.Body;
        float valueLeft = KeyWidth;
        float valueWidth = Width - valueLeft - (_copyable ? 26 : 4);

        if (_hoverCopy)
        {
            using var path = Theme.RoundedRect(
                new RectangleF(valueLeft - 6, 2, Width - valueLeft + 4, Height - 4), 5);
            using var hb = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(hb, path);
        }

        using (var vb = new SolidBrush(ValueColor ?? Theme.TextPrimary))
            g.DrawString(_value, font, vb, new RectangleF(valueLeft, 0, valueWidth, Height), fmt);

        if (_copyable && _hoverCopy)
        {
            using var cb = new SolidBrush(Theme.Accent);
            using var glyphFmt = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            g.DrawString("", CopyGlyphFont, cb, new RectangleF(Width - 24, 0, 20, Height), glyphFmt);
        }
    }

    private static Font CopyGlyphFont => _copyGlyph ??= new Font(
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
        9f, FontStyle.Regular, GraphicsUnit.Point);
    private static Font? _copyGlyph;
}

/// <summary>
/// Copre una sezione della finestra mentre i dati vengono riletti: un anello che gira,
/// il motivo dell'attesa e, sotto, una riga di dettaglio.
/// <para>
/// Serve a due cose: dire che sta succedendo qualcosa, e impedire di leggere valori
/// vecchi presentandoli come attuali mentre la lettura è in corso.
/// </para>
/// </summary>
public sealed class BusyOverlay : Control
{
    private const int Diameter = 46;

    private readonly System.Windows.Forms.Timer _animazione = new() { Interval = 55 };
    private float _angolo;

    public string Message { get; private set; } = "";
    public string? Detail { get; private set; }

    public BusyOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Visible = false;
        _animazione.Tick += (_, _) =>
        {
            _angolo = (_angolo + 11f) % 360f;
            Invalidate();
        };
    }

    /// <summary>Mostra l'indicatore. Il timer gira solo mentre è visibile.</summary>
    public void Begin(string message, string? detail = null)
    {
        Message = message;
        Detail = detail;

        if (!Visible)
        {
            Visible = true;
            BringToFront();
        }
        else
        {
            Invalidate();
        }

        _animazione.Start();
    }

    public void End()
    {
        _animazione.Stop();
        if (Visible) Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        float centroX = Width / 2f;
        float centroY = Height / 2f - 26;
        var anello = new RectangleF(centroX - Diameter / 2f, centroY - Diameter / 2f, Diameter, Diameter);

        using (var pista = new Pen(Theme.Track, 4f))
            g.DrawEllipse(pista, anello);

        // L'arco in movimento: un quarto di giro, con le estremità arrotondate.
        using (var arco = new Pen(Theme.Accent, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arco, anello, _angolo, 96f);

        using var fmt = new StringFormat { Alignment = StringAlignment.Center };

        using (var b = new SolidBrush(Theme.TextSecondary))
            g.DrawString(Message, Theme.BodyStrong, b,
                new RectangleF(10, centroY + Diameter / 2f + 16, Width - 20, 24), fmt);

        if (Detail is null) return;

        using (var b = new SolidBrush(Theme.TextMuted))
            g.DrawString(Detail, Theme.Small, b,
                new RectangleF(10, centroY + Diameter / 2f + 42, Width - 20, 22), fmt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animazione.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Barra di scorrimento verticale disegnata da noi, al posto di quella di sistema.
/// <para>
/// Serve perché la barra nativa non si lascia colorare: in tema chiaro Windows la
/// disegna quasi bianca, e su una tavolozza attenuata come questa spicca come una
/// striscia luminosa dentro la scheda. Qui invece il binario prende il colore della
/// superficie che la ospita e sparisce, come fa quella scura.
/// </para>
/// <para>
/// Lavora per unità, non per pixel: chi la usa decide cosa sia un'unità — una riga di
/// tabella, un pixel di contenuto — e riceve il valore corrente.
/// </para>
/// </summary>
public sealed class ThemedScrollBar : Control
{
    private const int ThumbInset = 3;
    private const int MinimumThumb = 26;

    private int _maximum;
    private int _largeChange = 1;
    private int _value;

    private bool _hover;
    private bool _dragging;
    private int _dragOffset;

    public event EventHandler? ValueChanged;

    public ThemedScrollBar()
    {
        Width = 12;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Valore massimo raggiungibile, cioè quante unità restano fuori dalla vista.</summary>
    public int Maximum
    {
        get => _maximum;
        set
        {
            int nuovo = Math.Max(0, value);
            if (_maximum == nuovo) return;
            _maximum = nuovo;
            if (_value > _maximum) Value = _maximum;
            Invalidate();
        }
    }

    /// <summary>Quante unità entrano nella vista: determina la lunghezza del cursore.</summary>
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            int nuovo = Math.Max(1, value);
            if (_largeChange == nuovo) return;
            _largeChange = nuovo;
            Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int nuovo = Math.Clamp(value, 0, _maximum);
            if (_value == nuovo) return;
            _value = nuovo;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Vero quando c'è davvero qualcosa da scorrere.</summary>
    public bool Needed => _maximum > 0;

    // --------------------------------------------------------------- geometria

    private float TotaleUnita => _maximum + _largeChange;

    private RectangleF Thumb
    {
        get
        {
            if (!Needed) return RectangleF.Empty;

            float pista = Height - ThumbInset * 2;
            float altezza = Math.Max(MinimumThumb, pista * (_largeChange / TotaleUnita));
            float corsa = pista - altezza;
            float y = ThumbInset + corsa * (_maximum > 0 ? (float)_value / _maximum : 0);
            return new RectangleF(ThumbInset, y, Width - ThumbInset * 2, altezza);
        }
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Needed)
        {
            var thumb = Thumb;
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = (int)(e.Y - thumb.Y);
                Capture = true;
            }
            else
            {
                // Clic sul binario: si salta di una schermata, come nella barra di sistema.
                Value += e.Y < thumb.Y ? -_largeChange : _largeChange;
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) TrascinaA(e.Y);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; Capture = false; }
        base.OnMouseUp(e);
    }

    private void TrascinaA(int y)
    {
        float pista = Height - ThumbInset * 2;
        float altezza = Thumb.Height;
        float corsa = pista - altezza;
        if (corsa <= 0) return;

        float posizione = Math.Clamp(y - _dragOffset - ThumbInset, 0, corsa);
        Value = (int)Math.Round(posizione / corsa * _maximum);
    }

    // ---------------------------------------------------------------- disegno

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (!Needed) return;

        var thumb = Thumb;
        var colore = _dragging ? Theme.TextMuted
                   : _hover ? Theme.Mix(Theme.Border, Theme.TextMuted, 0.6f)
                   : Theme.Mix(Theme.Border, Theme.TextMuted, 0.25f);

        using var path = Theme.RoundedRect(thumb, thumb.Width / 2f);
        using var b = new SolidBrush(colore);
        g.FillPath(b, path);
    }
}

/// <summary>
/// Anello che gira, minuto, da mettere accanto a un testo.
/// <para>
/// Serve a dire "sto leggendo" senza rubare la riga: la barra di stato continua a
/// mostrare il riepilogo di prima, che resta leggibile, e il movimento qui di fianco
/// basta a far capire che c'è un'operazione in corso. Sostituire il testo a ogni giro,
/// come si faceva, rendeva la riga illeggibile e per giunta inutile.
/// </para>
/// </summary>
public sealed class BusySpinner : Control
{
    private const float Diameter = 12f;

    private readonly System.Windows.Forms.Timer _animazione = new() { Interval = 65 };
    private float _angolo;
    private bool _attivo;

    public BusySpinner()
    {
        Width = 20;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);

        _animazione.Tick += (_, _) =>
        {
            _angolo = (_angolo + 16f) % 360f;
            Invalidate();
        };
    }

    /// <summary>Il controllo resta sempre al suo posto: acceso gira, spento è vuoto.</summary>
    public bool Active
    {
        get => _attivo;
        set
        {
            if (_attivo == value) return;
            _attivo = value;

            if (value) _animazione.Start();
            else _animazione.Stop();

            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (!_attivo) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var anello = new RectangleF(
            (Width - Diameter) / 2f, (Height - Diameter) / 2f, Diameter, Diameter);

        using (var pista = new Pen(Theme.Alpha(Theme.TextMuted, 70), 2f))
            g.DrawEllipse(pista, anello);

        using var arco = new Pen(Theme.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arco, anello, _angolo, 110f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animazione.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Etichetta di sola lettura disegnata da noi, in doppio buffer.
/// <para>
/// La Label di WinForms non lo è: ogni volta che le si assegna un testo si ridipinge sullo
/// schermo, e su una riga che cambia di continuo — come la barra di stato — si vede
/// sfarfallare. Qui il disegno passa da un buffer, e il cambio di testo è netto.
/// </para>
/// </summary>
public sealed class StatusLabel : Control
{
    private ContentAlignment _alignment = ContentAlignment.MiddleLeft;

    public StatusLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public ContentAlignment Alignment
    {
        get => _alignment;
        set { if (_alignment != value) { _alignment = value; Invalidate(); } }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (Text.Length == 0) return;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = _alignment is ContentAlignment.MiddleRight or ContentAlignment.TopRight
                                   or ContentAlignment.BottomRight
                ? StringAlignment.Far
                : StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        using var b = new SolidBrush(ForeColor);
        g.DrawString(Text, Font, b, new RectangleF(0, 0, Width, Height), fmt);
    }
}

/// <summary>
/// Campo per scegliere una combinazione di tasti premendola invece che sceglierla da un
/// elenco.
/// <para>
/// Un elenco di combinazioni pronte sembra comodo finché non si scopre che sono tutte
/// già occupate da qualcos'altro: driver video, programmi di registrazione, la barra di
/// gioco di Windows. Qui si preme quello che si vuole, e chi lo preme vede subito se
/// quella combinazione è libera.
/// </para>
/// </summary>
public sealed class HotkeyBox : Control
{
    private bool _hover;
    private bool _capturing;
    private string _value = "";

    public event EventHandler? ValueChanged;

    public HotkeyBox()
    {
        Height = 28;
        Width = 200;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>La combinazione, scritta come "Ctrl+Alt+O". Vuota se non ce n'è nessuna.</summary>
    public string Value
    {
        get => _value;
        set
        {
            string nuovo = value ?? "";
            if (nuovo == _value) return;
            _value = nuovo;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Messaggio da mostrare sotto al campo: dice se la combinazione è libera.</summary>
    public string Esito { get; set; } = "";

    /// <summary>Vero quando l'esito è un problema, e va scritto nel colore dell'allarme.</summary>
    public bool EsitoNegativo { get; set; }

    protected override bool IsInputKey(Keys keyData) => true;   // servono anche Tab e frecce

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _capturing = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _capturing = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }

        e.Handled = true;
        e.SuppressKeyPress = true;

        var tasto = e.KeyCode;

        // Esc annulla, Backspace e Canc tolgono la combinazione.
        if (tasto == Keys.Escape) { _capturing = false; Invalidate(); return; }
        if (tasto is Keys.Back or Keys.Delete) { Value = ""; _capturing = false; Invalidate(); return; }

        // I tasti di servizio da soli non fanno una combinazione: si aspetta il terzo.
        if (tasto is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            Invalidate();
            return;
        }

        // Senza almeno un tasto di servizio la combinazione ruberebbe quel tasto a tutto
        // il sistema: "P" premuto in un documento aprirebbe la sovrimpressione.
        if (!e.Control && !e.Alt && !e.Shift) { Invalidate(); return; }

        var pezzi = new List<string>();
        if (e.Control) pezzi.Add("Ctrl");
        if (e.Alt) pezzi.Add("Alt");
        if (e.Shift) pezzi.Add("Shift");
        pezzi.Add(tasto.ToString());

        Value = string.Join("+", pezzi);
        _capturing = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        bool attivo = _hover || Focused || _capturing;
        Theme.DrawSurface(g, r, 6, Theme.SurfaceAlt,
                          _capturing ? Theme.Accent : attivo ? Theme.Accent : Theme.Border,
                          _capturing ? 1.8f : attivo ? 1.4f : 1f);

        using var fmt = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        string testo = _capturing ? "Premi la combinazione…"
                     : _value.Length > 0 ? _value
                     : "Nessuna — fai clic e premi i tasti";

        var colore = _capturing ? Theme.Accent
                   : _value.Length > 0 ? Theme.TextPrimary
                   : Theme.TextSecondary;

        using var b = new SolidBrush(colore);
        g.DrawString(testo, Theme.Body, b, new RectangleF(10, 0, Width - 20, Height), fmt);
    }
}
