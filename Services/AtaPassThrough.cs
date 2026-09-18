using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Comandi ATA inviati con <c>IOCTL_ATA_PASS_THROUGH</c>.
/// <para>
/// È la strada moderna: il vecchio canale <c>SMART_RCV_DRIVE_DATA</c> lo espongono solo
/// alcuni driver (e mai i controller RAID o gli AHCI di certi chipset), mentre il
/// pass-through ATA è quello che Windows offre a tutti i dispositivi collegati a un
/// bus ATA/SATA. È anche la via usata da CrystalDiskInfo prima di ripiegare sulle altre.
/// </para>
/// <para>
/// Si usa la variante non "Direct": il buffer dei dati viaggia in coda alla struttura,
/// quindi non serve preoccuparsi dell'allineamento richiesto dall'adattatore.
/// </para>
/// </summary>
internal static class AtaPassThrough
{
    // ATA_PASS_THROUGH_EX su piattaforma a 64 bit
    private const int OffLength = 0;
    private const int OffAtaFlags = 2;
    private const int OffPathId = 4;
    private const int OffTargetId = 5;
    private const int OffLun = 6;
    private const int OffDataTransferLength = 8;
    private const int OffTimeOutValue = 12;
    private const int OffDataBufferOffset = 24;      // ULONG_PTR
    private const int OffPreviousTaskFile = 32;
    private const int OffCurrentTaskFile = 40;
    private const int HeaderSize = 48;

    private const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    private const ushort ATA_FLAGS_DATA_IN = 0x02;

    private const int SectorSize = 512;

    /// <summary>IDENTIFY DEVICE (ECh).</summary>
    public static byte[]? Identify(SafeFileHandle h) =>
        ReadSector(h, features: 0, lbaLow: 0, lbaMid: 0, lbaHigh: 0, command: NativeMethods.IDE_ATA_IDENTIFY);

    /// <summary>SMART READ DATA (B0h/D0h): i 30 attributi.</summary>
    public static byte[]? SmartAttributes(SafeFileHandle h) =>
        ReadSector(h, NativeMethods.SMART_READ_ATTRIBUTES, 0,
                   NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD);

    /// <summary>SMART READ THRESHOLDS (B0h/D1h).</summary>
    public static byte[]? SmartThresholds(SafeFileHandle h) =>
        ReadSector(h, NativeMethods.SMART_READ_THRESHOLDS, 0,
                   NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD);

    /// <summary>SMART EXECUTE OFF-LINE IMMEDIATE (B0h/D4h): avvia l'autodiagnosi.</summary>
    public static bool StartSelfTest(SafeFileHandle h, byte subcommand) =>
        SendNoData(h, 0xD4, subcommand, NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI,
                   NativeMethods.SMART_CMD);

    // ------------------------------------------------------------------ invio

    private static byte[]? ReadSector(SafeFileHandle h, byte features, byte lbaLow,
                                      byte lbaMid, byte lbaHigh, byte command)
    {
        var buffer = new byte[HeaderSize + SectorSize];
        BuildHeader(buffer, ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN, SectorSize);
        WriteTaskFile(buffer, features, sectorCount: 1, lbaLow, lbaMid, lbaHigh, command);

        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_ATA_PASS_THROUGH,
                buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
            return null;

        if (returned < HeaderSize + SectorSize) return null;

        // Bit 0 dello stato (registro di stato nel task file di ritorno) = errore.
        byte status = buffer[OffCurrentTaskFile + 6];
        if ((status & 0x01) != 0) return null;

        var data = new byte[SectorSize];
        Array.Copy(buffer, HeaderSize, data, 0, SectorSize);
        return AtaData.LooksEmpty(data) ? null : data;
    }

    private static bool SendNoData(SafeFileHandle h, byte features, byte lbaLow,
                                   byte lbaMid, byte lbaHigh, byte command)
    {
        var buffer = new byte[HeaderSize];
        BuildHeader(buffer, ATA_FLAGS_DRDY_REQUIRED, 0);
        WriteTaskFile(buffer, features, sectorCount: 1, lbaLow, lbaMid, lbaHigh, command);

        if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_ATA_PASS_THROUGH,
                buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
            return false;

        if (returned < HeaderSize) return false;
        return (buffer[OffCurrentTaskFile + 6] & 0x01) == 0;
    }

    private static void BuildHeader(byte[] buffer, ushort flags, int transferLength)
    {
        BitConverter.GetBytes((ushort)HeaderSize).CopyTo(buffer, OffLength);
        BitConverter.GetBytes(flags).CopyTo(buffer, OffAtaFlags);
        buffer[OffPathId] = 0;
        buffer[OffTargetId] = 0;
        buffer[OffLun] = 0;
        BitConverter.GetBytes(transferLength).CopyTo(buffer, OffDataTransferLength);
        BitConverter.GetBytes(10).CopyTo(buffer, OffTimeOutValue);     // secondi
        BitConverter.GetBytes((ulong)(transferLength > 0 ? HeaderSize : 0)).CopyTo(buffer, OffDataBufferOffset);
    }

    /// <summary>IDEREGS: features, conteggio settori, LBA 0-2, device, comando.</summary>
    private static void WriteTaskFile(byte[] buffer, byte features, byte sectorCount,
                                      byte lbaLow, byte lbaMid, byte lbaHigh, byte command)
    {
        int t = OffCurrentTaskFile;
        buffer[t + 0] = features;
        buffer[t + 1] = sectorCount;
        buffer[t + 2] = lbaLow;
        buffer[t + 3] = lbaMid;
        buffer[t + 4] = lbaHigh;
        buffer[t + 5] = 0xA0;            // device
        buffer[t + 6] = command;
        buffer[t + 7] = 0;

        // Il task file "precedente" serve solo ai comandi a 48 bit: resta a zero.
        Array.Clear(buffer, OffPreviousTaskFile, 8);
    }
}

/// <summary>Controlli di validità comuni ai settori restituiti dai comandi ATA.</summary>
internal static class AtaData
{
    /// <summary>Un settore tutto a zero significa che il comando non è passato davvero.</summary>
    public static bool LooksEmpty(byte[] data) => Array.TrueForAll(data, b => b == 0);

    /// <summary>
    /// Vero se il settore ha l'aspetto di una tabella di attributi S.M.A.R.T.: almeno
    /// un identificativo valido fra i 30 possibili. Serve a scartare le risposte fasulle
    /// di certi ponti USB, che accettano il comando e restituiscono spazzatura.
    /// </summary>
    public static bool LooksLikeSmart(byte[]? data)
    {
        if (data is null || data.Length < 512) return false;
        for (int i = 0; i < 30; i++)
            if (data[2 + i * 12] != 0) return true;
        return false;
    }

    /// <summary>Vero se il settore ha l'aspetto di una risposta a IDENTIFY DEVICE.</summary>
    public static bool LooksLikeIdentify(byte[]? data)
    {
        if (data is null || data.Length < 512) return false;
        // I word 27-46 contengono modello e seriale in ASCII: basta trovarci del testo.
        for (int i = 54; i < 94; i++)
            if (data[i] > 32 && data[i] < 127) return true;
        return false;
    }
}
