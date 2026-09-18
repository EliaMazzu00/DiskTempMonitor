using System.Runtime.InteropServices;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Comandi ATA incapsulati in comandi SCSI (standard SAT, "SCSI/ATA Translation").
/// <para>
/// I box esterni USB non espongono il canale S.M.A.R.T. classico: il disco dentro è un
/// normale SATA, ma il ponte USB→SATA parla SCSI. Per leggerne temperatura e attributi
/// bisogna quindi spedire il comando ATA dentro un CDB "ATA PASS-THROUGH", che il ponte
/// traduce e inoltra al disco.
/// </para>
/// <para>
/// Non tutti i ponti lo supportano, e quelli che lo fanno non concordano sulla variante:
/// si provano nell'ordine ATA PASS-THROUGH(16), lo stesso con <c>ck_cond</c> attivo e
/// infine la versione a 12 byte.
/// </para>
/// </summary>
internal static class SatPassThrough
{
    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    private const byte AtaPassThrough16 = 0x85;
    private const byte AtaPassThrough12 = 0xA1;

    private const byte ProtocolNonData = 3;
    private const byte ProtocolPioDataIn = 4;

    // Offset dentro SCSI_PASS_THROUGH_DIRECT su piattaforma a 64 bit
    private const int OffLength = 0;
    private const int OffScsiStatus = 2;
    private const int OffPathId = 3;
    private const int OffTargetId = 4;
    private const int OffLun = 5;
    private const int OffCdbLength = 6;
    private const int OffSenseInfoLength = 7;
    private const int OffDataIn = 8;
    private const int OffDataTransferLength = 12;
    private const int OffTimeOutValue = 16;
    private const int OffDataBuffer = 24;
    private const int OffSenseInfoOffset = 32;
    private const int OffCdb = 36;
    private const int SptSize = 56;              // arrotondato all'allineamento a 8
    private const int SenseLength = 32;
    private const int TotalSize = SptSize + SenseLength;

    private const int TimeoutSeconds = 6;

    /// <summary>Le varianti di incapsulamento, provate in quest'ordine.</summary>
    private enum Variant
    {
        Sat16,
        Sat16CkCond,
        Sat12,
    }

    /// <summary>IDENTIFY DEVICE (ECh) attraverso il ponte.</summary>
    public static byte[]? Identify(SafeFileHandle handle) =>
        ReadSector(handle, features: 0, lbaMid: 0, lbaHigh: 0, command: NativeMethods.IDE_ATA_IDENTIFY,
                   AtaData.LooksLikeIdentify);

    /// <summary>SMART READ DATA (B0h/D0h) attraverso il ponte.</summary>
    public static byte[]? SmartAttributes(SafeFileHandle handle) =>
        ReadSector(handle, NativeMethods.SMART_READ_ATTRIBUTES,
                   NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD,
                   AtaData.LooksLikeSmart);

    /// <summary>SMART READ THRESHOLDS (B0h/D1h) attraverso il ponte.</summary>
    public static byte[]? SmartThresholds(SafeFileHandle handle) =>
        ReadSector(handle, NativeMethods.SMART_READ_THRESHOLDS,
                   NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD,
                   AtaData.LooksLikeSmart);

    /// <summary>SMART EXECUTE OFF-LINE IMMEDIATE (B0h/D4h): avvia l'autodiagnosi.</summary>
    public static bool StartSelfTest(SafeFileHandle handle, byte subcommand)
    {
        foreach (var variant in new[] { Variant.Sat16, Variant.Sat12 })
        {
            if (Send(handle, NativeMethods.SMART_EXECUTE_OFFLINE, subcommand,
                     NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI,
                     NativeMethods.SMART_CMD, variant, dataIn: false, out byte status, out _) &&
                status == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Prova le varianti finché una restituisce un settore plausibile. Lo stato SCSI da
    /// solo non basta a decidere: molti ponti rispondono CHECK CONDITION, con sense "ATA
    /// pass through information available", pur avendo consegnato i dati corretti.
    /// </summary>
    private static byte[]? ReadSector(SafeFileHandle handle, byte features, byte lbaMid, byte lbaHigh,
                                      byte command, Func<byte[]?, bool> looksValid)
    {
        foreach (var variant in Enum.GetValues<Variant>())
        {
            if (!Send(handle, features, lbaLow: 0, lbaMid, lbaHigh, command, variant,
                      dataIn: true, out _, out byte[]? data))
                continue;

            if (looksValid(data)) return data;
        }

        return null;
    }

    private static bool Send(SafeFileHandle handle, byte features, byte lbaLow, byte lbaMid,
                             byte lbaHigh, byte command, Variant variant, bool dataIn,
                             out byte scsiStatus, out byte[]? data)
    {
        const int sectorSize = 512;
        scsiStatus = 0xFF;
        data = null;

        bool use16 = variant != Variant.Sat12;
        bool ckCond = variant == Variant.Sat16CkCond;

        IntPtr spt = Marshal.AllocHGlobal(TotalSize);
        IntPtr buffer = dataIn ? Marshal.AllocHGlobal(sectorSize) : IntPtr.Zero;
        try
        {
            for (int i = 0; i < TotalSize; i++) Marshal.WriteByte(spt, i, 0);
            if (dataIn) for (int i = 0; i < sectorSize; i++) Marshal.WriteByte(buffer, i, 0);

            Marshal.WriteInt16(spt, OffLength, SptSize);
            Marshal.WriteByte(spt, OffScsiStatus, 0);
            Marshal.WriteByte(spt, OffPathId, 0);
            Marshal.WriteByte(spt, OffTargetId, 0);
            Marshal.WriteByte(spt, OffLun, 0);
            Marshal.WriteByte(spt, OffCdbLength, (byte)(use16 ? 16 : 12));
            Marshal.WriteByte(spt, OffSenseInfoLength, SenseLength);
            Marshal.WriteByte(spt, OffDataIn, dataIn ? SCSI_IOCTL_DATA_IN : SCSI_IOCTL_DATA_UNSPECIFIED);
            Marshal.WriteInt32(spt, OffDataTransferLength, dataIn ? sectorSize : 0);
            Marshal.WriteInt32(spt, OffTimeOutValue, TimeoutSeconds);
            Marshal.WriteIntPtr(spt, OffDataBuffer, buffer);
            Marshal.WriteInt32(spt, OffSenseInfoOffset, SptSize);

            // --- CDB ---
            // flags: t_length = 2 (il conteggio è nel campo sector count),
            //        byte_block = 1 (blocchi, non byte), t_dir = 1 (dal dispositivo).
            // Per i comandi senza trasferimento restano tutti a zero.
            byte flags = dataIn ? (byte)0x0E : (byte)0x00;
            if (ckCond) flags |= 0x20;                       // chiede indietro il task file

            byte protocol = dataIn ? ProtocolPioDataIn : ProtocolNonData;
            byte sectorCount = (byte)(dataIn ? 1 : 0);

            if (use16)
            {
                Marshal.WriteByte(spt, OffCdb + 0, AtaPassThrough16);
                Marshal.WriteByte(spt, OffCdb + 1, (byte)(protocol << 1));    // extend = 0
                Marshal.WriteByte(spt, OffCdb + 2, flags);
                Marshal.WriteByte(spt, OffCdb + 4, features);
                Marshal.WriteByte(spt, OffCdb + 6, sectorCount);
                Marshal.WriteByte(spt, OffCdb + 8, lbaLow);
                Marshal.WriteByte(spt, OffCdb + 10, lbaMid);
                Marshal.WriteByte(spt, OffCdb + 12, lbaHigh);
                Marshal.WriteByte(spt, OffCdb + 13, 0xA0);                    // device
                Marshal.WriteByte(spt, OffCdb + 14, command);
            }
            else
            {
                Marshal.WriteByte(spt, OffCdb + 0, AtaPassThrough12);
                Marshal.WriteByte(spt, OffCdb + 1, (byte)(protocol << 1));
                Marshal.WriteByte(spt, OffCdb + 2, flags);
                Marshal.WriteByte(spt, OffCdb + 3, features);
                Marshal.WriteByte(spt, OffCdb + 4, sectorCount);
                Marshal.WriteByte(spt, OffCdb + 5, lbaLow);
                Marshal.WriteByte(spt, OffCdb + 6, lbaMid);
                Marshal.WriteByte(spt, OffCdb + 7, lbaHigh);
                Marshal.WriteByte(spt, OffCdb + 8, 0xA0);
                Marshal.WriteByte(spt, OffCdb + 9, command);
            }

            if (!DeviceIoControl(handle, NativeMethods.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                                 spt, TotalSize, spt, TotalSize, out _, IntPtr.Zero))
                return false;

            scsiStatus = Marshal.ReadByte(spt, OffScsiStatus);

            if (!dataIn) return true;

            var result = new byte[sectorSize];
            Marshal.Copy(buffer, result, 0, sectorSize);

            // Un settore interamente a zero indica che il ponte ha accettato il
            // comando ma non ha inoltrato nulla.
            if (AtaData.LooksEmpty(result)) return false;

            data = result;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
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
