using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;

namespace DiskTempMonitor.Services;

public sealed class CacheLevel
{
    public int Level { get; init; }
    public string Type { get; init; } = "";
    public int SizeKb { get; init; }
    public int Ways { get; init; }
    public int LineSize { get; init; }
    public int Instances { get; init; }

    public string Display => Instances > 1
        ? $"{Instances} × {Format(SizeKb)}  ({Ways} vie, linea {LineSize} B)"
        : $"{Format(SizeKb)}  ({Ways} vie, linea {LineSize} B)";

    private static string Format(int kb) => kb >= 1024 ? $"{kb / 1024.0:0.##} MB" : $"{kb} KB";
}

/// <summary>
/// Identità e caratteristiche del processore, lette con l'istruzione <c>CPUID</c> e
/// dalle informazioni che Windows espone. Nessun driver: sono dati che la CPU stessa
/// restituisce a qualunque programma.
/// </summary>
public sealed class CpuInfo
{
    public string Vendor { get; private set; } = "";
    public string BrandString { get; private set; } = "";
    public string CodeName { get; private set; } = "";
    public int Family { get; private set; }
    public int Model { get; private set; }
    public int Stepping { get; private set; }
    public string Microarchitecture { get; private set; } = "";
    public int PhysicalCores { get; private set; }
    public int LogicalCores { get; private set; }
    public int BaseClockMhz { get; private set; }
    public List<CacheLevel> Caches { get; } = [];
    public List<string> Features { get; } = [];

    public bool HyperThreading => LogicalCores > PhysicalCores;

    public static CpuInfo Read()
    {
        var info = new CpuInfo();
        info.ReadCpuId();
        info.ReadTopology();
        info.ReadRegistry();
        return info;
    }

    // ------------------------------------------------------------------ CPUID

    private void ReadCpuId()
    {
        if (!X86Base.IsSupported) return;

        var (maxLeaf, ebx0, ecx0, edx0) = X86Base.CpuId(0, 0);
        Vendor = Chars(ebx0) + Chars(edx0) + Chars(ecx0);

        // Nome commerciale: tre foglie estese da 16 caratteri ciascuna
        var brand = new StringBuilder();
        for (int leaf = unchecked((int)0x80000002); leaf <= unchecked((int)0x80000004); leaf++)
        {
            var (a, b, c, d) = X86Base.CpuId(leaf, 0);
            brand.Append(Chars(a)).Append(Chars(b)).Append(Chars(c)).Append(Chars(d));
        }
        BrandString = brand.ToString().Replace("\0", "").Trim();

        var (eax1, _, ecx1, edx1) = X86Base.CpuId(1, 0);
        Stepping = eax1 & 0xF;
        int model = (eax1 >> 4) & 0xF;
        int family = (eax1 >> 8) & 0xF;
        int extModel = (eax1 >> 16) & 0xF;
        int extFamily = (eax1 >> 20) & 0xFF;

        Family = family == 0xF ? family + extFamily : family;
        Model = family is 0x6 or 0xF ? model + (extModel << 4) : model;
        Microarchitecture = DescribeIntel(Family, Model, Vendor);

        // Istruzioni supportate, quelle che interessa davvero vedere elencate
        void Flag(bool present, string name) { if (present) Features.Add(name); }

        Flag((edx1 & (1 << 23)) != 0, "MMX");
        Flag((edx1 & (1 << 25)) != 0, "SSE");
        Flag((edx1 & (1 << 26)) != 0, "SSE2");
        Flag((ecx1 & (1 << 0)) != 0, "SSE3");
        Flag((ecx1 & (1 << 9)) != 0, "SSSE3");
        Flag((ecx1 & (1 << 19)) != 0, "SSE4.1");
        Flag((ecx1 & (1 << 20)) != 0, "SSE4.2");
        Flag((ecx1 & (1 << 28)) != 0, "AVX");
        Flag((ecx1 & (1 << 25)) != 0, "AES");
        Flag((ecx1 & (1 << 12)) != 0, "FMA3");
        Flag((ecx1 & (1 << 30)) != 0, "RDRAND");
        Flag((ecx1 & (1 << 5)) != 0, "VT-x");

        if (maxLeaf >= 7)
        {
            var (_, ebx7, ecx7, _) = X86Base.CpuId(7, 0);
            Flag((ebx7 & (1 << 5)) != 0, "AVX2");
            Flag((ebx7 & (1 << 3)) != 0, "BMI1");
            Flag((ebx7 & (1 << 8)) != 0, "BMI2");
            Flag((ebx7 & (1 << 16)) != 0, "AVX-512F");
            Flag((ebx7 & (1 << 29)) != 0, "SHA");
            Flag((ecx7 & (1 << 9)) != 0, "VAES");
        }

        ReadCaches(maxLeaf);
    }

    private void ReadCaches(int maxLeaf)
    {
        if (maxLeaf < 4) return;

        for (int sub = 0; sub < 8; sub++)
        {
            var (eax, ebx, ecx, _) = X86Base.CpuId(4, sub);
            int type = eax & 0x1F;
            if (type == 0) break;                       // fine dell'elenco

            int level = (eax >> 5) & 0x7;
            int ways = ((ebx >> 22) & 0x3FF) + 1;
            int partitions = ((ebx >> 12) & 0x3FF) + 1;
            int lineSize = (ebx & 0xFFF) + 1;
            int sets = ecx + 1;
            int sizeKb = ways * partitions * lineSize * sets / 1024;

            Caches.Add(new CacheLevel
            {
                Level = level,
                Type = type switch { 1 => "dati", 2 => "istruzioni", 3 => "unificata", _ => "" },
                SizeKb = sizeKb,
                Ways = ways,
                LineSize = lineSize,
            });
        }
    }

    private static string Chars(int register) =>
        new(BitConverter.GetBytes(register).Select(b => (char)b).ToArray());

    /// <summary>Nome della microarchitettura, per le famiglie più diffuse.</summary>
    private static string DescribeIntel(int family, int model, string vendor)
    {
        if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return family switch
            {
                0x17 => "Zen / Zen+ / Zen 2",
                0x19 => "Zen 3 / Zen 4",
                0x1A => "Zen 5",
                _ => "",
            };
        }

        if (family != 6) return "";
        return model switch
        {
            0x9E or 0x8E => "Coffee Lake / Kaby Lake",
            0xA5 or 0xA6 => "Comet Lake",
            0x7E or 0x7D => "Ice Lake",
            0x8C or 0x8D => "Tiger Lake",
            0x97 or 0x9A or 0xBF => "Alder Lake",
            0xB7 or 0xBA or 0xBE => "Raptor Lake",
            0x5E or 0x5F => "Skylake",
            0x3C or 0x3F or 0x45 or 0x46 => "Haswell",
            0x4E or 0x55 => "Skylake",
            _ => "",
        };
    }

    // ---------------------------------------------------- topologia dei core

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfoNative
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public IntPtr MinimumApplicationAddress;
        public IntPtr MaximumApplicationAddress;
        public IntPtr ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetSystemInfo(out SystemInfoNative info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref int returnedLength);

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    private void ReadTopology()
    {
        GetSystemInfo(out var si);
        LogicalCores = (int)si.NumberOfProcessors;
        PhysicalCores = CountRelations(RelationProcessorCore);
        if (PhysicalCores == 0) PhysicalCores = LogicalCores;

        CountCacheInstances();
    }

    /// <summary>Conta le voci restituite per un tipo di relazione fra processori.</summary>
    private static int CountRelations(int relation)
    {
        int length = 0;
        GetLogicalProcessorInformationEx(relation, IntPtr.Zero, ref length);
        if (length == 0) return 0;

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relation, buffer, ref length)) return 0;

            int count = 0, offset = 0;
            while (offset < length)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4) ...
                int size = Marshal.ReadInt32(buffer + offset + 4);
                if (size <= 0) break;
                count++;
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Quante istanze di ogni livello di cache esistono nel sistema.</summary>
    private void CountCacheInstances()
    {
        int length = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
        if (length == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length)) return;

            var counts = new Dictionary<(int level, int type), int>();
            int offset = 0;
            while (offset < length)
            {
                int size = Marshal.ReadInt32(buffer + offset + 4);
                if (size <= 0) break;

                // CACHE_RELATIONSHIP comincia dopo Relationship e Size
                byte level = Marshal.ReadByte(buffer + offset + 8);
                byte type = Marshal.ReadByte(buffer + offset + 11);   // 0 unified, 1 instr, 2 data, 3 trace

                int mapped = type switch { 1 => 2, 2 => 1, 3 => 2, _ => 3 };
                var key = (level, mapped);
                counts[key] = counts.GetValueOrDefault(key) + 1;

                offset += size;
            }

            // Ciclo per indice: sostituire elementi mentre si enumera la lista
            // farebbe fallire l'enumerazione.
            for (int i = 0; i < Caches.Count; i++)
            {
                var cache = Caches[i];
                int typeCode = cache.Type switch { "dati" => 1, "istruzioni" => 2, _ => 3 };
                if (!counts.TryGetValue((cache.Level, typeCode), out int n)) continue;

                Caches[i] = new CacheLevel
                {
                    Level = cache.Level,
                    Type = cache.Type,
                    SizeKb = cache.SizeKb,
                    Ways = cache.Ways,
                    LineSize = cache.LineSize,
                    Instances = n,
                };
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ------------------------------------------------------------- registro

    private void ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is null) return;

            if (BrandString.Length == 0 && key.GetValue("ProcessorNameString") is string name)
                BrandString = name.Trim();

            if (key.GetValue("~MHz") is int mhz) BaseClockMhz = mhz;
        }
        catch { /* registro non leggibile: restano i dati da CPUID */ }
    }
}
