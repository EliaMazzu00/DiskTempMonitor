using System.Runtime.InteropServices;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Pagine di log SCSI standard (comando LOG SENSE, 4Dh).
/// <para>
/// È la via più semplice per avere la temperatura da un dispositivo che parla SCSI: non
/// serve tradurre comandi ATA né conoscere il ponte USB che si ha davanti. Molti box
/// esterni che rifiutano il pass-through rispondono invece a queste pagine, perché fanno
/// parte del linguaggio SCSI di base.
/// </para>
/// <para>
/// Sono comandi di sola lettura definiti dallo standard: non toccano i dati del disco.
/// </para>
/// </summary>
internal static class ScsiLogSense
{
    private const byte LogSense = 0x4D;
    private const byte PageTemperature = 0x0D;
    private const byte PageInformationalExceptions = 0x2F;

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

    /// <summary>Temperatura corrente in gradi, dalla pagina 0Dh o, in mancanza, dalla 2Fh.</summary>
    public static int? ReadTemperature(SafeFileHandle handle) =>
        FromTemperaturePage(handle) ?? FromInformationalExceptions(handle);

    /// <summary>
    /// Pagina "Temperature" (0Dh): il parametro 0000h contiene la temperatura corrente,
    /// il parametro 0001h quella massima consigliata dal costruttore.
    /// </summary>
    private static int? FromTemperaturePage(SafeFileHandle handle)
    {
        var page = Read(handle, PageTemperature);
        if (page is null) return null;

        foreach (var (code, data) in Parameters(page))
        {
            // Il valore sta nel secondo byte del parametro; FFh significa "non disponibile".
            if (code != 0x0000 || data.Length < 2) continue;
            int celsius = data[1];
            return celsius is > 0 and < 120 ? celsius : null;
        }

        return null;
    }

    /// <summary>
    /// Pagina "Informational Exceptions" (2Fh): oltre al codice di allarme S.M.A.R.T.
    /// riporta l'ultima temperatura letta.
    /// </summary>
    private static int? FromInformationalExceptions(SafeFileHandle handle)
    {
        var page = Read(handle, PageInformationalExceptions);
        if (page is null) return null;

        foreach (var (code, data) in Parameters(page))
        {
            if (code != 0x0000 || data.Length < 3) continue;
            int celsius = data[2];
            return celsius is > 0 and < 120 ? celsius : null;
        }

        return null;
    }

    /// <summary>
    /// Scorre i parametri di una pagina di log: intestazione di 4 byte, poi per ciascun
    /// parametro codice (2 byte), byte di controllo, lunghezza e dati.
    /// </summary>
    private static IEnumerable<(int Code, byte[] Data)> Parameters(byte[] page)
    {
        int declared = page.Length >= 4 ? (page[2] << 8) | page[3] : 0;
        int end = Math.Min(page.Length, 4 + declared);

        int offset = 4;
        while (offset + 4 <= end)
        {
            int code = (page[offset] << 8) | page[offset + 1];
            int length = page[offset + 3];
            if (offset + 4 + length > end) yield break;

            var data = new byte[length];
            Array.Copy(page, offset + 4, data, 0, length);
            yield return (code, data);

            offset += 4 + length;
        }
    }

    private static byte[]? Read(SafeFileHandle handle, byte pageCode)
    {
        const int bufferSize = 512;

        IntPtr spt = Marshal.AllocHGlobal(TotalSize);
        IntPtr data = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int i = 0; i < TotalSize; i++) Marshal.WriteByte(spt, i, 0);
            for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(data, i, 0);

            Marshal.WriteInt16(spt, OffLength, SptSize);
            Marshal.WriteByte(spt, OffCdbLength, 10);
            Marshal.WriteByte(spt, OffSenseInfoLength, SenseLength);
            Marshal.WriteByte(spt, OffDataIn, SCSI_IOCTL_DATA_IN);
            Marshal.WriteInt32(spt, OffDataTransferLength, bufferSize);
            Marshal.WriteInt32(spt, OffTimeOutValue, TimeoutSeconds);
            Marshal.WriteIntPtr(spt, OffDataBuffer, data);
            Marshal.WriteInt32(spt, OffSenseInfoOffset, SptSize);

            // CDB di LOG SENSE: PC = 01b (valori correnti cumulativi)
            Marshal.WriteByte(spt, OffCdb + 0, LogSense);
            Marshal.WriteByte(spt, OffCdb + 2, (byte)(0x40 | pageCode));
            Marshal.WriteByte(spt, OffCdb + 7, (byte)(bufferSize >> 8));
            Marshal.WriteByte(spt, OffCdb + 8, (byte)(bufferSize & 0xFF));

            if (!DeviceIoControl(handle, NativeMethods.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                                 spt, TotalSize, spt, TotalSize, out _, IntPtr.Zero))
                return null;

            if (Marshal.ReadByte(spt, OffScsiStatus) != 0) return null;

            var page = new byte[bufferSize];
            Marshal.Copy(data, page, 0, bufferSize);

            // Il primo byte riporta il codice della pagina restituita: se non coincide,
            // il dispositivo ha risposto qualcos'altro.
            return (page[0] & 0x3F) == pageCode ? page : null;
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
