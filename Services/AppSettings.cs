using System.Text.Json;
using System.Text.Json.Serialization;
using DiskTempMonitor.UI;

namespace DiskTempMonitor.Services;

public enum TrayMode
{
    /// <summary>Una icona per ogni disco (stile Core Temp).</summary>
    PerDisk,
    /// <summary>Una sola icona con la temperatura più alta.</summary>
    HottestOnly,
    /// <summary>Nessuna icona in area di notifica.</summary>
    None,
}

public enum TempUnit
{
    Celsius,
    Fahrenheit,
}

public sealed class AppSettings
{
    // --- aggiornamento ---
    public int RefreshSeconds { get; set; } = 3;

    /// <summary>
    /// Toglie dall'elenco i dispositivi USB che non espongono né temperatura né
    /// S.M.A.R.T.: chiavette e lettori di schede, che riempirebbero le schede di trattini.
    /// </summary>
    public bool HideUsbWithoutData { get; set; } = true;

    // --- aspetto ---
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;

    // --- area di notifica ---
    public TrayMode TrayMode { get; set; } = TrayMode.PerDisk;
    public TempUnit Unit { get; set; } = TempUnit.Celsius;
    public string IconFontFamily { get; set; } = "Segoe UI";
    public bool IconBold { get; set; } = true;
    public int IconFontSizeOffset { get; set; }             // -2 .. +4
    public bool IconTransparentBackground { get; set; } = true;
    public string IconBackgroundColor { get; set; } = "#101010";
    public bool ShowDiskIndexOnIcon { get; set; }

    // --- altre icone in area di notifica ---
    public bool TrayShowDisks { get; set; } = true;

    /// <summary>
    /// I dischi che non devono comparire in area di notifica, per
    /// <see cref="DiskTempMonitor.Models.DiskInfo.TrayKey"/>. Si tiene l'elenco di
    /// quelli esclusi e non di quelli inclusi, così un disco appena collegato compare
    /// da solo invece di restare invisibile finché non lo si spunta.
    /// </summary>
    public List<string> TrayHiddenDisks { get; set; } = [];
    public bool TrayShowCpu { get; set; }
    public bool TrayShowGpu { get; set; }
    public bool TrayShowCpuLoad { get; set; }
    public bool TrayShowGpuLoad { get; set; }
    public bool TrayShowGpuVram { get; set; }
    public bool TrayShowMemory { get; set; }
    public CpuTempMode TrayCpuMode { get; set; } = CpuTempMode.Hottest;

    // --- soglie colore dei sensori di sistema (in gradi Celsius) ---
    public int CpuWarnTemp { get; set; } = 75;
    public int CpuCriticalTemp { get; set; } = 90;
    public int GpuWarnTemp { get; set; } = 75;
    public int GpuCriticalTemp { get; set; } = 88;

    // --- soglie colore (in gradi Celsius) ---
    public int WarnTemp { get; set; } = 50;
    public int CriticalTemp { get; set; } = 60;
    public string ColorNormal { get; set; } = "#35C759";
    public string ColorWarn { get; set; } = "#FFB020";
    public string ColorCritical { get; set; } = "#FF453A";

    // --- riquadro compatto sempre in primo piano ---
    public bool MiniWindowEnabled { get; set; }
    public int MiniWindowX { get; set; } = int.MinValue;    // MinValue = mai posizionato
    public int MiniWindowY { get; set; } = int.MinValue;
    public bool MiniWindowCompact { get; set; }             // senza micro-grafico
    public int MiniWindowOpacity { get; set; } = 94;        // percentuale

    // Cosa mostra il riquadro, oltre ai dischi
    public bool MiniShowDisks { get; set; } = true;
    public bool MiniShowCpu { get; set; }
    public bool MiniShowCpuLoad { get; set; }
    public bool MiniShowCpuCores { get; set; }
    public bool MiniShowGpu { get; set; }
    public bool MiniShowGpuLoad { get; set; }
    public bool MiniShowGpuVram { get; set; }
    public bool MiniShowMemory { get; set; }
    public CpuTempMode MiniCpuMode { get; set; } = CpuTempMode.Hottest;

    // --- sovrimpressione di gioco ---

    /// <summary>
    /// La targhetta con FPS e misure sopra al gioco. Spenta finché non la si chiede:
    /// per contare i fotogrammi va aperta una sessione di tracciamento di sistema, e
    /// non è cosa da fare a insaputa di chi non la userà mai.
    /// </summary>
    public bool OverlayEnabled { get; set; }

    /// <summary>Comparire da sola quando in primo piano c'è qualcosa che disegna.</summary>
    public bool OverlayOnlyInGames { get; set; } = true;

    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.AltoDestra;
    public int OverlayOpacityPercent { get; set; } = 72;
    public int OverlayScalePercent { get; set; } = 100;
    public int OverlayMargin { get; set; } = 16;

    /// <summary>
    /// Ritocco fine della posizione, in pixel, rispetto all'angolo scelto: positivo
    /// sposta verso destra e verso il basso. Serve a scansare quello che il gioco
    /// disegna proprio lì — una minimappa, una barra della vita, l'orologio di un'altra
    /// sovrimpressione.
    /// </summary>
    public int OverlayOffsetX { get; set; }
    public int OverlayOffsetY { get; set; }

    /// <summary>Come si dispongono i valori: uno per riga, o tre per riga.</summary>
    public OverlayLayout OverlayLayout { get; set; } = OverlayLayout.Verticale;

    public bool OverlayShowFps { get; set; } = true;
    public bool OverlayShowDisks { get; set; }
    public bool OverlayShowCpu { get; set; } = true;
    public bool OverlayShowCpuLoad { get; set; } = true;
    public bool OverlayShowGpu { get; set; } = true;
    public bool OverlayShowGpuLoad { get; set; } = true;
    public bool OverlayShowGpuVram { get; set; } = true;
    public bool OverlayShowMemory { get; set; }
    public CpuTempMode OverlayCpuMode { get; set; } = CpuTempMode.Hottest;

    /// <summary>
    /// Combinazione che accende e spegne la sovrimpressione, ovunque ci si trovi.
    /// <para>
    /// Un tasto funzione con due tasti di servizio: le combinazioni con una lettera —
    /// la prima scelta era Ctrl+Alt+O — sono spesso già prese da driver video, programmi
    /// di registrazione o dalla barra di gioco di Windows, e Windows non lascia
    /// strappare a nessuno una combinazione già registrata. Se anche questa risultasse
    /// occupata, le impostazioni lo dicono e se ne sceglie un'altra premendola.
    /// </para>
    /// </summary>
    public string OverlayHotkey { get; set; } = "Ctrl+Alt+F9";

    // --- comportamento finestra ---
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }

    // --- notifiche ---

    /// <summary>
    /// Interruttore generale: a zero non parte nessun avviso, di nessun tipo, senza
    /// dover spegnere le singole voci una per una.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    public bool UseNativeNotifications { get; set; } = true;
    public bool NotifyOnCritical { get; set; } = true;
    public bool NotifyOnLowSpace { get; set; } = true;
    public bool NotifyOnHealthChange { get; set; } = true;
    public bool NotifyOnSelfTest { get; set; } = true;
    public int LowSpaceThresholdPercent { get; set; } = 90;
    public int NotificationCooldownMinutes { get; set; } = 30;

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskTempMonitor", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null) { s.Clamp(); return s; }
            }
        }
        catch { /* impostazioni illeggibili: si riparte dai default */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* nessun blocco dell'app se il salvataggio fallisce */ }
    }

    /// <summary>
    /// Scrive le impostazioni correnti in un file, per portarle su un'altra macchina o
    /// per tenersi da parte una configurazione a cui tornare.
    /// </summary>
    public void ExportTo(string path)
    {
        Clamp();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>
    /// Rilegge una configurazione salvata. Restituisce null se il file non è leggibile
    /// o non contiene impostazioni: meglio dirlo che partire con valori a caso.
    /// <para>
    /// Le voci mancanti — un file esportato da una versione più vecchia — restano ai
    /// valori predefiniti invece di far fallire tutta la lettura.
    /// </para>
    /// </summary>
    public static AppSettings? ImportFrom(string path)
    {
        try
        {
            var letto = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts);
            letto?.Clamp();
            return letto;
        }
        catch { return null; }
    }

    private void Clamp()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 1, 3600);
        IconFontSizeOffset = Math.Clamp(IconFontSizeOffset, -4, 6);
        WarnTemp = Math.Clamp(WarnTemp, 20, 110);
        // Il minimo non deve mai superare il massimo, altrimenti Math.Clamp solleva
        // un'eccezione: succederebbe con un file di impostazioni manomesso.
        CriticalTemp = Math.Clamp(CriticalTemp, Math.Min(WarnTemp + 1, 120), 120);

        CpuWarnTemp = Math.Clamp(CpuWarnTemp, 30, 120);
        CpuCriticalTemp = Math.Clamp(CpuCriticalTemp, Math.Min(CpuWarnTemp + 1, 130), 130);
        GpuWarnTemp = Math.Clamp(GpuWarnTemp, 30, 120);
        GpuCriticalTemp = Math.Clamp(GpuCriticalTemp, Math.Min(GpuWarnTemp + 1, 130), 130);

        if (string.IsNullOrWhiteSpace(IconFontFamily)) IconFontFamily = "Segoe UI";

        LowSpaceThresholdPercent = Math.Clamp(LowSpaceThresholdPercent, 50, 99);
        NotificationCooldownMinutes = Math.Clamp(NotificationCooldownMinutes, 1, 1440);
        MiniWindowOpacity = Math.Clamp(MiniWindowOpacity, 40, 100);

        OverlayOpacityPercent = Math.Clamp(OverlayOpacityPercent, 10, 100);
        OverlayScalePercent = Math.Clamp(OverlayScalePercent, 60, 250);
        OverlayMargin = Math.Clamp(OverlayMargin, 0, 400);
        OverlayOffsetX = Math.Clamp(OverlayOffsetX, -4000, 4000);
        OverlayOffsetY = Math.Clamp(OverlayOffsetY, -4000, 4000);
        // Una combinazione vuota è legittima: vuol dire "nessuna scorciatoia".
    }

    /// <summary>Vero se quel disco deve avere la sua icona in area di notifica.</summary>
    public bool IsDiskShownInTray(string trayKey) =>
        !TrayHiddenDisks.Contains(trayKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>Aggiunge o toglie un disco dall'elenco degli esclusi.</summary>
    public void SetDiskShownInTray(string trayKey, bool shown)
    {
        TrayHiddenDisks.RemoveAll(k => string.Equals(k, trayKey, StringComparison.OrdinalIgnoreCase));
        if (!shown) TrayHiddenDisks.Add(trayKey);
    }

    /// <summary>Applica al tema globale la modalità salvata.</summary>
    public void ApplyTheme() => UI.Theme.Apply(Theme);

    /// <summary>
    /// Copia di lavoro, già normalizzata: la finestra delle impostazioni parte sempre
    /// da valori validi anche se quelli correnti non lo fossero.
    /// </summary>
    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        copy.Clamp();
        return copy;
    }

    // --------------------------------------------------------------- helper

    public double ToDisplay(int celsius) =>
        Unit == TempUnit.Fahrenheit ? celsius * 9.0 / 5.0 + 32 : celsius;

    [JsonIgnore]
    public string UnitSuffix => Unit == TempUnit.Fahrenheit ? "°F" : "°C";

    public string FormatTemp(int? celsius) =>
        celsius is int c ? $"{Math.Round(ToDisplay(c)):0}{UnitSuffix}" : "—";

    /// <summary>Valore breve per l'icona in area di notifica (massimo 3 caratteri).</summary>
    public string FormatTempShort(int? celsius)
    {
        if (celsius is not int c) return "--";
        int v = (int)Math.Round(ToDisplay(c));
        return v > 999 ? "999" : v < -99 ? "-99" : v.ToString();
    }
}
