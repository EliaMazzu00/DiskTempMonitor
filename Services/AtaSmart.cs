using System.Text;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Lettura S.M.A.R.T. "classica" per dischi ATA/SATA tramite SMART_RCV_DRIVE_DATA.
/// Richiede l'handle aperto in GENERIC_READ | GENERIC_WRITE, quindi privilegi di
/// amministratore.
/// </summary>
internal static class AtaSmart
{
    // SENDCMDINPARAMS: cBufferSize(4) IDEREGS(8) bDriveNumber(1) bReserved(3) dwReserved(16) bBuffer(1)
    private const int SendCmdInSize = 36;
    // SENDCMDOUTPARAMS: cBufferSize(4) DRIVERSTATUS(12) bBuffer(...)
    private const int SendCmdOutHeader = 16;
    private const int SectorSize = 512;

    /// <summary>SMART_GET_VERSION: verifica che il driver esponga il canale SMART.</summary>
    public static bool IsSmartSupported(SafeFileHandle h)
    {
        var outBuf = new byte[24];
        return NativeMethods.DeviceIoControl(h, NativeMethods.SMART_GET_VERSION,
            null, 0, outBuf, outBuf.Length, out int returned, IntPtr.Zero) && returned > 0;
    }

    private static byte[]? SendCommand(SafeFileHandle h, byte drive, byte features,
                                       byte cylLow, byte cylHigh, byte command)
    {
        var inBuf = new byte[SendCmdInSize];
        BitConverter.GetBytes(SectorSize).CopyTo(inBuf, 0);  // cBufferSize
        inBuf[4] = features;                                  // bFeaturesReg
        inBuf[5] = 1;                                         // bSectorCountReg
        inBuf[6] = 1;                                         // bSectorNumberReg
        inBuf[7] = cylLow;                                    // bCylLowReg
        inBuf[8] = cylHigh;                                   // bCylHighReg
        inBuf[9] = 0xA0;                                      // bDriveHeadReg
        inBuf[10] = command;                                  // bCommandReg
        inBuf[11] = 0;                                        // bReserved
        inBuf[12] = drive;                                    // bDriveNumber

        var outBuf = new byte[SendCmdOutHeader + SectorSize];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.SMART_RCV_DRIVE_DATA,
                inBuf, inBuf.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero))
            return null;

        if (returned < SendCmdOutHeader + SectorSize) return null;
        if (outBuf[4] != 0) return null;                      // bDriverError

        var data = new byte[SectorSize];
        Array.Copy(outBuf, SendCmdOutHeader, data, 0, SectorSize);
        return data;
    }

    public static byte[]? ReadAttributesRaw(SafeFileHandle h, byte drive) =>
        SendCommand(h, drive, NativeMethods.SMART_READ_ATTRIBUTES,
                    NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD);

    public static byte[]? ReadThresholdsRaw(SafeFileHandle h, byte drive) =>
        SendCommand(h, drive, NativeMethods.SMART_READ_THRESHOLDS,
                    NativeMethods.SMART_CYL_LOW, NativeMethods.SMART_CYL_HI, NativeMethods.SMART_CMD);

    public static byte[]? IdentifyRaw(SafeFileHandle h, byte drive, bool atapi = false) =>
        SendCommand(h, drive, 0, 0, 0,
                    atapi ? NativeMethods.IDE_ATAPI_IDENTIFY : NativeMethods.IDE_ATA_IDENTIFY);

    // ------------------------------------------------------------- IDENTIFY

    internal sealed class IdentifyData
    {
        public string Model = "";
        public string Serial = "";
        public string Firmware = "";
        public int RotationRate;        // 0 = sconosciuto, 1 = SSD, altrimenti RPM
        public bool SmartSupported;
        public bool SmartEnabled;
        public bool SelfTestSupported;
        public string TransferMode = "";
    }

    public static IdentifyData? ParseIdentify(byte[]? d)
    {
        if (d is null || d.Length < 512) return null;

        var id = new IdentifyData
        {
            Serial = SwappedString(d, 20, 20),
            Firmware = SwappedString(d, 46, 8),
            Model = SwappedString(d, 54, 40),
        };

        ushort word82 = BitConverter.ToUInt16(d, 82 * 2);
        ushort word85 = BitConverter.ToUInt16(d, 85 * 2);
        id.SmartSupported = (word82 & 0x0001) != 0;
        id.SmartEnabled = (word85 & 0x0001) != 0;

        // Bit 1 dei word 84 e 87: autodiagnosi S.M.A.R.T. prevista dal firmware. I due
        // word valgono solo se il bit 15 è a zero e il 14 a uno, altrimenti sono rumore.
        id.SelfTestSupported = HasBit(d, 84, 1) || HasBit(d, 87, 1);

        ushort word217 = BitConverter.ToUInt16(d, 217 * 2);
        if (word217 == 1) id.RotationRate = 1;                       // SSD
        else if (word217 >= 0x0401 && word217 <= 0xFFFE) id.RotationRate = word217;

        // Word 76/77: velocità SATA supportata e corrente
        ushort word76 = BitConverter.ToUInt16(d, 76 * 2);
        if (word76 != 0 && word76 != 0xFFFF)
        {
            string max = (word76 & 0x0008) != 0 ? "SATA/600"
                       : (word76 & 0x0004) != 0 ? "SATA/300"
                       : (word76 & 0x0002) != 0 ? "SATA/150" : "";
            ushort word77 = BitConverter.ToUInt16(d, 77 * 2);
            int gen = (word77 >> 1) & 0x07;
            string cur = gen switch { 1 => "SATA/150", 2 => "SATA/300", 3 => "SATA/600", _ => "" };
            id.TransferMode = (cur.Length > 0 && max.Length > 0) ? $"{cur} | {max}" : max;
        }

        if (id.Model.Length == 0 && id.Serial.Length == 0) return null;
        return id;
    }

    /// <summary>
    /// Legge un bit da un word di IDENTIFY, ma solo se il word è dichiarato valido:
    /// bit 15 a zero e bit 14 a uno, come prescrive lo standard.
    /// </summary>
    private static bool HasBit(byte[] d, int word, int bit)
    {
        if (word * 2 + 1 >= d.Length) return false;

        ushort value = BitConverter.ToUInt16(d, word * 2);
        if ((value & 0xC000) != 0x4000) return false;

        return (value & (1 << bit)) != 0;
    }

    /// <summary>Le stringhe ATA sono memorizzate con i byte invertiti a coppie.</summary>
    private static string SwappedString(byte[] b, int offset, int length)
    {
        if (offset + length > b.Length) return "";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i += 2)
        {
            sb.Append(Printable(b[offset + i + 1]));
            sb.Append(Printable(b[offset + i]));
        }
        return sb.ToString().Trim();
    }

    private static char Printable(byte b) => b >= 32 && b < 127 ? (char)b : ' ';
}
