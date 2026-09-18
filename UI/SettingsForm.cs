using System.Reflection;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

public sealed class SettingsForm : Form
{
    private const int RowHeight = 32;

    private readonly AppSettings _target;
    private readonly AppSettings _work;
    private readonly ThemeMode _originalTheme;

    private readonly ThemedSpin _refresh = new(1, 3600);
    private readonly ThemedCombo _theme = new();
    private readonly ThemedCombo _trayMode = new();
    private readonly ThemedCombo _unit = new();
    private readonly ThemedCheckBox _hideMuteUsb = new("Nascondi quelli senza temperatura né dati");
    private readonly ThemedCombo _fontFamily = new();
    private readonly ThemedCheckBox _bold = new("Grassetto");
    private readonly ThemedSpin _fontOffset = new(-4, 6);
    private readonly ThemedCheckBox _transparent = new("Trasparente");
    private readonly Button _backColor = new();
    private readonly ThemedCheckBox _showIndex = new("Mostra la lettera di unità sotto il valore");

    private readonly ThemedCheckBox _trayDisks = new("Dischi");
    private readonly List<(DiskInfo Disk, ThemedCheckBox Box)> _trayDiskBoxes = [];
    private readonly ThemedCheckBox _trayCpu = new("Processore");
    private readonly ThemedCheckBox _trayGpu = new("Scheda video");
    private readonly ThemedCheckBox _trayCpuLoad = new("CPU");
    private readonly ThemedCheckBox _trayGpuLoad = new("GPU");
    private readonly ThemedCheckBox _trayGpuVram = new("VRAM");
    private readonly ThemedCheckBox _trayMemory = new("RAM");
    private readonly ThemedCombo _trayCpuMode = new();

    private readonly ThemedSpin _cpuWarn = new(30, 120);
    private readonly ThemedSpin _cpuCrit = new(31, 130);
    private readonly ThemedSpin _gpuWarn = new(30, 120);
    private readonly ThemedSpin _gpuCrit = new(31, 130);

    private readonly ThemedCheckBox _miniDisks = new("Dischi");
    private readonly ThemedCheckBox _miniCpu = new("Temperatura");
    private readonly ThemedCheckBox _miniCpuLoad = new("Carico (CPU %)");
    private readonly ThemedCheckBox _miniCores = new("Griglia dei core");
    private readonly ThemedCheckBox _miniGpu = new("Temperatura");
    private readonly ThemedCheckBox _miniGpuLoad = new("Carico (GPU %)");
    private readonly ThemedCheckBox _miniGpuVram = new("VRAM %");
    private readonly ThemedCheckBox _miniMemory = new("Percentuale occupata (RAM %)");
    private readonly ThemedCombo _miniCpuMode = new();

    private readonly ThemedCheckBox _overlayOn = new("Attiva");
    private readonly ThemedCheckBox _overlayOnlyGames = new("Solo quando si leggono gli FPS");
    private readonly ThemedCheckBox _overlayFps = new("FPS");
    private readonly ThemedCheckBox _overlayDisks = new("Dischi");
    private readonly ThemedCheckBox _overlayCpu = new("CPU°");
    private readonly ThemedCheckBox _overlayGpu = new("GPU°");
    private readonly ThemedCheckBox _overlayCpuLoad = new("CPU");
    private readonly ThemedCheckBox _overlayGpuLoad = new("GPU");
    private readonly ThemedCheckBox _overlayVram = new("VRAM");
    private readonly ThemedCheckBox _overlayMemory = new("RAM");
    private readonly ThemedCombo _overlayCorner = new();
    private readonly ThemedCombo _overlayLayout = new();
    private readonly ThemedSpin _overlayOffsetX = new(-4000, 4000);
    private readonly ThemedSpin _overlayOffsetY = new(-4000, 4000);
    private readonly HotkeyBox _overlayHotkey = new();
    private readonly Label _overlayHotkeyEsito = new();
    private readonly ThemedSpin _overlayOpacity = new(10, 100);
    private readonly ThemedSpin _overlayScale = new(60, 250);

    private readonly ThemedSpin _warnTemp = new(20, 110);
    private readonly ThemedSpin _critTemp = new(21, 120);
    private readonly Button _colorNormal = new();
    private readonly Button _colorWarn = new();
    private readonly Button _colorCritical = new();
    private readonly ThemedCheckBox _notify = new("Notifica al superamento della soglia critica");

    private readonly ThemedCheckBox _notificationsOn = new("Mostra le notifiche");
    private readonly ThemedCheckBox _nativeToasts = new("Usa le notifiche di Windows (altrimenti fumetti)");
    private readonly ThemedCheckBox _notifyLowSpace = new("Avvisa quando lo spazio libero si sta esaurendo");
    private readonly ThemedCheckBox _notifyHealth = new("Avvisa se lo stato di salute peggiora");
    private readonly ThemedCheckBox _notifySelfTest = new("Avvisa al termine di un autotest");
    private readonly ThemedSpin _lowSpace = new(50, 99);
    private readonly ThemedSpin _cooldown = new(1, 1440);
    private readonly Button _testNotification = new();

    private readonly ThemedCheckBox _startMinimized = new("Parti ridotto in area di notifica");
    private readonly ThemedCheckBox _minimizeToTray = new("Riduci a icona nell'area di notifica");
    private readonly ThemedCheckBox _closeToTray = new("Alla chiusura resta in area di notifica");
    private readonly ThemedCheckBox _startWithWindows = new("Avvia automaticamente con Windows");

    private readonly Panel _preview = new();
    private readonly ToolTip _tips = new();
    private readonly List<Control> _themed = [];
    private FlowLayoutPanel? _root;
    private bool _loading;

    private readonly IReadOnlyList<DiskInfo> _disks;

    /// <summary>
    /// Le impostazioni sono state applicate senza chiudere la finestra. Chi ci ascolta
    /// deve fare quello che farebbe alla conferma: rileggere l'intervallo, ridisegnare
    /// le icone, accendere o spegnere la sovrimpressione.
    /// </summary>
    public event EventHandler? Applied;

    public SettingsForm(AppSettings settings, IReadOnlyList<DiskInfo>? disks = null)
    {
        _disks = disks ?? [];
        _target = settings;
        _work = settings.Clone();
        _originalTheme = Theme.Mode;

        BuildUi();
        LoadValues();
        ApplyThemeColors();
        Theme.Changed += OnThemeChanged;
    }

    private void BuildUi()
    {
        Text = "Impostazioni";
        Icon = IconRenderer.CreateAppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(608, 770);
        Font = Theme.Body;

        var root = _root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 16, 16, 8),
        };

        // ------------------------------------------------ aggiornamento e aspetto
        var cGeneral = Card("Aggiornamento e aspetto", 4);
        var tGeneral = Rows(cGeneral, 4);

        _refresh.ValueChanged += (_, _) => { if (!_loading) _work.RefreshSeconds = _refresh.Value; };
        AddRow(tGeneral, 0, "Intervallo di aggiornamento", Wrap(_refresh, Hint("secondi · da 1 a 3600")));

        _theme.Width = 250;
        _theme.SetItems(["Automatico (segue Windows)", "Chiaro", "Scuro"]);
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.Theme = (ThemeMode)_theme.SelectedIndex;
            Theme.Apply(_work.Theme);              // anteprima immediata
        };
        AddRow(tGeneral, 1, "Tema", _theme);

        _unit.Width = 250;
        _unit.SetItems(["Celsius (°C)", "Fahrenheit (°F)"]);
        _unit.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.Unit = (TempUnit)_unit.SelectedIndex;
            UpdatePreview();
        };
        AddRow(tGeneral, 2, "Unità di misura", _unit);

        _hideMuteUsb.AutoWidth();
        _hideMuteUsb.Margin = new Padding(0, 3, 0, 0);
        _hideMuteUsb.CheckedChanged += (_, _) => { if (!_loading) _work.HideUsbWithoutData = _hideMuteUsb.Checked; };
        _tips.SetToolTip(_hideMuteUsb,
            "Chiavette e lettori di schede non hanno sensore di temperatura né S.M.A.R.T.:\n" +
            "nell'elenco comparirebbero come una riga di trattini.");
        AddRow(tGeneral, 3, "Dispositivi USB", _hideMuteUsb);

        // ------------------------------------------------------ area di notifica
        // La riga con l'elenco dei dischi può occupare più di una riga standard se i
        // dischi sono molti: si costruisce prima, si chiede quanto spazio vuole davvero
        // una volta mandato a capo, e la scheda cresce di conseguenza. Contare i dischi
        // non basterebbe: "C: E:" è largo il doppio di "D:".
        var sceltaDischi = BuildDiskChoice();
        int righeDischi = Math.Max(1, (sceltaDischi.PreferredSize.Height + RowHeight - 1) / RowHeight);

        var cTray = Card("Area di notifica", 8 + righeDischi);
        var tTray = Rows(cTray, 9);
        tTray.RowStyles[2] = new RowStyle(SizeType.Absolute, RowHeight * righeDischi);

        _trayMode.Width = 290;
        _trayMode.SetItems(["Una icona per ogni disco", "Una sola icona (disco più caldo)", "Nessuna icona"]);
        _trayMode.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.TrayMode = (TrayMode)_trayMode.SelectedIndex;
            UpdateDiskChoiceState();
            UpdatePreview();
        };
        AddRow(tTray, 0, "Modalità dei dischi", _trayMode);

        foreach (var (box, apply) in new (ThemedCheckBox Box, Action<bool> Apply)[]
        {
            (_trayDisks, v => _work.TrayShowDisks = v),
            (_trayCpu, v => _work.TrayShowCpu = v),
            (_trayGpu, v => _work.TrayShowGpu = v),
            (_trayCpuLoad, v => _work.TrayShowCpuLoad = v),
            (_trayGpuLoad, v => _work.TrayShowGpuLoad = v),
            (_trayGpuVram, v => _work.TrayShowGpuVram = v),
            (_trayMemory, v => _work.TrayShowMemory = v),
        })
        {
            box.AutoWidth();
            box.Margin = new Padding(0, 3, 12, 0);
            box.CheckedChanged += (_, _) => { if (!_loading) apply(box.Checked); };
        }
        _tips.SetToolTip(_trayCpu, "Aggiunge un'icona con la temperatura del processore");
        _tips.SetToolTip(_trayGpu, "Aggiunge un'icona con la temperatura della scheda video, se i sensori ci sono");
        _tips.SetToolTip(_trayGpuVram, "Quanta memoria della scheda video risulta occupata");
        AddRow(tTray, 1, "Temperature", Wrap(_trayDisks, _trayCpu, _trayGpu));
        AddRow(tTray, 2, "Quali dischi", sceltaDischi);
        _trayDisks.CheckedChanged += (_, _) => UpdateDiskChoiceState();
        AddRow(tTray, 3, "Percentuali", Wrap(_trayCpuLoad, _trayGpuLoad, _trayGpuVram, _trayMemory));

        _trayCpuMode.Width = 290;
        _trayCpuMode.SetItems(["Core più caldo", "Media dei core", "Package"]);
        _trayCpuMode.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.TrayCpuMode = (CpuTempMode)_trayCpuMode.SelectedIndex;
        };
        AddRow(tTray, 4, "Temperatura del processore", _trayCpuMode);

        _fontFamily.Width = 290;
        _fontFamily.SetItems(FontFamily.Families.Select(f => f.Name).Distinct().OrderBy(n => n));
        _fontFamily.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.IconFontFamily = _fontFamily.SelectedText;
            UpdatePreview();
        };
        AddRow(tTray, 5, "Carattere dell'icona", _fontFamily);

        _fontOffset.Width = 76;
        _fontOffset.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _work.IconFontSizeOffset = _fontOffset.Value;
            UpdatePreview();
        };
        _bold.AutoWidth();
        _bold.Margin = new Padding(14, 3, 0, 0);
        _bold.CheckedChanged += (_, _) => { if (!_loading) { _work.IconBold = _bold.Checked; UpdatePreview(); } };
        AddRow(tTray, 6, "Dimensione testo", Wrap(_fontOffset, _bold));

        _transparent.AutoWidth();
        _transparent.Margin = new Padding(0, 3, 0, 0);
        _transparent.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _work.IconTransparentBackground = _transparent.Checked;
            _backColor.Visible = !_transparent.Checked;
            UpdatePreview();
        };
        SetupColorButton(_backColor, () => _work.IconBackgroundColor, v => _work.IconBackgroundColor = v,
                         "Colore di sfondo dell'icona");
        AddRow(tTray, 7, "Sfondo dell'icona", Wrap(_transparent, _backColor));

        _showIndex.AutoWidth();
        _showIndex.Margin = new Padding(0, 3, 0, 0);
        _showIndex.CheckedChanged += (_, _) => { if (!_loading) { _work.ShowDiskIndexOnIcon = _showIndex.Checked; UpdatePreview(); } };
        _tips.SetToolTip(_showIndex,
            "Vale per i dischi, e mostra la lettera di unità con i due punti (C:).\n" +
            "Le icone di sistema portano sempre la loro sigla (C\u00B0, G\u00B0, C%, G%,\n" +
            "V%, R%): senza, due numeri uguali sarebbero indistinguibili.");
        AddRow(tTray, 8, "Sigla dei dischi", _showIndex);

        // ------------------------------------------------------------ anteprima
        var cPreview = Card("Anteprima delle icone", 0, 132);
        _preview.Dock = DockStyle.Fill;
        _preview.Paint += PreviewPaint;
        cPreview.Controls.Add(_preview);
        _themed.Add(_preview);

        // --------------------------------------------------------- soglie e colori
        var cThresholds = Card("Soglie e colori", 3);
        var tThr = Rows(cThresholds, 3);

        _warnTemp.Width = 76;
        _warnTemp.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _work.WarnTemp = _warnTemp.Value;
            if (_critTemp.Value <= _warnTemp.Value) _critTemp.Value = _warnTemp.Value + 1;
            UpdatePreview();
        };
        _critTemp.Width = 76;
        _critTemp.Margin = new Padding(10, 0, 0, 0);
        _critTemp.ValueChanged += (_, _) => { if (!_loading) { _work.CriticalTemp = _critTemp.Value; UpdatePreview(); } };
        AddRow(tThr, 0, "Attenzione / critica (°C)", Wrap(_warnTemp, _critTemp));

        SetupColorButton(_colorNormal, () => _work.ColorNormal, v => _work.ColorNormal = v, "Temperatura normale");
        SetupColorButton(_colorWarn, () => _work.ColorWarn, v => _work.ColorWarn = v, "Soglia di attenzione");
        SetupColorButton(_colorCritical, () => _work.ColorCritical, v => _work.ColorCritical = v, "Soglia critica");
        AddRow(tThr, 1, "Colori", Wrap(_colorNormal, _colorWarn, _colorCritical,
            Hint("normale · attenzione · critico")));

        _notify.AutoWidth();
        _notify.Margin = new Padding(0, 3, 0, 0);
        _notify.CheckedChanged += (_, _) => { if (!_loading) _work.NotifyOnCritical = _notify.Checked; };
        AddRow(tThr, 2, "Notifiche", _notify);

        // ------------------------------------- soglie di processore e scheda video
        var cSystem = Card("Soglie di processore e scheda video", 2);
        var tSystem = Rows(cSystem, 2);

        _cpuWarn.Width = 76;
        _cpuCrit.Width = 76;
        _cpuCrit.Margin = new Padding(10, 0, 0, 0);
        _cpuWarn.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _work.CpuWarnTemp = _cpuWarn.Value;
            if (_cpuCrit.Value <= _cpuWarn.Value) _cpuCrit.Value = _cpuWarn.Value + 1;
        };
        _cpuCrit.ValueChanged += (_, _) => { if (!_loading) _work.CpuCriticalTemp = _cpuCrit.Value; };
        AddRow(tSystem, 0, "Processore (°C)", Wrap(_cpuWarn, _cpuCrit,
            Hint("attenzione · critica · usate quando il limite termico non è noto")));

        _gpuWarn.Width = 76;
        _gpuCrit.Width = 76;
        _gpuCrit.Margin = new Padding(10, 0, 0, 0);
        _gpuWarn.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _work.GpuWarnTemp = _gpuWarn.Value;
            if (_gpuCrit.Value <= _gpuWarn.Value) _gpuCrit.Value = _gpuWarn.Value + 1;
        };
        _gpuCrit.ValueChanged += (_, _) => { if (!_loading) _work.GpuCriticalTemp = _gpuCrit.Value; };
        AddRow(tSystem, 1, "Scheda video (°C)", Wrap(_gpuWarn, _gpuCrit, Hint("attenzione · critica")));

        // ----------------------------------------------------- riquadro compatto
        var cMini = Card("Riquadro compatto", 6);
        var tMini = Rows(cMini, 6);

        foreach (var (box, apply) in new (ThemedCheckBox Box, Action<bool> Apply)[]
        {
            (_miniDisks, v => _work.MiniShowDisks = v),
            (_miniCpu, v => _work.MiniShowCpu = v),
            (_miniCpuLoad, v => _work.MiniShowCpuLoad = v),
            (_miniCores, v => _work.MiniShowCpuCores = v),
            (_miniGpu, v => _work.MiniShowGpu = v),
            (_miniGpuLoad, v => _work.MiniShowGpuLoad = v),
            (_miniGpuVram, v => _work.MiniShowGpuVram = v),
            (_miniMemory, v => _work.MiniShowMemory = v),
        })
        {
            box.AutoWidth();
            box.Margin = new Padding(0, 3, 12, 0);
            box.CheckedChanged += (_, _) => { if (!_loading) apply(box.Checked); };
        }

        // Tre caselle su una riga sola non ci stanno: l'ultima veniva troncata a metà
        // parola. La griglia dei core sta perciò su una riga sua.
        AddRow(tMini, 0, "Dischi", _miniDisks);
        AddRow(tMini, 1, "Processore", Wrap(_miniCpu, _miniCpuLoad));
        AddRow(tMini, 2, "", _miniCores);
        AddRow(tMini, 3, "Scheda video", Wrap(_miniGpu, _miniGpuLoad, _miniGpuVram));
        AddRow(tMini, 4, "Memoria", _miniMemory);

        _miniCpuMode.Width = 250;
        _miniCpuMode.SetItems(["Core più caldo", "Media dei core", "Package"]);
        _miniCpuMode.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.MiniCpuMode = (CpuTempMode)_miniCpuMode.SelectedIndex;
        };
        AddRow(tMini, 5, "Temperatura del processore", _miniCpuMode);

        // -------------------------------------------------- sovrimpressione di gioco
        var cOverlay = Card("Sovrimpressione di gioco", 9);
        var tOver = Rows(cOverlay, 9);

        foreach (var (box, apply) in new (ThemedCheckBox Box, Action<bool> Apply)[]
        {
            (_overlayOn, v => _work.OverlayEnabled = v),
            (_overlayOnlyGames, v => _work.OverlayOnlyInGames = v),
            (_overlayFps, v => _work.OverlayShowFps = v),
            (_overlayDisks, v => _work.OverlayShowDisks = v),
            (_overlayCpu, v => _work.OverlayShowCpu = v),
            (_overlayGpu, v => _work.OverlayShowGpu = v),
            (_overlayCpuLoad, v => _work.OverlayShowCpuLoad = v),
            (_overlayGpuLoad, v => _work.OverlayShowGpuLoad = v),
            (_overlayVram, v => _work.OverlayShowGpuVram = v),
            (_overlayMemory, v => _work.OverlayShowMemory = v),
        })
        {
            box.AutoWidth();
            box.Margin = new Padding(0, 3, 12, 0);
            box.CheckedChanged += (_, _) => { if (!_loading) apply(box.Checked); };
        }

        _tips.SetToolTip(_overlayOn,
            "Una targhetta sopra al gioco con FPS, temperature e carichi.\n" +
            "Funziona sui giochi a finestra e a finestra senza bordi; in schermo\n" +
            "intero esclusivo Windows non lascia comparire nessuna finestra.");
        _tips.SetToolTip(_overlayOnlyGames,
            "Compare da sola quando in primo piano c'è un programma che disegna\n" +
            "fotogrammi, e sparisce quando torni al desktop.");

        AddRow(tOver, 0, "Sovrimpressione", _overlayOn);
        AddRow(tOver, 1, "Quando", _overlayOnlyGames);
        AddRow(tOver, 2, "Temperature", Wrap(_overlayFps, _overlayCpu, _overlayGpu, _overlayDisks));
        AddRow(tOver, 3, "Percentuali", Wrap(_overlayCpuLoad, _overlayGpuLoad, _overlayVram, _overlayMemory));
        _overlayLayout.Width = 250;
        _overlayLayout.SetItems(["Verticale (un valore per riga)", "Orizzontale (tre per riga)"]);
        _overlayLayout.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.OverlayLayout = (OverlayLayout)_overlayLayout.SelectedIndex;
        };
        AddRow(tOver, 4, "Disposizione", _overlayLayout);

        _overlayCorner.Width = 206;
        _overlayCorner.SetItems(["In alto a sinistra", "In alto a destra",
                                 "In basso a sinistra", "In basso a destra"]);
        _overlayCorner.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _work.OverlayCorner = (OverlayCorner)_overlayCorner.SelectedIndex;
        };

        var posiziona = MakeButton("Posiziona...", primary: false);
        posiziona.Width = 118;
        posiziona.Margin = new Padding(10, 0, 0, 0);
        posiziona.Click += (_, _) =>
        {
            using var dlg = new OverlayPlacementForm(_work, _disks);
            dlg.ShowDialog(this);
            LoadValues();          // angolo, scostamento e disposizione possono essere cambiati lì
        };
        _tips.SetToolTip(posiziona,
            "Apre l'anteprima: lo schermo in miniatura con la targhetta al suo posto,\n" +
            "da trascinare dove la vuoi, e come si vedrà a grandezza naturale.");

        AddRow(tOver, 5, "Posizione", Wrap(_overlayCorner, posiziona));

        _overlayOpacity.Width = 76;
        _overlayOpacity.ValueChanged += (_, _) => { if (!_loading) _work.OverlayOpacityPercent = _overlayOpacity.Value; };
        _overlayScale.Width = 76;
        _overlayScale.Margin = new Padding(10, 0, 0, 0);
        _overlayScale.ValueChanged += (_, _) => { if (!_loading) _work.OverlayScalePercent = _overlayScale.Value; };
        _overlayOffsetX.Width = 86;
        _overlayOffsetX.ValueChanged += (_, _) => { if (!_loading) _work.OverlayOffsetX = _overlayOffsetX.Value; };
        _overlayOffsetY.Width = 86;
        _overlayOffsetY.Margin = new Padding(10, 0, 0, 0);
        _overlayOffsetY.ValueChanged += (_, _) => { if (!_loading) _work.OverlayOffsetY = _overlayOffsetY.Value; };
        _tips.SetToolTip(_overlayOffsetX,
            "Sposta la targhetta rispetto all'angolo scelto, in pixel.\n" +
            "Positivo verso destra e verso il basso, negativo al contrario.");
        AddRow(tOver, 6, "Scostamento (px)", Wrap(_overlayOffsetX, _overlayOffsetY,
            Hint("orizzontale · verticale")));

        AddRow(tOver, 7, "Sfondo / dimensione (%)", Wrap(_overlayOpacity, _overlayScale,
            Hint("coprente · testo")));

        _overlayHotkey.Width = 158;
        _overlayHotkey.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _work.OverlayHotkey = _overlayHotkey.Value;
            VerificaCombinazione();
        };
        _tips.SetToolTip(_overlayHotkey,
            "Fai clic e premi la combinazione che vuoi: serve almeno un tasto fra\n" +
            "Ctrl, Alt e Maiusc. Esc annulla, Canc la toglie.");

        _overlayHotkeyEsito.AutoSize = true;
        _overlayHotkeyEsito.Margin = new Padding(12, 8, 0, 0);
        _overlayHotkeyEsito.BackColor = Color.Transparent;
        _overlayHotkeyEsito.Tag = "hint";

        AddRow(tOver, 8, "Accendi e spegni con", Wrap(_overlayHotkey, _overlayHotkeyEsito));

        // ------------------------------------------------------------- notifiche
        var cNotify = Card("Notifiche", 7);
        var tNot = Rows(cNotify, 7);

        _notificationsOn.AutoWidth();
        _notificationsOn.Margin = new Padding(0, 3, 0, 0);
        _notificationsOn.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _work.NotificationsEnabled = _notificationsOn.Checked;
            ApplyNotificationsEnabled();
        };
        _tips.SetToolTip(_notificationsOn,
            "Interruttore generale: da spento non parte nessun avviso, di nessun tipo.");
        AddRow(tNot, 0, "Avvisi", _notificationsOn);

        _nativeToasts.AutoWidth();
        _nativeToasts.Margin = new Padding(0, 3, 0, 0);
        _nativeToasts.CheckedChanged += (_, _) => { if (!_loading) _work.UseNativeNotifications = _nativeToasts.Checked; };
        AddRow(tNot, 1, "Tipo", _nativeToasts);

        _notifyLowSpace.AutoWidth();
        _notifyLowSpace.Margin = new Padding(0, 3, 0, 0);
        _notifyLowSpace.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _work.NotifyOnLowSpace = _notifyLowSpace.Checked;
            ApplyNotificationsEnabled();
        };
        AddRow(tNot, 2, "Spazio disco", _notifyLowSpace);

        _lowSpace.Width = 76;
        _lowSpace.ValueChanged += (_, _) => { if (!_loading) _work.LowSpaceThresholdPercent = _lowSpace.Value; };
        AddRow(tNot, 3, "Avvisa oltre il", Wrap(_lowSpace, Hint("% di spazio occupato")));

        _notifyHealth.AutoWidth();
        _notifyHealth.Margin = new Padding(0, 3, 0, 0);
        _notifyHealth.CheckedChanged += (_, _) => { if (!_loading) _work.NotifyOnHealthChange = _notifyHealth.Checked; };
        AddRow(tNot, 4, "Salute", _notifyHealth);

        _notifySelfTest.AutoWidth();
        _notifySelfTest.Margin = new Padding(0, 3, 0, 0);
        _notifySelfTest.CheckedChanged += (_, _) => { if (!_loading) _work.NotifyOnSelfTest = _notifySelfTest.Checked; };
        AddRow(tNot, 5, "Autotest", _notifySelfTest);

        _cooldown.Width = 76;
        _cooldown.ValueChanged += (_, _) => { if (!_loading) _work.NotificationCooldownMinutes = _cooldown.Value; };

        _testNotification.Text = "Prova";
        _testNotification.Width = 88;
        _testNotification.Height = 26;
        _testNotification.FlatStyle = FlatStyle.Flat;
        _testNotification.Cursor = Cursors.Hand;
        _testNotification.Margin = new Padding(14, 3, 0, 0);
        _testNotification.Tag = "secondary";
        _testNotification.FlatAppearance.BorderSize = 1;
        _testNotification.Click += (_, _) => SendTestNotification();
        _themed.Add(_testNotification);

        AddRow(tNot, 6, "Non ripetere prima di", Wrap(_cooldown, Hint("minuti"), _testNotification));

        // ------------------------------------------------------ avvio e finestra
        var cWindow = Card("Avvio e finestra", 4);
        var tWin = Rows(cWindow, 4);

        foreach (var (box, row, label) in new (ThemedCheckBox Box, int Row, string Label)[]
        {
            (_startWithWindows, 0, "Avvio"),
            (_startMinimized, 1, ""),
            (_minimizeToTray, 2, "Finestra"),
            (_closeToTray, 3, ""),
        })
        {
            box.AutoWidth();
            box.Margin = new Padding(0, 3, 0, 0);
            AddRow(tWin, row, label, box);
        }

        // ------------------------------------------------------- configurazione
        var cConfig = Card("Configurazione", 1);
        var tConfig = Rows(cConfig, 1);

        var esporta = MakeButton("Esporta...", primary: false);
        esporta.Margin = new Padding(0, 0, 8, 0);
        esporta.Click += (_, _) => Esporta();

        var importa = MakeButton("Importa...", primary: false);
        importa.Margin = new Padding(0, 0, 0, 0);
        importa.Click += (_, _) => Importa();

        AddRow(tConfig, 0, "Impostazioni su file", Wrap(esporta, importa,
            Hint("per portarle altrove")));

        root.Controls.AddRange([cGeneral, cTray, cPreview, cThresholds, cSystem, cMini,
                                cOverlay, cNotify, cWindow, cConfig]);

        // ------------------------------------------------------------- pulsanti
        var buttons = new BufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(16, 12, 16, 12),
            Tag = "bar",
        };
        _themed.Add(buttons);

        var ok = MakeButton("OK", primary: true);
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => Apply();

        var cancel = MakeButton("Annulla", primary: false);
        cancel.DialogResult = DialogResult.Cancel;

        var applica = MakeButton("Applica", primary: false);
        applica.Click += (_, _) =>
        {
            Apply();
            Applied?.Invoke(this, EventArgs.Empty);

            // La combinazione è appena stata ripresa da chi ci ascolta: se resta
            // nostra, la verifica qui direbbe "occupata" indicando noi stessi.
            VerificaCombinazione();
        };

        var reset = MakeButton("Predefiniti", primary: false);
        reset.Width = 112;
        reset.Click += (_, _) =>
        {
            CopyInto(new AppSettings(), _work);
            LoadValues();
            Theme.Apply(_work.Theme);
            UpdatePreview();
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Tag = "transparent",
        };
        flow.Controls.AddRange([ok, cancel, applica, reset]);
        buttons.Controls.Add(flow);
        _themed.Add(flow);

        Controls.Add(root);
        Controls.Add(buttons);
        _themed.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) =>
        {
            Theme.ApplyToWindow(this);
            Theme.ApplyNativeTheme(this);
            // Il pannello scorrevole salta sul primo controllo che prende il fuoco:
            // lo riportiamo in cima.
            if (_root is not null) _root.AutoScrollPosition = Point.Empty;
        };
    }

    // ------------------------------------------------------------- helper UI

    /// <summary>
    /// Una casella per disco: si sceglie quali far comparire in area di notifica invece
    /// di doverli prendere o lasciare tutti insieme. L'elenco è quello dell'ultima
    /// rilevazione; un disco collegato dopo compare da solo, già spuntato.
    /// </summary>
    private FlowLayoutPanel BuildDiskChoice()
    {
        var flusso = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            MaximumSize = new Size(346, 0),
            Margin = new Padding(0, 1, 0, 0),
            BackColor = Color.Transparent,
            Tag = "transparent",
        };
        _themed.Add(flusso);

        if (_disks.Count == 0)
        {
            var vuoto = new Label
            {
                Text = "Nessun disco rilevato",
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0),
                BackColor = Color.Transparent,
                Tag = "hint",
            };
            flusso.Controls.Add(vuoto);
            _themed.Add(vuoto);
            return flusso;
        }

        foreach (var disk in _disks)
        {
            var box = new ThemedCheckBox(disk.ShortName);
            box.AutoWidth();
            box.Margin = new Padding(0, 3, 12, 0);

            var copia = disk;
            box.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _work.SetDiskShownInTray(copia.TrayKey, box.Checked);
            };

            _tips.SetToolTip(box, $"{copia.DisplayName}\n{copia.KindDisplay}");

            flusso.Controls.Add(box);
            _themed.Add(box);
            _trayDiskBoxes.Add((copia, box));
        }

        return flusso;
    }

    /// <summary>
    /// Le caselle dei singoli dischi hanno senso solo se le icone dei dischi ci sono:
    /// spente insieme all'interruttore generale, così non sembra che non funzionino.
    /// </summary>
    private void UpdateDiskChoiceState()
    {
        bool attive = _trayDisks.Checked && _work.TrayMode != TrayMode.None;
        foreach (var (_, box) in _trayDiskBoxes) box.Enabled = attive;
    }

    /// <summary>Salva le impostazioni correnti in un file scelto dall'utente.</summary>
    private void Esporta()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Esporta le impostazioni",
            Filter = "Impostazioni di Disk Temp Monitor (*.json)|*.json|Tutti i file (*.*)|*.*",
            FileName = $"DiskTempMonitor-{DateTime.Now:yyyy-MM-dd}.json",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _work.ExportTo(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Non è stato possibile salvare il file.\n\n{ex.Message}",
                            "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Rilegge una configurazione da file e la mette nella copia di lavoro: si vede
    /// subito nella finestra, e diventa definitiva solo con Applica o OK. Chi importa
    /// per sbaglio se ne accorge e annulla.
    /// </summary>
    private void Importa()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Importa le impostazioni",
            Filter = "Impostazioni di Disk Temp Monitor (*.json)|*.json|Tutti i file (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (AppSettings.ImportFrom(dlg.FileName) is not { } letto)
        {
            MessageBox.Show(this,
                "Il file non contiene impostazioni leggibili.",
                "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        CopyInto(letto, _work);
        LoadValues();
        Theme.Apply(_work.Theme);
        UpdatePreview();
        VerificaCombinazione();
    }

    /// <summary>
    /// Dice se la combinazione scelta è libera, provando a prendersela per un istante.
    /// Saperlo qui, mentre la si sceglie, evita di scoprire più tardi che il tasto non
    /// fa niente e non capire perché.
    /// </summary>
    private void VerificaCombinazione()
    {
        if (_overlayHotkey.Value.Length == 0)
        {
            _overlayHotkeyEsito.Text = "nessuna scorciatoia";
            _overlayHotkeyEsito.ForeColor = Theme.TextSecondary;
            return;
        }

        bool libera = Hotkey.IsAvailable(Handle, _overlayHotkey.Value);
        _overlayHotkeyEsito.Text = libera ? "libera" : "occupata: scegline un'altra";
        _overlayHotkeyEsito.ForeColor = libera
            ? Theme.TextSecondary
            : IconRenderer.ParseColor(_work.ColorCritical, Color.OrangeRed);
    }

    private CardPanel Card(string title, int rows, int fixedHeight = 0)
    {
        var c = new CardPanel(title)
        {
            Width = 556,
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

    private void SetupColorButton(Button b, Func<string> get, Action<string> set, string tip)
    {
        b.Width = 46;
        b.Height = 26;
        b.FlatStyle = FlatStyle.Flat;
        b.Text = "";
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(0, 3, 8, 2);
        b.Tag = "color";
        b.BackColor = IconRenderer.ParseColor(get(), Color.Gray);
        b.FlatAppearance.BorderSize = 1;
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = b.BackColor, FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            b.BackColor = dlg.Color;
            set($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
            UpdatePreview();
        };
    }

    /// <summary>Manda subito una notifica di prova con le impostazioni correnti.</summary>
    private void SendTestNotification()
    {
        var notifier = new Notifier(_work)
        {
            BalloonFallback = (t, b) => MessageBox.Show(this, b, t,
                MessageBoxButtons.OK, MessageBoxIcon.Information),
        };

        notifier.Send("Disk Temp Monitor",
            _work.UseNativeNotifications && notifier.NativeAvailable
                ? "Notifica di prova. Le notifiche di Windows funzionano."
                : "Notifica di prova (le notifiche di Windows non sono disponibili).");

        if (_work.UseNativeNotifications && !notifier.NativeAvailable)
            MessageBox.Show(this,
                $"Windows non accetta le notifiche native:\n{notifier.NativeError}\n\n" +
                "Verrà usato il fumetto dell'area di notifica.",
                "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------------ tema

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyThemeColors();
        Theme.ApplyToWindow(this);
        Invalidate(true);
    }

    private void ApplyThemeColors()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;

        foreach (var c in _themed)
        {
            switch (c)
            {
                case Button b when (string?)b.Tag == "color":
                    b.FlatAppearance.BorderColor = Theme.Border;
                    break;                                        // il colore è il contenuto

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
                    c.Invalidate();          // i controlli disegnati da noi rileggono il tema
                    break;
            }
        }

        if (_root is not null) _root.BackColor = Theme.Background;
        _preview.BackColor = Theme.IsDark ? Theme.FromHex("#0E1013") : Theme.FromHex("#2A2D33");
        _root?.Invalidate(true);
    }

    // ------------------------------------------------------------- anteprima

    /// <summary>
    /// Con l'interruttore generale spento le singole scelte non hanno più effetto: si
    /// disattivano, così si vede subito che non contano più.
    /// </summary>
    private void ApplyNotificationsEnabled()
    {
        bool acceso = _notificationsOn.Checked;

        foreach (var c in new Control[]
                 { _nativeToasts, _notifyLowSpace, _notifyHealth, _notifySelfTest, _cooldown, _testNotification })
            c.Enabled = acceso;

        _notify.Enabled = acceso;
        _lowSpace.Enabled = acceso && _notifyLowSpace.Checked;
    }

    private void UpdatePreview() => _preview.Invalidate();

    private void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int[] samples = [_work.WarnTemp - 12, _work.WarnTemp + 2, _work.CriticalTemp + 5];
        DrawRow(g, samples, 16, 18, 32, "16 px · schermo standard");
        DrawRow(g, samples, 32, 258, 24, "32 px · alta densità");
    }

    private void DrawRow(Graphics g, int[] samples, int size, int x, int y, string caption)
    {
        using var captionBrush = new SolidBrush(Color.FromArgb(148, 154, 166));
        g.DrawString(caption, Theme.Micro, captionBrush, x, y - 15);

        foreach (int c in samples)
        {
            var color = IconRenderer.ColorForTemp(c, _work);
            using var icon = IconRenderer.Create(_work.FormatTempShort(c), color, _work, size,
                _work.ShowDiskIndexOnIcon ? "C:" : null);
            g.DrawIcon(icon, new Rectangle(x, y, size, size));

            using var b = new SolidBrush(Color.FromArgb(128, 134, 146));
            g.DrawString(_work.FormatTemp(c), Theme.Micro, b, x - 4, y + size + 4);
            x += size + 44;
        }
    }

    // ------------------------------------------------------------- valori

    private void LoadValues()
    {
        _loading = true;
        try
        {
            _refresh.Value = _work.RefreshSeconds;
            _theme.SelectedIndex = (int)_work.Theme;
            _trayMode.SelectedIndex = (int)_work.TrayMode;
            _unit.SelectedIndex = (int)_work.Unit;
            _hideMuteUsb.Checked = _work.HideUsbWithoutData;

            int fontIdx = _fontFamily.IndexOf(_work.IconFontFamily);
            _fontFamily.SelectedIndex = fontIdx >= 0 ? fontIdx : Math.Max(0, _fontFamily.IndexOf("Segoe UI"));

            _bold.Checked = _work.IconBold;
            _fontOffset.Value = _work.IconFontSizeOffset;
            _transparent.Checked = _work.IconTransparentBackground;
            _backColor.BackColor = IconRenderer.ParseColor(_work.IconBackgroundColor, Color.Black);
            _backColor.Visible = !_work.IconTransparentBackground;
            _showIndex.Checked = _work.ShowDiskIndexOnIcon;

            _trayDisks.Checked = _work.TrayShowDisks;
            foreach (var (disk, box) in _trayDiskBoxes)
                box.Checked = _work.IsDiskShownInTray(disk.TrayKey);
            _trayCpu.Checked = _work.TrayShowCpu;
            _trayGpu.Checked = _work.TrayShowGpu;
            _trayCpuLoad.Checked = _work.TrayShowCpuLoad;
            _trayGpuLoad.Checked = _work.TrayShowGpuLoad;
            _trayGpuVram.Checked = _work.TrayShowGpuVram;
            _trayMemory.Checked = _work.TrayShowMemory;
            _trayCpuMode.SelectedIndex = (int)_work.TrayCpuMode;

            _cpuWarn.Value = _work.CpuWarnTemp;
            _cpuCrit.Value = _work.CpuCriticalTemp;
            _gpuWarn.Value = _work.GpuWarnTemp;
            _gpuCrit.Value = _work.GpuCriticalTemp;

            _miniDisks.Checked = _work.MiniShowDisks;
            _miniCpu.Checked = _work.MiniShowCpu;
            _miniCpuLoad.Checked = _work.MiniShowCpuLoad;
            _miniCores.Checked = _work.MiniShowCpuCores;
            _miniGpu.Checked = _work.MiniShowGpu;
            _miniGpuLoad.Checked = _work.MiniShowGpuLoad;
            _miniGpuVram.Checked = _work.MiniShowGpuVram;
            _miniMemory.Checked = _work.MiniShowMemory;
            _miniCpuMode.SelectedIndex = (int)_work.MiniCpuMode;

            _overlayOn.Checked = _work.OverlayEnabled;
            _overlayOnlyGames.Checked = _work.OverlayOnlyInGames;
            _overlayFps.Checked = _work.OverlayShowFps;
            _overlayDisks.Checked = _work.OverlayShowDisks;
            _overlayCpu.Checked = _work.OverlayShowCpu;
            _overlayGpu.Checked = _work.OverlayShowGpu;
            _overlayCpuLoad.Checked = _work.OverlayShowCpuLoad;
            _overlayGpuLoad.Checked = _work.OverlayShowGpuLoad;
            _overlayVram.Checked = _work.OverlayShowGpuVram;
            _overlayMemory.Checked = _work.OverlayShowMemory;
            _overlayCorner.SelectedIndex = (int)_work.OverlayCorner;
            _overlayLayout.SelectedIndex = (int)_work.OverlayLayout;
            _overlayOffsetX.Value = _work.OverlayOffsetX;
            _overlayOffsetY.Value = _work.OverlayOffsetY;
            _overlayOpacity.Value = _work.OverlayOpacityPercent;
            _overlayScale.Value = _work.OverlayScalePercent;

            _overlayHotkey.Value = _work.OverlayHotkey;

            _warnTemp.Value = _work.WarnTemp;
            _critTemp.Value = _work.CriticalTemp;
            _colorNormal.BackColor = IconRenderer.ParseColor(_work.ColorNormal, Color.LimeGreen);
            _colorWarn.BackColor = IconRenderer.ParseColor(_work.ColorWarn, Color.Orange);
            _colorCritical.BackColor = IconRenderer.ParseColor(_work.ColorCritical, Color.OrangeRed);
            _notify.Checked = _work.NotifyOnCritical;
            _notificationsOn.Checked = _work.NotificationsEnabled;
            _nativeToasts.Checked = _work.UseNativeNotifications;
            _notifyLowSpace.Checked = _work.NotifyOnLowSpace;
            _notifyHealth.Checked = _work.NotifyOnHealthChange;
            _notifySelfTest.Checked = _work.NotifyOnSelfTest;
            _lowSpace.Value = _work.LowSpaceThresholdPercent;
            _lowSpace.Enabled = _work.NotifyOnLowSpace;
            _cooldown.Value = _work.NotificationCooldownMinutes;
            ApplyNotificationsEnabled();

            _startMinimized.Checked = _work.StartMinimized;
            _minimizeToTray.Checked = _work.MinimizeToTray;
            _closeToTray.Checked = _work.CloseToTray;
            _startWithWindows.Checked = Startup.IsEnabled();
        }
        finally { _loading = false; }

        UpdateDiskChoiceState();
    }

    private void Apply()
    {
        _work.StartMinimized = _startMinimized.Checked;
        _work.MinimizeToTray = _minimizeToTray.Checked;
        _work.CloseToTray = _closeToTray.Checked;

        bool wantStartup = _startWithWindows.Checked;
        if (wantStartup != Startup.IsEnabled())
        {
            if (!Startup.SetEnabled(wantStartup))
                MessageBox.Show(this,
                    "Non è stato possibile modificare l'avvio automatico.\n" +
                    "Prova a eseguire il programma come amministratore.",
                    "Disk Temp Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _work.StartWithWindows = Startup.IsEnabled();

        CopyInto(_work, _target);
    }

    /// <summary>
    /// La verifica della combinazione vuole una finestra vera su cui provare a
    /// registrarla, quindi non si può fare prima che esista.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        VerificaCombinazione();
        base.OnShown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        // Annullando, il tema torna com'era prima dell'anteprima.
        if (DialogResult != DialogResult.OK && Theme.Mode != _originalTheme) Theme.Apply(_originalTheme);
        _tips.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Impostazioni che la finestra non mostra e che cambiano per conto loro mentre è
    /// aperta: posizione del riquadro compatto, se è aperto, il suo aspetto. Ricopiarle
    /// dalla copia di lavoro le riporterebbe a com'erano all'apertura della finestra,
    /// disfacendo per esempio uno spostamento fatto nel frattempo.
    /// </summary>
    private static readonly HashSet<string> NonRicopiare =
    [
        nameof(AppSettings.MiniWindowEnabled),
        nameof(AppSettings.MiniWindowX),
        nameof(AppSettings.MiniWindowY),
        nameof(AppSettings.MiniWindowCompact),
        nameof(AppSettings.MiniWindowOpacity),
    ];

    /// <summary>
    /// Riversa nelle impostazioni vere tutto quello che si è cambiato nella copia di
    /// lavoro.
    /// <para>
    /// Si va per riflessione e non voce per voce. L'elenco scritto a mano che c'era
    /// prima funzionava finché qualcuno aggiungeva un'impostazione e si dimenticava di
    /// aggiungerla anche qui: la voce compariva nella finestra, si lasciava spuntare, e
    /// alla chiusura tornava com'era senza dire niente. È successo con la scelta dei
    /// dischi in area di notifica, con le percentuali di VRAM e con la sovrimpressione
    /// di gioco. Così il problema non si ripresenta alla prossima impostazione.
    /// </para>
    /// </summary>
    private static void CopyInto(AppSettings from, AppSettings to)
    {
        foreach (var voce in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!voce.CanRead || !voce.CanWrite) continue;
            if (NonRicopiare.Contains(voce.Name)) continue;

            object? valore = voce.GetValue(from);

            // Le liste vanno copiate e non condivise, altrimenti le due impostazioni
            // resterebbero legate alla stessa e modificarne una cambierebbe l'altra.
            if (valore is List<string> elenco) valore = new List<string>(elenco);

            voce.SetValue(to, valore);
        }
    }
}
