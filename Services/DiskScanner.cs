using System.Security.Principal;
using DiskTempMonitor.Models;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

/// <summary>
/// Enumera i dischi fisici collegati al PC e ne raccoglie identità, S.M.A.R.T. e
/// temperatura. Usa in cascata: log page NVMe -> SMART ATA -> sensori del driver.
/// </summary>
public sealed class DiskScanner
{
    private const int MaxPhysicalDrives = 64;

    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Scansione completa: elenco dischi con tutti i dati disponibili.</summary>
    public List<DiskInfo> ScanAll()
    {
        // La rienumerazione riparte da zero: un disco appena collegato, o un box che
        // prima non rispondeva, deve poter essere riprovato su tutti i canali.
        lock (KnownChannels) KnownChannels.Clear();

        // Anche la geometria va riletta, e non è un eccesso di zelo. La capacità viene
        // tenuta da parte per non richiederla a ogni giro — su certe chiavette USB
        // costa secondi — e l'etichetta sotto cui è archiviata è quella che dichiara il
        // dispositivo. Ma un box USB dichiara sé stesso, non il disco che ha dentro:
        // modello e numero di serie restano gli stessi anche cambiando l'SSD, così la
        // capacità di quello vecchio veniva ripresentata per quello nuovo. Qui, dove si
        // riparte da capo, non si dà niente per acquisito; gli aggiornamenti periodici
        // continuano a ricopiare dal giro precedente e restano gratuiti.
        lock (KnownGeometry) KnownGeometry.Clear();

        var letters = MapDriveLetters();
        var found = new DiskInfo?[MaxPhysicalDrives];

        // I dischi si interrogano in parallelo: un dispositivo lento (certe chiavette
        // USB impiegano secondi solo a dichiarare la propria geometria) non deve far
        // aspettare tutti gli altri.
        Parallel.For(0, MaxPhysicalDrives, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var disk = ReadDisk(i);
            if (disk is null) return;

            if (letters.TryGetValue(i, out var l))
            {
                disk.DriveLetters.AddRange(l.OrderBy(x => x));
                FillVolumeUsage(disk);
            }
            found[i] = disk;
        });

        return DiskInfo.InDisplayOrder(found.OfType<DiskInfo>()).ToList();
    }

    /// <summary>Aggiorna solo i valori variabili (temperatura, contatori, salute).</summary>
    public void Refresh(DiskInfo disk)
    {
        var fresh = ReadDisk(disk.Index, disk);
        if (fresh is null)
        {
            disk.TemperatureC = null;
            disk.LastError = "Disco non più raggiungibile";
            return;
        }

        disk.TemperatureC = fresh.TemperatureC;
        disk.TemperatureMaxC = fresh.TemperatureMaxC;
        disk.WarningTemperatureC = fresh.WarningTemperatureC;
        disk.CriticalTemperatureC = fresh.CriticalTemperatureC;
        disk.Health = fresh.Health;
        disk.HealthDetail = fresh.HealthDetail;
        disk.LifePercent = fresh.LifePercent;
        disk.PowerOnHours = fresh.PowerOnHours;
        disk.PowerOnCount = fresh.PowerOnCount;
        disk.HostReadsBytes = fresh.HostReadsBytes;
        disk.HostWritesBytes = fresh.HostWritesBytes;
        disk.DataSource = fresh.DataSource;
        disk.LastError = fresh.LastError;
        disk.Channel = fresh.Channel;
        disk.SelfTestStatusByte = fresh.SelfTestStatusByte;
        disk.SupportsSelfTest = fresh.SupportsSelfTest ?? disk.SupportsSelfTest;
        if (fresh.TransferMode.Length > 0) disk.TransferMode = fresh.TransferMode;

        disk.ExtraSensors.Clear();
        disk.ExtraSensors.AddRange(fresh.ExtraSensors);

        disk.Attributes.Clear();
        disk.Attributes.AddRange(fresh.Attributes);

        FillVolumeUsage(disk);
        TrackSession(disk);
    }

    private static void TrackSession(DiskInfo disk)
    {
        if (disk.TemperatureC is not int t) return;
        disk.SessionMinC = disk.SessionMinC is int min ? Math.Min(min, t) : t;
        disk.SessionMaxC = disk.SessionMaxC is int max ? Math.Max(max, t) : t;
        disk.PushHistory(t);
    }

    /// <summary>Somma spazio totale e occupato dei volumi montati sul disco.</summary>
    private static void FillVolumeUsage(DiskInfo disk)
    {
        ulong total = 0, used = 0;
        foreach (string letter in disk.DriveLetters)
        {
            try
            {
                var info = new DriveInfo(letter);
                if (info.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!info.IsReady) continue;
                total += (ulong)info.TotalSize;
                used += (ulong)(info.TotalSize - info.TotalFreeSpace);
            }
            catch { /* volume non pronto o non accessibile */ }
        }
        disk.VolumeTotalBytes = total;
        disk.VolumeUsedBytes = used;
    }

    // --------------------------------------------------------------- lettura

    /// <param name="previous">
    /// Lo stato già noto dello stesso disco, quando si tratta di un aggiornamento. Ciò
    /// che non cambia mai — capacità, geometria, tipo — viene ricopiato invece di essere
    /// richiesto di nuovo: su certe chiavette USB la sola lettura della geometria costa
    /// diversi secondi, e bloccherebbe ogni giro di aggiornamento.
    /// </param>
    private DiskInfo? ReadDisk(int index, DiskInfo? previous = null)
    {
        string path = $@"\\.\PhysicalDrive{index}";

        // Prima tentiamo l'accesso completo (serve per i comandi SMART ATA).
        var handle = NativeMethods.CreateFileW(path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        bool fullAccess = !handle.IsInvalid;
        if (!fullAccess)
        {
            handle.Dispose();
            // Fallback in sola interrogazione: identità e sensori del driver restano leggibili.
            handle = NativeMethods.CreateFileW(path, 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }

        using (handle)
        {
            if (handle.IsInvalid) return null;

            var desc = StorageQuery.GetDeviceDescriptor(handle);
            if (desc is null) return null;

            var disk = new DiskInfo
            {
                Index = index,
                BusType = desc.BusType,
                IsRemovable = desc.RemovableMedia,
                Vendor = desc.Vendor,
                Model = BuildModel(desc),
                Firmware = desc.Revision,
                SerialNumber = NormalizeSerial(desc.Serial),
            };

            bool? seekPenalty = null;

            if (previous is null)
            {
                var geometry = Geometry(handle, disk);
                disk.SizeBytes = geometry.Size;
                disk.LogicalSectorSize = geometry.Logical;
                disk.PhysicalSectorSize = geometry.Physical;
                seekPenalty = geometry.SeekPenalty;
            }
            else
            {
                disk.SizeBytes = previous.SizeBytes;
                disk.LogicalSectorSize = previous.LogicalSectorSize;
                disk.PhysicalSectorSize = previous.PhysicalSectorSize;
                disk.RotationRate = previous.RotationRate;
                disk.TransferMode = previous.TransferMode;
            }

            if (desc.BusType == StorageBusType.Nvme)
            {
                disk.Kind = DiskKind.Nvme;
                ReadNvme(handle, disk);
            }
            else
            {
                // Dentro un box USB può esserci un SSD NVMe: se il ponte è di quelli che
                // sanno inoltrare i comandi NVMe si ottiene tutto, e si evitano i
                // tentativi ATA che su un disco simile non potrebbero funzionare.
                bool bridged = fullAccess
                    && desc.BusType == StorageBusType.Usb
                    && UsbNvmeBridge.LooksLikeRealtek(desc.Vendor, desc.Product)
                    && ReadNvmeOverUsb(handle, disk);

                if (!bridged) ReadAta(handle, disk, fullAccess, previous);

                disk.Kind = bridged ? DiskKind.Nvme
                          : previous is not null && previous.Kind != DiskKind.Unknown ? previous.Kind
                          : disk.RotationRate > 1 ? DiskKind.Hdd
                          : disk.RotationRate == 1 ? DiskKind.Ssd
                          : seekPenalty == true ? DiskKind.Hdd
                          : seekPenalty == false ? DiskKind.Ssd
                          : desc.RemovableMedia ? DiskKind.Removable
                          : DiskKind.Unknown;
            }

            // Un box USB può contenere un SSD NVMe invece di un SATA: certi ponti
            // inoltrano le query NVMe di Windows, e allora si ottiene tutto.
            if (disk.TemperatureC is null && desc.BusType == StorageBusType.Usb && fullAccess)
            {
                string? primaNota = disk.LastError;
                ReadNvme(handle, disk);

                if (disk.TemperatureC is null) disk.LastError = primaNota;   // non era un NVMe
                else disk.Kind = DiskKind.Nvme;
            }

            // I sensori esposti direttamente dal driver di storage.
            if (disk.TemperatureC is null)
            {
                var temp = StorageQuery.GetTemperature(handle);
                if (temp is not null && temp.Sensors.Count > 0)
                {
                    disk.TemperatureC = temp.Sensors[0];
                    disk.WarningTemperatureC ??= temp.Warning;
                    disk.CriticalTemperatureC ??= temp.Critical;
                    disk.DataSource = "Driver storage (sensore)";
                    for (int i = 1; i < temp.Sensors.Count; i++)
                        disk.ExtraSensors.Add(($"Sensore {i + 1}", temp.Sensors[i]));
                }
            }

            // Pagine di log SCSI: le capiscono parecchi box esterni che rifiutano tutto
            // il resto, ed è comunque un comando di sola lettura.
            if (disk.TemperatureC is null && fullAccess &&
                desc.BusType is StorageBusType.Usb or StorageBusType.Scsi or StorageBusType.Sas &&
                ScsiLogSense.ReadTemperature(handle) is int scsiTemp)
            {
                disk.TemperatureC = scsiTemp;
                disk.DataSource = "Pagina di log SCSI (temperatura)";
                disk.LastError = null;
            }

            if (disk.TemperatureC is null && disk.LastError is null)
            {
                disk.LastError = fullAccess
                    ? "Temperatura non esposta da questo dispositivo"
                    : "Servono privilegi di amministratore per leggere lo S.M.A.R.T.";
            }

            if (disk.IsRemovable && disk.Kind == DiskKind.Unknown) disk.Kind = DiskKind.Removable;

            TrackSession(disk);
            return disk;
        }
    }

    /// <summary>Dati fisici che non cambiano finché quel disco resta quel disco.</summary>
    private readonly record struct DiskGeometry(ulong Size, uint Logical, uint Physical, bool? SeekPenalty);

    private static readonly Dictionary<string, DiskGeometry> KnownGeometry = [];

    /// <summary>
    /// Capacità, dimensione dei settori e presenza di parti in movimento. Si leggono una
    /// volta sola per dispositivo: su certe chiavette USB la sola richiesta della
    /// geometria impegna il bus per diversi secondi.
    /// <para>
    /// L'etichetta sotto cui si archiviano è quella dichiarata dal dispositivo, e per un
    /// box USB è quella del <b>box</b>, non del disco che ha dentro: cambiando l'SSD
    /// resta identica. Per questo <see cref="ScanAll"/> svuota tutto prima di
    /// ricominciare — è l'unico momento in cui si può scoprire che dietro la stessa
    /// etichetta c'è un disco diverso.
    /// </para>
    /// </summary>
    private static DiskGeometry Geometry(SafeFileHandle handle, DiskInfo disk)
    {
        string key = $"{disk.Index}|{disk.SerialNumber}|{disk.Model}";

        lock (KnownGeometry)
            if (KnownGeometry.TryGetValue(key, out var cached)) return cached;

        var sectors = StorageQuery.GetSectorSizes(handle);
        var geometry = new DiskGeometry(
            GetLength(handle),
            sectors?.Logical ?? 0,
            sectors?.Physical ?? 0,
            StorageQuery.GetSeekPenalty(handle));

        lock (KnownGeometry) KnownGeometry[key] = geometry;
        return geometry;
    }

    private static string BuildModel(StorageQuery.DeviceDescriptor desc)
    {
        string vendor = desc.Vendor.Trim();
        string product = desc.Product.Trim();
        if (vendor.Length == 0) return product;
        if (product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return product;
        return $"{vendor} {product}".Trim();
    }

    // --------------------------------------------------------------------- NVMe

    private static void ReadNvme(SafeFileHandle h, DiskInfo disk)
    {
        var identRaw = StorageQuery.GetNvmeIdentifyRaw(h);
        disk.SupportsSelfTest = StorageQuery.NvmeSupportsSelfTest(identRaw);

        var ident = StorageQuery.ParseNvmeIdentify(identRaw);
        if (ident is not null)
        {
            if (ident.Value.Model.Length > 0) disk.Model = ident.Value.Model;
            if (ident.Value.Serial.Length > 0) disk.SerialNumber = ident.Value.Serial;
            if (ident.Value.Firmware.Length > 0) disk.Firmware = ident.Value.Firmware;
        }

        var health = StorageQuery.GetNvmeHealth(h);
        if (health is null)
        {
            disk.LastError = DiskScanner.IsAdministrator
                ? "Log page NVMe non disponibile su questo controller"
                : "Servono privilegi di amministratore per la log page NVMe";
            return;
        }

        ApplyNvmeHealth(disk, health, "NVMe SMART/Health log (02h)");
    }

    /// <summary>
    /// Stesso disco NVMe, ma raggiunto attraverso il protocollo del ponte USB: i dati
    /// che tornano hanno esattamente la forma di quelli letti per via diretta.
    /// </summary>
    private static bool ReadNvmeOverUsb(SafeFileHandle h, DiskInfo disk)
    {
        var health = StorageQuery.ParseNvmeHealth(UsbNvmeBridge.SmartHealth(h));
        if (health is null) return false;

        var identRaw = UsbNvmeBridge.Identify(h);
        disk.SupportsSelfTest = false;      // il ponte non inoltra i comandi, solo le letture

        var ident = StorageQuery.ParseNvmeIdentify(identRaw);
        if (ident is not null)
        {
            if (ident.Value.Model.Length > 0) disk.Model = ident.Value.Model;
            if (ident.Value.Serial.Length > 0) disk.SerialNumber = ident.Value.Serial;
            if (ident.Value.Firmware.Length > 0) disk.Firmware = ident.Value.Firmware;
        }

        disk.LastError = null;
        ApplyNvmeHealth(disk, health, "NVMe SMART/Health via ponte USB (Realtek)");
        return true;
    }

    private static void ApplyNvmeHealth(DiskInfo disk, StorageQuery.NvmeHealth health, string source)
    {
        disk.SupportsSmart = true;
        disk.DataSource = source;
        disk.TemperatureC = health.CompositeTemperatureC > -60 && health.CompositeTemperatureC < 150
            ? health.CompositeTemperatureC
            : null;

        foreach (var (idx, t) in health.Sensors)
            disk.ExtraSensors.Add(($"Sensore {idx}", t));

        disk.LifePercent = Math.Max(0, 100 - health.PercentageUsed);
        disk.PowerOnHours = health.PowerOnHours;
        disk.PowerOnCount = health.PowerCycles;
        // Una "data unit" NVMe vale 1000 blocchi da 512 byte.
        disk.HostReadsBytes = health.DataUnitsRead * 512UL * 1000UL;
        disk.HostWritesBytes = health.DataUnitsWritten * 512UL * 1000UL;

        disk.Health = health.CriticalWarning != 0 ? HealthState.Bad
                    : health.PercentageUsed >= 90 ? HealthState.Caution
                    : health.AvailableSpare > 0 && health.AvailableSpare < health.AvailableSpareThreshold ? HealthState.Caution
                    : health.MediaErrors > 0 ? HealthState.Caution
                    : HealthState.Good;

        disk.HealthDetail = health.CriticalWarning != 0
            ? DescribeCriticalWarning(health.CriticalWarning)
            : $"Usura {health.PercentageUsed}% · spare {health.AvailableSpare}%";

        AddNvmeRows(disk, health);
    }

    private static string DescribeCriticalWarning(byte w)
    {
        var parts = new List<string>();
        if ((w & 0x01) != 0) parts.Add("spare sotto soglia");
        if ((w & 0x02) != 0) parts.Add("temperatura fuori range");
        if ((w & 0x04) != 0) parts.Add("affidabilità degradata");
        if ((w & 0x08) != 0) parts.Add("supporto in sola lettura");
        if ((w & 0x10) != 0) parts.Add("memoria volatile di backup compromessa");
        if ((w & 0x20) != 0) parts.Add("memoria persistente in sola lettura");
        return parts.Count > 0 ? string.Join(", ", parts) : "avviso critico attivo";
    }

    private static void AddNvmeRows(DiskInfo disk, StorageQuery.NvmeHealth n)
    {
        void Row(byte id, string name, ulong raw, HealthState state = HealthState.Unknown) =>
            disk.Attributes.Add(new SmartAttribute
            {
                Id = id,
                Name = name,
                RawValue = raw,
                Current = 0,
                Worst = 0,
                Threshold = 0,
                State = state,
            });

        Row(0x01, "Critical Warning", n.CriticalWarning, n.CriticalWarning == 0 ? HealthState.Good : HealthState.Bad);
        Row(0x02, "Composite Temperature (°C)", (ulong)Math.Max(0, n.CompositeTemperatureC));
        Row(0x03, "Available Spare (%)", n.AvailableSpare,
            n.AvailableSpare >= n.AvailableSpareThreshold ? HealthState.Good : HealthState.Caution);
        Row(0x04, "Available Spare Threshold (%)", n.AvailableSpareThreshold);
        Row(0x05, "Percentage Used (%)", n.PercentageUsed,
            n.PercentageUsed < 90 ? HealthState.Good : HealthState.Caution);
        Row(0x06, "Data Units Read", n.DataUnitsRead);
        Row(0x07, "Data Units Written", n.DataUnitsWritten);
        Row(0x08, "Host Read Commands", n.HostReadCommands);
        Row(0x09, "Host Write Commands", n.HostWriteCommands);
        Row(0x0B, "Power Cycles", n.PowerCycles);
        Row(0x0C, "Power On Hours", n.PowerOnHours);
        Row(0x0D, "Unsafe Shutdowns", n.UnsafeShutdowns);
        Row(0x0E, "Media Errors", n.MediaErrors, n.MediaErrors == 0 ? HealthState.Good : HealthState.Caution);
        Row(0x0F, "Error Info Log Entries", n.ErrorLogEntries);
        Row(0x10, "Warning Temp. Time (min)", n.WarningTempTimeMin);
        Row(0x11, "Critical Temp. Time (min)", n.CriticalTempTimeMin,
            n.CriticalTempTimeMin == 0 ? HealthState.Good : HealthState.Caution);

        foreach (var (idx, t) in n.Sensors)
            Row((byte)(0x20 + idx), $"Temperature Sensor {idx} (°C)", (ulong)Math.Max(0, t));
    }

    // ---------------------------------------------------------------------- ATA

    /// <summary>
    /// Canale che ha funzionato l'ultima volta, disco per disco. Riprovarle tutte a ogni
    /// giro costa secondi su un ponte USB che non risponde, e l'aggiornamento periodico
    /// ne resterebbe bloccato.
    /// </summary>
    private static readonly Dictionary<string, AtaChannel> KnownChannels = [];

    private static string ChannelKey(DiskInfo disk) => $"{disk.Index}|{disk.SerialNumber}";

    private static IEnumerable<AtaChannel> ChannelOrder(DiskInfo disk)
    {
        AtaChannel[] all = [AtaChannel.PassThrough, AtaChannel.Legacy, AtaChannel.Sat];

        lock (KnownChannels)
        {
            if (KnownChannels.TryGetValue(ChannelKey(disk), out var known))
            {
                // Nessun canale ha funzionato alla scansione: riprovarli a ogni
                // aggiornamento periodico costerebbe un timeout per ciascuno. Si
                // ritenta solo alla prossima rienumerazione.
                return known == AtaChannel.None
                    ? []
                    : all.OrderBy(c => c == known ? 0 : 1).ToArray();
            }
        }

        // Sui dispositivi USB conviene partire dal SAT: gli altri due falliscono quasi
        // sempre, e ogni tentativo a vuoto costa un timeout.
        return disk.BusType == StorageBusType.Usb
            ? [AtaChannel.Sat, AtaChannel.PassThrough, AtaChannel.Legacy]
            : all;
    }

    private static void Remember(DiskInfo disk, AtaChannel channel)
    {
        lock (KnownChannels) KnownChannels[ChannelKey(disk)] = channel;
    }

    /// <summary>Il canale ATA già riconosciuto per questo disco, se ne esiste uno.</summary>
    public static AtaChannel ChannelFor(DiskInfo disk)
    {
        lock (KnownChannels)
            return KnownChannels.TryGetValue(ChannelKey(disk), out var known) ? known : AtaChannel.None;
    }

    private static string DescribeChannel(AtaChannel channel) => channel switch
    {
        AtaChannel.PassThrough => "S.M.A.R.T. ATA (pass-through)",
        AtaChannel.Legacy => "S.M.A.R.T. ATA (attributi)",
        AtaChannel.Sat => "S.M.A.R.T. ATA via ponte SCSI (SAT)",
        _ => "",
    };

    private static byte[]? ReadIdentify(SafeFileHandle h, byte drive, AtaChannel channel) => channel switch
    {
        AtaChannel.PassThrough => AtaPassThrough.Identify(h),
        AtaChannel.Legacy => AtaSmart.IdentifyRaw(h, drive),
        AtaChannel.Sat => SatPassThrough.Identify(h),
        _ => null,
    };

    private static void ReadAta(SafeFileHandle h, DiskInfo disk, bool fullAccess, DiskInfo? previous = null)
    {
        byte drive = (byte)disk.Index;

        if (!fullAccess)
        {
            disk.LastError = "Servono privilegi di amministratore per leggere lo S.M.A.R.T.";
            return;
        }

        // --- identità: modello, seriale, firmware, giri al minuto ---
        var identChannel = AtaChannel.None;

        // In aggiornamento l'identità è già nota: si va dritti agli attributi.
        if (previous is { Channel: not AtaChannel.None })
        {
            identChannel = previous.Channel;
            if (previous.Model.Length > 0) disk.Model = previous.Model;
            if (previous.SerialNumber.Length > 0) disk.SerialNumber = previous.SerialNumber;
            if (previous.Firmware.Length > 0) disk.Firmware = previous.Firmware;
            disk.SupportsSmart = previous.SupportsSmart;
            disk.SupportsSelfTest = previous.SupportsSelfTest;
        }

        foreach (var channel in identChannel != AtaChannel.None ? [] : ChannelOrder(disk))
        {
            var raw = ReadIdentify(h, drive, channel);
            if (!AtaData.LooksLikeIdentify(raw)) continue;

            var ident = AtaSmart.ParseIdentify(raw);
            if (ident is null) continue;

            if (ident.Model.Length > 0) disk.Model = ident.Model;
            if (ident.Serial.Length > 0) disk.SerialNumber = ident.Serial;
            if (ident.Firmware.Length > 0) disk.Firmware = ident.Firmware;
            disk.RotationRate = ident.RotationRate;
            disk.SupportsSmart = ident.SmartSupported;
            disk.SupportsSelfTest = ident.SelfTestSupported;
            disk.TransferMode = ident.TransferMode;
            identChannel = channel;
            break;
        }

        // --- attributi: si riparte dal canale che ha già risposto all'identificazione ---
        byte[]? attrs = null;
        byte[]? thresholds = null;
        var used = AtaChannel.None;

        var order = identChannel == AtaChannel.None
            ? ChannelOrder(disk)
            : new[] { identChannel }.Concat(ChannelOrder(disk).Where(c => c != identChannel));

        foreach (var channel in order)
        {
            var candidate = channel switch
            {
                AtaChannel.PassThrough => AtaPassThrough.SmartAttributes(h),
                AtaChannel.Legacy => AtaSmart.IsSmartSupported(h) ? AtaSmart.ReadAttributesRaw(h, drive) : null,
                AtaChannel.Sat => SatPassThrough.SmartAttributes(h),
                _ => null,
            };

            if (!AtaData.LooksLikeSmart(candidate)) continue;

            attrs = candidate;
            thresholds = channel switch
            {
                AtaChannel.PassThrough => AtaPassThrough.SmartThresholds(h),
                AtaChannel.Legacy => AtaSmart.ReadThresholdsRaw(h, drive),
                AtaChannel.Sat => SatPassThrough.SmartThresholds(h),
                _ => null,
            };
            used = channel;
            break;
        }

        Remember(disk, used != AtaChannel.None ? used : identChannel);
        disk.Channel = used != AtaChannel.None ? used : identChannel;

        if (attrs is null)
        {
            disk.LastError = disk.BusType == StorageBusType.Usb
                ? "Il box esterno non inoltra i comandi S.M.A.R.T. al disco"
                : "Il driver non espone il canale S.M.A.R.T. per questo disco";
            return;
        }

        // Offset 363 della struttura SMART READ DATA: stato dell'autodiagnosi.
        disk.SelfTestStatusByte = attrs[363];

        var thrMap = new Dictionary<byte, byte>();
        if (thresholds is not null)
        {
            for (int i = 0; i < 30; i++)
            {
                int off = 2 + i * 12;
                byte id = thresholds[off];
                if (id != 0) thrMap[id] = thresholds[off + 1];
            }
        }

        disk.SupportsSmart = true;
        disk.DataSource = DescribeChannel(used);
        var worst = HealthState.Good;

        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            byte id = attrs[off];
            if (id == 0) continue;

            ushort flags = BitConverter.ToUInt16(attrs, off + 1);
            byte current = attrs[off + 3];
            byte worstVal = attrs[off + 4];
            var rawBytes = new byte[6];
            Array.Copy(attrs, off + 5, rawBytes, 0, 6);

            ulong raw = 0;
            for (int b = 5; b >= 0; b--) raw = (raw << 8) | rawBytes[b];

            thrMap.TryGetValue(id, out byte threshold);

            var state = HealthState.Unknown;
            if (threshold > 0 && current > 0)
                state = current <= threshold ? HealthState.Bad
                      : current <= threshold + 10 ? HealthState.Caution
                      : HealthState.Good;
            if (SmartNames.IsCritical(id) && raw > 0)
                state = state == HealthState.Bad ? HealthState.Bad : HealthState.Caution;
            if (state == HealthState.Unknown && threshold == 0 && current > 0)
                state = HealthState.Good;

            if (state == HealthState.Bad) worst = HealthState.Bad;
            else if (state == HealthState.Caution && worst != HealthState.Bad) worst = HealthState.Caution;

            disk.Attributes.Add(new SmartAttribute
            {
                Id = id,
                Name = SmartNames.Get(id),
                Flags = flags,
                Current = current,
                Worst = worstVal,
                Threshold = threshold,
                RawValue = raw,
                RawBytes = rawBytes,
                State = state,
            });

            switch (id)
            {
                case 0xC2 or 0xBE:
                    // Il byte 0 del raw è la temperatura corrente; su molti firmware i word
                    // successivi contengono il minimo e il massimo storici.
                    int t = (int)(raw & 0xFF);
                    // Alcuni firmware (tipicamente Seagate, attributo BEh) espongono la
                    // temperatura solo come 100 - valore normalizzato.
                    if (t is <= 0 or >= 120 && current is > 0 and < 100) t = 100 - current;
                    if (t is > 0 and < 120 && disk.TemperatureC is null) disk.TemperatureC = t;

                    int tMax = (int)((raw >> 32) & 0xFFFF);
                    if (tMax is > 0 and < 120) disk.TemperatureMaxC = tMax;
                    break;
                case 0x09:
                    disk.PowerOnHours = raw & 0xFFFFFFFF;
                    break;
                case 0x0C:
                    disk.PowerOnCount = raw & 0xFFFFFFFF;
                    break;
                case 0xF1 or 0xF3:
                    disk.HostWritesBytes = raw * 512UL;
                    break;
                case 0xF2 or 0xF4:
                    disk.HostReadsBytes = raw * 512UL;
                    break;
                case 0xE7 or 0xE8 or 0xE9 or 0xAD:
                    disk.LifePercent ??= current;
                    break;
            }
        }

        disk.Health = worst;
        disk.HealthDetail = worst switch
        {
            HealthState.Good => "Tutti gli attributi entro soglia",
            HealthState.Caution => "Uno o più attributi vicini alla soglia",
            HealthState.Bad => "Uno o più attributi sotto soglia",
            _ => "",
        };
    }

    // ------------------------------------------------------------------ utility

    private static ulong GetLength(SafeFileHandle h)
    {
        // Richiede l'accesso in lettura: disponibile solo se l'handle è quello completo.
        var outBuf = new byte[8];
        if (NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                null, 0, outBuf, outBuf.Length, out int returned, IntPtr.Zero) && returned >= 8)
            return BitConverter.ToUInt64(outBuf, 0);

        // Fallback senza privilegi: DISK_GEOMETRY_EX espone DiskSize all'offset 24.
        var geo = new byte[64];
        if (NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                null, 0, geo, geo.Length, out int geoReturned, IntPtr.Zero) && geoReturned >= 32)
            return BitConverter.ToUInt64(geo, 24);

        return 0;
    }

    private static string NormalizeSerial(string serial)
    {
        serial = serial.Trim().Trim('.');
        // Alcuni driver restituiscono il seriale in esadecimale con i byte a coppie invertite.
        if (serial.Length >= 16 && serial.All(Uri.IsHexDigit) && serial.Length % 4 == 0)
        {
            try
            {
                var chars = new char[serial.Length];
                for (int i = 0; i < serial.Length; i += 4)
                {
                    chars[i] = serial[i + 2];
                    chars[i + 1] = serial[i + 3];
                    chars[i + 2] = serial[i];
                    chars[i + 3] = serial[i + 1];
                }
                string swapped = new(chars);
                var bytes = Convert.FromHexString(swapped);
                if (bytes.All(b => b >= 32 && b < 127))
                    return System.Text.Encoding.ASCII.GetString(bytes).Trim();
            }
            catch { /* non era un seriale codificato: teniamo l'originale */ }
        }
        return serial;
    }

    /// <summary>Associa a ogni indice di disco fisico le lettere di unità che ospita.</summary>
    private static Dictionary<int, List<string>> MapDriveLetters()
    {
        var map = new Dictionary<int, List<string>>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            // Solo i volumi che possono stare su un disco fisico. Interrogare un'unità
            // di rete non raggiungibile blocca la scansione per interi secondi, e per i
            // nostri scopi non serve a niente.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            string letter = drive.Name.TrimEnd('\\');
            if (letter.Length != 2) continue;

            var handle = NativeMethods.CreateFileW($@"\\.\{letter}", 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            using (handle)
            {
                if (handle.IsInvalid) continue;

                var outBuf = new byte[8 + 24 * 32];
                if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        null, 0, outBuf, outBuf.Length, out int returned, IntPtr.Zero) || returned < 8)
                    continue;

                int count = BitConverter.ToInt32(outBuf, 0);
                for (int i = 0; i < count; i++)
                {
                    int off = 8 + i * 24;
                    if (off + 4 > outBuf.Length) break;
                    int diskNumber = BitConverter.ToInt32(outBuf, off);
                    if (!map.TryGetValue(diskNumber, out var list))
                        map[diskNumber] = list = [];
                    if (!list.Contains(letter)) list.Add(letter);
                }
            }
        }

        return map;
    }
}
