using System.Runtime.InteropServices;
using DiskTempMonitor.Models;
using DiskTempMonitor.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskTempMonitor.Services;

public enum SelfTestKind
{
    /// <summary>Pochi minuti: controlli rapidi su elettronica e aree campione.</summary>
    Short,
    /// <summary>Anche ore: scansione completa della superficie.</summary>
    Extended,
    /// <summary>Non avvia niente: annulla quello in corso.</summary>
    Abort,
}

public sealed class SelfTestStatus
{
    public bool Running { get; init; }
    public int PercentComplete { get; init; }
    public string Description { get; init; } = "";
    public bool LastResultKnown { get; init; }
    public bool LastResultPassed { get; init; }
    public string LastResultDescription { get; init; } = "";
}

/// <summary>
/// Avvio e monitoraggio dell'autodiagnosi del disco.
/// <para>
/// È l'unica operazione che <b>invia</b> un comando al dispositivo invece di limitarsi a
/// leggerlo. Non tocca i dati: il disco esegue una verifica interna e resta utilizzabile,
/// solo un po' più lento finché non ha finito.
/// </para>
/// <para>
/// Il comando viaggia sullo stesso canale da cui si leggono gli attributi (pass-through
/// ATA, canale storico o incapsulamento SCSI), perché un disco che risponde su una via
/// non risponde per forza sulle altre.
/// </para>
/// </summary>
internal static class SelfTest
{
    private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x002DD3C0;

    private const int ProtocolTypeNvme = 3;
    private const uint StorageProtocolStructureVersion = 1;

    /// <summary>Byte che precedono il comando dentro STORAGE_PROTOCOL_COMMAND.</summary>
    private const int ProtocolCommandHeader = 80;

    /// <summary>
    /// Quanto misura la struttura secondo il compilatore C: l'intestazione più il primo
    /// byte del comando, arrotondato. Il driver confronta il campo Length con questo.
    /// </summary>
    private const int ProtocolCommandStructSize = 84;

    private const int NvmeCommandLength = 64;
    private const int NvmeErrorInfoLength = 64;      // sizeof(NVME_ERROR_INFO_LOG)
    private const uint NvmeAdminDeviceSelfTest = 0x14;
    private const uint NvmeAdminCommandFlag = 1;      // STORAGE_PROTOCOL_SPECIFIC_NVME_ADMIN_COMMAND

    // ---------------------------------------------------------------- avvio

    public static bool Start(DiskInfo disk, SelfTestKind kind, out string error)
    {
        error = "";
        using var handle = Open(disk.Index);
        if (handle is null || handle.IsInvalid)
        {
            error = "Impossibile aprire il disco con accesso in scrittura: " +
                    "l'applicazione deve essere in esecuzione come amministratore.";
            return false;
        }

        // Se il firmware dichiara di non prevederla, non ha senso provarci: si spiega
        // perché, invece di lasciare credere a un difetto dell'applicazione.
        if (disk.SupportsSelfTest == false && disk.BusType != StorageBusType.Usb)
        {
            error = $"{disk.DisplayName} non prevede l'autodiagnosi.\n" +
                    "Il firmware non dichiara il comando: è una funzione facoltativa, che " +
                    "parecchi SSD di fascia consumer non implementano.";
            return false;
        }

        if (disk.BusType == StorageBusType.Usb && disk.Kind == DiskKind.Nvme)
        {
            error = "Il disco è un NVMe dentro un box esterno: il ponte USB inoltra le " +
                    "letture dello stato, ma non il comando di autodiagnosi.\n" +
                    "Per eseguirlo va collegato direttamente alla scheda madre.";
            return false;
        }

        if (disk.BusType == StorageBusType.Nvme)
        {
            if (StartNvme(handle, kind, out string nvmeError)) return true;
            error = nvmeError;
            return false;
        }

        if (StartAta(handle, disk, kind)) return true;

        error = disk.Channel == AtaChannel.None
            ? "Questo disco non espone alcun canale S.M.A.R.T.: l'autodiagnosi non è avviabile.\n" +
              "Sui box USB dipende dal ponte USB→SATA, che spesso non inoltra i comandi."
            : "Il disco ha rifiutato il comando di autotest, o non prevede l'autodiagnosi.";
        return false;
    }

    /// <summary>
    /// Interrompe l'autodiagnosi in corso. È l'unico comando di governo previsto: mettere
    /// in pausa non esiste, né negli NVMe né negli ATA. Interrompere un test che non c'è
    /// non fa danno, il dispositivo lo ignora.
    /// </summary>
    public static bool Abort(DiskInfo disk, out string error)
    {
        error = "";
        using var handle = Open(disk.Index);
        if (handle is null || handle.IsInvalid)
        {
            error = "Impossibile aprire il disco con accesso in scrittura.";
            return false;
        }

        if (disk.BusType == StorageBusType.Nvme)
        {
            if (StartNvme(handle, SelfTestKind.Abort, out string nvmeError)) return true;
            error = nvmeError;
            return false;
        }

        foreach (var channel in Channels(disk))
        {
            bool ok = channel switch
            {
                AtaChannel.PassThrough => AtaPassThrough.StartSelfTest(handle, AtaAbortSubcommand),
                AtaChannel.Legacy => StartAtaLegacy(handle, disk, AtaAbortSubcommand),
                AtaChannel.Sat => SatPassThrough.StartSelfTest(handle, AtaAbortSubcommand),
                _ => false,
            };
            if (ok) return true;
        }

        error = "Il disco non ha accettato il comando di interruzione.";
        return false;
    }

    /// <summary>SMART EXECUTE OFF-LINE IMMEDIATE, sottocomando 7Fh: annulla il test.</summary>
    private const byte AtaAbortSubcommand = 0x7F;

    private static bool StartNvme(SafeFileHandle handle, SelfTestKind kind, out string error)
    {
        // Disposizione del buffer: intestazione, comando NVMe, area per l'eventuale
        // errore restituito. Senza l'area di errore dichiarata il driver rifiuta
        // l'operazione, ed è quello che faceva fallire ogni tentativo.
        int commandOffset = ProtocolCommandHeader;
        int errorInfoOffset = commandOffset + NvmeCommandLength;
        int total = errorInfoOffset + NvmeErrorInfoLength;
        var buffer = new byte[total];

        BitConverter.GetBytes(StorageProtocolStructureVersion).CopyTo(buffer, 0);   // Version
        BitConverter.GetBytes(ProtocolCommandStructSize).CopyTo(buffer, 4);         // Length
        BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, 8);                  // ProtocolType
        BitConverter.GetBytes(0).CopyTo(buffer, 12);                                // Flags
        BitConverter.GetBytes(NvmeCommandLength).CopyTo(buffer, 24);                // CommandLength
        BitConverter.GetBytes(NvmeErrorInfoLength).CopyTo(buffer, 28);              // ErrorInfoLength
        BitConverter.GetBytes(0).CopyTo(buffer, 32);                                // dati verso il device
        BitConverter.GetBytes(0).CopyTo(buffer, 36);                                // dati dal device
        BitConverter.GetBytes(60).CopyTo(buffer, 40);                               // TimeOutValue (s)
        BitConverter.GetBytes(errorInfoOffset).CopyTo(buffer, 44);                  // ErrorInfoOffset
        // Senza questo il driver non sa che è un comando amministrativo e lo rifiuta.
        BitConverter.GetBytes(NvmeAdminCommandFlag).CopyTo(buffer, 56);             // CommandSpecific

        // Comando NVMe: opcode in DWORD0, spazio nomi in DWORD1, codice del test in DWORD10
        buffer[commandOffset] = (byte)NvmeAdminDeviceSelfTest;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(buffer, commandOffset + 4);
        uint codice = kind switch
        {
            SelfTestKind.Short => 1u,
            SelfTestKind.Extended => 2u,
            _ => 0xFu,                  // interrompi
        };
        BitConverter.GetBytes(codice).CopyTo(buffer, commandOffset + 40);

        if (!NativeMethods.DeviceIoControl(handle, IOCTL_STORAGE_PROTOCOL_COMMAND,
                buffer, total, buffer, total, out int returned, IntPtr.Zero) || returned <= 0)
        {
            int errore = Marshal.GetLastWin32Error();
            error = "Il driver NVMe ha respinto il comando di autodiagnosi " +
                    $"(errore di sistema {errore}).\n" +
                    "Su parecchi controller Windows lascia passare solo la lettura delle log page.";
            return false;
        }

        uint status = BitConverter.ToUInt32(buffer, 16);      // ReturnStatus
        uint code = BitConverter.ToUInt32(buffer, 20);        // ErrorCode: stato NVMe

        // Attenzione al valore: nell'enumerazione di Windows lo zero è "in attesa" e
        // l'uno è "riuscito". Trattare il diverso-da-zero come errore faceva sembrare
        // fallito ogni comando andato a buon fine.
        bool riuscito = status is ProtocolStatusPending or ProtocolStatusSuccess && code == 0;
        if (!riuscito)
        {
            error = $"Il dispositivo ha rifiutato l'autodiagnosi ({DescribeProtocolStatus(status)}" +
                    (code != 0 ? $", stato NVMe {code}" : "") + ").\n" +
                    "Il comando Device Self-test è facoltativo, e non tutti gli SSD lo implementano.";
            return false;
        }

        error = "";
        return true;
    }

    // STORAGE_PROTOCOL_STATUS
    private const uint ProtocolStatusPending = 0;
    private const uint ProtocolStatusSuccess = 1;

    private static string DescribeProtocolStatus(uint status) => status switch
    {
        2 => "errore del dispositivo",
        3 => "richiesta non valida",
        4 => "dispositivo assente",
        5 => "dispositivo occupato",
        6 => "troppi dati restituiti",
        7 => "risorse insufficienti",
        8 => "richiesta rallentata",
        0xFF => "comando non supportato",
        _ => $"stato {status}",
    };

    private static bool StartAta(SafeFileHandle handle, DiskInfo disk, SelfTestKind kind)
    {
        byte subcommand = kind == SelfTestKind.Short
            ? NativeMethods.SELFTEST_SHORT
            : NativeMethods.SELFTEST_EXTENDED;

        foreach (var channel in Channels(disk))
        {
            bool ok = channel switch
            {
                AtaChannel.PassThrough => AtaPassThrough.StartSelfTest(handle, subcommand),
                AtaChannel.Legacy => StartAtaLegacy(handle, disk, subcommand),
                AtaChannel.Sat => SatPassThrough.StartSelfTest(handle, subcommand),
                _ => false,
            };
            if (ok) return true;
        }
        return false;
    }

    /// <summary>SMART EXECUTE OFF-LINE IMMEDIATE sul canale storico.</summary>
    private static bool StartAtaLegacy(SafeFileHandle handle, DiskInfo disk, byte subcommand)
    {
        // SENDCMDINPARAMS: cBufferSize(4) IDEREGS(8) bDriveNumber(1) ...
        var inBuf = new byte[36];
        BitConverter.GetBytes(0).CopyTo(inBuf, 0);                   // nessun dato da trasferire
        inBuf[4] = NativeMethods.SMART_EXECUTE_OFFLINE;              // features
        inBuf[5] = 1;                                                // sector count
        inBuf[6] = subcommand;                                       // LBA low: breve o esteso
        inBuf[7] = NativeMethods.SMART_CYL_LOW;
        inBuf[8] = NativeMethods.SMART_CYL_HI;
        inBuf[9] = 0xA0;
        inBuf[10] = NativeMethods.SMART_CMD;
        inBuf[12] = (byte)disk.Index;

        var outBuf = new byte[16];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.SMART_SEND_DRIVE_COMMAND,
                inBuf, inBuf.Length, outBuf, outBuf.Length, out int returned, IntPtr.Zero))
            return false;

        // DRIVERSTATUS.bDriverError è il byte 4 di SENDCMDOUTPARAMS.
        return returned < 5 || outBuf[4] == 0;
    }

    /// <summary>Il canale già riconosciuto per primo, poi gli altri come riserva.</summary>
    private static IEnumerable<AtaChannel> Channels(DiskInfo disk)
    {
        AtaChannel[] all = [AtaChannel.PassThrough, AtaChannel.Legacy, AtaChannel.Sat];
        var known = disk.Channel != AtaChannel.None ? disk.Channel : DiskScanner.ChannelFor(disk);
        return known == AtaChannel.None ? all : all.OrderBy(c => c == known ? 0 : 1);
    }

    // ------------------------------------------------------------- stato

    public static SelfTestStatus? Query(DiskInfo disk)
    {
        using var handle = Open(disk.Index);
        if (handle is null || handle.IsInvalid) return null;

        return disk.BusType == StorageBusType.Nvme
            ? QueryNvme(handle)
            : QueryAta(handle, disk);
    }

    private static SelfTestStatus? QueryNvme(SafeFileHandle handle)
    {
        // Log page 06h: Device Self-test Log
        var log = StorageQuery.GetNvmeLogPage(handle, 0x06, 564);
        if (log is null || log.Length < 32) return null;

        int operation = log[0] & 0x0F;
        int percent = log[1] & 0x7F;

        // Primo risultato archiviato: byte 4 = esito (bits 3:0) e tipo (bits 7:4)
        byte entry = log[4];
        int result = entry & 0x0F;
        bool known = result != 0x0F;

        return new SelfTestStatus
        {
            Running = operation != 0,
            PercentComplete = operation != 0 ? percent : 100,
            Description = operation switch
            {
                1 => "Autotest breve in corso",
                2 => "Autotest esteso in corso",
                0 => "Nessun autotest in corso",
                _ => "Autotest in corso",
            },
            LastResultKnown = known,
            LastResultPassed = result == 0,
            LastResultDescription = DescribeNvmeResult(result),
        };
    }

    private static string DescribeNvmeResult(int code) => code switch
    {
        0x0 => "Completato senza errori",
        0x1 => "Interrotto dall'utente",
        0x2 => "Interrotto da un reset del controller",
        0x3 => "Interrotto da una cancellazione dello spazio nomi",
        0x4 => "Interrotto da una formattazione",
        0x5 => "Errore fatale durante il test",
        0x6 => "Completato: segmento sconosciuto non riuscito",
        0x7 => "Completato: un segmento non è riuscito",
        0x8 => "Completato: più segmenti non riusciti",
        0x9 => "Interrotto da una richiesta esterna",
        0xA => "Interrotto per temperatura fuori range",
        0xF => "Nessun risultato registrato",
        _ => $"Esito sconosciuto ({code})",
    };

    private static SelfTestStatus? QueryAta(SafeFileHandle handle, DiskInfo disk)
    {
        byte[]? data = null;
        foreach (var channel in Channels(disk))
        {
            data = channel switch
            {
                AtaChannel.PassThrough => AtaPassThrough.SmartAttributes(handle),
                AtaChannel.Legacy => AtaSmart.ReadAttributesRaw(handle, (byte)disk.Index),
                AtaChannel.Sat => SatPassThrough.SmartAttributes(handle),
                _ => null,
            };
            if (AtaData.LooksLikeSmart(data)) break;
            data = null;
        }

        // Se nessun canale risponde adesso, vale l'ultimo stato letto dalla scansione.
        byte? status = data is not null ? data[363] : disk.SelfTestStatusByte;
        if (status is not byte s) return null;

        // Nibble alto = esito, nibble basso = decine di percento ancora da fare.
        int high = (s >> 4) & 0x0F;
        int remaining = (s & 0x0F) * 10;
        bool running = high == 0x0F;

        return new SelfTestStatus
        {
            Running = running,
            PercentComplete = running ? 100 - remaining : 100,
            Description = running ? "Autotest in corso" : "Nessun autotest in corso",
            LastResultKnown = !running,
            LastResultPassed = high == 0,
            LastResultDescription = DescribeAtaResult(high),
        };
    }

    private static string DescribeAtaResult(int code) => code switch
    {
        0x0 => "Completato senza errori",
        0x1 => "Interrotto dall'utente",
        0x2 => "Interrotto da un reset",
        0x3 => "Errore fatale o sconosciuto",
        0x4 => "Non riuscito: guasto sconosciuto",
        0x5 => "Non riuscito: parte elettrica",
        0x6 => "Non riuscito: servo o posizionamento",
        0x7 => "Non riuscito: lettura dei dati",
        0x8 => "Non riuscito: danno gestione",
        0xF => "Autotest in corso",
        _ => $"Esito sconosciuto ({code})",
    };

    // ----------------------------------------------------------------- utility

    /// <summary>Handle in lettura e scrittura: serve sia per avviare che per interrogare.</summary>
    private static SafeFileHandle? Open(int index) =>
        NativeMethods.CreateFileW($@"\\.\PhysicalDrive{index}",
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
}
