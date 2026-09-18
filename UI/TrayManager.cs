using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Gestisce le icone in area di notifica: una per disco (stile Core Temp) oppure una
/// sola con la temperatura più alta, più quelle facoltative di processore e scheda video.
/// </summary>
public sealed class TrayManager : IDisposable
{
    // Gli identificativi devono restare stabili fra un avvio e l'altro, perché Windows
    // ricordi quali icone l'utente ha fissato nella barra. I dischi partono da 1; le
    // altre due misure hanno numeri alti, così non si spostano quando cambia il numero
    // di dischi collegati.
    private const uint CpuIconId = 101;
    private const uint GpuIconId = 102;
    private const uint CpuLoadIconId = 103;
    private const uint GpuLoadIconId = 104;
    private const uint MemoryIconId = 105;
    private const uint GpuVramIconId = 106;

    private readonly AppSettings _settings;
    private readonly List<TrayIcon> _icons = [];
    private TrayIcon? _cpuIcon;
    private TrayIcon? _gpuIcon;
    private TrayIcon? _cpuLoadIcon;
    private TrayIcon? _gpuLoadIcon;
    private TrayIcon? _memoryIcon;
    private TrayIcon? _gpuVramIcon;
    private int _iconSize = 16;
    private bool _disposed;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayManager(AppSettings settings) => _settings = settings;

    public void SetDpi(int dpi)
    {
        int size = IconRenderer.IconSizeForDpi(dpi);
        if (size == _iconSize) return;
        _iconSize = size;          // basta ridisegnare: le icone non vanno ricreate
    }

    /// <summary>Aggiorna testo, colore e suggerimento delle icone.</summary>
    public void Update(IReadOnlyList<DiskInfo> disks, SystemMetrics? metrics = null)
    {
        if (_disposed) return;

        bool showDisks = _settings.TrayShowDisks && _settings.TrayMode != TrayMode.None;

        // Non tutti i dischi devono per forza avere la loro icona: l'utente può
        // escluderne qualcuno dalle impostazioni.
        var scelti = showDisks ? Chosen(disks) : [];

        int wanted = _settings.TrayMode switch
        {
            TrayMode.HottestOnly => scelti.Count > 0 ? 1 : 0,
            _ => scelti.Count,
        };

        EnsureCount(wanted);

        if (wanted > 0)
        {
            if (_settings.TrayMode == TrayMode.HottestOnly)
            {
                var hottest = scelti
                    .Where(d => d.TemperatureC.HasValue)
                    .OrderByDescending(d => d.TemperatureC!.Value)
                    .FirstOrDefault() ?? scelti[0];

                ApplyIcon(_icons[0], hottest, scelti, showAll: true);
            }
            else
            {
                for (int i = 0; i < _icons.Count && i < scelti.Count; i++)
                    ApplyIcon(_icons[i], scelti[i], scelti, showAll: false);
            }
        }

        UpdateSystemIcons(metrics);
    }

    /// <summary>
    /// I dischi da mostrare in area di notifica, nell'ordine in cui arrivano: quelli
    /// che l'utente non ha escluso. Anche il riepilogo dell'icona unica si limita a
    /// questi, altrimenti mostrerebbe proprio i dischi che si è chiesto di nascondere.
    /// </summary>
    private List<DiskInfo> Chosen(IReadOnlyList<DiskInfo> disks) =>
        _settings.TrayHiddenDisks.Count == 0
            ? [.. disks]
            : [.. disks.Where(d => _settings.IsDiskShownInTray(d.TrayKey))];

    // ---------------------------------------- misure di sistema, una icona ciascuna

    /// <summary>
    /// Le misure facoltative: temperature di processore e scheda video, e percentuali di
    /// utilizzo di processore, scheda video e memoria. Sono tutte uguali nella struttura —
    /// un valore, una sigla, un colore, un suggerimento — quindi le governa un solo metodo.
    /// </summary>
    private void UpdateSystemIcons(SystemMetrics? metrics)
    {
        var cpu = metrics?.Cpu;
        var gpu = metrics?.PrimaryGpu;

        int? cpuTemp = metrics?.CpuTemperature(_settings.TrayCpuMode);
        int? gpuTemp = metrics?.GpuTemperature;

        string modo = _settings.TrayCpuMode switch
        {
            CpuTempMode.Average => "media dei core",
            CpuTempMode.Package => "package",
            _ => "core più caldo",
        };

        // La sigla delle temperature porta il segno di grado, quella dei dischi i due
        // punti dopo la lettera: senza, la "C" del processore e la "C" del disco C
        // erano la stessa cosa.
        Apply(ref _cpuIcon, CpuIconId, _settings.TrayShowCpu, "C°",
            _settings.FormatTempShort(cpuTemp),
            IconRenderer.ColorForCpu(cpuTemp, _settings),
            cpu is null
                ? "Processore\nTemperature non disponibili"
                : $"{Shorten(cpu.Name)}\n{_settings.FormatTemp(cpuTemp)}  ·  {modo}");

        Apply(ref _gpuIcon, GpuIconId, _settings.TrayShowGpu, "G°",
            _settings.FormatTempShort(gpuTemp),
            IconRenderer.ColorForGpu(gpuTemp, _settings),
            gpu is null
                ? "Scheda video\nSensori non disponibili"
                : $"{Shorten(gpu.Name)}\n{_settings.FormatTemp(gpuTemp)}");

        ApplyLoad(ref _cpuLoadIcon, CpuLoadIconId, _settings.TrayShowCpuLoad, "C%",
            cpu?.AverageLoadPercent,
            cpu is null ? "Processore" : $"{Shorten(cpu.Name)}\nCarico");

        ApplyLoad(ref _gpuLoadIcon, GpuLoadIconId, _settings.TrayShowGpuLoad, "G%",
            gpu?.CoreLoadPercent,
            gpu is null ? "Scheda video" : $"{Shorten(gpu.Name)}\nCarico");

        ApplyLoad(ref _gpuVramIcon, GpuVramIconId, _settings.TrayShowGpuVram, "V%",
            gpu?.VramUsedPercent,
            gpu is null ? "Memoria della scheda video" : $"{Shorten(gpu.Name)}\nMemoria occupata");

        ApplyLoad(ref _memoryIcon, MemoryIconId, _settings.TrayShowMemory, "R%",
            metrics?.MemoryUsedPercent, "Memoria occupata");
    }

    private void ApplyLoad(ref TrayIcon? icon, uint uid, bool wanted, string label,
                           float? percent, string description)
    {
        Apply(ref icon, uid, wanted, label,
            percent is float p ? Math.Clamp(p, 0, 100).ToString("0") : "--",
            IconRenderer.ColorForLoad(percent, _settings),
            percent is float q ? $"{description}: {q:0}%" : description);
    }

    private void Apply(ref TrayIcon? icon, uint uid, bool wanted, string label,
                       string value, Color color, string tooltip)
    {
        if (!(wanted && _settings.TrayMode != TrayMode.None)) { Remove(ref icon); return; }

        icon ??= CreateIcon(uid);

        // Qui la sigla c'è sempre: senza, due icone col numero 43 sarebbero
        // indistinguibili, e l'icona non direbbe niente.
        icon.Update(IconRenderer.Create(value, color, _settings, _iconSize, label), tooltip);
    }

    private static string Shorten(string name) => name.Length <= 48 ? name : name[..47] + "…";

    private TrayIcon CreateIcon(uint uid)
    {
        var icon = new TrayIcon(uid) { ContextMenu = BuildMenu() };
        icon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        icon.BalloonClicked += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        return icon;
    }

    private static void Remove(ref TrayIcon? icon)
    {
        if (icon is null) return;
        icon.ContextMenu?.Dispose();
        icon.Dispose();
        icon = null;
    }

    /// <summary>Fumetto dell'area di notifica, usato se quelle native non funzionano.</summary>
    public void ShowBalloon(string title, string body) =>
        AllIcons().FirstOrDefault()?.ShowBalloon(title, body, 8000);

    /// <summary>
    /// Crea o rimuove icone solo quando il loro numero cambia davvero. Ricrearle
    /// significherebbe perdere la posizione scelta dall'utente nella barra.
    /// </summary>
    private void EnsureCount(int wanted)
    {
        while (_icons.Count > wanted)
        {
            var last = _icons[^1];
            _icons.RemoveAt(_icons.Count - 1);
            last.ContextMenu?.Dispose();
            last.Dispose();
        }

        while (_icons.Count < wanted)
        {
            // L'uID coincide con la posizione: stabile fra un avvio e l'altro.
            _icons.Add(CreateIcon((uint)(_icons.Count + 1)));
        }
    }

    private void ApplyIcon(TrayIcon tray, DiskInfo disk, IReadOnlyList<DiskInfo> all, bool showAll)
    {
        string text = _settings.FormatTempShort(disk.TemperatureC);
        var color = IconRenderer.ColorForTemp(disk.TemperatureC, _settings);
        // La lettera tiene i suoi due punti, che dicono "disco": tanto basta, e il
        // segno di grado in coda sarebbe solo un pallino in piu' da guardare. Un
        // disco senza lettera usa il suo numero.
        string? sub = _settings.ShowDiskIndexOnIcon
            ? disk.PrimaryLetter ?? $"{disk.Index}:"
            : null;

        var image = IconRenderer.Create(text, color, _settings, _iconSize, sub);
        tray.Update(image, showAll ? BuildSummaryTooltip(all) : BuildDiskTooltip(disk));
    }

    private string BuildDiskTooltip(DiskInfo disk)
    {
        string name = disk.Model.Length > 0 ? disk.Model : $"Disco {disk.Index}";
        string letters = disk.DriveLetters.Count > 0 ? $" ({disk.LettersDisplay})" : "";
        return $"{name}{letters}\n{_settings.FormatTemp(disk.TemperatureC)}  ·  {DiskCard.HealthText(disk.Health)}";
    }

    private string BuildSummaryTooltip(IReadOnlyList<DiskInfo> disks)
    {
        var lines = disks.Select(d =>
        {
            string shortName = d.DriveLetters.Count > 0
                ? string.Join(",", d.DriveLetters)
                : $"Disco {d.Index}";
            return $"{shortName}: {_settings.FormatTemp(d.TemperatureC)}";
        });
        return "Disk Temp Monitor\n" + string.Join("\n", lines);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ThemedMenuRenderer(),
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextPrimary,
            ShowImageMargin = false,
            Font = Theme.Body,
        };
        menu.Items.Add("Mostra finestra", null, (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Aggiorna adesso", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Impostazioni...", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        return menu;
    }

    /// <summary>
    /// Da chiamare quando cambia solo l'aspetto (tema, font, colori): le icone vengono
    /// ridisegnate al prossimo aggiornamento, senza essere rimosse e riaggiunte.
    /// </summary>
    public void Invalidate()
    {
        // I menu non vanno ricreati: si arriva qui anche subito dopo un clic sulla voce
        // "Impostazioni..." del menu stesso, che WinForms sta ancora elaborando.
        // Le tinte le prende il renderer dal tema; qui bastano sfondo e carattere.
        foreach (var icon in AllIcons())
        {
            if (icon.ContextMenu is not { } menu) continue;
            menu.BackColor = Theme.SurfaceAlt;
            menu.ForeColor = Theme.TextPrimary;
            menu.Font = Theme.Body;
        }
    }

    private IEnumerable<TrayIcon> AllIcons()
    {
        foreach (var icon in _icons) yield return icon;
        foreach (var icon in new[] { _cpuIcon, _gpuIcon, _cpuLoadIcon, _gpuLoadIcon, _gpuVramIcon, _memoryIcon })
            if (icon is not null) yield return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var icon in AllIcons())
        {
            icon.ContextMenu?.Dispose();
            icon.Dispose();
        }
        _icons.Clear();
        _cpuIcon = _gpuIcon = _cpuLoadIcon = _gpuLoadIcon = _gpuVramIcon = _memoryIcon = null;
    }
}
