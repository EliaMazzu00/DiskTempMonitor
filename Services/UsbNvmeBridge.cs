using System.Runtime.InteropServices;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Comandi NVMe inoltrati dai ponti USB→NVMe che usano un protocollo proprio.
/// <para>
/// Dentro molti box esterni moderni non c'è un disco SATA ma un SSD NVMe: il ponte
/// mostra a Windows un normale disco SCSI e non sa nulla di ATA, quindi il pass-through
/// SAT non serve a niente. Per arrivare al disco bisogna parlare il dialetto del ponte.
/// </para>
/// <para>
/// Qui è implementato quello dei Realtek RTL9210/RTL9210B/RTL9210C, il più diffuso,
/// nella forma documentata da smartmontools (<c>-d sntrealtek</c>). Si inviano solo due
/// comandi amministrativi NVMe di sola lettura: Identify Controller e la log page
/// SMART/Health. Il comando viene provato unicamente sui ponti che si dichiarano
/// Realtek, perché lo stesso codice operativo su un ponte di un'altra marca vorrebbe
/// dire tutt'altro.
/// </para>
/// </summary>
internal static class UsbNvmeBridge
{
    private const byte RealtekVendorCommand = 0xE4;

    private const byte NvmeAdminIdentify = 0x06;
    private const byte NvmeAdminGetLogPage = 0x02;

    private const int IdentifySize = 4096;
    // Il controller restituisce dati vecchi se gli si chiede più di 512 byte per volta.
    private const int LogPageSize = 512;

    // Offset dentro SCSI_PASS_THROUGH_DIRECT su piattaforma a 64 bit
    private const int OffLength = 0;
    private const int OffScsiStatus = 2;
    private const int OffCdbLength = 6;
    private const int OffSenseInfoLength = 7;
    private const int OffDataIn = 8;
    private const int OffDataTransferLength = 12;
    private const int OffTimeOutValue = 16;
    private const int OffDataBuffer = 24;
    private const int OffSenseInfoOffset = 32;
    private const int OffCdb = 36;
    private const int SptSize = 56;
    private const int SenseLength = 32;
    private const int TotalSize = SptSize + SenseLength;

    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const int TimeoutSeconds = 6;

    /// <summary>Vero se il dispositivo si presenta come un ponte Realtek conosciuto.</summary>
    public static bool LooksLikeRealtek(string vendor, string product)
    {
        string text = $"{vendor} {product}";
        return text.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RTL9210", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RTL9220", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Identify Controller (CNS 01h): modello, seriale e firmware del disco interno.</summary>
    public static byte[]? Identify(SafeFileHandle handle) =>
        Send(handle, NvmeAdminIdentify, cdw10: 0x01, IdentifySize);

    /// <summary>Log page SMART/Health (02h): temperatura, usura, ore, errori.</summary>
    public static byte[]? SmartHealth(SafeFileHandle handle) =>
        Send(handle, NvmeAdminGetLogPage, cdw10: 0x02, LogPageSize);

    private static byte[]? Send(SafeFileHandle handle, byte opcode, byte cdw10, int size)
    {
        IntPtr spt = Marshal.AllocHGlobal(TotalSize);
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < TotalSize; i++) Marshal.WriteByte(spt, i, 0);
            for (int i = 0; i < size; i++) Marshal.WriteByte(data, i, 0);

            Marshal.WriteInt16(spt, OffLength, SptSize);
            Marshal.WriteByte(spt, OffCdbLength, 16);
            Marshal.WriteByte(spt, OffSenseInfoLength, SenseLength);
            Marshal.WriteByte(spt, OffDataIn, SCSI_IOCTL_DATA_IN);
            Marshal.WriteInt32(spt, OffDataTransferLength, size);
            Marshal.WriteInt32(spt, OffTimeOutValue, TimeoutSeconds);
            Marshal.WriteIntPtr(spt, OffDataBuffer, data);
            Marshal.WriteInt32(spt, OffSenseInfoOffset, SptSize);

            // CDB: comando del produttore, lunghezza dei dati, opcode NVMe, CDW10
            Marshal.WriteByte(spt, OffCdb + 0, RealtekVendorCommand);
            Marshal.WriteByte(spt, OffCdb + 1, (byte)(size & 0xFF));
            Marshal.WriteByte(spt, OffCdb + 2, (byte)(size >> 8));
            Marshal.WriteByte(spt, OffCdb + 3, opcode);
            Marshal.WriteByte(spt, OffCdb + 4, cdw10);

            if (!DeviceIoControl(handle, NativeMethods.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                                 spt, TotalSize, spt, TotalSize, out _, IntPtr.Zero))
                return null;

            if (Marshal.ReadByte(spt, OffScsiStatus) != 0) return null;

            var result = new byte[size];
            Marshal.Copy(data, result, 0, size);
            return AtaData.LooksEmpty(result) ? null : result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(spt);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);
}
