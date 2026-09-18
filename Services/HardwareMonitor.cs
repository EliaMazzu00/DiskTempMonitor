using LibreHardwareMonitor.Hardware;

namespace DiskTempMonitor.Services;

public sealed class CoreReading
{
    /// <summary>Come lo chiama il processore: "P-Core 3", "E-Core 11", "Core 5".</summary>
    public string Name { get; init; } = "";
    public float? TemperatureC { get; init; }
    public float? LoadPercent { get; init; }
    public float? ClockMhz { get; init; }
}

public sealed class CpuReading
{
    public string Name { get; init; } = "";
    public float? PackageTemperatureC { get; init; }
    public float? TotalLoadPercent { get; init; }
    public float? PackagePowerWatt { get; init; }
    public float? BusClockMhz { get; init; }
    public float? MaxClockMhz { get; init; }
    /// <summary>Limite termico, ricavato dalla distanza dichiarata per un core.</summary>
    public int TjMaxC { get; init; }
    public List<CoreReading> Cores { get; init; } = [];

    /// <summary>Temperatura più alta fra package e singoli core.</summary>
    public float? HottestC
    {
        get
        {
            var values = Cores.Where(c => c.TemperatureC.HasValue).Select(c => c.TemperatureC!.Value).ToList();
            if (PackageTemperatureC is float p) values.Add(p);
            return values.Count > 0 ? values.Max() : null;
        }
    }
}

public sealed class GpuReading
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public float? CoreTemperatureC { get; init; }
    public float? HotSpotTemperatureC { get; init; }
    public float? MemoryTemperatureC { get; init; }
    public float? CoreLoadPercent { get; init; }
    public float? MemoryLoadPercent { get; init; }
    public float? CoreClockMhz { get; init; }
    public float? MemoryClockMhz { get; init; }
    public float? PowerWatt { get; init; }
    public float? FanPercent { get; init; }
    public float? FanRpm { get; init; }
    public float? MemoryUsedMb { get; init; }
    public float? MemoryTotalMb { get; init; }

    /// <summary>La temperatura da mostrare: il core, o il punto più caldo se è l'unico.</summary>
    public float? TemperatureC => CoreTemperatureC ?? HotSpotTemperatureC ?? MemoryTemperatureC;

    /// <summary>
    /// Quanta memoria della scheda risulta occupata, in percentuale.
    /// <para>
    /// Si ricava da quanta ne è in uso sul totale, non dal sensore di carico della
    /// memoria: il nome "GPU Memory" copre due misure diverse. Su una Radeon indica
    /// quanto <i>lavora</i> la memoria — su questa macchina segnava 20% mentre ne erano
    /// occupati 7,6 GB su 8,1 — mentre su una GeForce indica proprio l'occupazione.
    /// Il rapporto fra usata e totale invece vuol dire sempre la stessa cosa, e
    /// coincide con la "memoria GPU dedicata" di Gestione attività; il sensore resta
    /// come ripiego per le schede che non dicono quanta memoria hanno in uso.
    /// </para>
    /// </summary>
    public float? VramUsedPercent =>
        MemoryUsedMb is float used && MemoryTotalMb is float total && total > 0
            ? Math.Clamp(used / total * 100f, 0f, 100f)
            : VramLoadPercent;

    /// <summary>Occupazione della memoria letta direttamente, dove la scheda la espone.</summary>
    public float? VramLoadPercent { get; init; }
}

/// <summary>
/// Lettura dei sensori del processore tramite LibreHardwareMonitor.
/// <para>
/// Le temperature del processore non sono accessibili da un normale programma: stanno in
/// registri interni alla CPU (MSR) che solo il kernel può leggere. La libreria installa
/// perciò un driver che fa da tramite. Richiede privilegi di amministratore e può essere
/// bloccata se è attivo l'Isolamento core di Windows: in quel caso i sensori non
/// compaiono e l'applicazione continua a funzionare senza.
/// </para>
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsGpuEnabled = true,
        IsStorageEnabled = false,        // i dischi li leggiamo già per conto nostro
        IsNetworkEnabled = false,
        IsControllerEnabled = false,
        IsBatteryEnabled = false,
    };

    private bool _open;
    private bool _disposed;

    public bool Available { get; private set; }
    public bool GpuAvailable { get; private set; }
    public string? Error { get; private set; }

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public void Start()
    {
        if (_open || _disposed) return;
        try
        {
            _computer.Open();
            _open = true;
            Available = _computer.Hardware.Any(h => h.HardwareType == HardwareType.Cpu);
            GpuAvailable = _computer.Hardware.Any(IsGpu);
            if (!Available)
                Error = "Nessun sensore del processore rilevato: il driver di lettura potrebbe essere " +
                        "bloccato dall'Isolamento core di Windows.";
        }
        catch (Exception ex)
        {
            Available = false;
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Rilegge i sensori e restituisce lo stato del processore.
    /// <para>
    /// I nomi non coincidono fra i vari tipi di sensore: sui processori ibridi Intel le
    /// temperature e le frequenze arrivano come "P-Core #n" ed "E-Core #n", mentre i
    /// carichi restano numerati di seguito come "CPU Core #n", con una voce per thread.
    /// Si costruisce perciò l'elenco dei core dalle temperature, nell'ordine in cui la
    /// libreria li espone, e lo si accoppia posizionalmente con i carichi.
    /// </para>
    /// </summary>
    public CpuReading? ReadCpu()
    {
        if (!_open || _disposed) return null;

        try
        {
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu is null) return null;

            cpu.Update();
            foreach (var sub in cpu.SubHardware) sub.Update();

            var temperatures = CoreSensors(cpu, SensorType.Temperature);
            var clocks = CoreSensors(cpu, SensorType.Clock);
            var loads = CoreLoads(cpu);

            // L'elenco dei core segue le temperature; se mancano (driver bloccato) si
            // ripiega sulle frequenze, che si leggono anche senza.
            var labels = temperatures.Count > 0 ? temperatures.Keys.ToList() : clocks.Keys.ToList();

            var cores = new List<CoreReading>();
            for (int i = 0; i < labels.Count; i++)
            {
                string label = labels[i];
                cores.Add(new CoreReading
                {
                    Name = label.Replace("#", "").Replace("  ", " ").Trim(),
                    TemperatureC = temperatures.GetValueOrDefault(label),
                    ClockMhz = clocks.GetValueOrDefault(label),
                    LoadPercent = i < loads.Count ? loads[i] : null,
                });
            }

            float? maxClock = cores.Where(c => c.ClockMhz.HasValue).Select(c => c.ClockMhz!.Value)
                                   .DefaultIfEmpty(0).Max();

            return new CpuReading
            {
                Name = cpu.Name,
                PackageTemperatureC = Find(cpu, SensorType.Temperature, "CPU Package", "Core Max"),
                TotalLoadPercent = Find(cpu, SensorType.Load, "CPU Total"),
                PackagePowerWatt = Find(cpu, SensorType.Power, "CPU Package"),
                BusClockMhz = Find(cpu, SensorType.Clock, "Bus Speed"),
                MaxClockMhz = maxClock > 0 ? maxClock : null,
                TjMaxC = ReadTjMax(cpu, temperatures),
                Cores = cores,
            };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Sensori per core del tipo indicato, nell'ordine in cui la libreria li espone.
    /// Si escludono i riepiloghi ("Core Max", "CPU Package") e le distanze dal limite.
    /// </summary>
    private static Dictionary<string, float?> CoreSensors(IHardware cpu, SensorType type)
    {
        var found = new List<ISensor>();

        foreach (var s in cpu.Sensors)
        {
            if (s.SensorType != type) continue;
            if (!s.Name.Contains('#')) continue;
            if (s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Name.Contains("Thread", StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(s);
        }

        // L'ordine deve essere quello con cui sono numerati i carichi: prima i core ad
        // alte prestazioni, poi quelli a basso consumo, ciascun gruppo per numero.
        var result = new Dictionary<string, float?>();
        foreach (var s in found.OrderBy(s => Rank(s.Name)).ThenBy(s => Number(s.Name)))
            result.TryAdd(s.Name, s.Value);

        return result;
    }

    private static int Rank(string name) =>
        name.StartsWith("E-Core", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static int Number(string name)
    {
        int hash = name.IndexOf('#');
        if (hash < 0) return 0;
        var digits = new string(name[(hash + 1)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int value) ? value : 0;
    }

    /// <summary>
    /// Carico di ogni core fisico, in ordine. I processori con multi-thread espongono
    /// una voce per thread ("CPU Core #1 Thread #2"): del core conta il thread più
    /// carico, che è quello che ne determina la temperatura.
    /// </summary>
    private static List<float?> CoreLoads(IHardware cpu)
    {
        var byCore = new SortedDictionary<int, float?>();

        foreach (var s in cpu.Sensors)
        {
            if (s.SensorType != SensorType.Load) continue;
            if (!s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)) continue;

            var digits = new string(s.Name["CPU Core #".Length..].TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int index)) continue;

            float? current = byCore.GetValueOrDefault(index);
            if (s.Value is float v && (current is null || v > current)) byCore[index] = v;
            else byCore.TryAdd(index, current);
        }

        return byCore.Values.ToList();
    }

    /// <summary>
    /// Il limite termico non è un sensore a sé: si ricava sommando a una temperatura di
    /// core la distanza dal limite che la libreria espone accanto.
    /// </summary>
    private static int ReadTjMax(IHardware cpu, Dictionary<string, float?> temperatures)
    {
        foreach (var (name, value) in temperatures)
        {
            if (value is not float temp) continue;

            var distance = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                s.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase));

            if (distance?.Value is float d && d >= 0) return (int)Math.Round(temp + d);
        }

        return 0;
    }

    /// <summary>
    /// Stato di tutte le schede video riconosciute. Per NVIDIA e AMD i valori arrivano
    /// dalle librerie del produttore, quindi senza bisogno del driver di lettura dei
    /// registri: se le temperature del processore non ci sono, quelle della scheda video
    /// possono esserci lo stesso.
    /// </summary>
    public List<GpuReading> ReadGpus()
    {
        var result = new List<GpuReading>();
        if (!_open || _disposed) return result;

        try
        {
            foreach (var gpu in _computer.Hardware.Where(IsGpu))
            {
                gpu.Update();
                foreach (var sub in gpu.SubHardware) sub.Update();

                float? Temp(params string[] names) => Find(gpu, SensorType.Temperature, names);
                float? Load(params string[] names) => Find(gpu, SensorType.Load, names);
                float? Clock(params string[] names) => Find(gpu, SensorType.Clock, names);

                result.Add(new GpuReading
                {
                    Name = gpu.Name,
                    Vendor = gpu.HardwareType switch
                    {
                        HardwareType.GpuNvidia => "NVIDIA",
                        HardwareType.GpuAmd => "AMD",
                        HardwareType.GpuIntel => "Intel",
                        _ => "",
                    },
                    CoreTemperatureC = Temp("GPU Core", "GPU Temperature", "Temperature"),
                    HotSpotTemperatureC = Temp("GPU Hot Spot", "Hot Spot"),
                    MemoryTemperatureC = Temp("GPU Memory Junction", "GPU VR Memory", "Memory"),
                    CoreLoadPercent = Load("GPU Core", "D3D 3D"),
                    MemoryLoadPercent = Load("GPU Memory", "GPU Memory Controller"),
                    CoreClockMhz = Clock("GPU Core"),
                    MemoryClockMhz = Clock("GPU Memory"),
                    PowerWatt = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "Power"),
                    FanPercent = Find(gpu, SensorType.Control, "GPU Fan", "Fan"),
                    FanRpm = Find(gpu, SensorType.Fan, "GPU Fan", "Fan"),
                    MemoryUsedMb = Find(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
                    MemoryTotalMb = Find(gpu, SensorType.SmallData, "GPU Memory Total"),

                    // Solo il nome esatto: "GPU Memory Controller" è un'altra misura e
                    // una ricerca parziale la prenderebbe per buona.
                    VramLoadPercent = gpu.Sensors.FirstOrDefault(sensor =>
                        sensor.SensorType == SensorType.Load &&
                        string.Equals(sensor.Name, "GPU Memory", StringComparison.OrdinalIgnoreCase))?.Value,
                });
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Primo sensore del tipo indicato il cui nome corrisponde a uno di quelli cercati.
    /// I nomi cambiano fra NVIDIA, AMD e Intel: si prova in ordine di preferenza.
    /// </summary>
    private static float? Find(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (string wanted in names)
        {
            var exact = hardware.Sensors.FirstOrDefault(s => s.SensorType == type &&
                s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact?.Value is float v) return v;
        }

        foreach (string wanted in names)
        {
            var partial = hardware.Sensors.FirstOrDefault(s => s.SensorType == type &&
                s.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (partial?.Value is float v) return v;
        }

        return null;
    }

    /// <summary>Quanta memoria risulta in uso, secondo i sensori della scheda madre.</summary>
    public (float? usedGb, float? availableGb) ReadMemory()
    {
        if (!_open || _disposed) return (null, null);
        try
        {
            var mem = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
            if (mem is null) return (null, null);

            mem.Update();
            float? used = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data &&
                s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase) &&
                !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))?.Value;
            float? free = mem.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data &&
                s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase) &&
                !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))?.Value;
            return (used, free);
        }
        catch { return (null, null); }
    }

    /// <summary>Nome della scheda madre, se la libreria riesce a leggerlo.</summary>
    public string? MotherboardName
    {
        get
        {
            if (!_open) return null;
            return _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?.Name;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_open) _computer.Close(); } catch { /* driver già scaricato */ }
    }
}
