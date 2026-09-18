using System.Drawing.Drawing2D;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Dove va la sovrimpressione, con l'anteprima di come verrà.
/// <para>
/// Regolare la posizione a numeri — angolo, scostamento, dimensione — significava
/// chiudere le impostazioni, entrare in partita, guardare, uscire e ricominciare. Qui
/// si vede subito: lo schermo in miniatura con la targhetta al suo posto, che si
/// <b>trascina</b> dove si vuole, e accanto la stessa targhetta a grandezza naturale
/// per giudicarne la leggibilità.
/// </para>
/// <para>
/// L'anteprima non è un disegno somigliante: è la targhetta vera, prodotta dallo stesso
/// codice che la disegna in partita. Un'anteprima fatta a parte prometterebbe prima o
/// poi qualcosa di diverso da quello che si vede poi.
/// </para>
/// </summary>
public sealed class OverlayPlacementForm : Form
{
    private readonly AppSettings _work;
    private readonly IReadOnlyList<DiskInfo> _disks;
    private readonly SystemMetrics _finti = Campione();

    private readonly Panel _schermo = new();
    private readonly Panel _vero = new();
    private CardPanel? _cardVero;
    private readonly ThemedCombo _corner = new();
    private readonly ThemedCombo _layout = new();
    private readonly ThemedSpin _offsetX = new(-4000, 4000);
    private readonly ThemedSpin _offsetY = new(-4000, 4000);
    private readonly ThemedSpin _opacity = new(10, 100);
    private readonly ThemedSpin _scale = new(60, 250);
    private readonly Label _spiega = new();
    private readonly List<Control> _themed = [];

    private Rectangle _areaSchermo;      // l'area di lavoro del monitor, in pixel veri
    private Rectangle _targhetta;        // dove cade la targhetta nell'anteprima
    private bool _trascina;
    private Point _presaIn;
    private bool _loading;

    public OverlayPlacementForm(AppSettings work, IReadOnlyList<DiskInfo>? disks = null)
    {
        _work = work;
        _disks = disks ?? [];
        _areaSchermo = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;

        BuildUi();
        LoadValues();
        ApplyThemeColors();
        Theme.Changed += OnThemeChanged;
    }

    // --------------------------------------------------------------- interfaccia

    private void BuildUi()
    {
        Text = "Posizione della sovrimpressione";
        Icon = IconRenderer.CreateAppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 872);
        Font = Theme.Body;

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 16, 16, 8),
        };

        // --------------------------------------------------- lo schermo in miniatura
        var cSchermo = Card("Trascina la targhetta dove la vuoi", 0, 286);
        _schermo.Dock = DockStyle.Fill;
        _schermo.Cursor = Cursors.SizeAll;
        _schermo.Paint += SchermoPaint;
        _schermo.MouseDown += SchermoMouseDown;
        _schermo.MouseMove += SchermoMouseMove;
        _schermo.MouseUp += (_, _) => _trascina = false;
        cSchermo.Controls.Add(_schermo);
        _themed.Add(_schermo);

        // --------------------------------------------------- comandi
        var cComandi = Card("Regolazioni", 4);
        var t = Rows(cComandi, 4);

        _corner.Width = 250;
        _corner.SetItems(["In alto a sinistra", "In alto a destra",
                          "In basso a sinistra", "In basso a destra"]);
        _corner.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.OverlayCorner = (OverlayCorner)_corner.SelectedIndex;

            // Cambiando angolo lo scostamento vecchio non vuol più dire niente: era
            // riferito a un altro punto di partenza, e lascerebbe la targhetta in un
            // posto che nessuno ha scelto.
            _work.OverlayOffsetX = 0;
            _work.OverlayOffsetY = 0;
            LoadValues();
            Aggiorna();
        };
        AddRow(t, 0, "Angolo", _corner);

        _layout.Width = 250;
        _layout.SetItems(["Verticale (un valore per riga)", "Orizzontale (tre per riga)"]);
        _layout.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.OverlayLayout = (OverlayLayout)_layout.SelectedIndex;
            Aggiorna();
        };
        AddRow(t, 1, "Disposizione", _layout);

        _offsetX.Width = 86;
        _offsetY.Width = 86;
        _offsetY.Margin = new Padding(10, 0, 0, 0);
        _offsetX.ValueChanged += (_, _) => { if (!_loading) { _work.OverlayOffsetX = _offsetX.Value; Aggiorna(); } };
        _offsetY.ValueChanged += (_, _) => { if (!_loading) { _work.OverlayOffsetY = _offsetY.Value; Aggiorna(); } };
        AddRow(t, 2, "Scostamento (px)", Wrap(_offsetX, _offsetY, Hint("orizzontale · verticale")));

        _opacity.Width = 86;
        _scale.Width = 86;
        _scale.Margin = new Padding(10, 0, 0, 0);
        _opacity.ValueChanged += (_, _) => { if (!_loading) { _work.OverlayOpacityPercent = _opacity.Value; Aggiorna(); } };
        _scale.ValueChanged += (_, _) => { if (!_loading) { _work.OverlayScalePercent = _scale.Value; Aggiorna(); } };
        AddRow(t, 3, "Sfondo / dimensione (%)", Wrap(_opacity, _scale, Hint("coprente · testo")));

        // --------------------------------------------------- a grandezza vera
        var cVero = _cardVero = Card("Come si vedrà, a grandezza naturale", 0, 168);
        _vero.Dock = DockStyle.Fill;
        _vero.Paint += VeroPaint;
        cVero.Controls.Add(_vero);
        _themed.Add(_vero);

        root.Controls.AddRange([cSchermo, cComandi, cVero]);

        // --------------------------------------------------- pulsanti
        var barra = new BufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(16, 12, 16, 12),
            Tag = "bar",
        };
        _themed.Add(barra);

        var chiudi = MakeButton("Chiudi", primary: true);
        chiudi.DialogResult = DialogResult.OK;

        var azzera = MakeButton("Azzera scostamento", primary: false);
        azzera.Width = 168;
        azzera.Click += (_, _) =>
        {
            _work.OverlayOffsetX = 0;
            _work.OverlayOffsetY = 0;
            LoadValues();
            Aggiorna();
        };

        _spiega.AutoSize = true;
        _spiega.Margin = new Padding(12, 10, 0, 0);
        _spiega.BackColor = Color.Transparent;
        _spiega.Tag = "hint";
        _themed.Add(_spiega);

        var flusso = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Tag = "transparent",
        };
        flusso.Controls.AddRange([chiudi, azzera, _spiega]);
        barra.Controls.Add(flusso);
        _themed.Add(flusso);

        Controls.Add(root);
        Controls.Add(barra);
        _themed.Add(root);

        AcceptButton = chiudi;
        CancelButton = chiudi;

        Shown += (_, _) =>
        {
            Theme.ApplyToWindow(this);
            Theme.ApplyNativeTheme(this);
            Aggiorna();
        };
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            _corner.SelectedIndex = (int)_work.OverlayCorner;
            _layout.SelectedIndex = (int)_work.OverlayLayout;
            _offsetX.Value = _work.OverlayOffsetX;
            _offsetY.Value = _work.OverlayOffsetY;
            _opacity.Value = _work.OverlayOpacityPercent;
            _scale.Value = _work.OverlayScalePercent;
        }
        finally { _loading = false; }
    }

    private void Aggiorna()
    {
        _spiega.Text = $"scostamento {_work.OverlayOffsetX:+#;-#;0} · {_work.OverlayOffsetY:+#;-#;0} px";

        // Il riquadro a grandezza naturale prende l'altezza che serve davvero: la
        // disposizione verticale fa una colonna alta, quella orizzontale una striscia
        // bassa, e un'altezza fissa o sprecherebbe spazio o taglierebbe la targhetta.
        if (_cardVero is not null)
        {
            using var misura = OverlayRenderer.Draw(_work, _disks, _finti, 237f);
            int voluta = (misura?.Height ?? 80) + 44 + 28;
            int nuova = Math.Clamp(voluta, 120, 360);
            if (_cardVero.Height != nuova) _cardVero.Height = nuova;
        }

        _schermo.Invalidate();
        _vero.Invalidate();
    }

    // --------------------------------------------------------------- anteprima

    /// <summary>
    /// Dove cade la targhetta sullo schermo vero, con l'angolo e lo scostamento
    /// scelti. È lo stesso conto che fa la sovrimpressione quando si posiziona: se
    /// fossero due conti diversi, l'anteprima mentirebbe.
    /// </summary>
    private Rectangle PostoVero(Size targhetta)
    {
        int margine = Math.Max(8, _work.OverlayMargin);

        int x = _work.OverlayCorner is OverlayCorner.AltoSinistra or OverlayCorner.BassoSinistra
            ? _areaSchermo.Left + margine
            : _areaSchermo.Right - targhetta.Width - margine;

        int y = _work.OverlayCorner is OverlayCorner.AltoSinistra or OverlayCorner.AltoDestra
            ? _areaSchermo.Top + margine
            : _areaSchermo.Bottom - targhetta.Height - margine;

        return new Rectangle(x + _work.OverlayOffsetX, y + _work.OverlayOffsetY,
                             targhetta.Width, targhetta.Height);
    }

    private void SchermoPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Theme.Surface);

        var viewport = _schermo.ClientRectangle;
        if (viewport.Width < 40 || viewport.Height < 40) return;

        // Il rettangolo dello schermo, in proporzione, dentro lo spazio disponibile.
        float fattore = Math.Min((viewport.Width - 24f) / _areaSchermo.Width,
                                 (viewport.Height - 24f) / _areaSchermo.Height);
        int w = (int)(_areaSchermo.Width * fattore);
        int h = (int)(_areaSchermo.Height * fattore);
        var cornice = new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);

        using (var sfondo = new LinearGradientBrush(cornice, Color.FromArgb(44, 52, 66),
                                                    Color.FromArgb(22, 26, 34), 55f))
            g.FillRectangle(sfondo, cornice);
        using (var bordo = new Pen(Theme.Border, 1f))
            g.DrawRectangle(bordo, cornice);

        using (var etichetta = new SolidBrush(Theme.TextSecondary))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center })
            g.DrawString($"schermo {_areaSchermo.Width}×{_areaSchermo.Height}", Theme.Micro, etichetta,
                         new RectangleF(cornice.X, cornice.Bottom - 20, cornice.Width, 18), fmt);

        using var targhetta = OverlayRenderer.Draw(_work, _disks, _finti, 237f);
        if (targhetta is null) return;

        var posto = PostoVero(targhetta.Size);

        // Dalle coordinate dello schermo a quelle dell'anteprima.
        _targhetta = new Rectangle(
            cornice.X + (int)((posto.X - _areaSchermo.X) * fattore),
            cornice.Y + (int)((posto.Y - _areaSchermo.Y) * fattore),
            Math.Max(4, (int)(posto.Width * fattore)),
            Math.Max(4, (int)(posto.Height * fattore)));

        g.DrawImage(targhetta, _targhetta);

        // Un contorno che la faccia trovare: in miniatura la targhetta è piccola, e
        // senza un segno non si capirebbe che è quella la cosa da trascinare.
        using var evidenza = new Pen(Theme.Accent, 1.4f) { DashStyle = DashStyle.Dot };
        g.DrawRectangle(evidenza, Rectangle.Inflate(_targhetta, 2, 2));
    }

    private void VeroPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);

        var area = _vero.ClientRectangle;
        using (var fondo = new SolidBrush(Color.FromArgb(30, 34, 42)))
            g.FillRectangle(fondo, area);

        using var targhetta = OverlayRenderer.Draw(_work, _disks, _finti, 237f);
        if (targhetta is null) return;

        // A grandezza naturale, e centrata: se non ci sta si dice, invece di mostrarne
        // metà e lasciar credere che sia tutta lì.
        int x = area.X + Math.Max(8, (area.Width - targhetta.Width) / 2);
        int y = area.Y + Math.Max(8, (area.Height - targhetta.Height) / 2);
        g.DrawImageUnscaled(targhetta, x, y);

        if (targhetta.Height > area.Height - 16)
        {
            using var avviso = new SolidBrush(Theme.TextSecondary);
            g.DrawString("(non ci sta tutta qui; sullo schermo sì)", Theme.Micro, avviso, 10, 6);
        }
    }

    // --------------------------------------------------------------- trascinamento

    private void SchermoMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!Rectangle.Inflate(_targhetta, 6, 6).Contains(e.Location)) return;

        _trascina = true;
        _presaIn = new Point(e.X - _targhetta.X, e.Y - _targhetta.Y);
    }

    private void SchermoMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_trascina) return;

        var viewport = _schermo.ClientRectangle;
        float fattore = Math.Min((viewport.Width - 24f) / _areaSchermo.Width,
                                 (viewport.Height - 24f) / _areaSchermo.Height);
        if (fattore <= 0) return;

        int w = (int)(_areaSchermo.Width * fattore);
        int h = (int)(_areaSchermo.Height * fattore);
        var cornice = new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);

        // Dove è finita la targhetta, riportato allo schermo vero.
        int xVero = _areaSchermo.X + (int)((e.X - _presaIn.X - cornice.X) / fattore);
        int yVero = _areaSchermo.Y + (int)((e.Y - _presaIn.Y - cornice.Y) / fattore);

        using var targhetta = OverlayRenderer.Draw(_work, _disks, _finti, 237f);
        if (targhetta is null) return;

        var senzaScostamento = PostoVero(targhetta.Size);
        senzaScostamento.Offset(-_work.OverlayOffsetX, -_work.OverlayOffsetY);

        _work.OverlayOffsetX = Math.Clamp(xVero - senzaScostamento.X, -4000, 4000);
        _work.OverlayOffsetY = Math.Clamp(yVero - senzaScostamento.Y, -4000, 4000);

        LoadValues();
        Aggiorna();
    }

    // --------------------------------------------------------------- dati di prova

    /// <summary>
    /// Valori verosimili per l'anteprima: senza, tutte le righe direbbero "—" e non si
    /// capirebbe né quanto è larga la targhetta né come si legge.
    /// </summary>
    private static SystemMetrics Campione()
    {
        var m = new SystemMetrics();
        m.Apply(new SystemSnapshot
        {
            Cpu = new CpuSnapshot
            {
                Name = "Processore",
                TjMaxC = 95,
                PackageC = 64,
                TotalLoadPercent = 38,
                Cores = [new CoreSample("0", 64, 38, 4200)],
            },
            Gpus =
            [
                new GpuReading
                {
                    Name = "Scheda video",
                    CoreTemperatureC = 57,
                    CoreLoadPercent = 72,
                    MemoryUsedMb = 6200,
                    MemoryTotalMb = 8144,
                }
            ],
            MemoryTotalBytes = 32UL * 1024 * 1024 * 1024,
            MemoryUsedBytes = 19UL * 1024 * 1024 * 1024,
        });
        return m;
    }

    // --------------------------------------------------------------- impalcatura

    private const int RowHeight = 32;

    private CardPanel Card(string title, int rows, int fixedHeight = 0)
    {
        var c = new CardPanel(title)
        {
            Width = 708,
            Height = fixedHeight > 0 ? fixedHeight : 44 + rows * RowHeight + 12,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(16, 44, 16, 12),
        };
        _themed.Add(c);
        return c;
    }

    private TableLayoutPanel Rows(CardPanel host, int rows)
    {
        var t = new BufferedTable
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            BackColor = Color.Transparent,
            Tag = "transparent",
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        host.Controls.Add(t);
        _themed.Add(t);
        return t;
    }

    private void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        var lbl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            Tag = "key",
        };
        table.Controls.Add(lbl, 0, row);
        _themed.Add(lbl);

        control.Anchor = AnchorStyles.Left;
        table.Controls.Add(control, 1, row);
        _themed.Add(control);
    }

    private Control Wrap(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 1, 0, 0),
            BackColor = Color.Transparent,
            Tag = "transparent",
        };
        p.Controls.AddRange(controls);
        _themed.Add(p);
        foreach (var c in controls) _themed.Add(c);
        return p;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(12, 8, 0, 0),
        BackColor = Color.Transparent,
        Tag = "hint",
    };

    private Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Width = 104,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
            Tag = primary ? "primary" : "secondary",
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        _themed.Add(b);
        return b;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyThemeColors();

    private void ApplyThemeColors()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;

        foreach (var c in _themed)
        {
            switch (c)
            {
                case Button b when (string?)b.Tag == "primary":
                    b.BackColor = Theme.Accent;
                    b.ForeColor = Theme.IsDark ? Theme.FromHex("#0B1A25") : Color.White;
                    b.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.Accent, Color.White, 0.12f);
                    break;

                case Button b:
                    b.BackColor = Theme.Surface;
                    b.ForeColor = Theme.TextPrimary;
                    b.FlatAppearance.BorderColor = Theme.Border;
                    b.FlatAppearance.MouseOverBackColor = Theme.SurfaceHover;
                    break;

                case Label l when (string?)l.Tag == "hint":
                    l.ForeColor = Theme.TextMuted;
                    break;

                case Label l:
                    l.ForeColor = Theme.TextSecondary;
                    break;

                case CardPanel card:
                    card.BackColor = Theme.Surface;
                    break;

                case Control ct when (string?)ct.Tag == "transparent":
                    ct.BackColor = Color.Transparent;
                    break;

                case Control ct when (string?)ct.Tag == "bar":
                    ct.BackColor = Theme.Background;
                    break;

                default:
                    c.Invalidate();
                    break;
            }
        }

        Invalidate(true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnFormClosing(e);
    }
}
