using System.Text;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Wrapper su IOCTL_STORAGE_QUERY_PROPERTY: descrittore dispositivo, sensori di
/// temperatura esposti dal driver e log page NVMe.
/// </summary>
internal static class StorageQuery
{
    private const int PropertyStandardQuery = 0;

    // ---------------------------------------------------------------- descrittore

    internal sealed class DeviceDescriptor
    {
        public string Vendor = "";
        public string Product = "";
        public string Revision = "";
        public string Serial = "";
        public StorageBusType BusType;
        public bool RemovableMedia;
    }

    public static DeviceDescriptor? GetDeviceDescriptor(SafeFileHandle h)
    {
        var query = BuildQuery(StoragePropertyId.StorageDeviceProperty);
        var outBuf = new byte[2048];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                query, query.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero) || returned < 32)
            return null;

        return new DeviceDescriptor
        {
            RemovableMedia = outBuf[10] != 0,
            BusType = (StorageBusType)BitConverter.ToUInt32(outBuf, 28),
            Vendor = ReadAnsiAt(outBuf, BitConverter.ToInt32(outBuf, 12)),
            Product = ReadAnsiAt(outBuf, BitConverter.ToInt32(outBuf, 16)),
            Revision = ReadAnsiAt(outBuf, BitConverter.ToInt32(outBuf, 20)),
            Serial = ReadAnsiAt(outBuf, BitConverter.ToInt32(outBuf, 24)),
        };
    }

    /// <summary>Dimensione del settore logico e fisico dichiarata dal dispositivo.</summary>
    public static (uint Logical, uint Physical)? GetSectorSizes(SafeFileHandle h)
    {
        var query = BuildQuery(StoragePropertyId.StorageAccessAlignmentProperty);
        var outBuf = new byte[64];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                query, query.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero) || returned < 24)
            return null;

        uint logical = BitConverter.ToUInt32(outBuf, 16);
        uint physical = BitConverter.ToUInt32(outBuf, 20);
        return logical == 0 ? null : (logical, physical);
    }

    /// <summary>StorageDeviceSeekPenaltyProperty: true = supporto meccanico (HDD).</summary>
    public static bool? GetSeekPenalty(SafeFileHandle h)
    {
        var query = BuildQuery(StoragePropertyId.StorageDeviceSeekPenaltyProperty);
        var outBuf = new byte[16];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                query, query.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero) || returned < 9)
            return null;
        return outBuf[8] != 0;
    }

    // ------------------------------------------------------------- temperatura

    internal sealed class TemperatureData
    {
        public int? Critical;
        public int? Warning;
        public readonly List<int> Sensors = [];
    }

    /// <summary>
    /// StorageDeviceTemperatureProperty (Windows 10+). Funziona per NVMe e per i dischi
    /// SATA il cui driver espone i sensori, senza bisogno di comandi SMART diretti.
    /// </summary>
    public static TemperatureData? GetTemperature(SafeFileHandle h, bool adapter = false)
    {
        var id = adapter
            ? StoragePropertyId.StorageAdapterTemperatureProperty
            : StoragePropertyId.StorageDeviceTemperatureProperty;

        var query = BuildQuery(id);
        var outBuf = new byte[1024];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                query, query.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero) || returned < 24)
            return null;

        var data = new TemperatureData();
        short critical = BitConverter.ToInt16(outBuf, 8);
        short warning = BitConverter.ToInt16(outBuf, 10);
        ushort infoCount = BitConverter.ToUInt16(outBuf, 12);

        if (critical > -100 && critical < 200 && critical != 0) data.Critical = critical;
        if (warning > -100 && warning < 200 && warning != 0) data.Warning = warning;

        const int headerSize = 24;
        const int infoSize = 16;
        for (int i = 0; i < infoCount; i++)
        {
            int off = headerSize + i * infoSize;
            if (off + infoSize > returned) break;
            short temp = BitConverter.ToInt16(outBuf, off + 2);
            if (temp > -60 && temp < 150 && temp != 0) data.Sensors.Add(temp);
        }

        return data.Sensors.Count > 0 || data.Critical.HasValue ? data : null;
    }

    // ------------------------------------------------------------------- NVMe

    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeIdentify = 1;
    private const int NVMeDataTypeLogPage = 2;

    /// <summary>Legge una struttura protocol-specific (identify / log page) da un NVMe.</summary>
    private static byte[]? QueryProtocol(SafeFileHandle h, int dataType, int requestValue, int length)
    {
        const int queryHeader = 8;      // PropertyId + QueryType
        const int protoSize = 40;       // STORAGE_PROTOCOL_SPECIFIC_DATA
        int total = queryHeader + protoSize + length;

        var inBuf = new byte[total];
        BitConverter.GetBytes((uint)StoragePropertyId.StorageDeviceProtocolSpecificProperty).CopyTo(inBuf, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(inBuf, 4);

        const int p = queryHeader;
        BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(inBuf, p + 0);   // ProtocolType
        BitConverter.GetBytes(dataType).CopyTo(inBuf, p + 4);           // DataType
        BitConverter.GetBytes(requestValue).CopyTo(inBuf, p + 8);       // ProtocolDataRequestValue
        BitConverter.GetBytes(0).CopyTo(inBuf, p + 12);                 // ProtocolDataRequestSubValue
        BitConverter.GetBytes(protoSize).CopyTo(inBuf, p + 16);         // ProtocolDataOffset
        BitConverter.GetBytes(length).CopyTo(inBuf, p + 20);            // ProtocolDataLength

        var outBuf = new byte[total];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, inBuf.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero))
            return null;

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version(4) Size(4) ProtocolSpecificData(40) ...
        const int descHeader = 8;
        if (returned < descHeader + protoSize) return null;

        int dataOffset = BitConverter.ToInt32(outBuf, descHeader + 16);
        int dataLength = BitConverter.ToInt32(outBuf, descHeader + 20);
        if (dataLength <= 0) return null;

        int start = descHeader + dataOffset;
        if (start < 0 || start + dataLength > outBuf.Length) return null;

        var result = new byte[dataLength];
        Array.Copy(outBuf, start, result, 0, dataLength);
        return result;
    }

    /// <summary>Identify Controller (CNS 01h): modello, seriale e firmware "veri".</summary>
    /// <summary>I 4096 byte grezzi della struttura Identify Controller.</summary>
    public static byte[]? GetNvmeIdentifyRaw(SafeFileHandle h) =>
        QueryProtocol(h, NVMeDataTypeIdentify, 1, 4096);

    public static (string Model, string Serial, string Firmware)? GetNvmeIdentify(SafeFileHandle h) =>
        ParseNvmeIdentify(GetNvmeIdentifyRaw(h));

    /// <summary>
    /// OACS (Optional Admin Command Support), ai byte 256-257: il bit 4 dice se il
    /// controller implementa il comando Device Self-test. È una funzione facoltativa, e
    /// parecchi SSD di fascia consumer non la prevedono affatto.
    /// </summary>
    public static bool? NvmeSupportsSelfTest(byte[]? identify) =>
        identify is null || identify.Length < 258
            ? null
            : (BitConverter.ToUInt16(identify, 256) & (1 << 4)) != 0;

    /// <summary>Legge modello, seriale e firmware da una struttura Identify Controller.</summary>
    public static (string Model, string Serial, string Firmware)? ParseNvmeIdentify(byte[]? data)
    {
        if (data is null || data.Length < 72) return null;

        string serial = CleanAscii(data, 4, 20);
        string model = CleanAscii(data, 24, 40);
        string fw = CleanAscii(data, 64, 8);

        if (model.Length == 0 && serial.Length == 0) return null;
        return (model, serial, fw);
    }

    internal sealed class NvmeHealth
    {
        public byte CriticalWarning;
        public int CompositeTemperatureC;
        public byte AvailableSpare;
        public byte AvailableSpareThreshold;
        public byte PercentageUsed;
        public ulong DataUnitsRead;
        public ulong DataUnitsWritten;
        public ulong HostReadCommands;
        public ulong HostWriteCommands;
        public ulong PowerCycles;
        public ulong PowerOnHours;
        public ulong UnsafeShutdowns;
        public ulong MediaErrors;
        public ulong ErrorLogEntries;
        public uint WarningTempTimeMin;
        public uint CriticalTempTimeMin;
        public readonly List<(int Index, int TempC)> Sensors = [];
    }

    /// <summary>Legge una log page NVMe qualsiasi (usata anche per l'autotest).</summary>
    public static byte[]? GetNvmeLogPage(SafeFileHandle h, int page, int length) =>
        QueryProtocol(h, NVMeDataTypeLogPage, page, length);

    /// <summary>SMART / Health Information log page (02h): il cuore dei dati NVMe.</summary>
    public static NvmeHealth? GetNvmeHealth(SafeFileHandle h) =>
        ParseNvmeHealth(QueryProtocol(h, NVMeDataTypeLogPage, 0x02, 512));

    /// <summary>Interpreta i 512 byte della log page 02h, da qualunque via arrivino.</summary>
    public static NvmeHealth? ParseNvmeHealth(byte[]? d)
    {
        if (d is null || d.Length < 512) return null;

        // Se i primi 64 byte sono tutti a zero il comando non è realmente passato.
        bool allZero = true;
        for (int i = 0; i < 64 && allZero; i++) if (d[i] != 0) allZero = false;
        if (allZero) return null;

        var n = new NvmeHealth
        {
            CriticalWarning = d[0],
            CompositeTemperatureC = KelvinToCelsius(BitConverter.ToUInt16(d, 1)),
            AvailableSpare = d[3],
            AvailableSpareThreshold = d[4],
            PercentageUsed = d[5],
            DataUnitsRead = Read128Low(d, 32),
            DataUnitsWritten = Read128Low(d, 48),
            HostReadCommands = Read128Low(d, 64),
            HostWriteCommands = Read128Low(d, 80),
            PowerCycles = Read128Low(d, 112),
            PowerOnHours = Read128Low(d, 128),
            UnsafeShutdowns = Read128Low(d, 144),
            MediaErrors = Read128Low(d, 160),
            ErrorLogEntries = Read128Low(d, 176),
            WarningTempTimeMin = BitConverter.ToUInt32(d, 192),
            CriticalTempTimeMin = BitConverter.ToUInt32(d, 196),
        };

        for (int i = 0; i < 8; i++)
        {
            ushort k = BitConverter.ToUInt16(d, 200 + i * 2);
            if (k == 0) continue;                       // sensore non implementato
            int c = KelvinToCelsius(k);
            if (c > -60 && c < 150) n.Sensors.Add((i + 1, c));
        }

        return n;
    }

    private static int KelvinToCelsius(ushort kelvin) => kelvin == 0 ? 0 : kelvin - 273;

    /// <summary>I contatori NVMe sono a 128 bit little-endian: usiamo i 64 bit bassi.</summary>
    private static ulong Read128Low(byte[] b, int offset) => BitConverter.ToUInt64(b, offset);

    // ---------------------------------------------------------------- utility

    private static byte[] BuildQuery(StoragePropertyId id)
    {
        var buf = new byte[16];
        BitConverter.GetBytes((uint)id).CopyTo(buf, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buf, 4);
        return buf;
    }

    private static string CleanAscii(byte[] buf, int offset, int length)
    {
        if (offset + length > buf.Length) return "";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte b = buf[offset + i];
            if (b == 0) break;
            sb.Append(b >= 32 && b < 127 ? (char)b : ' ');
        }
        return sb.ToString().Trim();
    }

    private static string ReadAnsiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return CleanAscii(buf, offset, end - offset);
    }
}
