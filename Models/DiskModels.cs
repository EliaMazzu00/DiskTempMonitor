using DiskTempMonitor.Native;

namespace DiskTempMonitor.Models;

public enum HealthState
{
    Unknown,
    Good,
    Caution,
    Bad,
}

public enum DiskKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Removable,
}

/// <summary>
/// I canali possibili per parlare con un disco ATA/SATA. Quale funzioni dipende dal
/// driver di storage e, per i box esterni, dal ponte USB→SATA che ci sta dentro.
/// </summary>
public enum AtaChannel
{
    None,
    /// <summary>IOCTL_ATA_PASS_THROUGH: la via standard di Windows.</summary>
    PassThrough,
    /// <summary>SMART_RCV_DRIVE_DATA: il canale storico, non sempre esposto.</summary>
    Legacy,
    /// <summary>Comando ATA incapsulato in un CDB SCSI: i box USB.</summary>
    Sat,
}

/// <summary>Un attributo S.M.A.R.T. (formato ATA) oppure una voce della log page NVMe.</summary>
public sealed class SmartAttribute
{
    public byte Id { get; init; }
    public string Name { get; init; } = "";
    public ushort Flags { get; init; }
    public byte Current { get; init; }
    public byte Worst { get; init; }
    public byte Threshold { get; init; }
    public ulong RawValue { get; init; }
    public byte[] RawBytes { get; init; } = [];
    public HealthState State { get; init; } = HealthState.Unknown;

    public string IdHex => $"{Id:X2}";
    public string RawHex => RawBytes.Length == 6
        ? string.Concat(Enumerable.Reverse(RawBytes).Select(b => b.ToString("X2")))
        : RawValue.ToString("X12");
}

/// <summary>Snapshot completo di un disco fisico.</summary>
public sealed class DiskInfo
{
    // Identità
    public int Index { get; set; }
    public string DevicePath => $@"\.\PhysicalDrive{Index}";
    public string Model { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Vendor { get; set; } = "";
    public StorageBusType BusType { get; set; }
    public DiskKind Kind { get; set; }
    public bool IsRemovable { get; set; }

    // Geometria
    public ulong SizeBytes { get; set; }
    public uint LogicalSectorSize { get; set; }
    public uint PhysicalSectorSize { get; set; }
    public int RotationRate { get; set; }          // 0 = SSD, >0 = RPM
    public List<string> DriveLetters { get; } = [];

    // Stato
    public int? TemperatureC { get; set; }
    public int? TemperatureMaxC { get; set; }      // massimo storico rilevato dal firmware
    public int? WarningTemperatureC { get; set; }
    public int? CriticalTemperatureC { get; set; }
    public List<(string Label, int Value)> ExtraSensors { get; } = [];

    public HealthState Health { get; set; } = HealthState.Unknown;
    public string HealthDetail { get; set; } = "";
    public int? LifePercent { get; set; }          // vita residua stimata 0-100
    public ulong? PowerOnHours { get; set; }
    public ulong? PowerOnCount { get; set; }
    public ulong? HostReadsBytes { get; set; }
    public ulong? HostWritesBytes { get; set; }
    public bool SupportsSmart { get; set; }
    public string DataSource { get; set; } = "";   // da dove arriva la temperatura
    public string? LastError { get; set; }
    public string TransferMode { get; set; } = ""; // es. "SATA/600 | SATA/600"

    /// <summary>Canale ATA che ha risposto: serve anche all'autotest.</summary>
    public AtaChannel Channel { get; set; }

    /// <summary>Byte 363 dello S.M.A.R.T.: stato dell'ultima autodiagnosi.</summary>
    public byte? SelfTestStatusByte { get; set; }

    /// <summary>
    /// Se il firmware dichiara di prevedere l'autodiagnosi. Nullo quando non si è
    /// riusciti a chiederglielo.
    /// </summary>
    public bool? SupportsSelfTest { get; set; }

    public List<SmartAttribute> Attributes { get; } = [];

    // Temperature storiche accumulate dall'app (per il tooltip / grafico)
    public int? SessionMinC { get; set; }
    public int? SessionMaxC { get; set; }

    /// <summary>Ultime letture di temperatura, dalla più vecchia alla più recente.</summary>
    public Queue<int> History { get; } = new();
    public const int HistoryCapacity = 240;

    public void PushHistory(int celsius)
    {
        History.Enqueue(celsius);
        while (History.Count > HistoryCapacity) History.Dequeue();
    }

    // Occupazione complessiva dei volumi ospitati dal disco
    public ulong VolumeTotalBytes { get; set; }
    public ulong VolumeUsedBytes { get; set; }

    public double? UsedFraction => VolumeTotalBytes > 0
        ? Math.Clamp((double)VolumeUsedBytes / VolumeTotalBytes, 0, 1)
        : null;

    public string SizeDisplay => FormatBytes(SizeBytes);

    public string InterfaceDisplay => BusType switch
    {
        StorageBusType.Nvme => "NVM Express",
        StorageBusType.Sata => "Serial ATA",
        StorageBusType.Ata => "Parallel ATA",
        StorageBusType.Usb => "USB",
        StorageBusType.Sas => "SAS",
        StorageBusType.Scsi => "SCSI",
        StorageBusType.RAID => "RAID",
        StorageBusType.Sd => "SD Card",
        StorageBusType.Mmc => "eMMC",
        StorageBusType.Ufs => "UFS",
        StorageBusType.Spaces => "Storage Spaces",
        StorageBusType.Virtual or StorageBusType.FileBackedVirtual => "Virtuale",
        _ => BusType.ToString(),
    };

    public string KindDisplay => Kind switch
    {
        DiskKind.Nvme => "SSD NVMe",
        DiskKind.Ssd => "SSD",
        DiskKind.Hdd => RotationRate > 0 ? $"HDD {RotationRate} RPM" : "HDD",
        DiskKind.Removable => "Rimovibile",
        _ => "Sconosciuto",
    };

    public string LettersDisplay => DriveLetters.Count > 0 ? string.Join(" ", DriveLetters) : "—";

    /// <summary>Prima lettera di unità in ordine alfabetico, se il disco ne ha.</summary>
    public string? PrimaryLetter => DriveLetters.Count > 0 ? DriveLetters[0] : null;

    /// <summary>
    /// Identificativo per le impostazioni: deve restare lo stesso fra un avvio e
    /// l'altro, anche se Windows rinumera i dischi o cambia la lettera di unità.
    /// Il numero di serie è la cosa più stabile che si abbia; se manca si ripiega su
    /// modello e capacità, e solo in ultimo sul numero del disco.
    /// </summary>
    public string TrayKey =>
        SerialNumber.Trim() is { Length: > 0 } sn ? "sn:" + sn
        : Model.Trim() is { Length: > 0 } md ? $"md:{md}|{SizeBytes}"
        : "ix:" + Index;

    /// <summary>Nome corto per elenchi e caselle: la lettera se c'è, altrimenti il numero.</summary>
    public string ShortName => DriveLetters.Count > 0 ? LettersDisplay : $"Disco {Index}";

    /// <summary>Vero se dal disco arriva davvero una temperatura da mostrare.</summary>
    public bool HasMeasurements => TemperatureC is not null;

    /// <summary>
    /// Vero se dal dispositivo non arriva niente: né temperatura né attributi. Resta
    /// solo quello che Windows sa già dirne, cioè modello e capacità.
    /// </summary>
    public bool HasNoReadings => TemperatureC is null && Attributes.Count == 0;

    /// <summary>
    /// Vero per i dispositivi USB da cui non si ottiene alcun dato: tipicamente chiavette
    /// e lettori di schede, che non hanno né sensore di temperatura né S.M.A.R.T., e che
    /// nell'elenco comparirebbero come una riga di trattini.
    /// </summary>
    public bool IsMuteUsbDevice => BusType == StorageBusType.Usb && HasNoReadings;

    /// <summary>
    /// Ordine di presentazione richiesto: prima i dischi da cui arriva una misura, poi
    /// gli altri; dentro ciascun gruppo si va per lettera di unità, e i dischi senza
    /// lettera chiudono la fila nell'ordine in cui Windows li numera.
    /// </summary>
    public static IEnumerable<DiskInfo> InDisplayOrder(IEnumerable<DiskInfo> disks) =>
        disks.OrderBy(d => d.HasMeasurements ? 0 : 1)
             .ThenBy(d => d.PrimaryLetter ?? "￿", StringComparer.OrdinalIgnoreCase)
             .ThenBy(d => d.Index);

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Model) ? $"Disco {Index}" : Model;
            return $"{name} ({SizeDisplay})";
        }
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes == 0) return "—";
        double gb = bytes / 1_000_000_000d;
        if (gb >= 1000) return $"{gb / 1000d:0.##} TB";
        return $"{gb:0.#} GB";
    }
}
