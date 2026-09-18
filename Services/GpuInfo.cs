using Microsoft.Win32;

namespace DiskTempMonitor.Services;

/// <summary>Identità di una scheda video, indipendente dai sensori.</summary>
public sealed class GpuAdapter
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string DriverDate { get; init; } = "";
    public ulong MemoryBytes { get; init; }

    public string MemoryDisplay => MemoryBytes > 0
        ? SystemInfo.FormatBytes(MemoryBytes)
        : "—";
}

/// <summary>
/// Elenco delle schede video installate, letto dalla chiave di classe degli adattatori
/// di visualizzazione.
/// <para>
/// Serve come base: il nome, il driver e la memoria dedicata ci sono sempre, anche
/// quando i sensori di temperatura non sono disponibili (scheda integrata, driver
/// generico, macchina virtuale).
/// </para>
/// </summary>
public static class GpuInfo
{
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static List<GpuAdapter> Read()
    {
        var adapters = new List<GpuAdapter>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root is null) return adapters;

            foreach (string name in root.GetSubKeyNames())
            {
                // Le sottochiavi degli adattatori sono numerate: 0000, 0001, ...
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is not string desc || desc.Length == 0) continue;

                adapters.Add(new GpuAdapter
                {
                    Name = desc,
                    Vendor = DescribeVendor(desc, key.GetValue("ProviderName") as string ?? ""),
                    DriverVersion = key.GetValue("DriverVersion") as string ?? "",
                    DriverDate = key.GetValue("DriverDate") as string ?? "",
                    MemoryBytes = ReadMemory(key),
                });
            }
        }
        catch { /* chiave non leggibile: si resta senza identità della scheda */ }

        return adapters;
    }

    /// <summary>
    /// La memoria dedicata è un valore a 64 bit su driver recenti e a 32 bit su quelli
    /// vecchi; alcuni la scrivono come stringa esadecimale.
    /// </summary>
    private static ulong ReadMemory(RegistryKey key)
    {
        object?[] candidates =
        [
            key.GetValue("HardwareInformation.qwMemorySize"),
            key.GetValue("HardwareInformation.MemorySize"),
        ];

        foreach (var value in candidates)
        {
            switch (value)
            {
                case long l when l > 0:
                    return (ulong)l;
                case int i when i > 0:
                    return (uint)i;
                case byte[] b when b.Length is 4 or 8:
                    ulong raw = b.Length == 8 ? BitConverter.ToUInt64(b, 0) : BitConverter.ToUInt32(b, 0);
                    if (raw > 0) return raw;
                    break;
            }
        }

        return 0;
    }

    private static string DescribeVendor(string description, string provider)
    {
        if (description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (description.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (description.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return provider;
    }
}
