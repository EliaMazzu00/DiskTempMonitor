using System.Drawing.Drawing2D;
using DiskTempMonitor.Models;
using DiskTempMonitor.Native;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>Esito di una diagnosi appena conclusa, da segnalare a chi ospita la pagina.</summary>
public sealed record SelfTestFinished(DiskInfo Disk, SelfTestRun Run);

/// <summary>
/// Scheda "Autotest": una riga per disco con lo stato dell'autodiagnosi e i comandi, più
/// l'elenco delle esecuzioni registrate.
/// <para>
/// Quello che si può davvero fare lo decide il dispositivo, non l'applicazione: si può
/// <b>avviare</b> una verifica breve o estesa e <b>interromperla</b>. Metterla in pausa
/// non esiste — né negli NVMe né negli ATA il comando è previsto — e nemmeno cancellare
/// il registro interno del disco, che tiene solo l'esito dell'ultima. Cancellare qui
/// significa perciò svuotare la cronologia tenuta dall'applicazione, ed è scritto così.
/// </para>
/// </summary>
public sealed class SelfTestView : Panel
{
    private const int RowHeight = 76;
    private const int DisksCardChrome = 54;

    private readonly AppSettings _settings;
    private readonly SelfTestHistory _history;

    private readonly BufferedTable _layout = new();
    private readonly CardPanel _disksCard = new("Autodiagnosi dei dischi");
    private readonly BufferedPanel _rows = new();
    private readonly CardPanel _historyCard = new("Esecuzioni registrate");
    private readonly BufferedGrid _grid = new();
    private readonly ThemedScrollBar _gridScroll = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 4000 };
    private readonly ToolTip _tips = new();

    private List<DiskInfo> _disks = [];
    private bool _querying;
    private bool _syncingScroll;

    /// <summary>Una diagnosi è finita: serve a chi manda le notifiche.</summary>
    public event EventHandler<SelfTestFinished>? Finished;

    /// <summary>Messaggio da mostrare nella barra di stato della finestra.</summary>
    public event EventHandler<string>? Announced;

    public SelfTestView(AppSettings settings, SelfTestHistory history)
    {
        _settings = settings;
        _history = history;

        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        Margin = new Padding(0);

        _layout.Dock = DockStyle.Fill;
        _layout.ColumnCount = 1;
        _layout.RowCount = 2;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DisksCardChrome + RowHeight));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _disksCard.Dock = DockStyle.Fill;
        _disksCard.Margin = new Padding(0, 0, 0, 12);
        _disksCard.Padding = new Padding(1, 44, 1, 8);
        _rows.Dock = DockStyle.Fill;
        _disksCard.Controls.Add(_rows);

        _historyCard.Dock = DockStyle.Fill;
        _historyCard.Margin = new Padding(0);
        _historyCard.Padding = new Padding(1, 42, 1, 1);
        _historyCard.ActionText = "Svuota";
        _historyCard.ActionClicked += (_, _) => ClearHistory();
        BuildGrid();
        _historyCard.Controls.Add(_grid);
        _historyCard.Controls.Add(_gridScroll);

        _layout.Controls.Add(_disksCard, 0, 0);
        _layout.Controls.Add(_historyCard, 0, 1);
        Controls.Add(_layout);

        _poll.Tick += (_, _) => _ = QueryAsync(onlyRunning: true);
    }

    // ------------------------------------------------------------------ dati

    /// <summary>Aggiorna l'elenco dei dischi. Le righe si ricostruiscono solo se cambiano.</summary>
    public void Update(IReadOnlyList<DiskInfo> disks)
    {
        _disks = disks.ToList();

        string forma = string.Join("|", _disks.Select(d => $"{d.Index}:{d.SerialNumber}"));
        if (forma != _shape)
        {
            _shape = forma;
            RebuildRows();
        }
        else
        {
            foreach (var riga in _rows.Controls.OfType<SelfTestRow>())
            {
                var disco = _disks.FirstOrDefault(d => d.Index == riga.Disk.Index);
                if (disco is not null) riga.Bind(disco, _history.Active(SelfTestHistory.KeyFor(disco)));
            }
        }

        FillHistory();
        EnsurePolling();
    }

    private string _shape = "";

    /// <summary>La pagina è appena passata in primo piano: si legge lo stato dai dischi.</summary>
    public void Activate()
    {
        _ = QueryAsync(onlyRunning: false);
        FillHistory();
    }

    private void RebuildRows()
    {
        foreach (Control c in _rows.Controls.Cast<Control>().ToList()) c.Dispose();
        _rows.Controls.Clear();

        // Con Dock=Top l'ordine di inserimento è invertito rispetto a quello di lettura.
        foreach (var disk in Enumerable.Reverse(_disks))
        {
            var riga = new SelfTestRow(disk, _settings) { Dock = DockStyle.Top, Height = RowHeight };
            riga.StartRequested += (_, kind) => Start(riga, kind);
            riga.AbortRequested += (_, _) => Abort(riga);
            riga.Bind(disk, _history.Active(SelfTestHistory.KeyFor(disk)));
            _rows.Controls.Add(riga);
            _tips.SetToolTip(riga, "Avvia o interrompi la verifica interna del disco");
        }

        float altezza = DisksCardChrome + Math.Max(1, _disks.Count) * RowHeight;
        if (Math.Abs(_layout.RowStyles[0].Height - altezza) > 0.5f)
            _layout.RowStyles[0].Height = altezza;
    }

    // --------------------------------------------------------------- comandi

    private void Start(SelfTestRow riga, SelfTestKind kind)
    {
        var disk = riga.Disk;
        string durata = kind == SelfTestKind.Short
            ? "Dura in genere pochi minuti."
            : "Può durare diverse ore su un disco meccanico.";

        var risposta = MessageBox.Show(this,
            $"Avviare l'autodiagnosi su {disk.DisplayName}?\n\n{durata}\n\n" +
            "Il disco esegue una verifica interna: i dati non vengono toccati e puoi " +
            "continuare a usare il computer, ma il disco sarà un po' più lento finché non " +
            "ha finito. Si può interrompere in qualsiasi momento.",
            "Autodiagnosi del disco", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (risposta != DialogResult.OK) return;

        if (!SelfTest.Start(disk, kind, out string errore))
        {
            MessageBox.Show(this, errore, "Autodiagnosi non avviata",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var run = _history.Begin(disk, kind);
        riga.Bind(disk, run);
        FillHistory();
        EnsurePolling();

        Announced?.Invoke(this, $"Autodiagnosi {run.KindDisplay.ToLowerInvariant()} avviata su {disk.DisplayName}.");
        _ = QueryAsync(onlyRunning: true);
    }

    private void Abort(SelfTestRow riga)
    {
        var disk = riga.Disk;

        if (!SelfTest.Abort(disk, out string errore))
        {
            MessageBox.Show(this, errore, "Interruzione non riuscita",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_history.Active(SelfTestHistory.KeyFor(disk)) is { } run)
            _history.Complete(run, "Interrotta dall'utente", passed: null, aborted: true);

        riga.Bind(disk, null);
        FillHistory();
        Announced?.Invoke(this, $"Autodiagnosi interrotta su {disk.DisplayName}.");
        _ = QueryAsync(onlyRunning: false);
    }

    private void ClearHistory()
    {
        if (_history.Runs.Count == 0) return;

        var risposta = MessageBox.Show(this,
            "Svuotare l'elenco delle esecuzioni?\n\n" +
            "Sparisce solo la cronologia tenuta da questa applicazione: il registro interno " +
            "dei dischi non è cancellabile, e continuerà a riportare l'esito dell'ultima verifica.",
            "Cronologia autodiagnosi", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (risposta != DialogResult.OK) return;

        _history.Clear();
        FillHistory();
        Announced?.Invoke(this, "Cronologia delle autodiagnosi svuotata.");
    }

    // ----------------------------------------------------------- interrogazione

    private void EnsurePolling()
    {
        bool serve = _disks.Any(d => _history.Active(SelfTestHistory.KeyFor(d)) is not null);
        if (serve && !_poll.Enabled) _poll.Start();
        else if (!serve && _poll.Enabled) _poll.Stop();
    }

    /// <summary>
    /// Chiede lo stato ai dischi. È una lettura S.M.A.R.T. vera e propria, che su un box
    /// USB può richiedere un attimo: va fuori dal thread dell'interfaccia.
    /// </summary>
    private async Task QueryAsync(bool onlyRunning)
    {
        if (_querying || _disks.Count == 0) return;
        _querying = true;

        try
        {
            var bersagli = _disks
                .Where(d => !onlyRunning || _history.Active(SelfTestHistory.KeyFor(d)) is not null)
                .ToList();

            if (bersagli.Count == 0) return;

            var esiti = await Task.Run(() => bersagli
                .Select(d => (Disk: d, Status: SelfTest.Query(d)))
                .ToList());

            if (IsDisposed) return;

            foreach (var (disk, status) in esiti) Apply(disk, status);

            FillHistory();
            EnsurePolling();
        }
        finally { _querying = false; }
    }

    private void Apply(DiskInfo disk, SelfTestStatus? status)
    {
        var riga = _rows.Controls.OfType<SelfTestRow>().FirstOrDefault(r => r.Disk.Index == disk.Index);
        var run = _history.Active(SelfTestHistory.KeyFor(disk));

        riga?.Bind(disk, run, status);

        if (status is null || run is null) return;

        if (status.Running)
        {
            run.Outcome = $"{status.PercentComplete}% completato";
            return;
        }

        // Il disco non sta più verificando: la diagnosi che seguivamo è finita.
        _history.Complete(run,
            status.LastResultKnown ? status.LastResultDescription : "Conclusa, esito non riportato",
            status.LastResultKnown ? status.LastResultPassed : null);

        riga?.Bind(disk, null, status);
        Finished?.Invoke(this, new SelfTestFinished(disk, run));
    }

    // -------------------------------------------------------------- cronologia

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 32;
        _grid.RowTemplate.Height = 26;
        _grid.ScrollBars = ScrollBars.None;
        _grid.RowPrePaint += (_, e) => e.PaintParts &= ~DataGridViewPaintParts.Focus;
        _grid.RowPostPaint += (s, e) =>
        {
            using var pen = new Pen(Theme.Divider, 1f);
            int y = e.RowBounds.Bottom - 1;
            e.Graphics.DrawLine(pen, e.RowBounds.Left, y, e.RowBounds.Right, y);
        };

        _grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { Name = "disco", HeaderText = "Disco",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, MinimumWidth = 160 },
            new DataGridViewTextBoxColumn { Name = "tipo", HeaderText = "Tipo", Width = 96 },
            new DataGridViewTextBoxColumn { Name = "avvio", HeaderText = "Avviata", Width = 140 },
            new DataGridViewTextBoxColumn { Name = "durata", HeaderText = "Durata", Width = 96 },
            new DataGridViewTextBoxColumn { Name = "esito", HeaderText = "Esito",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 120, MinimumWidth = 180 },
        ]);

        _gridScroll.Dock = DockStyle.Right;
        _gridScroll.Width = 12;
        _gridScroll.Visible = false;
        _gridScroll.ValueChanged += (_, _) =>
        {
            if (_syncingScroll || _grid.RowCount == 0) return;
            try { _grid.FirstDisplayedScrollingRowIndex = Math.Clamp(_gridScroll.Value, 0, _grid.RowCount - 1); }
            catch (InvalidOperationException) { /* griglia in ricostruzione */ }
        };

        _grid.Scroll += (_, _) => SyncScroll();
        _grid.Resize += (_, _) => SyncScroll();
        _grid.MouseWheel += (_, e) =>
        {
            if (_gridScroll.Needed) _gridScroll.Value -= Math.Sign(e.Delta) * 3;
        };
    }

    private void SyncScroll()
    {
        int visibili = Math.Max(1, _grid.DisplayedRowCount(includePartialRow: false));

        _syncingScroll = true;
        try
        {
            _gridScroll.LargeChange = visibili;
            _gridScroll.Maximum = Math.Max(0, _grid.RowCount - visibili);
            int prima = _grid.FirstDisplayedScrollingRowIndex;
            if (prima >= 0) _gridScroll.Value = prima;
        }
        finally { _syncingScroll = false; }

        if (_gridScroll.Visible != _gridScroll.Needed) _gridScroll.Visible = _gridScroll.Needed;
    }

    private void FillHistory()
    {
        var elenco = _history.Runs.ToList();
        _historyCard.Hint = elenco.Count == 0 ? "" : $"{elenco.Count} esecuzioni";

        if (elenco.Count == 0)
        {
            _grid.Rows.Clear();
            _grid.Rows.Add("Nessuna autodiagnosi eseguita da questa applicazione", "", "", "", "");
            _grid.Rows[0].DefaultCellStyle.ForeColor = Theme.TextMuted;
            _grid.ClearSelection();
            SyncScroll();
            return;
        }

        if (_grid.RowCount != elenco.Count)
        {
            _grid.Rows.Clear();
            for (int i = 0; i < elenco.Count; i++) _grid.Rows.Add();
        }

        for (int i = 0; i < elenco.Count; i++)
        {
            var run = elenco[i];
            var row = _grid.Rows[i];

            Set(row, "disco", run.DiskName);
            Set(row, "tipo", run.KindDisplay);
            Set(row, "avvio", run.StartedAt.ToString("dd/MM/yyyy HH:mm"));
            Set(row, "durata", run.DurationDisplay);
            Set(row, "esito", run.Running ? $"In corso · {run.Outcome}" : run.Outcome);

            var tinta = run.Running ? Theme.Accent
                      : run.Aborted ? Theme.TextMuted
                      : run.Passed == true ? Theme.Good
                      : run.Passed == false ? Theme.Bad
                      : Theme.TextSecondary;

            if (row.Cells["esito"].Style.ForeColor != tinta) row.Cells["esito"].Style.ForeColor = tinta;
            row.DefaultCellStyle.BackColor = i % 2 == 0 ? Theme.Surface : Theme.SurfaceAlt;
        }

        _grid.ClearSelection();
        SyncScroll();

        static void Set(DataGridViewRow row, string colonna, string valore)
        {
            var cella = row.Cells[colonna];
            if (!Equals(cella.Value, valore)) cella.Value = valore;
        }
    }

    // ------------------------------------------------------------------ tema

    public void ApplyTheme()
    {
        BackColor = Theme.Background;
        _layout.BackColor = Theme.Background;
        _disksCard.BackColor = Theme.Surface;
        _historyCard.BackColor = Theme.Surface;
        _rows.BackColor = Theme.Surface;
        _gridScroll.BackColor = Theme.Surface;

        _grid.BackgroundColor = Theme.Surface;
        _grid.GridColor = Theme.Divider;
        _grid.DefaultCellStyle.BackColor = Theme.Surface;
        _grid.DefaultCellStyle.ForeColor = Theme.TextPrimary;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.TextPrimary;
        _grid.DefaultCellStyle.Font = Theme.Body;
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.SurfaceAlt;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextMuted;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.SmallStrong;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);

        foreach (var riga in _rows.Controls.OfType<SelfTestRow>()) riga.ApplyTheme();

        FillHistory();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _poll.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Una riga della pagina: il disco, lo stato della sua autodiagnosi e i comandi
/// disponibili. Quali siano disponibili dipende dal dispositivo, e la riga lo dice.
/// </summary>
public sealed class SelfTestRow : Control
{
    private readonly AppSettings _settings;
    private readonly ToolButton _breve;
    private readonly ToolButton _esteso;
    private readonly ToolButton _interrompi;

    private SelfTestRun? _run;
    private SelfTestStatus? _status;

    public event EventHandler<SelfTestKind>? StartRequested;
    public event EventHandler? AbortRequested;

    public DiskInfo Disk { get; private set; }

    public SelfTestRow(DiskInfo disk, AppSettings settings)
    {
        Disk = disk;
        _settings = settings;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _breve = new ToolButton("", "Breve", (_, _) => StartRequested?.Invoke(this, SelfTestKind.Short));
        _esteso = new ToolButton("", "Esteso", (_, _) => StartRequested?.Invoke(this, SelfTestKind.Extended));
        _interrompi = new ToolButton("", "Interrompi", (_, _) => AbortRequested?.Invoke(this, EventArgs.Empty));

        foreach (var b in new[] { _breve, _esteso, _interrompi })
        {
            b.AutoWidth();
            Controls.Add(b);
        }
    }

    /// <summary>Vero se il firmware dichiara di prevedere l'autodiagnosi.</summary>
    private bool Supported =>
        Disk.SupportsSelfTest != false &&
        !(Disk.BusType == StorageBusType.Usb && Disk.Kind == DiskKind.Nvme);

    public void Bind(DiskInfo disk, SelfTestRun? run, SelfTestStatus? status = null)
    {
        Disk = disk;
        _run = run;
        if (status is not null) _status = status;

        bool inCorso = run is not null || _status?.Running == true;

        _breve.Visible = _esteso.Visible = Supported && !inCorso;
        _interrompi.Visible = Supported && inCorso;

        LayoutButtons();
        Invalidate();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        foreach (Control c in Controls) c.Invalidate();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
    }

    private void LayoutButtons()
    {
        int destra = Width - 18;
        foreach (var b in new[] { _interrompi, _esteso, _breve })
        {
            if (!b.Visible) continue;
            b.Location = new Point(destra - b.Width, (Height - b.Height) / 2);
            destra -= b.Width + 8;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        using (var pen = new Pen(Theme.Divider, 1f))
            g.DrawLine(pen, 18, Height - 1, Width - 18, Height - 1);

        float larghezzaTesto = Width - 40 - LarghezzaPulsanti();

        using (var b = new SolidBrush(Theme.TextPrimary))
            g.DrawString(Theme.Ellipsize(g, Disk.DisplayName, Theme.BodyStrong, larghezzaTesto),
                         Theme.BodyStrong, b, 18, 12);

        string sottotitolo = Disk.DriveLetters.Count > 0
            ? $"{Disk.LettersDisplay}  ·  {Disk.InterfaceDisplay} · {Disk.KindDisplay}"
            : $"{Disk.InterfaceDisplay} · {Disk.KindDisplay}";

        using (var b = new SolidBrush(Theme.TextMuted))
            g.DrawString(Theme.Ellipsize(g, sottotitolo, Theme.Small, larghezzaTesto), Theme.Small, b, 18, 31);

        DrawState(g, larghezzaTesto);
    }

    private float LarghezzaPulsanti() =>
        Controls.OfType<ToolButton>().Where(b => b.Visible).Sum(b => b.Width + 8);

    private void DrawState(Graphics g, float larghezza)
    {
        float y = 52;

        if (!Supported)
        {
            using var b = new SolidBrush(Theme.TextMuted);
            g.DrawString(Disk.BusType == StorageBusType.Usb
                    ? "Il box esterno non inoltra il comando di autodiagnosi"
                    : "Il firmware non prevede l'autodiagnosi",
                Theme.Small, b, 18, y);
            return;
        }

        if (_run is not null || _status?.Running == true)
        {
            int percento = _status?.Running == true ? _status.PercentComplete : 0;
            var barra = new RectangleF(18, y + 3, Math.Max(60, larghezza - 80), 8);

            using (var path = Theme.RoundedRect(barra, 4))
            using (var tb = new SolidBrush(Theme.Track))
                g.FillPath(tb, path);

            float quota = Math.Clamp(percento / 100f, 0.02f, 1f);
            using (var path = Theme.RoundedRect(new RectangleF(barra.X, barra.Y, barra.Width * quota, barra.Height), 4))
            using (var fb = new SolidBrush(Theme.Accent))
                g.FillPath(fb, path);

            using var testo = new SolidBrush(Theme.Accent);
            g.DrawString($"{percento}%", Theme.SmallStrong, testo, barra.Right + 8, y);
            return;
        }

        string esito = _status?.LastResultKnown == true
            ? $"Ultima verifica del disco: {_status.LastResultDescription}"
            : "Nessuna verifica registrata dal disco";

        var tinta = _status?.LastResultKnown == true
            ? _status.LastResultPassed ? Theme.Good : Theme.Bad
            : Theme.TextMuted;

        using (var b = new SolidBrush(tinta))
            g.DrawString(Theme.Ellipsize(g, esito, Theme.Small, larghezza), Theme.Small, b, 18, y);
    }
}
