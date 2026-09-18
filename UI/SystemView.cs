using System.Drawing.Drawing2D;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Scheda "Sistema": temperature e carico del processore, scheda video, identità
/// dell'hardware, memoria installata, scheda madre e BIOS.
/// <para>
/// La griglia dei core prende tutta la larghezza, perché è quella che ha bisogno di
/// spazio: con ventiquattro core si dispone su quattro colonne invece che su due, e
/// occupa la metà delle righe. Le altre quattro schede stanno a coppie sotto, e ognuna è
/// alta quanto basta al proprio contenuto: a schermo intero ci sta tutto senza scorrere.
/// </para>
/// </summary>
public sealed class SystemView : Panel
{
    private readonly AppSettings _settings;
    private readonly CpuInfo _cpu;
    private readonly SystemInfo _system;

    private readonly BufferedTable _layout = new();
    private readonly ThemedScrollBar _scroll = new();
    private readonly CardPanel _coresCard = new("Processore");
    private readonly CoreGrid _cores;
    private readonly CardPanel _cpuCard = new("Identità del processore");
    private readonly BufferedPanel _cpuRows = new();
    private readonly CardPanel _gpuCard = new("Scheda video");
    private readonly BufferedTable _gpuHost = new();
    private readonly BufferedPanel _gpuLeft = new();
    private readonly BufferedPanel _gpuRight = new();
    private readonly CardPanel _memoryCard = new("Memoria");
    private readonly BufferedPanel _memoryRows = new();
    private readonly CardPanel _boardCard = new("Scheda madre e BIOS");
    private readonly BufferedPanel _boardRows = new();

    private readonly Dictionary<string, DetailRow> _gpuLive = [];
    private readonly ToolTip _tips = new();
    private string _gpuShape = "";
    private int _gpuRowCount;

    private const int DetailRowHeight = 24;
    private const int MinimumCardHeight = 132;

    public SystemView(AppSettings settings)
    {
        _settings = settings;
        _cpu = CpuInfo.Read();
        _system = SystemInfo.Read();
        _cores = new CoreGrid(settings);

        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(0);

        // Scorrimento gestito a mano, con la barra disegnata dal tema: quella di sistema
        // in modalità chiara è quasi bianca e non si lascia colorare.
        AutoScroll = false;
        _scroll.Dock = DockStyle.Right;
        _scroll.Width = 12;
        _scroll.Visible = false;
        _scroll.ValueChanged += (_, _) => _layout.Top = -_scroll.Value;
        Controls.Add(_scroll);

        _layout.Margin = new Padding(0);
        _layout.Location = Point.Empty;
        _layout.ColumnCount = 2;
        _layout.RowCount = 3;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++) _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, MinimumCardHeight));

        // --- processore: tutta la larghezza ---
        _coresCard.Dock = DockStyle.Fill;
        _coresCard.Margin = new Padding(0, 0, 0, 10);
        _coresCard.Padding = new Padding(14, 44, 14, 8);
        _cores.Dock = DockStyle.Fill;
        _coresCard.Controls.Add(_cores);
        _coresCard.ActionClicked += (_, _) => AvviaCoreTemp();

        Prepare(_cpuCard, _cpuRows, new Padding(0, 0, 6, 12));

        // --- scheda video: valori corti, quindi su due colonne ---
        _gpuCard.Dock = DockStyle.Fill;
        _gpuCard.Margin = new Padding(6, 0, 0, 12);
        _gpuCard.Padding = new Padding(14, 44, 10, 8);
        _gpuHost.Dock = DockStyle.Fill;
        _gpuHost.ColumnCount = 2;
        _gpuHost.RowCount = 1;
        _gpuHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _gpuHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // Senza una riga in percentuale le colonne, che contengono solo controlli in
        // Dock=Top, collassano a zero.
        _gpuHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _gpuLeft.Dock = DockStyle.Fill;
        _gpuRight.Dock = DockStyle.Fill;
        _gpuRight.Margin = new Padding(12, 0, 0, 0);
        _gpuHost.Controls.Add(_gpuLeft, 0, 0);
        _gpuHost.Controls.Add(_gpuRight, 1, 0);
        _gpuCard.Controls.Add(_gpuHost);

        Prepare(_memoryCard, _memoryRows, new Padding(0, 0, 6, 0));
        Prepare(_boardCard, _boardRows, new Padding(6, 0, 0, 0));

        _layout.Controls.Add(_coresCard, 0, 0);
        _layout.SetColumnSpan(_coresCard, 2);
        _layout.Controls.Add(_cpuCard, 0, 1);
        _layout.Controls.Add(_gpuCard, 1, 1);
        _layout.Controls.Add(_memoryCard, 0, 2);
        _layout.Controls.Add(_boardCard, 1, 2);

        Controls.Add(_layout);

        BuildStaticRows();
        ApplyRowHeights();

        Resize += (_, _) => AdattaScorrimento();
        MouseWheel += (_, e) =>
        {
            if (_scroll.Needed) _scroll.Value -= Math.Sign(e.Delta) * 60;
        };
    }

    private static void Prepare(CardPanel card, Control host, Padding margin)
    {
        card.Dock = DockStyle.Fill;
        card.Margin = margin;
        card.Padding = new Padding(14, 44, 10, 8);
        host.Dock = DockStyle.Fill;
        card.Controls.Add(host);
    }

    // ------------------------------------------------------------ contenuto

    private void BuildStaticRows()
    {
        AddRows(_cpuRows,
        [
            ("Modello", _cpu.BrandString, false),
            ("Microarchitettura", Or(_cpu.Microarchitecture, "—"), false),
            ("Produttore", _cpu.Vendor, false),
            ("Famiglia / modello / stepping", $"{_cpu.Family} / {_cpu.Model} / {_cpu.Stepping}", false),
            ("Core fisici", _cpu.PhysicalCores.ToString(), false),
            ("Thread", $"{_cpu.LogicalCores}{(_cpu.HyperThreading ? "  (multi-thread attivo)" : "")}", false),
            ("Frequenza di base", _cpu.BaseClockMhz > 0 ? $"{_cpu.BaseClockMhz} MHz" : "—", false),
            .. _cpu.Caches.OrderBy(c => c.Level).ThenBy(c => c.Type)
                  .Select(c => ($"Cache L{c.Level} {c.Type}", c.Display, false)),
            ("Istruzioni", string.Join(", ", _cpu.Features), true),
        ]);

        var memoryRows = new List<(string, string, bool)>
        {
            ("Installata", SystemInfo.FormatBytes(_system.TotalMemoryBytes), false),
            ("In uso", "—", false),
            ("Disponibile", "—", false),
        };

        foreach (var m in _system.Modules)
        {
            string descrizione = $"{SystemInfo.FormatBytes(m.SizeBytes)} {m.Type}".Trim();
            if (m.SpeedMhz > 0) descrizione += $" · {m.SpeedMhz} MHz";
            if (m.Manufacturer.Length > 0) descrizione += $" · {m.Manufacturer}";
            if (m.PartNumber.Length > 0) descrizione += $" {m.PartNumber}";
            memoryRows.Add((Or(m.Slot, "Banco"), descrizione, true));
        }

        AddRows(_memoryRows, memoryRows);

        AddRows(_boardRows,
        [
            ("Produttore", Or(_system.BoardManufacturer, "—"), false),
            ("Modello", Or(_system.BoardProduct, "—"), false),
            ("BIOS", $"{_system.BiosVendor} {_system.BiosVersion}".Trim(), true),
            ("Data BIOS", Or(_system.BiosDate, "—"), false),
            ("Sistema", Describe(_system.SystemManufacturer, _system.SystemProduct), true),
            ("Windows", Environment.OSVersion.VersionString, true),
            ("Nome computer", Environment.MachineName, false),
        ]);
    }

    /// <summary>I firmware OEM lasciano spesso segnaposto al posto dei nomi veri.</summary>
    private static string Describe(string manufacturer, string product)
    {
        string joined = $"{manufacturer} {product}".Trim();
        return joined.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || joined.Length == 0
            ? "assemblato (il firmware non riporta marca e modello)"
            : joined;
    }

    private static string Or(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private DetailRow NewRow(string key, string value, bool wide, int keyWidth)
    {
        var row = new DetailRow(key, value, copyable: wide)
        {
            Dock = DockStyle.Top,
            Height = DetailRowHeight,
            KeyWidth = keyWidth,
        };
        _tips.SetToolTip(row, $"{key}: {value}");
        return row;
    }

    /// <summary>Con Dock=Top l'ordine di inserimento è invertito rispetto a quello di lettura.</summary>
    private void AddRows(Control host, IEnumerable<(string Key, string Value, bool Wide)> rows, int keyWidth = 168)
    {
        foreach (var (key, value, wide) in rows.Reverse())
            host.Controls.Add(NewRow(key, value, wide, keyWidth));
    }

    // ----------------------------------------------------------- aggiornamento

    public void Update(SystemSnapshot snapshot)
    {
        var cpu = snapshot.Cpu;
        _cores.Update(cpu, snapshot.CpuUnavailableReason);

        _coresCard.Hint = cpu is null
            ? ""
            : string.Join("  ·  ", new[]
            {
                cpu.ClockMhz is float mhz && mhz > 0 ? $"{mhz:0} MHz" : "",
                cpu.Multiplier is float mult && mult > 0 ? $"{mult:0.0}×" : "",
                cpu.PowerWatt is float w ? $"{w:0} W" : "",
                cpu.Source,
            }.Where(s => s.Length > 0));

        // Quando manca la lettura ma Core Temp c'è, l'intestazione offre di avviarlo:
        // è l'unico passo che separa l'utente dai valori.
        string? azione = cpu is null && CoreTempReader.InstalledPath is not null
            ? "Avvia Core Temp"
            : null;

        if (_coresCard.ActionText != azione)
        {
            _coresCard.ActionText = azione;
            _coresCard.Invalidate();
        }

        UpdateGpu(snapshot);
        ApplyRowHeights(cpu);

        // La memoria cambia di continuo: si aggiorna qui
        var fresh = SystemInfo.Read();
        foreach (Control c in _memoryRows.Controls)
        {
            if (c is not DetailRow row) continue;
            if (row.Key == "In uso")
                row.Value = $"{SystemInfo.FormatBytes(fresh.UsedMemoryBytes)}  " +
                            $"({(double)fresh.UsedMemoryBytes / Math.Max(1, fresh.TotalMemoryBytes):P0})";
            else if (row.Key == "Disponibile")
                row.Value = SystemInfo.FormatBytes(fresh.AvailableMemoryBytes);
        }
    }

    private void AvviaCoreTemp()
    {
        if (CoreTempReader.Launch()) return;

        MessageBox.Show(this,
            "Non è stato possibile avviare Core Temp.\nProva ad aprirlo a mano dal menu Start.",
            "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // --------------------------------------------------------------- altezze

    /// <summary>
    /// Ogni riga di schede è alta quanto serve al suo contenuto: le righe in eccesso
    /// finirebbero altrimenti tagliate sotto il bordo del riquadro, come succedeva al
    /// nome del computer in fondo alla scheda madre.
    /// </summary>
    private void ApplyRowHeights(CpuSnapshot? cpu = null)
    {
        int larghezzaCore = Math.Max(240, _coresCard.ClientSize.Width - _coresCard.Padding.Horizontal);
        if (_coresCard.ClientSize.Width <= 1) larghezzaCore = Math.Max(240, ClientSize.Width - 40);

        float[] altezze =
        [
            _cores.PreferredHeight(larghezzaCore, cpu?.Cores.Count ?? 0)
                + _coresCard.Padding.Vertical + _coresCard.Margin.Vertical,
            Math.Max(CardHeight(_cpuCard, _cpuRows.Controls.Count),
                     CardHeight(_gpuCard, (int)Math.Ceiling(_gpuRowCount / 2.0))),
            Math.Max(CardHeight(_memoryCard, _memoryRows.Controls.Count),
                     CardHeight(_boardCard, _boardRows.Controls.Count)),
        ];

        bool cambiato = false;
        for (int riga = 0; riga < altezze.Length; riga++)
        {
            float voluta = Math.Max(MinimumCardHeight, altezze[riga]);
            if (Math.Abs(_layout.RowStyles[riga].Height - voluta) < 0.5f) continue;

            _layout.RowStyles[riga].Height = voluta;
            cambiato = true;
        }

        if (!cambiato) return;

        _layout.Height = (int)(_layout.RowStyles[0].Height
                             + _layout.RowStyles[1].Height
                             + _layout.RowStyles[2].Height);

        AdattaScorrimento();

        // La barra di scorrimento la crea il contenitore solo adesso, quando il
        // contenuto supera l'altezza visibile: il tema scuro va riapplicato qui,
        // altrimenti resta quella chiara di sistema.
        Theme.ApplyNativeTheme(this);
    }

    /// <summary>
    /// Larghezza della tabella e corsa della barra: la tabella non è agganciata ai bordi,
    /// perché deve poter scorrere sotto il bordo superiore del contenitore.
    /// </summary>
    private void AdattaScorrimento()
    {
        int eccedenza = Math.Max(0, _layout.Height - ClientSize.Height);

        _scroll.LargeChange = Math.Max(1, ClientSize.Height);
        _scroll.Maximum = eccedenza;
        if (_scroll.Visible != _scroll.Needed) _scroll.Visible = _scroll.Needed;

        int larghezza = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
        if (larghezza > 0 && _layout.Width != larghezza) _layout.Width = larghezza;

        // Rimpicciolendo la finestra la posizione corrente può finire oltre il massimo.
        _layout.Top = -_scroll.Value;
    }

    private static int CardHeight(CardPanel card, int rowCount) =>
        rowCount * DetailRowHeight + card.Padding.Vertical + card.Margin.Vertical;

    // ------------------------------------------------------------ scheda video

    private void UpdateGpu(SystemSnapshot snapshot)
    {
        var gpus = snapshot.Gpus;
        var adapters = Adapters;

        // Le righe si ricostruiscono quando cambia l'insieme delle schede, o quando
        // compare un sensore che prima non c'era: sono le righe stesse a cambiare.
        string shape = string.Join("|", adapters.Select(a => a.Name)
            .Concat(gpus.Select(g => $"{g.Name}:{SensorSignature(g)}")));

        if (shape != _gpuShape)
        {
            _gpuShape = shape;
            RebuildGpuRows(adapters, gpus, snapshot.GpuUnavailableReason);
        }

        var primary = gpus.FirstOrDefault(g => g.TemperatureC is not null) ?? gpus.FirstOrDefault();
        if (primary is null) return;

        int? temp = primary.TemperatureC is float t ? (int)Math.Round(t) : null;
        SetLive("Temperatura", _settings.FormatTemp(temp),
            Theme.OnSurface(IconRenderer.ColorForGpu(temp, _settings)));
        SetLive("Punto più caldo", primary.HotSpotTemperatureC is float hs
            ? _settings.FormatTemp((int)Math.Round(hs)) : "—");
        SetLive("Temp. memoria", primary.MemoryTemperatureC is float mt
            ? _settings.FormatTemp((int)Math.Round(mt)) : "—");
        SetLive("Carico", primary.CoreLoadPercent is float l ? $"{l:0}%" : "—");
        SetLive("Freq. core", primary.CoreClockMhz is float c ? $"{c:0} MHz" : "—");
        SetLive("Freq. memoria", primary.MemoryClockMhz is float mc ? $"{mc:0} MHz" : "—");
        SetLive("Memoria in uso", primary.MemoryUsedMb is float used
            ? primary.MemoryTotalMb is float total && total > 0
                ? $"{used / 1024:0.0} di {total / 1024:0.0} GB  ·  {used / total * 100:0}%"
                : $"{used / 1024:0.0} GB"
            : primary.VramUsedPercent is float solaPercentuale ? $"{solaPercentuale:0}%" : "—",
            Theme.OnSurface(IconRenderer.ColorForLoad(primary.VramUsedPercent, _settings)));
        SetLive("Potenza", primary.PowerWatt is float w ? $"{w:0} W" : "—");
        SetLive("Ventola", primary.FanRpm is float rpm && rpm > 0
            ? $"{rpm:0} giri/min"
            : primary.FanPercent is float only ? $"{only:0}%" : "—");

        _gpuCard.Hint = temp is int shown ? _settings.FormatTemp(shown) : "";
    }

    private void SetLive(string key, string value, Color? color = null)
    {
        if (!_gpuLive.TryGetValue(key, out var row)) return;
        if (row.Value != value) row.Value = value;
        if (row.ValueColor != color) { row.ValueColor = color; row.Invalidate(); }
    }

    /// <summary>Le schede trovate nel registro: nome, driver e memoria dedicata.</summary>
    public IReadOnlyList<GpuAdapter> Adapters { get; set; } = [];

    /// <summary>Le misure che questa scheda video espone, nell'ordine in cui si mostrano.</summary>
    private static List<string> LiveKeys(GpuReading? gpu)
    {
        var keys = new List<string>();
        if (gpu is null) return keys;

        void Add(string key, object? value) { if (value is not null) keys.Add(key); }

        Add("Temperatura", gpu.TemperatureC);
        Add("Punto più caldo", gpu.HotSpotTemperatureC);
        Add("Temp. memoria", gpu.MemoryTemperatureC);
        Add("Carico", gpu.CoreLoadPercent);
        Add("Freq. core", gpu.CoreClockMhz);
        Add("Freq. memoria", gpu.MemoryClockMhz);
        Add("Memoria in uso", gpu.MemoryUsedMb ?? gpu.VramUsedPercent);
        Add("Potenza", gpu.PowerWatt);
        Add("Ventola", gpu.FanRpm ?? gpu.FanPercent);

        return keys;
    }

    /// <summary>Quali sensori risultano presenti: serve a capire se le righe vanno rifatte.</summary>
    private static string SensorSignature(GpuReading gpu) => string.Join(",", LiveKeys(gpu));

    private void RebuildGpuRows(IReadOnlyList<GpuAdapter> adapters, IReadOnlyList<GpuReading> gpus,
                                string? unavailable)
    {
        foreach (var host in new[] { _gpuLeft, _gpuRight })
        {
            foreach (Control c in host.Controls.Cast<Control>().ToList()) c.Dispose();
            host.Controls.Clear();
        }
        _gpuLive.Clear();

        var rows = new List<(string Key, string Value, bool Wide)>();

        var primaryAdapter = adapters.FirstOrDefault(a =>
            gpus.Any(g => g.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase) ||
                          a.Name.Contains(g.Name, StringComparison.OrdinalIgnoreCase)))
            ?? adapters.FirstOrDefault();

        var primary = gpus.FirstOrDefault(g => g.TemperatureC is not null) ?? gpus.FirstOrDefault();

        rows.Add(("Modello", primary?.Name ?? primaryAdapter?.Name ?? "—", true));
        rows.Add(("Produttore", Or(primary?.Vendor ?? primaryAdapter?.Vendor ?? "", "—"), false));
        if (primaryAdapter is not null)
        {
            rows.Add(("Memoria", primaryAdapter.MemoryDisplay, false));
            rows.Add(("Driver", Or(primaryAdapter.DriverVersion, "—"), false));
        }

        // Solo le misure che questa scheda espone davvero: righe sempre a "—" farebbero
        // sembrare guasto ciò che semplicemente non esiste.
        var live = LiveKeys(primary);
        foreach (string key in live) rows.Add((key, "—", false));

        foreach (var other in adapters.Where(a => a != primaryAdapter))
            rows.Add(("Altra scheda", $"{other.Name}  ·  {other.MemoryDisplay}", true));

        if (primary is null && unavailable is not null) rows.Add(("Nota", unavailable, true));

        _gpuRowCount = rows.Count;

        // Metà a sinistra e metà a destra, con le chiavi strette: i valori sono corti.
        int metà = (rows.Count + 1) / 2;
        Riempi(_gpuLeft, rows.Take(metà), live);
        Riempi(_gpuRight, rows.Skip(metà), live);

        ApplyRowHeights();

        void Riempi(Control host, IEnumerable<(string Key, string Value, bool Wide)> items, List<string> vive)
        {
            foreach (var (key, value, wide) in Enumerable.Reverse(items.ToList()))
            {
                var row = NewRow(key, value, wide, keyWidth: 108);
                host.Controls.Add(row);
                if (vive.Contains(key)) _gpuLive[key] = row;
            }
        }
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Background;
        _layout.BackColor = Theme.Background;
        foreach (var card in new[] { _coresCard, _cpuCard, _gpuCard, _memoryCard, _boardCard })
            card.BackColor = Theme.Surface;
        foreach (var host in new Control[] { _cpuRows, _gpuHost, _gpuLeft, _gpuRight, _memoryRows, _boardRows, _cores })
            host.BackColor = Theme.Surface;

        // Il binario della barra prende il colore della pagina: così sparisce.
        _scroll.BackColor = Theme.Background;

        Theme.ApplyNativeTheme(this);
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Griglia dei core: una barra per core con temperatura e carico, disposta su più
/// colonne quando il processore ne ha tanti.
/// </summary>
public sealed class CoreGrid : Control
{
    private const int RowHeight = 24;
    private const int MinColumnWidth = 196;
    private const int SummaryHeight = 56;
    private const int ColumnGap = 18;
    private const int MaxColumns = 4;

    private readonly AppSettings _settings;
    private CpuSnapshot? _reading;
    private string? _unavailable;

    public CoreGrid(AppSettings settings)
    {
        _settings = settings;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Update(CpuSnapshot? reading, string? unavailable)
    {
        _reading = reading;
        _unavailable = unavailable;
        Invalidate();
    }

    /// <summary>Altezza che servirebbe per mostrare tutti i core alla larghezza data.</summary>
    public int PreferredHeight(int width, int coreCount)
    {
        if (coreCount == 0) return SummaryHeight + RowHeight * 2;
        int columns = ColumnsFor(width);
        int rows = (int)Math.Ceiling(coreCount / (double)columns);
        return SummaryHeight + rows * RowHeight + 2;
    }

    private static int ColumnsFor(int width) =>
        Math.Clamp((width + ColumnGap) / (MinColumnWidth + ColumnGap), 1, MaxColumns);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        if (_reading is null)
        {
            using var mb = new SolidBrush(Theme.TextMuted);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_unavailable ?? "Temperature del processore non disponibili",
                Theme.Body, mb, new RectangleF(10, 0, Width - 20, Height), fmt);
            return;
        }

        var r = _reading;
        DrawSummary(g, r);

        if (r.Cores.Count == 0) return;

        int columns = ColumnsFor(Width);
        int rows = (int)Math.Ceiling(r.Cores.Count / (double)columns);
        float columnWidth = (Width - (columns - 1) * ColumnGap) / (float)columns;

        for (int i = 0; i < r.Cores.Count; i++)
        {
            int column = i / rows;                      // si riempie una colonna alla volta
            int row = i % rows;
            float x = column * (columnWidth + ColumnGap);
            float y = SummaryHeight + row * RowHeight;
            DrawCore(g, r.Cores[i], new RectangleF(x, y, columnWidth, RowHeight));
        }
    }

    private void DrawSummary(Graphics g, CpuSnapshot r)
    {
        if (r.HottestC is not float hottest)
        {
            // Carichi e frequenze si leggono senza driver, le temperature no: meglio
            // dirlo qui che lasciare una fascia vuota senza spiegazione.
            using var nb = new SolidBrush(Theme.TextMuted);
            g.DrawString("Temperature non disponibili: carichi e frequenze si leggono comunque.",
                         Theme.Body, nb, 2, 10);
            return;
        }

        var color = TempColor(hottest);
        using (var f = new Font(Theme.Display.FontFamily, 25f, FontStyle.Bold, GraphicsUnit.Point))
        using (var b = new SolidBrush(color))
            g.DrawString($"{Math.Round(_settings.ToDisplay((int)Math.Round(hottest))):0}{_settings.UnitSuffix}",
                         f, b, 0, 0);

        using var mb = new SolidBrush(Theme.TextMuted);
        string dettaglio = r.Cores.Count > 0 ? $"più caldo dei {r.Cores.Count} core" : "temperatura del package";
        if (r.MarginC is float margin) dettaglio += $"  ·  {margin:0} °C dal limite di {r.TjMaxC} °C";
        if (r.AverageC is float avg) dettaglio += $"  ·  media {avg:0.0} °C";
        if (r.AverageLoadPercent is float load) dettaglio += $"  ·  carico {load:0}%";
        g.DrawString(Theme.Ellipsize(g, dettaglio, Theme.Small, Width - 4), Theme.Small, mb, 2, 36);
    }

    private void DrawCore(Graphics g, CoreSample core, RectangleF cell)
    {
        int? temp = core.TemperatureC is float t ? (int)Math.Round(t) : null;
        var color = temp is int c ? TempColor(c) : Theme.Neutral;

        float labelWidth = 78;
        float valueWidth = 42;
        float loadWidth = 34;
        float barLeft = cell.X + labelWidth;
        float barWidth = Math.Max(18, cell.Width - labelWidth - valueWidth - loadWidth);
        float barY = cell.Y + cell.Height / 2 - 4;

        using (var b = new SolidBrush(Theme.TextSecondary))
            g.DrawString(Theme.Ellipsize(g, core.Label, Theme.Small, labelWidth - 4), Theme.Small, b,
                         cell.X + 2, cell.Y + cell.Height / 2 - 8);

        // La barra va da 20 °C alla soglia critica impostata: così il riempimento e il
        // colore raccontano la stessa cosa.
        float limite = Math.Max(_settings.CpuCriticalTemp, 40);
        float fraction = temp is int value
            ? Math.Clamp((value - 20f) / (limite - 20f), 0.02f, 1f)
            : 0f;

        using (var track = Theme.RoundedRect(new RectangleF(barLeft, barY, barWidth, 8), 4))
        using (var tb = new SolidBrush(Theme.Track))
            g.FillPath(tb, track);

        if (fraction > 0)
        {
            using var fill = Theme.RoundedRect(new RectangleF(barLeft, barY, barWidth * fraction, 8), 4);
            using var fb = new SolidBrush(color);
            g.FillPath(fb, fill);
        }

        // Tacca sottile del carico, sopra la barra
        if (core.LoadPercent is float load)
        {
            float w = barWidth * Math.Clamp(load / 100f, 0f, 1f);
            using var lb = new SolidBrush(Theme.Alpha(Theme.TextMuted, 130));
            g.FillRectangle(lb, barLeft, barY - 6, w, 3);
        }

        using (var b = new SolidBrush(color))
        using (var f = new Font(Theme.Body.FontFamily, 10f, FontStyle.Bold, GraphicsUnit.Point))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Far })
            g.DrawString(temp is int shown
                    ? $"{Math.Round(_settings.ToDisplay(shown)):0}{_settings.UnitSuffix}"
                    : "—",
                f, b, new RectangleF(barLeft + barWidth, cell.Y + cell.Height / 2 - 10, valueWidth - 4, cell.Height), fmt);

        using (var b = new SolidBrush(Theme.TextMuted))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Far })
            g.DrawString(core.LoadPercent is float l ? $"{l:0}%" : "",
                Theme.Micro, b,
                new RectangleF(cell.Right - loadWidth, cell.Y + cell.Height / 2 - 7, loadWidth - 2, cell.Height), fmt);
    }

    /// <summary>
    /// Colore secondo le soglie impostate per il processore, le stesse che valgono per
    /// l'icona in area di notifica: un valore che lì è rosso deve essere rosso anche qui.
    /// </summary>
    private Color TempColor(float temp) =>
        Theme.OnSurface(IconRenderer.ColorForCpu((int)Math.Round(temp), _settings));
}
