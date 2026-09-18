using System.Drawing.Drawing2D;
using DiskTempMonitor.Models;
using DiskTempMonitor.Native;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

public sealed class MainForm : Form
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_HOTKEY = 0x0312;
    private const int OverlayHotkeyId = 0xD70;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly AppSettings _settings;
    private readonly DiskScanner _scanner = new();
    private readonly TrayManager _tray;
    private readonly Notifier _notifier;
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly SensorHub _sensors = new();
    private readonly SystemMetrics _metrics = new();

    private List<DiskInfo> _disks = [];
    private int _hiddenDisks;
    private int _selectedIndex = -1;
    private bool _reallyExit;
    private bool _busy;

    // Richieste arrivate mentre una lettura era in corso: vanno servite dopo, non perse.
    private bool _pendingRefresh;
    private bool _pendingFull;

    // Indicatore di lettura: compare al posto della scheda Dischi finché si legge.
    private readonly BusyOverlay _loading = new();
    private System.Windows.Forms.Timer? _loadingDelay;
    private bool _showLoading;
    private DateTime _loadingShownAt;
    private string _loadingMessage = "";
    private string? _loadingDetail;
    private System.Windows.Forms.Timer? _deviceChange;
    private DateTime _lastUpdate;

    // --- struttura ---
    private readonly BufferedTable _root = new();
    private readonly BufferedPanel _toolbar = new();
    private readonly BufferedFlow _cards = new();
    private readonly BufferedPanel _middle = new();
    private readonly BufferedPanel _content = new();
    private readonly TabBar _tabs = new();
    private SystemView? _systemView;
    private SelfTestView? _selfTestView;
    private readonly SelfTestHistory _selfTestHistory = SelfTestHistory.Load();
    private readonly BufferedTable _rightHost = new();
    private readonly Splitter _splitter = new();
    private readonly CardPanel _detailsCard = new("Dettagli del disco");
    private readonly BufferedTable _detailsHost = new();
    private readonly BufferedPanel _detailsLeft = new();
    private readonly BufferedPanel _detailsRight = new();
    private readonly CardPanel _chartCard = new("Andamento temperatura");
    private readonly TempChart _chart;
    private readonly CardPanel _smartCard = new("Attributi S.M.A.R.T.");
    private readonly BufferedGrid _grid = new();
    private readonly ThemedScrollBar _gridScroll = new();
    private bool _syncingGridScroll;
    private readonly BufferedPanel _status = new();
    private readonly BusySpinner _statusSpinner = new();
    private readonly StatusLabel _statusText = new();
    private readonly StatusLabel _statusBadge = new();

    // Stato di riconciliazione: consente di aggiornare i valori senza ricostruire
    // i controlli a ogni ciclo, che è la causa dello sfarfallio.
    private readonly Dictionary<string, DetailRow> _detailRows = [];
    private readonly ToolTip _tooltip = new() { AutoPopDelay = 20000, InitialDelay = 450, ReshowDelay = 100 };
    private readonly RegisteredWaitHandle? _showWait;
    private readonly ContextMenuStrip _exportMenu = new();
    private string _exportMenuKey = "";
    private ToolButton? _exportButton;
    private int _detailsForDisk = -1;
    private int _gridForDisk = -1;

    public MainForm(AppSettings settings, bool startHidden, EventWaitHandle? showRequested = null)
    {
        _settings = settings;
        _settings.ApplyTheme();
        _tray = new TrayManager(settings);
        _notifier = new Notifier(settings) { BalloonFallback = (t, b) => _tray.ShowBalloon(t, b) };
        _chart = new TempChart(settings);

        // Una seconda istanza segnala questo evento per chiedere la finestra.
        if (showRequested is not null)
        {
            _showWait = ThreadPool.RegisterWaitForSingleObject(showRequested, (_, _) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(ShowFromTray); }
                catch (ObjectDisposedException) { /* finestra in chiusura */ }
                catch (InvalidOperationException) { /* handle in ricreazione */ }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }

        BuildUi();
        HookTray();
        Theme.Changed += OnThemeChanged;

        _timer.Interval = Math.Max(1, _settings.RefreshSeconds) * 1000;
        _timer.Tick += async (_, _) => await RefreshAsync(full: false, requested: false);
        _timer.Start();

        Shown += async (_, _) =>
        {
            Theme.ApplyToWindow(this);
            Theme.ApplyNativeTheme(this);       // le barre di scorrimento hanno bisogno degli handle
            if (startHidden) HideToTray();
            await RefreshAsync(full: true);
            if (_settings.MiniWindowEnabled) ToggleMiniWindow();

            // Un avvio automatico configurato in passato nella chiave Run non
            // funzionerebbe più, ora che l'applicazione richiede l'elevazione.
            if (Startup.MigrateRegistryToTask())
                Announce("Avvio automatico spostato nell'Utilità di pianificazione, " +
                         "necessaria per partire con i privilegi richiesti.");
        };
    }

    // ------------------------------------------------------------------ interfaccia

    private void BuildUi()
    {
        Text = "Disk Temp Monitor";
        Icon = IconRenderer.CreateAppIcon(32);
        // La larghezza minima deve contenere barra laterale, divisore e colonna destra.
        MinimumSize = new Size(SidebarMinWidth + 8 + 400 + 40, 720);
        Size = new Size(1200, 880);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;

        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.RowCount = 5;
        _root.Padding = new Padding(16, 8, 16, 8);
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));            // barra strumenti
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));            // schede Dischi/Sistema
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, CardsRowBase));  // riquadri dischi
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));            // contenuto
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));            // barra di stato

        BuildToolbar();
        BuildTabs();
        BuildCards();
        BuildMiddle();
        BuildStatus();

        // Il contenuto delle due schede vive nello stesso riquadro, uno alla volta
        _content.Dock = DockStyle.Fill;
        _content.Margin = new Padding(0);
        _systemView = new SystemView(_settings) { Visible = false };

        _selfTestView = new SelfTestView(_settings, _selfTestHistory) { Visible = false };
        _selfTestView.Announced += (_, messaggio) => Announce(messaggio);
        _selfTestView.Finished += (_, esito) => AnnunciaAutotest(esito);

        _loading.Dock = DockStyle.Fill;
        // Aggiunto per ultimo e poi portato davanti: deve coprire le due schede.
        _content.Controls.Add(_systemView);
        _content.Controls.Add(_selfTestView);
        _content.Controls.Add(_middle);
        _content.Controls.Add(_loading);
        _loading.BringToFront();

        _root.Controls.Add(_toolbar, 0, 0);
        _root.Controls.Add(_tabs, 0, 1);
        _root.Controls.Add(_cards, 0, 2);
        _root.Controls.Add(_content, 0, 3);
        _root.Controls.Add(_status, 0, 4);

        Controls.Add(_root);
        ApplyThemeColors();
    }

    private void BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Margin = new Padding(0, 0, 0, 6);
        _tabs.SetItems("Dischi", "Sistema", "Autotest");
        _tabs.SelectedChanged += (_, _) => ApplyTabSelection();
    }

    /// <summary>Mostra il contenuto della scheda scelta e nasconde l'altro.</summary>
    private void ApplyTabSelection()
    {
        ApplyDiskPageState();

        if (_tabs.SelectedIndex == 1) _ = RefreshSystemAsync();
        else if (_tabs.SelectedIndex == SelfTestTabIndex) _selfTestView?.Activate();
    }

    /// <summary>
    /// Decide cosa si vede nella metà bassa della finestra. Passa tutto di qui, perché
    /// le tre condizioni in gioco — scheda scelta, lettura in corso, spazio necessario
    /// alle schede dei dischi — si influenzano a vicenda.
    /// </summary>
    private const int SelfTestTabIndex = 2;

    private void ApplyDiskPageState()
    {
        bool dischi = _tabs.SelectedIndex == 0;
        bool sistema = _tabs.SelectedIndex == 1;
        bool autotest = _tabs.SelectedIndex == SelfTestTabIndex;
        bool leggendo = dischi && _showLoading;

        _cards.Visible = dischi && !leggendo;
        _middle.Visible = dischi && !leggendo;
        if (_systemView is not null) _systemView.Visible = sistema;
        if (_selfTestView is not null) _selfTestView.Visible = autotest;

        // La striscia delle schede occupa spazio solo quando c'è davvero qualcosa da
        // mostrarci: da sola, la riga vuota spingerebbe giù tutto il resto.
        _root.RowStyles[2].Height = dischi && !leggendo ? CardsRowBase : 0;

        if (leggendo) _loading.Begin(_loadingMessage, _loadingDetail);
        else _loading.End();

        if (dischi && !leggendo) AdjustCardsRowHeight();
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Fill;
        _toolbar.Margin = new Padding(0);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
        };

        var title = new Label
        {
            Text = "Disk Temp Monitor",
            Font = Theme.Title,
            AutoSize = true,
            Margin = new Padding(2, 2, 18, 0),
        };

        BuildExportMenu();

        var refresh = new ToolButton("", "Aggiorna", async (_, _) => await RefreshAsync(full: false)) { Primary = true };
        var rescan = new ToolButton("", "Rileva dischi", async (_, _) => await RefreshAsync(full: true));
        var settings = new ToolButton("", "Impostazioni", (_, _) => OpenSettings());
        var export = _exportButton = new ToolButton("", "Esporta", (s, _) => ShowExportMenu((Control)s!));
        var sidebar = new ToolButton("", "Attributi", (_, _) => ToggleSidebar());
        var mini = new ToolButton("", "Riquadro", (_, _) => ToggleMiniWindow());
        var selftest = new ToolButton("", "Autotest", (_, _) => ShowSelfTestPage());
        var theme = new ToolButton("", "Tema", (_, _) => CycleTheme());
        var minimize = new ToolButton("", "Riduci in tray", (_, _) => HideToTray());

        var buttons = new[] { refresh, rescan, sidebar, settings, export, selftest, mini, theme, minimize };
        var tips = new[]
        {
            "Rilegge temperatura e attributi dei dischi già noti (F5)",
            "Rienumera i dischi fisici collegati al PC (F6)",
            "Mostra o riduce la barra laterale degli attributi (F9)",
            "Impostazioni (F4)",
            "Riepilogo di un disco o di tutti, oppure rapporto completo",
            "Apre la pagina dell'autodiagnosi dei dischi",
            "Riquadro compatto sempre in primo piano (F8)",
            "Chiaro, scuro o automatico (F2)",
            "Nasconde la finestra in area di notifica (Esc)",
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].AutoWidth();
            _tooltip.SetToolTip(buttons[i], tips[i]);
            left.Controls.Add(buttons[i]);
        }

        left.Controls.Add(title);
        left.Controls.SetChildIndex(title, 0);

        _toolbar.Controls.Add(left);
    }

    private const int CardsRowBase = 148;       // scheda 128 + padding + margine
    private const int ScrollBarAllowance = 18;

    private void BuildCards()
    {
        _cards.Dock = DockStyle.Fill;
        _cards.Margin = new Padding(0, 0, 0, 12);
        _cards.AutoScroll = true;
        _cards.WrapContents = false;
        _cards.Padding = new Padding(0, 3, 0, 3);
    }

    /// <summary>
    /// La riga deve contenere la scheda più il margine, e lasciare spazio alla barra di
    /// scorrimento orizzontale quando i dischi non ci stanno tutti: altrimenti la
    /// scheda sborda sulla sezione sottostante.
    /// </summary>
    private void AdjustCardsRowHeight()
    {
        // Fuori dalla scheda Dischi la riga deve restare chiusa: allargarla qui era
        // quello che faceva scivolare in basso i riquadri della scheda Sistema ogni
        // volta che finiva una lettura dei dischi.
        if (_tabs.SelectedIndex != 0 || _showLoading) return;

        int needed = _cards.Controls.OfType<DiskCard>()
            .Sum(c => c.Width + c.Margin.Horizontal);

        bool scrolls = needed > _cards.ClientSize.Width && _cards.Controls.Count > 0;
        int height = CardsRowBase + (scrolls ? ScrollBarAllowance : 0);

        if (Math.Abs(_root.RowStyles[2].Height - height) > 0.5f)
            _root.RowStyles[2].Height = height;
    }

    private const int SidebarCollapsedWidth = 34;
    private const int SidebarMinWidth = 520;
    private int _sidebarWidth = 560;

    private void BuildMiddle()
    {
        _middle.Dock = DockStyle.Fill;
        _middle.Margin = new Padding(0);

        // --- colonna destra: dettagli sopra, grafico sotto ---
        _rightHost.Dock = DockStyle.Fill;
        _rightHost.Margin = new Padding(0);
        _rightHost.ColumnCount = 1;
        _rightHost.RowCount = 2;
        _rightHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rightHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 312));
        _rightHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _detailsCard.Dock = DockStyle.Fill;
        _detailsCard.Margin = new Padding(0, 0, 0, 12);
        _detailsCard.Padding = new Padding(14, 44, 10, 10);

        _detailsHost.Dock = DockStyle.Fill;
        _detailsHost.ColumnCount = 2;
        _detailsHost.RowCount = 1;
        _detailsHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _detailsHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // Senza questo la riga resta in dimensionamento automatico e le colonne,
        // che contengono solo controlli in Dock=Top, collassano a zero.
        _detailsHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _detailsLeft.Dock = DockStyle.Fill;
        _detailsRight.Dock = DockStyle.Fill;
        _detailsRight.Margin = new Padding(14, 0, 0, 0);
        _detailsHost.Controls.Add(_detailsLeft, 0, 0);
        _detailsHost.Controls.Add(_detailsRight, 1, 0);
        _detailsCard.Controls.Add(_detailsHost);

        _chartCard.Dock = DockStyle.Fill;
        _chartCard.Margin = new Padding(0);
        _chartCard.Padding = new Padding(10, 44, 12, 10);
        _chartCard.ActionText = "Svuota";
        _chartCard.ActionClicked += (_, _) => ClearHistory();
        _chart.Dock = DockStyle.Fill;
        _chartCard.Controls.Add(_chart);

        _rightHost.Controls.Add(_detailsCard, 0, 0);
        _rightHost.Controls.Add(_chartCard, 0, 1);

        // --- barra laterale con gli attributi ---
        _smartCard.Dock = DockStyle.Left;
        _smartCard.Width = _sidebarWidth;
        _smartCard.Padding = new Padding(1, 42, 1, 1);
        _smartCard.Collapsible = true;
        _smartCard.CollapsedChanged += (_, _) => ApplySidebarState();
        BuildGrid();
        // Il controllo in Fill va aggiunto per primo: il docking parte dall'ultimo,
        // che così ottiene il bordo esterno.
        _smartCard.Controls.Add(_grid);
        _smartCard.Controls.Add(_gridScroll);

        _splitter.Dock = DockStyle.Left;
        _splitter.Width = 8;
        _splitter.MinSize = SidebarMinWidth;
        _splitter.MinExtra = 400;
        _splitter.Cursor = Cursors.VSplit;
        _splitter.SplitterMoved += (_, _) =>
        {
            if (!_smartCard.Collapsed) _sidebarWidth = _smartCard.Width;
        };
        _splitter.Paint += (s, e) =>
        {
            e.Graphics.Clear(Theme.Background);
            using var b = new SolidBrush(Theme.Border);
            e.Graphics.FillRectangle(b, new Rectangle(_splitter.Width / 2 - 1, _splitter.Height / 2 - 14, 2, 28));
        };

        // Il controllo in Fill va aggiunto per primo: il docking viene risolto
        // partendo dall'ultimo aggiunto, che così ottiene il bordo esterno.
        _middle.Controls.Add(_rightHost);
        _middle.Controls.Add(_splitter);
        _middle.Controls.Add(_smartCard);
    }

    private void ApplySidebarState()
    {
        bool collapsed = _smartCard.Collapsed;
        _smartCard.Padding = collapsed ? new Padding(0) : new Padding(1, 42, 1, 1);
        _smartCard.Width = collapsed ? SidebarCollapsedWidth : _sidebarWidth;
        _splitter.Visible = !collapsed;
        _splitter.Enabled = !collapsed;

        // Da ridotta lo splitter sparisce, e con lui la distanza dalle schede di
        // destra: la si rimette come spaziatura del contenitore.
        _rightHost.Padding = collapsed ? new Padding(12, 0, 0, 0) : new Padding(0);
    }

    private void ToggleSidebar() => _smartCard.Collapsed = !_smartCard.Collapsed;

    // ------------------------------------------------------------- autotest

    /// <summary>
    /// L'autodiagnosi ha una pagina tutta sua: qui resta solo il collegamento, perché il
    /// pulsante della barra strumenti continui a funzionare come prima.
    /// </summary>
    private void ShowSelfTestPage() => _tabs.SelectedIndex = SelfTestTabIndex;

    // ---------------------------------------------------- sovrimpressione di gioco

    private FrameRateMonitor? _frames;
    private OverlayWindow? _overlay;
    private System.Windows.Forms.Timer? _overlayTimer;

    /// <summary>
    /// Accende o spegne la sovrimpressione secondo le impostazioni. Si chiama
    /// all'avvio, all'uscita dalle impostazioni e dalla combinazione da tastiera.
    /// </summary>
    private void ApplyOverlayState()
    {
        if (_settings.OverlayEnabled) StartOverlay();
        else StopOverlay();
    }

    private void StartOverlay()
    {
        if (_overlay is not null) return;

        _frames ??= new FrameRateMonitor();
        _frames.Start();

        if (!_frames.Available)
            Announce($"Sovrimpressione aperta, ma i fotogrammi non si possono contare: {_frames.Error}.");

        _overlay = new OverlayWindow(_settings);
        _overlay.Show();

        // La sovrimpressione va aggiornata molto più spesso delle letture dei dischi:
        // gli FPS cambiano di continuo, e un numero che si muove ogni tre secondi non
        // servirebbe a niente.
        _overlayTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _overlayTimer.Tick += OnOverlayTick;
        _overlayTimer.Start();

        OnOverlayTick(null, EventArgs.Empty);
    }

    private void StopOverlay()
    {
        if (_overlayTimer is not null)
        {
            _overlayTimer.Stop();
            _overlayTimer.Tick -= OnOverlayTick;
            _overlayTimer.Dispose();
            _overlayTimer = null;
        }

        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay.Dispose();
            _overlay = null;
        }

        // Il conteggio dei fotogrammi tiene aperta una sessione di tracciamento di
        // sistema: quando la targhetta non c'è, non ha motivo di restare accesa.
        _frames?.Dispose();
        _frames = null;
    }

    private int _overlayTicks;

    private void OnOverlayTick(object? sender, EventArgs e)
    {
        if (_overlay is null) return;

        // La pulizia dei processi che non disegnano più non serve a ogni giro.
        if (++_overlayTicks % 25 == 0) _frames?.Prune();

        var app = _frames?.ForegroundApp();

        // Con l'impostazione attiva la targhetta esiste solo finché c'è un numero di
        // fotogrammi da mostrare: niente FPS, niente targhetta. Senza quel numero
        // resterebbe una targhetta di temperature sopra al desktop, che è quello che
        // fa già il riquadro compatto.
        bool mostra = !_settings.OverlayOnlyInGames || app is not null;

        if (!mostra) { _overlay.HideOverlay(); return; }

        _overlay.Update(_disks, _metrics, app?.Fps);
    }

    /// <summary>Accende e spegne la sovrimpressione, e ricorda la scelta.</summary>
    private void ToggleOverlay()
    {
        _settings.OverlayEnabled = !_settings.OverlayEnabled;
        _settings.Save();
        ApplyOverlayState();

        Announce(_settings.OverlayEnabled
            ? "Sovrimpressione di gioco accesa: comparirà quando un gioco starà disegnando."
            : "Sovrimpressione di gioco spenta.");
    }

    /// <summary>
    /// Registra la combinazione globale. Se è già presa da un altro programma non si
    /// insiste: la sovrimpressione resta comandabile dalle impostazioni.
    /// </summary>
    /// <summary>
    /// Registra la combinazione globale.
    /// <para>
    /// Se un altro programma se l'è già presa, Windows rifiuta e non c'è modo di
    /// strappargliela. Prima questo fallimento restava muto, e la combinazione sembrava
    /// semplicemente non funzionare: adesso lo si dice, perché è l'unico modo che ha
    /// l'utente di capire che deve sceglierne un'altra.
    /// </para>
    /// </summary>
    private void RegisterOverlayHotkey()
    {
        UnregisterHotKey(Handle, OverlayHotkeyId);
        _overlayHotkeyBusy = false;

        if (!Hotkey.TryParse(_settings.OverlayHotkey, out uint mods, out uint key)) return;

        if (!RegisterHotKey(Handle, OverlayHotkeyId, mods, key))
        {
            _overlayHotkeyBusy = true;
            Announce($"La combinazione {_settings.OverlayHotkey} è già usata da un altro " +
                     "programma: scegline un'altra nelle impostazioni.");
        }
    }

    /// <summary>Vero se la combinazione scelta non si è potuta registrare.</summary>
    private bool _overlayHotkeyBusy;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ------------------------------------------------------- riquadro compatto

    private MiniWindow? _mini;

    private void ToggleMiniWindow()
    {
        if (_mini is not null) { CloseMiniWindow(); return; }

        _mini = new MiniWindow(_settings);
        _mini.ShowMainRequested += (_, _) => ShowFromTray();
        _mini.CloseRequested += (_, _) => CloseMiniWindow();
        _mini.Update(_disks, _metrics);
        _mini.Show();

        _settings.MiniWindowEnabled = true;
        _settings.Save();
        Announce("Riquadro compatto aperto: trascinalo dove preferisci, doppio clic per tornare qui.");
    }

    private void CloseMiniWindow()
    {
        if (_mini is null) return;
        _mini.Close();
        _mini.Dispose();
        _mini = null;

        _settings.MiniWindowEnabled = false;
        _settings.Save();
    }

    /// <summary>
    /// Azzera lo storico del disco selezionato. Si riparte dalla lettura corrente,
    /// così il grafico e i minimi/massimi ricominciano da adesso invece di restare
    /// vuoti fino al prossimo aggiornamento.
    /// </summary>
    private void ClearHistory()
    {
        if (Current is not DiskInfo d) return;

        d.History.Clear();
        d.SessionMinC = null;
        d.SessionMaxC = null;

        if (d.TemperatureC is int t)
        {
            d.PushHistory(t);
            d.SessionMinC = t;
            d.SessionMaxC = t;
        }

        _chart.SetDisk(d);
        _chart.Invalidate();
        UpdateDetails();
        Announce($"Storico temperature azzerato per {d.DisplayName}.");
    }

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
        // Nessun bordo disegnato da WinForms: i separatori orizzontali li tracciamo noi
        // in RowPostPaint, così non restano mai frammenti verticali fuori posto.
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _grid.RowPostPaint += GridRowPostPaint;

        // Niente rettangolo tratteggiato sulla cella corrente: con la selezione per
        // riga non aggiunge informazione e lascia un segmento verticale dopo i clic.
        _grid.RowPrePaint += (_, e) => e.PaintParts &= ~DataGridViewPaintParts.Focus;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 32;
        _grid.RowTemplate.Height = 26;
        // La barra di sistema non si lascia colorare: in tema chiaro Windows la disegna
        // quasi bianca, e dentro una scheda attenuata sembra una striscia luminosa
        // appiccicata sopra. Si usa la nostra, che prende i colori del tema.
        _grid.ScrollBars = ScrollBars.None;
        // Larghezze contenute: la tabella vive in una barra laterale, non a tutta pagina.
        _grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { Name = "state", HeaderText = "", Width = 24, Resizable = DataGridViewTriState.False },
            new DataGridViewTextBoxColumn { Name = "id", HeaderText = "ID", Width = 42 },
            new DataGridViewTextBoxColumn
            {
                Name = "name", HeaderText = "Attributo",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, MinimumWidth = 150,
            },
            new DataGridViewTextBoxColumn { Name = "cur", HeaderText = "Cur", Width = 46 },
            new DataGridViewTextBoxColumn { Name = "worst", HeaderText = "Peg", Width = 46 },
            new DataGridViewTextBoxColumn { Name = "thr", HeaderText = "Sog", Width = 46 },
            new DataGridViewTextBoxColumn { Name = "raw", HeaderText = "Valore grezzo", Width = 116 },
            new DataGridViewTextBoxColumn { Name = "dec", HeaderText = "Decimale", Width = 96 },
        ]);

        _grid.Columns["cur"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["worst"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["thr"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["dec"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["state"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

        // Il pallino di stato viene disegnato, non scritto: resta nitido a ogni DPI.
        _grid.CellPainting += GridCellPainting;

        BuildGridScroll();
    }

    /// <summary>
    /// Collega la barra di scorrimento disegnata alla griglia. Si ragiona per righe e non
    /// per pixel: hanno tutte la stessa altezza, quindi l'indice della prima riga visibile
    /// è già la posizione.
    /// </summary>
    private void BuildGridScroll()
    {
        _gridScroll.Dock = DockStyle.Right;
        _gridScroll.Width = 12;
        _gridScroll.Visible = false;

        _gridScroll.ValueChanged += (_, _) =>
        {
            if (_syncingGridScroll || _grid.RowCount == 0) return;
            try { _grid.FirstDisplayedScrollingRowIndex = Math.Clamp(_gridScroll.Value, 0, _grid.RowCount - 1); }
            catch (InvalidOperationException) { /* griglia in ricostruzione */ }
        };

        _grid.Scroll += (_, _) => SyncGridScroll();
        _grid.Resize += (_, _) => SyncGridScroll();
        _grid.RowsAdded += (_, _) => SyncGridScroll();
        _grid.RowsRemoved += (_, _) => SyncGridScroll();

        // Senza barra nativa la rotellina non muove più niente: la si gestisce qui.
        _grid.MouseWheel += (_, e) =>
        {
            if (!_gridScroll.Needed) return;
            _gridScroll.Value -= Math.Sign(e.Delta) * 3;
        };
    }

    private void SyncGridScroll()
    {
        int visibili = Math.Max(1, _grid.DisplayedRowCount(includePartialRow: false));

        _syncingGridScroll = true;
        try
        {
            _gridScroll.LargeChange = visibili;
            _gridScroll.Maximum = Math.Max(0, _grid.RowCount - visibili);

            int prima = _grid.FirstDisplayedScrollingRowIndex;
            if (prima >= 0) _gridScroll.Value = prima;
        }
        finally { _syncingGridScroll = false; }

        if (_gridScroll.Visible != _gridScroll.Needed) _gridScroll.Visible = _gridScroll.Needed;
    }

    /// <summary>Separatore in fondo a ogni riga, disegnato dopo il contenuto.</summary>
    private void GridRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        using var pen = new Pen(Theme.Divider, 1f);
        int y = e.RowBounds.Bottom - 1;
        e.Graphics.DrawLine(pen, e.RowBounds.Left, y, e.RowBounds.Right, y);
    }

    private void GridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0) return;

        e.PaintBackground(e.CellBounds, true);
        if (_grid.Rows[e.RowIndex].Tag is HealthState state && state != HealthState.Unknown)
        {
            var color = DiskCard.HealthColor(state);
            var g = e.Graphics!;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(color);
            g.FillEllipse(b, e.CellBounds.X + e.CellBounds.Width / 2f - 3.5f,
                             e.CellBounds.Y + e.CellBounds.Height / 2f - 3.5f, 7, 7);
        }
        e.Handled = true;
    }

    /// <summary>Per quanto un messaggio dell'utente resiste prima che torni il riepilogo.</summary>
    private static readonly TimeSpan StatusHold = TimeSpan.FromSeconds(8);

    private DateTime _statusHoldUntil;

    /// <summary>
    /// Scrive un messaggio nella barra di stato e lo tiene lì per qualche secondo.
    /// <para>
    /// Senza questa attesa il riepilogo del giro successivo lo cancellerebbe subito: con
    /// l'intervallo di aggiornamento a un secondo, un messaggio come "rapporto salvato"
    /// spariva prima di poterlo leggere.
    /// </para>
    /// </summary>
    private void Announce(string message)
    {
        _statusText.Text = message;
        _statusHoldUntil = DateTime.UtcNow + StatusHold;
    }

    private void BuildStatus()
    {
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(2, 4, 2, 0);

        _statusText.Dock = DockStyle.Fill;
        _statusText.Alignment = ContentAlignment.MiddleLeft;
        _statusText.Font = Theme.Small;

        _statusBadge.Dock = DockStyle.Right;
        // Deve contenere versione e tipo di privilegi.
        _statusBadge.Width = 220;
        _statusBadge.Alignment = ContentAlignment.MiddleRight;
        _statusBadge.Font = Theme.Small;
        // Visibile fin da subito, senza aspettare la prima scansione.
        _statusBadge.Text = BuildInfo.Display;
        _tooltip.SetToolTip(_statusBadge,
            $"Disk Temp Monitor {BuildInfo.Full}\n{Environment.ProcessPath}");

        _statusSpinner.Dock = DockStyle.Left;
        _statusSpinner.Width = 20;

        // Il controllo in Fill va aggiunto per primo: il docking parte dall'ultimo, che
        // così ottiene il bordo esterno.
        _status.Controls.Add(_statusText);
        _status.Controls.Add(_statusBadge);
        _status.Controls.Add(_statusSpinner);
    }

    private void HookTray()
    {
        _tray.ShowWindowRequested += (_, _) => ShowFromTray();
        _tray.RefreshRequested += async (_, _) => await RefreshAsync(full: false);
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _tray.SetDpi(DeviceDpi);
    }

    // ------------------------------------------------------------------- tema

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => OnThemeChanged(sender, e))); return; }

        ApplyThemeColors();
        Theme.ApplyToWindow(this);

        // I controlli portano con sé i colori del tema precedente: vanno ricreati.
        _detailsForDisk = -1;
        _gridForDisk = -1;
        UpdateDetails();
        Invalidate(true);
    }

    private void ApplyThemeColors()
    {
        BackColor = Theme.Background;
        _root.BackColor = Theme.Background;
        _toolbar.BackColor = Theme.Background;
        _cards.BackColor = Theme.Background;
        _middle.BackColor = Theme.Background;
        _status.BackColor = Theme.Background;
        _statusSpinner.BackColor = Theme.Background;

        foreach (var c in _toolbar.Controls.OfType<FlowLayoutPanel>())
        {
            c.BackColor = Theme.Background;
            foreach (Control child in c.Controls) child.ForeColor = Theme.TextPrimary;
        }

        _detailsCard.BackColor = Theme.Surface;
        _detailsHost.BackColor = Theme.Surface;
        _detailsLeft.BackColor = Theme.Surface;
        _detailsRight.BackColor = Theme.Surface;
        _statusText.ForeColor = Theme.TextMuted;
        _statusBadge.ForeColor = Theme.TextMuted;

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

        _exportMenu.BackColor = Theme.SurfaceAlt;
        _exportMenu.ForeColor = Theme.TextPrimary;

        _systemView?.ApplyTheme();
        _selfTestView?.ApplyTheme();
        _tabs.Invalidate();
        Theme.ApplyNativeTheme(this);
    }

    private void CycleTheme()
    {
        _settings.Theme = Theme.Mode switch
        {
            ThemeMode.Auto => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.Auto,
        };
        Theme.Apply(_settings.Theme);
        _tray.Invalidate();
        _settings.Save();

        string label = _settings.Theme switch
        {
            ThemeMode.Light => "chiaro",
            ThemeMode.Dark => "scuro",
            _ => "automatico (segue Windows)",
        };
        Announce($"Tema: {label}");
    }

    // -------------------------------------------------------------------- dati

    /// <param name="requested">
    /// Vero se la lettura la sta chiedendo l'utente (pulsanti, scorciatoie, collegamento
    /// di un disco), falso se è il giro automatico dell'orologio. La differenza conta
    /// quando una lettura è già in corso: una richiesta si accoda, un giro automatico si
    /// lascia cadere. Accodarlo, come si faceva, bastava a mandare tutto in tilt: con
    /// l'intervallo a un secondo e i sensori di sistema da leggere, ogni ciclo durava più
    /// dell'intervallo, la coda non si svuotava mai e la scheda Dischi restava per sempre
    /// dietro l'indicatore di lettura.
    /// </param>
    private async Task RefreshAsync(bool full, bool requested = true)
    {
        if (_busy)
        {
            if (!requested) return;

            // Un ciclo è già in corso, e su un box USB che non risponde può durare
            // parecchio: la richiesta viene messa in coda invece di essere buttata via,
            // altrimenti un clic su "Rileva dischi" sembrerebbe non fare nulla.
            _pendingRefresh = true;
            _pendingFull |= full;
            if (full) Announce("Rilevamento in coda: attendo la fine della lettura in corso…");
            return;
        }

        _busy = true;
        BeginLoadingIndicator(full);
        try
        {

            if (full || _disks.Count == 0)
            {
                var scanned = await Task.Run(_scanner.ScanAll);

                // Chiavette e lettori di schede non hanno nulla da raccontare: nelle
                // schede sarebbero solo una riga di trattini.
                if (_settings.HideUsbWithoutData)
                {
                    _hiddenDisks = scanned.Count(d => d.IsMuteUsbDevice);
                    scanned = scanned.Where(d => !d.IsMuteUsbDevice).ToList();
                }
                else
                {
                    _hiddenDisks = 0;
                }

                // Conserva statistiche e storico già raccolti per lo stesso disco.
                foreach (var fresh in scanned)
                {
                    var old = _disks.FirstOrDefault(d => d.Index == fresh.Index && d.SerialNumber == fresh.SerialNumber);
                    if (old is null) continue;

                    fresh.SessionMinC = Min(old.SessionMinC, fresh.SessionMinC);
                    fresh.SessionMaxC = Max(old.SessionMaxC, fresh.SessionMaxC);

                    var carried = old.History.ToArray();
                    var tail = fresh.History.ToArray();
                    fresh.History.Clear();
                    foreach (int v in carried.Concat(tail).TakeLast(DiskInfo.HistoryCapacity))
                        fresh.History.Enqueue(v);
                }

                _disks = scanned;
                RebuildCards();
            }
            else
            {
                var snapshot = _disks;
                await Task.Run(() =>
                {
                    foreach (var d in snapshot) _scanner.Refresh(d);
                });
                foreach (var c in _cards.Controls.OfType<DiskCard>()) c.Invalidate();
            }

            _lastUpdate = DateTime.Now;
            UpdateDetails();

            // I sensori di sistema servono anche quando la scheda Sistema non è
            // visibile: li chiedono le icone in area di notifica e il riquadro compatto.
            if (_tabs.SelectedIndex == 1 || NeedsSystemSensors) await RefreshSystemAsync();

            _selfTestView?.Update(_disks);
            _tray.Update(_disks, _metrics);
            _notifier.Evaluate(_disks, _settings);
            _mini?.Update(_disks, _metrics);

            // Il conteggio dei nascosti va detto: un disco che sparisce senza spiegazione
            // sembra un difetto, non una scelta.
            string nascosti = _hiddenDisks > 0
                ? $" · {_hiddenDisks} dispositivo/i USB senza dati non mostrati"
                : "";

            // Un messaggio appena mostrato all'utente ha la precedenza: il riepilogo può
            // aspettare qualche secondo, tanto si riscrive da solo al giro dopo.
            // Il riepilogo periodico non porta l'ora: cambiandola a ogni giro la riga si
            // ridisegnava di continuo, e quel tremolio la rendeva illeggibile. Senza,
            // il testo resta identico e non viene nemmeno riscritto — che sia aggiornato
            // lo dice il segnalino che gira di fianco. L'ora la porta invece la
            // rienumerazione, che capita di rado ed è un fatto da datare.
            if (full || DateTime.UtcNow >= _statusHoldUntil)
                _statusText.Text = _disks.Count == 0
                    ? _hiddenDisks > 0
                        ? $"Nessun disco con dati leggibili.{nascosti}"
                        : "Nessun disco rilevato."
                    : full
                        ? $"{_disks.Count} disco/i rilevati alle {_lastUpdate:HH:mm:ss} · intervallo {_settings.RefreshSeconds}s{nascosti}"
                        : $"{_disks.Count} disco/i monitorati · intervallo {_settings.RefreshSeconds}s{nascosti}";
            _statusBadge.Text = $"{BuildInfo.Display}  ·  " +
                                (DiskScanner.IsAdministrator ? "Amministratore" : "Utente standard");
        }
        catch (Exception ex)
        {
            Announce($"Errore durante l'aggiornamento: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _statusSpinner.Active = false;
            EndLoadingIndicator();
        }

        if (!_pendingRefresh) return;

        _pendingRefresh = false;
        bool pendingFull = _pendingFull;
        _pendingFull = false;
        await RefreshAsync(pendingFull);
    }

    /// <summary>Segnala la fine di una autodiagnosi con le stesse preferenze degli altri avvisi.</summary>
    private void AnnunciaAutotest(SelfTestFinished esito)
    {
        string titolo = esito.Run.Aborted ? "Autodiagnosi interrotta"
                      : esito.Run.Passed == false ? "Autodiagnosi non superata"
                      : "Autodiagnosi completata";

        _notifier.Notify(AlertKind.SelfTest, $"{esito.Disk.Index}:{esito.Run.StartedAt.Ticks}",
            titolo, $"{esito.Disk.DisplayName}: {esito.Run.Outcome}",
            important: esito.Run.Passed == false);

        Announce($"{titolo} su {esito.Disk.DisplayName}: {esito.Run.Outcome}");
    }

    // ------------------------------------------------ indicatore di lettura

    /// <summary>Quanto resta visibile come minimo, una volta comparso.</summary>
    private const int LoadingMinimumMs = 450;

    /// <summary>
    /// Arma l'indicatore di lettura, ma <b>solo per una rienumerazione</b>.
    /// <para>
    /// È l'unico caso in cui ha senso coprire la pagina: l'elenco dei dischi può cambiare
    /// sotto, e mostrare le schede di prima mentre si cercano quelle nuove sarebbe
    /// fuorviante. Un aggiornamento periodico invece aggiorna i valori dove sono, senza
    /// toccare l'elenco: nasconderlo non protegge da niente.
    /// </para>
    /// <para>
    /// Prima l'indicatore compariva anche lì, dopo una breve attesa. Su questo PC il giro
    /// dura più di quella attesa — tre dischi da interrogare, di cui uno dietro un ponte
    /// USB, più i sensori di processore e scheda video che servono al riquadro — e con
    /// l'intervallo a un secondo la pagina passava più tempo coperta che scoperta. Quello
    /// che si vedeva era un rilevamento perpetuo.
    /// </para>
    /// </summary>
    private void BeginLoadingIndicator(bool full)
    {
        _statusSpinner.Active = true;

        if (!full) return;

        // Solo la rienumerazione si annuncia nella barra di stato: durante un giro
        // periodico il riepilogo di prima resta dov'è, e il segnalino accanto basta a
        // dire che si sta leggendo.
        _loadingMessage = "Rilevamento dei dischi collegati…";
        _loadingDetail = "Vengono interrogati tutti i dischi fisici, uno per uno.";
        _statusText.Text = _loadingMessage;

        ShowLoading();
    }

    private void EndLoadingIndicator()
    {
        _loadingDelay?.Stop();

        // Con una richiesta già in coda l'indicatore resta: nasconderlo per un istante,
        // fra una lettura e la successiva, sarebbe solo uno sfarfallio.
        if (_pendingRefresh || !_showLoading) return;

        int resta = LoadingMinimumMs - (int)(DateTime.UtcNow - _loadingShownAt).TotalMilliseconds;
        if (resta > 20) ScheduleHide(resta);
        else HideLoading();
    }

    private void ShowLoading()
    {
        if (_showLoading) return;

        _showLoading = true;
        _loadingShownAt = DateTime.UtcNow;
        ApplyDiskPageState();
    }

    private void HideLoading()
    {
        if (!_showLoading) return;

        _showLoading = false;
        ApplyDiskPageState();
    }

    /// <summary>
    /// Toglie l'indicatore fra un attimo: comparso e sparito nello stesso istante darebbe
    /// solo un lampo, e su una rienumerazione veloce non si capirebbe cosa è successo.
    /// </summary>
    private void ScheduleHide(int milliseconds)
    {
        _loadingDelay ??= new System.Windows.Forms.Timer();
        _loadingDelay.Tick -= OnLoadingTimerElapsed;
        _loadingDelay.Tick += OnLoadingTimerElapsed;
        _loadingDelay.Stop();
        _loadingDelay.Interval = Math.Max(1, milliseconds);
        _loadingDelay.Start();
    }

    private void OnLoadingTimerElapsed(object? sender, EventArgs e)
    {
        _loadingDelay?.Stop();

        // Comanda lo stato attuale: se nel frattempo è ripartita una lettura, l'indicatore
        // resta dov'è.
        if (!_busy) HideLoading();
    }

    // ------------------------------------------------------- scheda Sistema

    /// <summary>
    /// Vero se qualcosa fuori dalla scheda Sistema ha bisogno dei sensori: le icone in
    /// area di notifica di processore e scheda video, o il riquadro compatto.
    /// </summary>
    private bool NeedsSystemSensors =>
        _settings.TrayShowCpu || _settings.TrayShowGpu ||
        _settings.TrayShowCpuLoad || _settings.TrayShowGpuLoad || _settings.TrayShowMemory ||
        (_mini is not null && (_settings.MiniShowCpu || _settings.MiniShowCpuLoad ||
                               _settings.MiniShowCpuCores || _settings.MiniShowGpu ||
                               _settings.MiniShowGpuLoad || _settings.MiniShowGpuVram ||
                               _settings.MiniShowMemory)) ||
        _settings.TrayShowGpuVram ||
        (_overlay is not null && (_settings.OverlayShowCpu || _settings.OverlayShowCpuLoad ||
                                  _settings.OverlayShowGpu || _settings.OverlayShowGpuLoad ||
                                  _settings.OverlayShowGpuVram || _settings.OverlayShowMemory));

    /// <summary>
    /// Aggiorna temperature e carichi di processore e scheda video. La prima lettura
    /// carica la libreria dei sensori, perciò tutto avviene fuori dal thread
    /// dell'interfaccia.
    /// </summary>
    private async Task RefreshSystemAsync()
    {
        var snapshot = await Task.Run(_sensors.Read);
        _metrics.Apply(snapshot);

        if (_systemView is not null)
        {
            _systemView.Adapters = _sensors.Adapters;
            _systemView.Update(snapshot);
        }

        if (snapshot.Cpu is { } cpu && cpu.HottestC is float hottest)
        {
            bool critico = cpu.MarginC is float margin
                ? margin <= 5
                : hottest >= _settings.CpuCriticalTemp;

            if (critico)
            {
                string dettaglio = cpu.MarginC is float m
                    ? $"a {m:0} °C dal limite di {cpu.TjMaxC} °C"
                    : $"soglia critica {_settings.CpuCriticalTemp} °C";

                _notifier.Notify(AlertKind.Temperature, "cpu",
                    "Processore vicino al limite termico",
                    $"{cpu.Name}: {hottest:0} °C, {dettaglio}",
                    important: true);
            }
        }

        if (_metrics.GpuTemperature is int gpuTemp && gpuTemp >= _settings.GpuCriticalTemp)
        {
            _notifier.Notify(AlertKind.Temperature, "gpu",
                "Scheda video molto calda",
                $"{_metrics.PrimaryGpu?.Name}: {_settings.FormatTemp(gpuTemp)} " +
                $"(soglia critica {_settings.FormatTemp(_settings.GpuCriticalTemp)})",
                important: true);
        }
    }

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private void RebuildCards()
    {
        var existing = _cards.Controls.OfType<DiskCard>().ToList();

        // Se l'insieme dei dischi non è cambiato basta riagganciare i dati nuovi:
        // ricreare i controlli a ogni scansione farebbe sfarfallare la striscia.
        if (existing.Count == _disks.Count &&
            existing.Zip(_disks).All(p => p.First.Disk.Index == p.Second.Index))
        {
            foreach (var (card, disk) in existing.Zip(_disks)) card.Update(disk);
            HighlightSelection();
            AdjustCardsRowHeight();
            return;
        }

        _cards.SuspendLayout();
        foreach (Control c in existing) c.Dispose();
        _cards.Controls.Clear();

        foreach (var disk in _disks)
        {
            var card = new DiskCard(disk, _settings);
            card.Click += (s, _) => SelectDisk(((DiskCard)s!).Disk.Index);
            card.CopyRequested += (s, _) => CopyDiskSummary(((DiskCard)s!).Disk);
            _cards.Controls.Add(card);
        }
        _cards.ResumeLayout();

        if (_disks.Count > 0 && !_disks.Any(d => d.Index == _selectedIndex))
            _selectedIndex = _disks[0].Index;

        HighlightSelection();
        AdjustCardsRowHeight();
    }

    private void SelectDisk(int index)
    {
        _selectedIndex = index;
        HighlightSelection();
        UpdateDetails();
    }

    private void HighlightSelection()
    {
        foreach (var c in _cards.Controls.OfType<DiskCard>())
            c.Selected = c.Disk.Index == _selectedIndex;
    }

    private DiskInfo? Current => _disks.FirstOrDefault(d => d.Index == _selectedIndex);

    /// <summary>Descrizione di una riga dei dettagli, indipendente dai controlli.</summary>
    private readonly record struct DetailModel(
        string Key, string Value, bool Copyable = false, bool Mono = false, Color? Color = null);

    private List<DetailModel> BuildDetailModels(DiskInfo d)
    {
        var rows = new List<DetailModel>
        {
            new("Modello", Empty(d.Model)),
            new("Numero di serie", Empty(d.SerialNumber), Copyable: true, Mono: true),
            new("Firmware", Empty(d.Firmware), Mono: true),
            new("Interfaccia", d.TransferMode.Length > 0
                ? $"{d.InterfaceDisplay}  ·  {d.KindDisplay}  ·  {d.TransferMode}"
                : $"{d.InterfaceDisplay}  ·  {d.KindDisplay}"),
            new("Percorso dispositivo", d.DevicePath, Copyable: true, Mono: true),
            new("Capacità", d.SizeDisplay),
            // Sopra il 95% il decimale conta: "100%" su un disco non ancora pieno inganna.
            new("Spazio occupato", d.UsedFraction is double uf
                ? $"{(uf > 0.95 ? uf.ToString("P1") : uf.ToString("P0"))} di {DiskInfo.FormatBytes(d.VolumeTotalBytes)}"
                : "—",
                Color: d.UsedFraction is > 0.92 ? Theme.Bad : d.UsedFraction is > 0.8 ? Theme.Warn : null),
            new("Settore logico/fisico", d.LogicalSectorSize > 0
                ? $"{d.LogicalSectorSize} B / {d.PhysicalSectorSize} B" : "—"),
            new("Lettere di unità", d.LettersDisplay),
            new("Temperatura", FormatTempLine(d),
                Color: Theme.OnSurface(IconRenderer.ColorForTemp(d.TemperatureC, _settings))),
            new("Min / max sessione", $"{_settings.FormatTemp(d.SessionMinC)}  /  {_settings.FormatTemp(d.SessionMaxC)}"),
            new("Stato di salute", DiskCard.HealthText(d.Health), Color: DiskCard.HealthColor(d.Health)),
            new("Dettaglio salute", d.HealthDetail.Length > 0 ? d.HealthDetail : "—"),
            new("Vita residua", d.LifePercent is int lp ? $"{lp}%" : "—"),
            new("Ore di accensione", d.PowerOnHours is ulong h ? $"{h:N0} h" : "—"),
            new("Accensioni", d.PowerOnCount is ulong pc ? $"{pc:N0}" : "—"),
            new("Totale letto", d.HostReadsBytes is ulong r ? DiskInfo.FormatBytes(r) : "—"),
            new("Totale scritto", d.HostWritesBytes is ulong w ? DiskInfo.FormatBytes(w) : "—"),
            new("Origine dati", Empty(d.DataSource)),
        };

        if (d.ExtraSensors.Count > 0)
        {
            string sensors = string.Join("   ", d.ExtraSensors.Select(s => $"{s.Label}: {_settings.FormatTemp(s.Value)}"));
            int after = rows.FindIndex(r => r.Key == "Temperatura");
            rows.Insert(after < 0 ? rows.Count : after + 1, new DetailModel("Sensori aggiuntivi", sensors));
        }

        if (d.LastError is not null)
            rows.Add(new DetailModel("Note", d.LastError, Color: Theme.Warn));

        return rows;
    }

    private void UpdateDetails()
    {
        var d = Current;
        _chart.SetDisk(d);

        _detailsCard.Hint = d is null ? "" : $"Disco {d.Index}";
        _chartCard.Hint = d is null ? "" : _settings.FormatTemp(d.TemperatureC);

        if (d is null)
        {
            ClearDetailRows();
            _grid.Rows.Clear();
            _gridForDisk = -1;
            return;
        }

        var models = BuildDetailModels(d);

        // Se le righe sono le stesse di prima si aggiornano solo i valori cambiati:
        // niente Dispose/Add dei controlli, quindi niente sfarfallio.
        bool sameShape = _detailsForDisk == d.Index
                         && _detailRows.Count == models.Count
                         && models.All(m => _detailRows.ContainsKey(m.Key));

        if (sameShape)
        {
            foreach (var m in models)
            {
                var row = _detailRows[m.Key];
                if (row.Value != m.Value)
                {
                    row.Value = m.Value;
                    _tooltip.SetToolTip(row, $"{m.Key}: {m.Value}");
                }
                if (row.ValueColor != m.Color) { row.ValueColor = m.Color; row.Invalidate(); }
            }
        }
        else
        {
            RebuildDetailRows(models);
            _detailsForDisk = d.Index;
        }

        FillGrid(d);
    }

    private void ClearDetailRows()
    {
        foreach (var host in new[] { _detailsLeft, _detailsRight })
        {
            foreach (Control c in host.Controls.Cast<Control>().ToList()) c.Dispose();
            host.Controls.Clear();
        }
        _detailRows.Clear();
        _detailsForDisk = -1;
    }

    private void RebuildDetailRows(List<DetailModel> models)
    {
        _detailsHost.SuspendLayout();
        ClearDetailRows();

        // Prima metà a sinistra, seconda a destra. Con Dock=Top l'ordine di
        // inserimento è invertito rispetto a quello di lettura.
        int split = (models.Count + 1) / 2;
        Fill(_detailsLeft, models.Take(split));
        Fill(_detailsRight, models.Skip(split));

        _detailsHost.ResumeLayout();

        void Fill(Control host, IEnumerable<DetailModel> items)
        {
            var list = items.ToList();
            foreach (var m in Enumerable.Reverse(list))
            {
                var row = new DetailRow(m.Key, m.Value, m.Copyable)
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    KeyWidth = 128,
                    ValueFont = m.Mono ? Theme.Mono : null,
                    ValueColor = m.Color,
                };
                _detailRows[m.Key] = row;
                host.Controls.Add(row);
                // I valori lunghi vengono troncati: il testo completo resta nel tooltip.
                _tooltip.SetToolTip(row, m.Copyable
                    ? $"{m.Key}: {m.Value}\n(clic per copiare)"
                    : $"{m.Key}: {m.Value}");
            }
        }
    }

    private string FormatTempLine(DiskInfo d)
    {
        string s = _settings.FormatTemp(d.TemperatureC);
        if (d.TemperatureMaxC is int max) s += $"   max firmware {_settings.FormatTemp(max)}";
        if (d.WarningTemperatureC is int warn) s += $"   soglia {_settings.FormatTemp(warn)}";
        if (d.CriticalTemperatureC is int crit) s += $"   critica {_settings.FormatTemp(crit)}";
        return s;
    }

    private void FillGrid(DiskInfo d)
    {
        _smartCard.Hint = d.Attributes.Count > 0 ? $"{d.Attributes.Count} attributi · {d.DataSource}" : "";

        if (d.Attributes.Count == 0)
        {
            _grid.Rows.Clear();
            _grid.Rows.Add("", "", d.LastError ?? "Nessun attributo S.M.A.R.T. disponibile", "", "", "", "", "");
            _grid.ClearSelection();
            _gridForDisk = -1;
            return;
        }

        // Gli NVMe non hanno valori normalizzati: si nascondono le colonne vuote.
        bool normalized = d.Attributes.Any(a => a.Current != 0 || a.Threshold != 0);
        foreach (string name in new[] { "cur", "worst", "thr" })
            if (_grid.Columns[name]!.Visible != normalized) _grid.Columns[name]!.Visible = normalized;

        bool sameShape = _gridForDisk == d.Index && _grid.Rows.Count == d.Attributes.Count;

        if (!sameShape)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            for (int i = 0; i < d.Attributes.Count; i++) _grid.Rows.Add();
            _grid.ResumeLayout();
            _gridForDisk = d.Index;
        }

        for (int i = 0; i < d.Attributes.Count; i++)
        {
            var a = d.Attributes[i];
            var row = _grid.Rows[i];

            SetCell(row, "id", a.IdHex);
            SetCell(row, "name", a.Name);
            SetCell(row, "cur", a.Current == 0 ? "" : a.Current.ToString());
            SetCell(row, "worst", a.Worst == 0 ? "" : a.Worst.ToString());
            SetCell(row, "thr", a.Threshold == 0 ? "" : a.Threshold.ToString());
            SetCell(row, "raw", a.RawHex);
            SetCell(row, "dec", a.RawValue.ToString("N0"));

            if (!Equals(row.Tag, a.State))
            {
                row.Tag = a.State;
                row.Cells["state"].Value = "";
                row.Cells["raw"].Style.Font = Theme.Mono;
                row.Cells["raw"].Style.ForeColor = Theme.TextSecondary;

                if (a.State is HealthState.Bad or HealthState.Caution)
                {
                    var tint = a.State == HealthState.Bad ? Theme.Bad : Theme.Warn;
                    row.DefaultCellStyle.BackColor = Theme.Alpha(tint, Theme.IsDark ? 40 : 26);
                    row.DefaultCellStyle.SelectionBackColor = Theme.Alpha(tint, Theme.IsDark ? 70 : 50);
                }
                else
                {
                    // Colori espliciti invece di Color.Empty: lasciare lo stile "vuoto"
                    // mescolato alle righe alternate produce celle dipinte a metà.
                    row.DefaultCellStyle.BackColor = i % 2 == 0 ? Theme.Surface : Theme.SurfaceAlt;
                    row.DefaultCellStyle.SelectionBackColor = Theme.AccentSoft;
                }
            }
        }

        if (!sameShape) _grid.ClearSelection();

        SyncGridScroll();

        // La barra di scorrimento viene creata dal DataGridView dopo il popolamento:
        // il tema nativo va riapplicato qui, altrimenti resta chiara sul fondo scuro.
        Theme.ApplyNativeTheme(_grid);

        static void SetCell(DataGridViewRow row, string column, string value)
        {
            var cell = row.Cells[column];
            if (!Equals(cell.Value, value)) cell.Value = value;
        }
    }

    // ------------------------------------------------------------- comandi

    /// <summary>
    /// Quale era il filtro dei dispositivi muti quando si sono aperte le impostazioni:
    /// se cambia bisogna rienumerare i dischi, e con il pulsante Applica può cambiare
    /// più volte mentre la finestra resta aperta.
    /// </summary>
    private bool _usbNascostiPrima;

    private void OpenSettings()
    {
        _usbNascostiPrima = _settings.HideUsbWithoutData;

        // La combinazione va rilasciata mentre le impostazioni sono aperte: sono loro
        // a provare se è libera, e trovandola presa da noi stessi direbbero all'utente
        // che è occupata da un altro programma.
        UnregisterHotKey(Handle, OverlayHotkeyId);

        using var dlg = new SettingsForm(_settings, _disks);

        // Applica fa tutto quello che si fa alla conferma, tranne riprendersi la
        // combinazione: la finestra è ancora aperta e la sta ancora verificando.
        dlg.Applied += (_, _) => ApplicaImpostazioni(riprendiCombinazione: false);

        // Aperte dall'area di notifica, le impostazioni compaiono da sole: la finestra
        // principale resta dov'era. Senza una finestra visibile su cui centrarsi la
        // finestra andrebbe però dove stava l'ultima volta quella principale — anche
        // fuori schermo — quindi si centra sullo schermo e compare fra le applicazioni
        // della barra, altrimenti, finendo dietro a un gioco, non si saprebbe come
        // tornarci.
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.ShowInTaskbar = true;

            // In primo piano ci va messa a forza: Windows non lascia che un programma
            // che non ha il fuoco porti avanti una finestra, e senza questo le
            // impostazioni potrebbero aprirsi dietro al gioco a schermo intero.
            dlg.TopMost = true;
            dlg.Shown += (_, _) => { dlg.Activate(); dlg.BringToFront(); };
        }

        if (dlg.ShowDialog(this) != DialogResult.OK) { RegisterOverlayHotkey(); return; }

        ApplicaImpostazioni(riprendiCombinazione: true);
    }

    /// <summary>
    /// Rende effettive le impostazioni appena cambiate. Si arriva qui sia dalla
    /// conferma sia dal pulsante Applica, e deve valere lo stesso in tutti e due i
    /// casi: due percorsi diversi finirebbero per divergere alla prima aggiunta.
    /// </summary>
    private void ApplicaImpostazioni(bool riprendiCombinazione)
    {
        _timer.Interval = Math.Max(1, _settings.RefreshSeconds) * 1000;
        Theme.Apply(_settings.Theme);
        _tray.Invalidate();
        _settings.Save();

        // Il riquadro compatto può aver cambiato contenuto e quindi altezza: va
        // ridisegnato subito, senza aspettare il prossimo giro di letture.
        _mini?.Update(_disks, _metrics);
        _tray.Update(_disks, _metrics);
        ApplyOverlayState();

        if (riprendiCombinazione) RegisterOverlayHotkey();

        // Cambiare il filtro dei dispositivi muti richiede di rienumerare: quelli esclusi
        // non sono più nell'elenco, e una rilettura normale non li farebbe tornare.
        bool cambiato = _usbNascostiPrima != _settings.HideUsbWithoutData;
        _usbNascostiPrima = _settings.HideUsbWithoutData;
        _ = RefreshAsync(full: cambiato);
    }

    private void BuildExportMenu()
    {
        _exportMenu.Renderer = new ThemedMenuRenderer();
        _exportMenu.ShowImageMargin = false;
    }

    /// <summary>
    /// Ricostruisce le voci solo se l'elenco dei dischi è cambiato, e solo al momento
    /// di aprire il menu: toccarlo mentre WinForms lo sta elaborando lo manderebbe in
    /// eccezione.
    /// </summary>
    private void EnsureExportMenu()
    {
        _exportMenu.Font = Theme.Body;
        _exportMenu.BackColor = Theme.SurfaceAlt;
        _exportMenu.ForeColor = Theme.TextPrimary;

        string key = string.Join("|", _disks.Select(d => $"{d.Index}:{d.SerialNumber}:{d.Model}"));
        if (key == _exportMenuKey && _exportMenu.Items.Count > 0) return;

        foreach (ToolStripItem old in _exportMenu.Items) old.Dispose();
        _exportMenu.Items.Clear();

        var summary = new ToolStripMenuItem("Riepilogo")
        {
            ToolTipText = "Solo i dati principali, senza informazioni sul computer",
            Enabled = _disks.Count > 0,
        };

        var all = new ToolStripMenuItem("Tutti i dischi…");
        all.Click += (_, _) => ExportSummary(null);
        summary.DropDownItems.Add(all);

        if (_disks.Count > 0) summary.DropDownItems.Add(new ToolStripSeparator());

        foreach (var disk in _disks)
        {
            var d = disk;
            string label = d.Model.Length > 0 ? d.Model : $"Disco {d.Index}";
            var item = new ToolStripMenuItem($"{label}  ({d.SizeDisplay})…")
            {
                ToolTipText = d.DriveLetters.Count > 0
                    ? $"Disco {d.Index} · {d.LettersDisplay}"
                    : $"Disco {d.Index}",
            };
            item.Click += (_, _) => ExportSummary(d);
            summary.DropDownItems.Add(item);
        }

        // Il sottomenu eredita renderer e colori dal menu che lo ospita solo in parte.
        summary.DropDown.Renderer = new ThemedMenuRenderer();
        summary.DropDown.BackColor = Theme.SurfaceAlt;
        summary.DropDown.ForeColor = Theme.TextPrimary;
        summary.DropDown.Font = Theme.Body;
        if (summary.DropDown is ToolStripDropDownMenu dm) dm.ShowImageMargin = false;

        var full = new ToolStripMenuItem("Rapporto completo…")
        {
            ToolTipText = "Tutti i dati, contesto del computer e tabella degli attributi",
            Enabled = _disks.Count > 0,
        };
        full.Click += (_, _) => ExportFullReport();

        _exportMenu.Items.AddRange([summary, new ToolStripSeparator(), full]);
        _exportMenuKey = key;
    }

    private void ShowExportMenu(Control anchor)
    {
        EnsureExportMenu();
        _exportMenu.Show(anchor, new Point(0, anchor.Height + 4));
    }

    /// <summary>
    /// Copia negli appunti lo stesso testo che finirebbe nel riepilogo esportato.
    /// </summary>
    private void CopyDiskSummary(DiskInfo disk)
    {
        try
        {
            string text = Report.BuildSummary([disk], _settings);
            if (text.Length == 0) return;

            Clipboard.SetText(text);
            Announce($"Riepilogo di {disk.DisplayName} copiato negli appunti.");
        }
        catch (Exception ex)
        {
            // Gli appunti possono essere momentaneamente occupati da un'altra app.
            Announce($"Impossibile copiare negli appunti: {ex.Message}");
        }
    }

    /// <param name="disk">Il disco da esportare, oppure null per tutti quanti.</param>
    private void ExportSummary(DiskInfo? disk)
    {
        var target = disk is null ? _disks : (IReadOnlyList<DiskInfo>)[disk];

        string suggested = disk is null
            ? $"dischi_riepilogo_{DateTime.Now:yyyyMMdd_HHmm}.txt"
            : $"riepilogo_{SafeFileName(disk.Model.Length > 0 ? disk.Model : $"disco{disk.Index}")}_{DateTime.Now:yyyyMMdd_HHmm}.txt";

        SaveReport(
            disk is null ? "Esporta riepilogo di tutti i dischi" : "Esporta riepilogo del disco",
            suggested,
            () => Report.BuildSummary(target, _settings));
    }

    private void ExportFullReport() =>
        SaveReport("Esporta rapporto completo",
                   $"dischi_completo_{DateTime.Now:yyyyMMdd_HHmm}.txt",
                   () => Report.Build(_disks, _settings));

    private void SaveReport(string title, string suggestedName, Func<string> build)
    {
        using var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = "File di testo (*.txt)|*.txt",
            FileName = suggestedName,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, build());
            Announce($"Rapporto salvato in {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossibile salvare il rapporto:\n{ex.Message}",
                "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Rende un modello utilizzabile come nome di file.</summary>
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
        return cleaned.Trim().Replace(' ', '_');
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        if (_settings.TrayMode == TrayMode.None)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }
        Hide();
        ShowInTaskbar = false;
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    // -------------------------------------------------------------- eventi form

    /// <summary>
    /// La combinazione globale e la sovrimpressione vogliono una finestra vera a cui
    /// agganciarsi: prima che il gestore esista non si può fare né l'una né l'altra.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterOverlayHotkey();
        ApplyOverlayState();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5:
                _ = RefreshAsync(full: false);      // rilegge temperature e S.M.A.R.T.
                e.Handled = true;
                break;

            case Keys.F6:
                _ = RefreshAsync(full: true);       // rienumera i dischi collegati
                e.Handled = true;
                break;

            case Keys.F8:
                ToggleMiniWindow();
                e.Handled = true;
                break;

            case Keys.F9:
                ToggleSidebar();
                e.Handled = true;
                break;

            case Keys.F2:
                CycleTheme();
                e.Handled = true;
                break;

            case Keys.F3 when _exportButton is not null:
                ShowExportMenu(_exportButton);
                e.Handled = true;
                break;

            case Keys.F4:
                OpenSettings();
                e.Handled = true;
                break;

            case Keys.Escape when _settings.TrayMode != TrayMode.None:
                HideToTray();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray && _settings.TrayMode != TrayMode.None)
            HideToTray();
        else if (IsHandleCreated)
            AdjustCardsRowHeight();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing &&
            _settings.CloseToTray && _settings.TrayMode != TrayMode.None)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        Theme.Changed -= OnThemeChanged;
        _showWait?.Unregister(null);
        UnregisterHotKey(Handle, OverlayHotkeyId);
        StopOverlay();
        _mini?.Dispose();
        _exportMenu.Dispose();
        _timer.Stop();
        _tray.Dispose();
        _sensors.Dispose();
        _deviceChange?.Dispose();
        _loadingDelay?.Dispose();
        _tooltip.Dispose();
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _tray.SetDpi(e.DeviceDpiNew);
        _tray.Update(_disks, _metrics);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_DEVICECHANGE)
        {
            int evt = m.WParam.ToInt32();
            if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) ScheduleDeviceRescan();
        }
        else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == OverlayHotkeyId)
        {
            ToggleOverlay();
        }
    }

    /// <summary>
    /// Collegare un disco genera una raffica di notifiche: si usa un solo timer, che
    /// riparte a ogni messaggio, così la rienumerazione avviene una volta sola e quando
    /// Windows ha finito di montare i volumi.
    /// </summary>
    private void ScheduleDeviceRescan()
    {
        _deviceChange ??= new System.Windows.Forms.Timer { Interval = 2000 };
        _deviceChange.Tick -= OnDeviceRescanElapsed;
        _deviceChange.Tick += OnDeviceRescanElapsed;
        _deviceChange.Stop();
        _deviceChange.Start();
    }

    private async void OnDeviceRescanElapsed(object? sender, EventArgs e)
    {
        _deviceChange?.Stop();
        await RefreshAsync(full: true);
    }

    private static string Empty(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}
