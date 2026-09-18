using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Native;

/// <summary>
/// P/Invoke verso le API di storage di Windows. Tutto quello che serve per parlare
/// direttamente con il driver del disco (stessa strada usata da CrystalDiskInfo).
/// </summary>
internal static class NativeMethods
{
    // ---- CreateFile ----
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // ---- IOCTL ----
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    internal const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    internal const uint SMART_GET_VERSION = 0x00074080;
    internal const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    internal const uint SMART_SEND_DRIVE_COMMAND = 0x0007C084;
    internal const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    internal const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    internal const uint IOCTL_SCSI_GET_ADDRESS = 0x00041018;

    // ---- Comandi ATA ----
    internal const byte IDE_ATA_IDENTIFY = 0xEC;
    internal const byte IDE_ATAPI_IDENTIFY = 0xA1;
    internal const byte SMART_CMD = 0xB0;
    internal const byte SMART_READ_ATTRIBUTES = 0xD0;
    internal const byte SMART_READ_THRESHOLDS = 0xD1;
    internal const byte SMART_EXECUTE_OFFLINE = 0xD4;
    internal const byte SELFTEST_SHORT = 0x01;
    internal const byte SELFTEST_EXTENDED = 0x02;
    internal const byte SMART_CYL_LOW = 0x4F;
    internal const byte SMART_CYL_HI = 0xC2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}

/// <summary>STORAGE_PROPERTY_ID (subset utile).</summary>
internal enum StoragePropertyId : uint
{
    StorageDeviceProperty = 0,
    StorageAdapterProperty = 1,
    StorageAccessAlignmentProperty = 6,
    StorageDeviceSeekPenaltyProperty = 7,
    StorageDeviceTrimProperty = 8,
    StorageAdapterProtocolSpecificProperty = 49,
    StorageDeviceProtocolSpecificProperty = 50,
    StorageAdapterTemperatureProperty = 51,
    StorageDeviceTemperatureProperty = 52,
}

/// <summary>STORAGE_BUS_TYPE.</summary>
public enum StorageBusType : uint
{
    Unknown = 0x00,
    Scsi = 0x01,
    Atapi = 0x02,
    Ata = 0x03,
    Ieee1394 = 0x04,
    Ssa = 0x05,
    Fibre = 0x06,
    Usb = 0x07,
    RAID = 0x08,
    iScsi = 0x09,
    Sas = 0x0A,
    Sata = 0x0B,
    Sd = 0x0C,
    Mmc = 0x0D,
    Virtual = 0x0E,
    FileBackedVirtual = 0x0F,
    Spaces = 0x10,
    Nvme = 0x11,
    SCM = 0x12,
    Ufs = 0x13,
    Max = 0x14,
}
