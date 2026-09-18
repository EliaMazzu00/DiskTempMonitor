using DiskTempMonitor.Models;

namespace DiskTempMonitor.Services;

/// <summary>Tipi di avviso, per poterli abilitare singolarmente e limitarne la frequenza.</summary>
public enum AlertKind
{
    Temperature,
    LowSpace,
    Health,
    SelfTest,
}

/// <summary>
/// Decide cosa notificare e come. Applica le preferenze dell'utente, evita di ripetere
/// lo stesso avviso e ripiega sul fumetto dell'area di notifica se Windows non accetta
/// le notifiche native.
/// </summary>
public sealed class Notifier
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, DateTime> _lastSent = [];

    /// <summary>Usato quando le notifiche native non sono disponibili.</summary>
    public Action<string, string>? BalloonFallback { get; set; }

    public Notifier(AppSettings settings) => _settings = settings;

    public bool NativeAvailable => WindowsToast.IsAvailable;
    public string? NativeError => WindowsToast.LastError;

    private bool Enabled(AlertKind kind) => _settings.NotificationsEnabled && kind switch
    {
        AlertKind.Temperature => _settings.NotifyOnCritical,
        AlertKind.LowSpace => _settings.NotifyOnLowSpace,
        AlertKind.Health => _settings.NotifyOnHealthChange,
        AlertKind.SelfTest => _settings.NotifyOnSelfTest,
        _ => false,
    };

    /// <summary>
    /// Invia un avviso se il tipo è abilitato e se lo stesso avviso non è già stato
    /// mandato di recente.
    /// </summary>
    public void Notify(AlertKind kind, string key, string title, string body, bool important = false)
    {
        if (!Enabled(kind)) return;

        string id = $"{kind}:{key}";
        var now = DateTime.UtcNow;
        if (_lastSent.TryGetValue(id, out var last) &&
            (now - last).TotalMinutes < _settings.NotificationCooldownMinutes)
            return;

        _lastSent[id] = now;
        Send(title, body, important);
    }

    /// <summary>Invio immediato, senza filtri: per le prove dalle impostazioni.</summary>
    public void Send(string title, string body, bool important = false)
    {
        if (_settings.UseNativeNotifications && WindowsToast.Show(title, body, important)) return;
        BalloonFallback?.Invoke(title, body);
    }

    /// <summary>Azzera la memoria degli avvisi già inviati (es. al cambio impostazioni).</summary>
    public void Reset(AlertKind? kind = null)
    {
        if (kind is null) { _lastSent.Clear(); return; }
        foreach (var k in _lastSent.Keys.Where(k => k.StartsWith($"{kind}:")).ToList())
            _lastSent.Remove(k);
    }

    // ----------------------------------------------------- valutazione dei dischi

    private readonly Dictionary<int, HealthState> _lastHealth = [];

    /// <summary>Esamina i dischi e manda gli avvisi del caso.</summary>
    public void Evaluate(IReadOnlyList<DiskInfo> disks, AppSettings settings)
    {
        foreach (var d in disks)
        {
            string name = d.Model.Length > 0 ? d.Model : $"Disco {d.Index}";

            // --- temperatura ---
            if (d.TemperatureC is int t)
            {
                if (t >= settings.CriticalTemp)
                {
                    Notify(AlertKind.Temperature, $"{d.Index}",
                        "Temperatura elevata",
                        $"{name}: {settings.FormatTemp(t)} (soglia critica {settings.FormatTemp(settings.CriticalTemp)})",
                        important: true);
                }
                else if (t < settings.CriticalTemp - 3)
                {
                    // Rientrata: il prossimo superamento torna a essere segnalabile.
                    Reset(AlertKind.Temperature);
                }
            }

            // --- spazio libero ---
            if (d.UsedFraction is double used)
            {
                int percent = (int)Math.Round(used * 100);
                if (percent >= settings.LowSpaceThresholdPercent)
                {
                    string free = DiskInfo.FormatBytes(d.VolumeTotalBytes - d.VolumeUsedBytes);
                    Notify(AlertKind.LowSpace, $"{d.Index}",
                        "Spazio quasi esaurito",
                        $"{d.LettersDisplay} occupato al {percent}% · liberi {free}",
                        important: percent >= 98);
                }
            }

            // --- peggioramento dello stato di salute ---
            if (_lastHealth.TryGetValue(d.Index, out var previous))
            {
                if (d.Health > previous && d.Health != HealthState.Unknown)
                {
                    Notify(AlertKind.Health, $"{d.Index}:{d.Health}",
                        "Stato del disco peggiorato",
                        $"{name}: da {Describe(previous)} a {Describe(d.Health)}." +
                        (d.HealthDetail.Length > 0 ? $" {d.HealthDetail}" : ""),
                        important: true);
                }
            }
            _lastHealth[d.Index] = d.Health;
        }
    }

    private static string Describe(HealthState s) => s switch
    {
        HealthState.Good => "Buono",
        HealthState.Caution => "Attenzione",
        HealthState.Bad => "Critico",
        _ => "Sconosciuto",
    };
}
