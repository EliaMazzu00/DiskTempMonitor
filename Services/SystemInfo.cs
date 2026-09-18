using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DiskTempMonitor.Services;

public sealed class MemoryModule
{
    public string Slot { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public ulong SizeBytes { get; init; }
    public int SpeedMhz { get; init; }
    public string Type { get; init; } = "";
}

/// <summary>
/// Memoria, scheda madre e BIOS. I moduli di memoria vengono letti dalle tabelle SMBIOS
/// che il firmware espone, senza passare da WMI: sono le stesse informazioni, ottenute
/// con una singola chiamata di sistema.
/// </summary>
public sealed class SystemInfo
{
    public ulong TotalMemoryBytes { get; private set; }
    public ulong AvailableMemoryBytes { get; private set; }
    public List<MemoryModule> Modules { get; } = [];

    public string BoardManufacturer { get; private set; } = "";
    public string BoardProduct { get; private set; } = "";
    public string BiosVendor { get; private set; } = "";
    public string BiosVersion { get; private set; } = "";
    public string BiosDate { get; private set; } = "";
    public string SystemManufacturer { get; private set; } = "";
    public string SystemProduct { get; private set; } = "";

    public ulong UsedMemoryBytes => TotalMemoryBytes > AvailableMemoryBytes
        ? TotalMemoryBytes - AvailableMemoryBytes
        : 0;

    public static SystemInfo Read()
    {
        var info = new SystemInfo();
        info.ReadMemoryStatus();
        info.ReadFirmwareRegistry();
        info.ReadSmbios();
        return info;
    }

    // ------------------------------------------------------------- memoria

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// Solo lo stato della memoria, senza leggere le tabelle del firmware.
    /// <para>
    /// <see cref="Read"/> interroga anche l'SMBIOS per i banchi installati, che è lento e
    /// non cambia mai: per un valore da aggiornare a ogni giro serve questa, che è una
    /// sola chiamata di sistema.
    /// </para>
    /// </summary>
    public static (ulong Total, ulong Available) MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : (0, 0);
    }

    private void ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return;

        TotalMemoryBytes = status.TotalPhys;
        AvailableMemoryBytes = status.AvailPhys;
    }

    // --------------------------------------------------- scheda madre e BIOS

    private void ReadFirmwareRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null) return;

            BoardManufacturer = Text(key, "BaseBoardManufacturer");
            BoardProduct = Text(key, "BaseBoardProduct");
            BiosVendor = Text(key, "BIOSVendor");
            BiosVersion = Text(key, "BIOSVersion");
            BiosDate = Text(key, "BIOSReleaseDate");
            SystemManufacturer = Text(key, "SystemManufacturer");
            SystemProduct = Text(key, "SystemProductName");
        }
        catch { /* niente dati di firmware */ }

        static string Text(RegistryKey key, string name) => key.GetValue(name) as string ?? "";
    }

    // ----------------------------------------------------------- SMBIOS

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    private const uint RawSmbios = 0x52534D42;      // 'RSMB'

    /// <summary>
    /// Scorre le tabelle SMBIOS cercando quelle di tipo 17 (Memory Device), una per
    /// banco di memoria, e ne estrae capacità, velocità, produttore e sigla.
    /// </summary>
    private void ReadSmbios()
    {
        try
        {
            uint size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
            if (size == 0) return;

            var buffer = new byte[size];
            if (GetSystemFirmwareTable(RawSmbios, 0, buffer, size) == 0) return;

            // Intestazione RawSMBIOSData: 8 byte, poi i dati veri
            int pos = 8;
            while (pos + 4 < buffer.Length)
            {
                byte type = buffer[pos];
                byte length = buffer[pos + 1];
                if (length < 4) break;

                int stringsStart = pos + length;
                var strings = ReadStrings(buffer, stringsStart, out int next);

                if (type == 17 && length >= 0x15) ParseMemoryDevice(buffer, pos, length, strings);
                if (type == 127) break;                   // fine tabella

                pos = next;
            }
        }
        catch { /* firmware che non espone le tabelle */ }
    }

    private void ParseMemoryDevice(byte[] b, int offset, byte length, List<string> strings)
    {
        ushort sizeField = BitConverter.ToUInt16(b, offset + 0x0C);
        if (sizeField == 0) return;                        // banco vuoto

        ulong bytes;
        if (sizeField == 0x7FFF && length >= 0x20)
        {
            uint extended = BitConverter.ToUInt32(b, offset + 0x1C) & 0x7FFFFFFF;
            bytes = (ulong)extended * 1024 * 1024;
        }
        else
        {
            bool kilobytes = (sizeField & 0x8000) != 0;
            ulong value = (ulong)(sizeField & 0x7FFF);
            bytes = kilobytes ? value * 1024 : value * 1024 * 1024;
        }

        int speed = length >= 0x17 ? BitConverter.ToUInt16(b, offset + 0x15) : 0;
        // Dalla revisione 3.1 la velocità effettiva sta in un campo a 32 bit
        if (length >= 0x5C)
        {
            uint extendedSpeed = BitConverter.ToUInt32(b, offset + 0x54);
            if (extendedSpeed > 0) speed = (int)extendedSpeed;
        }

        Modules.Add(new MemoryModule
        {
            Slot = StringAt(strings, b[offset + 0x10]),
            Manufacturer = StringAt(strings, length > 0x17 ? b[offset + 0x17] : (byte)0),
            PartNumber = StringAt(strings, length > 0x1A ? b[offset + 0x1A] : (byte)0),
            SizeBytes = bytes,
            SpeedMhz = speed,
            Type = MemoryTypeName(length > 0x12 ? b[offset + 0x12] : (byte)0),
        });
    }

    private static List<string> ReadStrings(byte[] b, int start, out int next)
    {
        var list = new List<string>();
        int i = start;

        while (i < b.Length)
        {
            if (b[i] == 0)
            {
                // Due zeri consecutivi chiudono l'area delle stringhe
                if (i + 1 < b.Length && b[i + 1] == 0) { i += 2; break; }
                i++;
                continue;
            }

            int end = i;
            while (end < b.Length && b[end] != 0) end++;
            list.Add(Encoding.ASCII.GetString(b, i, end - i));
            i = end + 1;

            if (i < b.Length && b[i] == 0) { i++; break; }
        }

        next = i;
        return list;
    }

    private static string StringAt(List<string> strings, byte index) =>
        index > 0 && index <= strings.Count ? strings[index - 1].Trim() : "";

    private static string MemoryTypeName(byte code) => code switch
    {
        0x1A => "DDR4",
        0x22 => "DDR5",
        0x18 => "DDR3",
        0x14 => "DDR2",
        0x13 => "DDR",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x23 => "LPDDR5",
        _ => "",
    };

    public static string FormatBytes(ulong bytes)
    {
        if (bytes == 0) return "—";
        double gb = bytes / 1024.0 / 1024 / 1024;
        return gb >= 1 ? $"{gb:0.##} GB" : $"{bytes / 1024.0 / 1024:0} MB";
    }
}
