using System.Text.Json;
using System.Text.Json.Serialization;
using DiskTempMonitor.Models;

namespace DiskTempMonitor.Services;

/// <summary>Una esecuzione di autodiagnosi, come la ricorda l'applicazione.</summary>
public sealed class SelfTestRun
{
    /// <summary>Identifica il disco fra un avvio e l'altro, anche se cambia numero.</summary>
    public string DiskKey { get; set; } = "";
    public string DiskName { get; set; } = "";
    public SelfTestKind Kind { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Outcome { get; set; } = "";
    public bool? Passed { get; set; }
    public bool Aborted { get; set; }

    [JsonIgnore]
    public bool Running => EndedAt is null;

    [JsonIgnore]
    public TimeSpan Duration => (EndedAt ?? DateTime.Now) - StartedAt;

    [JsonIgnore]
    public string KindDisplay => Kind switch
    {
        SelfTestKind.Short => "Breve",
        SelfTestKind.Extended => "Esteso",
        _ => "Interruzione",
    };

    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0} s";
            if (d.TotalHours < 1) return $"{d.Minutes} min {d.Seconds:00} s";
            return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        }
    }
}

/// <summary>
/// Memoria delle autodiagnosi eseguite dall'applicazione.
/// <para>
/// Il disco, per conto suo, ricorda soltanto l'esito dell'ultima: non tiene un registro
/// di quando sia stata avviata, quanto sia durata o se l'abbia interrotta l'utente. Se
/// quelle cose si vogliono vedere bisogna annotarle qui, e questo file è l'unico posto
/// dove esistono — per questo la voce "cancella" svuota questo elenco, non il disco.
/// </para>
/// </summary>
public sealed class SelfTestHistory
{
    private const int MaxRuns = 200;

    private readonly List<SelfTestRun> _runs = [];

    public static string HistoryPath { get; } = Path.Combine(
        Path.GetDirectoryName(AppSettings.SettingsPath)!, "autotest.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Le esecuzioni, dalla più recente alla più vecchia.</summary>
    public IReadOnlyList<SelfTestRun> Runs => _runs;

    public static string KeyFor(DiskInfo disk) =>
        $"{disk.SerialNumber}|{disk.Model}".Trim('|');

    public static SelfTestHistory Load()
    {
        var history = new SelfTestHistory();
        try
        {
            if (File.Exists(HistoryPath))
            {
                var caricate = JsonSerializer.Deserialize<List<SelfTestRun>>(
                    File.ReadAllText(HistoryPath), JsonOpts);
                if (caricate is not null) history._runs.AddRange(caricate);
            }
        }
        catch { /* file illeggibile: si riparte da un elenco vuoto */ }

        // Una diagnosi che risultava in corso quando l'applicazione è stata chiusa non
        // può più essere seguita: il disco l'ha finita per conto suo, o l'ha persa in uno
        // spegnimento. Si chiude come esito sconosciuto invece di restare appesa.
        foreach (var run in history._runs.Where(r => r.Running))
        {
            run.EndedAt = run.StartedAt;
            run.Outcome = "Esito non osservato: l'applicazione è stata chiusa durante la verifica";
        }

        history.Sort();
        return history;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_runs, JsonOpts));
        }
        catch { /* nessun blocco dell'app se il salvataggio fallisce */ }
    }

    /// <summary>Registra una diagnosi appena avviata e la restituisce.</summary>
    public SelfTestRun Begin(DiskInfo disk, SelfTestKind kind)
    {
        var run = new SelfTestRun
        {
            DiskKey = KeyFor(disk),
            DiskName = disk.DisplayName,
            Kind = kind,
            StartedAt = DateTime.Now,
        };

        _runs.Insert(0, run);
        while (_runs.Count > MaxRuns) _runs.RemoveAt(_runs.Count - 1);

        Save();
        return run;
    }

    public void Complete(SelfTestRun run, string outcome, bool? passed, bool aborted = false)
    {
        run.EndedAt = DateTime.Now;
        run.Outcome = outcome;
        run.Passed = passed;
        run.Aborted = aborted;
        Save();
    }

    /// <summary>La diagnosi che risulta ancora in corso su quel disco, se c'è.</summary>
    public SelfTestRun? Active(string diskKey) =>
        _runs.FirstOrDefault(r => r.Running && r.DiskKey == diskKey);

    public IEnumerable<SelfTestRun> For(string diskKey) => _runs.Where(r => r.DiskKey == diskKey);

    public void Clear(string? diskKey = null)
    {
        if (diskKey is null) _runs.Clear();
        else _runs.RemoveAll(r => r.DiskKey == diskKey);
        Save();
    }

    private void Sort() =>
        _runs.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
}
