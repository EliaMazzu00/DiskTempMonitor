namespace DiskTempMonitor.Services;

/// <summary>Una riga della griglia dei core.</summary>
public readonly record struct CoreSample(string Label, float? TemperatureC, float? LoadPercent, float? ClockMhz);

/// <summary>Stato del processore, qualunque sia la fonte da cui arriva.</summary>
public sealed class CpuSnapshot
{
    public string Name { get; init; } = "";
    /// <summary>Da dove arrivano i valori, per poterlo dire all'utente.</summary>
    public string Source { get; init; } = "";
    public int TjMaxC { get; init; }
    public float? PackageC { get; init; }
    public float? TotalLoadPercent { get; init; }
    public float? ClockMhz { get; init; }
    public float? BusClockMhz { get; init; }
    public float? Multiplier { get; init; }
    public float? PowerWatt { get; init; }
    public List<CoreSample> Cores { get; init; } = [];

    private IEnumerable<float> Temperatures => Cores.Where(c => c.TemperatureC.HasValue)
                                                    .Select(c => c.TemperatureC!.Value);

    public float? HottestC
    {
        get
        {
            var values = Temperatures.ToList();
            if (PackageC is float p) values.Add(p);
            return values.Count > 0 ? values.Max() : null;
        }
    }

    public float? AverageC
    {
        get
        {
            var values = Temperatures.ToList();
            return values.Count > 0 ? values.Average() : PackageC;
        }
    }

    /// <summary>Quanti gradi mancano al limite termico: più è alto, meglio è.</summary>
    public float? MarginC => HottestC is float h && TjMaxC > 0 ? TjMaxC - h : null;

    public float? AverageLoadPercent
    {
        get
        {
            var values = Cores.Where(c => c.LoadPercent.HasValue).Select(c => c.LoadPercent!.Value).ToList();
            float? media = values.Count > 0 ? values.Average() : null;

            // Il sensore complessivo vale zero alla primissima lettura, prima che la
            // libreria abbia due campioni da confrontare: se i singoli core dicono altro,
            // meglio la loro media che uno zero che non c'è.
            if (TotalLoadPercent is float t && (t > 0 || media is null or 0)) return t;
            return media;
        }
    }
}

/// <summary>Quale temperatura del processore mostrare quando ce n'è una sola da dare.</summary>
public enum CpuTempMode
{
    /// <summary>Il core più caldo: è il valore che conta per il limite termico.</summary>
    Hottest,
    /// <summary>Media dei core: più stabile, utile per capire l'andamento.</summary>
    Average,
    /// <summary>Package: la lettura unica del contenitore, se il processore la espone.</summary>
    Package,
}

/// <summary>Fotografia di processore e schede video presa nello stesso istante.</summary>
public sealed class SystemSnapshot
{
    public CpuSnapshot? Cpu { get; init; }

    /// <summary>Memoria fisica installata e quanta ne risulta occupata.</summary>
    public ulong MemoryTotalBytes { get; init; }
    public ulong MemoryUsedBytes { get; init; }

    public float? MemoryUsedPercent => MemoryTotalBytes > 0
        ? (float)MemoryUsedBytes / MemoryTotalBytes * 100f
        : null;

    public string? CpuUnavailableReason { get; init; }
    public List<GpuReading> Gpus { get; init; } = [];
    public string? GpuUnavailableReason { get; init; }
}

/// <summary>
/// Ultime letture di sistema, con l'andamento recente: è quello che serve all'area di
/// notifica e al riquadro compatto, che non rileggono i sensori per conto loro.
/// </summary>
public sealed class SystemMetrics
{
    public const int HistoryCapacity = 240;

    public CpuSnapshot? Cpu { get; private set; }
    public IReadOnlyList<GpuReading> Gpus { get; private set; } = [];

    /// <summary>Percentuale di memoria occupata, per il riquadro compatto.</summary>
    public float? MemoryUsedPercent { get; private set; }
    public Queue<int> CpuHistory { get; } = new();
    public Queue<int> GpuHistory { get; } = new();

    public void Apply(SystemSnapshot snapshot)
    {
        Cpu = snapshot.Cpu;
        Gpus = snapshot.Gpus;
        MemoryUsedPercent = snapshot.MemoryUsedPercent;

        Push(CpuHistory, CpuTemperature(CpuTempMode.Hottest));
        Push(GpuHistory, GpuTemperature);
    }

    private static void Push(Queue<int> history, int? value)
    {
        if (value is not int v) return;
        history.Enqueue(v);
        while (history.Count > HistoryCapacity) history.Dequeue();
    }

    public int? CpuTemperature(CpuTempMode mode)
    {
        float? value = mode switch
        {
            CpuTempMode.Average => Cpu?.AverageC,
            CpuTempMode.Package => Cpu?.PackageC ?? Cpu?.HottestC,
            _ => Cpu?.HottestC,
        };
        return value is float f ? (int)Math.Round(f) : null;
    }

    /// <summary>La scheda video da mostrare: la prima che riporta una temperatura.</summary>
    public GpuReading? PrimaryGpu =>
        Gpus.FirstOrDefault(g => g.TemperatureC is not null) ?? Gpus.FirstOrDefault();

    public int? GpuTemperature =>
        PrimaryGpu?.TemperatureC is float t ? (int)Math.Round(t) : null;
}

/// <summary>
/// Unico punto da cui leggere i sensori di processore e scheda video.
/// <para>
/// Per il processore si preferisce Core Temp, se è in esecuzione: pubblica i valori in
/// un blocco di memoria condivisa, quindi si leggono senza caricare nulla. Se non c'è,
/// si ripiega su LibreHardwareMonitor, che per leggere i registri interni della CPU
/// installa un proprio driver. Le schede video passano sempre da lì, perché i valori
/// arrivano dalle librerie NVIDIA/AMD/Intel e non richiedono il driver.
/// </para>
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly object _gate = new();
    private HardwareMonitor? _monitor;
    private bool _startAttempted;
    private bool _disposed;

    /// <summary>Elenco statico delle schede video, letto una sola volta.</summary>
    public IReadOnlyList<GpuAdapter> Adapters { get; } = GpuInfo.Read();

    /// <summary>
    /// Legge tutto. Va chiamata fuori dal thread dell'interfaccia: la prima chiamata
    /// carica la libreria dei sensori e può richiedere qualche secondo.
    /// </summary>
    public SystemSnapshot Read()
    {
        if (_disposed) return new SystemSnapshot();

        // Due letture insieme non si possono fare: la libreria dei sensori aggiorna lo
        // stato dell'hardware in posto, e ci si arriva sia dalla scheda Sistema sia dal
        // giro di aggiornamento periodico.
        lock (_gate)
        {
            if (_disposed) return new SystemSnapshot();

            var monitor = EnsureMonitor();

            // Prima la lettura propria: l'applicazione deve bastare a se stessa. Core
            // Temp resta come riserva, per il caso in cui il driver non possa caricarsi.
            var cpu = FromMonitor(monitor);
            if (cpu is null && CoreTempReader.Read() is { } coreTemp) cpu = FromCoreTemp(coreTemp);

            var gpus = monitor?.ReadGpus() ?? [];

            var (totale, disponibile) = SystemInfo.MemoryStatus();

            return new SystemSnapshot
            {
                Cpu = cpu,
                MemoryTotalBytes = totale,
                MemoryUsedBytes = totale > disponibile ? totale - disponibile : 0,
                CpuUnavailableReason = cpu is not null ? null : DescribeCpuProblem(monitor),
                Gpus = gpus,
                GpuUnavailableReason = gpus.Count > 0 ? null : DescribeGpuProblem(),
            };
        }
    }

    private HardwareMonitor? EnsureMonitor()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (!_startAttempted)
            {
                _startAttempted = true;
                _monitor = new HardwareMonitor();
                _monitor.Start();
            }
            return _monitor;
        }
    }

    // ------------------------------------------------------------- conversioni

    private static CpuSnapshot FromCoreTemp(CoreTempReader.Reading r) => new()
    {
        Name = r.CpuName,
        Source = "Core Temp (memoria condivisa)",
        TjMaxC = r.TjMaxC,
        ClockMhz = r.ClockMhz,
        BusClockMhz = r.BusClockMhz,
        Multiplier = r.Multiplier,
        Cores = r.Cores.Select(c => new CoreSample($"Core {c.Index}", c.TemperatureC, c.LoadPercent, null)).ToList(),
    };

    private static CpuSnapshot? FromMonitor(HardwareMonitor? monitor)
    {
        var reading = monitor?.ReadCpu();
        if (reading is null) return null;

        var cores = reading.Cores
            .Select(c => new CoreSample(c.Name, c.TemperatureC, c.LoadPercent, c.ClockMhz))
            .ToList();

        // Senza almeno una temperatura o un carico non c'è niente da mostrare.
        if (cores.Count == 0 && reading.PackageTemperatureC is null) return null;

        return new CpuSnapshot
        {
            Name = reading.Name,
            Source = "lettura diretta dei sensori",
            TjMaxC = reading.TjMaxC,
            PackageC = reading.PackageTemperatureC,
            TotalLoadPercent = reading.TotalLoadPercent,
            ClockMhz = reading.MaxClockMhz,
            BusClockMhz = reading.BusClockMhz,
            PowerWatt = reading.PackagePowerWatt,
            Cores = cores,
        };
    }

    private static string DescribeCpuProblem(HardwareMonitor? monitor)
    {
        // Le temperature dei core stanno nei registri interni della CPU, leggibili solo
        // dal kernel. L'applicazione si porta dietro il driver che fa da tramite, quindi
        // se qui non arriva niente è perché qualcosa ne ha impedito il caricamento.
        var righe = new List<string> { "Temperature del processore non disponibili." };

        if (monitor?.Error is string error && error.Length > 0) righe.Add(error);

        righe.Add("Il driver di lettura non si è caricato. Di solito è l'Isolamento core di " +
                  "Windows (Sicurezza di Windows → Sicurezza del dispositivo), che blocca " +
                  "questo tipo di driver, oppure un antivirus.");

        if (CoreTempReader.InstalledPath is not null)
            righe.Add("In alternativa Core Temp è installato: avvialo e i valori arrivano da lì.");

        return string.Join("\n", righe);
    }

    private string DescribeGpuProblem() =>
        Adapters.Count == 0
            ? "Nessuna scheda video rilevata."
            : "Sensori della scheda video non disponibili: il modello installato non li espone, " +
              "oppure il driver non permette di leggerli.";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _monitor?.Dispose();
            _monitor = null;
        }
    }
}
