using System.IO.MemoryMappedFiles;
using System.Text;

namespace DiskTempMonitor.Services;

/// <summary>
/// Legge le temperature del processore dalla memoria condivisa che Core Temp pubblica.
/// <para>
/// Le temperature dei core stanno in registri interni alla CPU leggibili solo dal
/// kernel. Core Temp ha un proprio driver firmato e mette i valori a disposizione in un
/// blocco di memoria condivisa documentato, pensato esattamente perché altri programmi
/// li usino: è la via più sicura per averli senza installare un secondo driver.
/// </para>
/// </summary>
public static class CoreTempReader
{
    // Disposizione del blocco condiviso (CoreTempSharedData del relativo SDK)
    private const int OffLoad = 0;              // uint[256]  carico per core
    private const int OffTjMax = 1024;          // uint[128]  limite termico per socket
    private const int OffCoreCount = 1536;
    private const int OffCpuCount = 1540;
    private const int OffTemp = 1544;           // float[256] temperatura per core
    private const int OffCpuSpeed = 2572;       // float      MHz correnti
    private const int OffFsbSpeed = 2576;
    private const int OffMultiplier = 2580;
    private const int OffCpuName = 2584;        // char[100]
    private const int OffFahrenheit = 2684;
    private const int OffDeltaToTjMax = 2685;

    public sealed class Reading
    {
        public string CpuName { get; init; } = "";
        public int CoreCount { get; init; }
        public int SocketCount { get; init; }
        public float ClockMhz { get; init; }
        public float BusClockMhz { get; init; }
        public float Multiplier { get; init; }
        public int TjMaxC { get; init; }
        public List<(int Index, float TemperatureC, int LoadPercent)> Cores { get; } = [];

        public float? HottestC => Cores.Count > 0 ? Cores.Max(c => c.TemperatureC) : null;
        public float? AverageC => Cores.Count > 0 ? Cores.Average(c => c.TemperatureC) : null;

        /// <summary>Quanti gradi mancano al limite termico: più è alto, meglio è.</summary>
        public float? MarginC => HottestC is float h && TjMaxC > 0 ? TjMaxC - h : null;
    }

    public static bool IsRunning => TryOpen(out var mmf) && Dispose(mmf);

    /// <summary>
    /// Percorso di Core Temp, se risulta installato. Serve a distinguere i due casi che
    /// per l'utente sono molto diversi: il programma non c'è, oppure c'è e basta avviarlo.
    /// </summary>
    public static string? InstalledPath
    {
        get
        {
            foreach (var cartella in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                     })
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(cartella), "Core Temp", "Core Temp.exe");
                    if (File.Exists(path)) return path;
                }
                catch { /* cartella non accessibile */ }
            }

            return null;
        }
    }

    /// <summary>
    /// Avvia Core Temp, se installato. È un programma che l'utente ha scelto di avere:
    /// qui lo si lancia solo su sua richiesta esplicita.
    /// </summary>
    public static bool Launch()
    {
        if (InstalledPath is not string path) return false;

        try
        {
            using var processo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
            return processo is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool Dispose(MemoryMappedFile? mmf) { mmf?.Dispose(); return true; }

    private static bool TryOpen(out MemoryMappedFile? mmf)
    {
        foreach (string name in new[] { "CoreTempMappingObjectEx", "CoreTempMappingObject" })
        {
            try
            {
                mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                return true;
            }
            catch { /* nome successivo */ }
        }
        mmf = null;
        return false;
    }

    public static Reading? Read()
    {
        if (!TryOpen(out var mmf) || mmf is null) return null;

        try
        {
            using (mmf)
            using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                int coreCount = (int)view.ReadUInt32(OffCoreCount);
                int cpuCount = (int)view.ReadUInt32(OffCpuCount);
                if (coreCount is <= 0 or > 128) return null;

                var nameBytes = new byte[100];
                view.ReadArray(OffCpuName, nameBytes, 0, nameBytes.Length);

                bool fahrenheit = view.ReadByte(OffFahrenheit) != 0;
                bool deltaToTjMax = view.ReadByte(OffDeltaToTjMax) != 0;
                int tjMax = (int)view.ReadUInt32(OffTjMax);

                var reading = new Reading
                {
                    CpuName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' '),
                    CoreCount = coreCount,
                    SocketCount = Math.Max(1, cpuCount),
                    ClockMhz = view.ReadSingle(OffCpuSpeed),
                    BusClockMhz = view.ReadSingle(OffFsbSpeed),
                    Multiplier = view.ReadSingle(OffMultiplier),
                    TjMaxC = tjMax,
                };

                int total = Math.Min(coreCount * Math.Max(1, cpuCount), 256);
                for (int i = 0; i < total; i++)
                {
                    float value = view.ReadSingle(OffTemp + i * 4);
                    if (float.IsNaN(value) || value <= 0) continue;

                    // Core Temp può esporre i valori nell'unità scelta dall'utente,
                    // oppure come distanza dal limite termico: qui si normalizza in °C.
                    float celsius = fahrenheit ? (value - 32f) * 5f / 9f : value;
                    if (deltaToTjMax && tjMax > 0) celsius = tjMax - celsius;

                    int load = (int)view.ReadUInt32(OffLoad + i * 4);
                    reading.Cores.Add((i, celsius, Math.Clamp(load, 0, 100)));
                }

                return reading.Cores.Count > 0 ? reading : null;
            }
        }
        catch
        {
            return null;
        }
    }
}
