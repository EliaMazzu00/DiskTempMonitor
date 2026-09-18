using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskTempMonitor.Services;

/// <summary>
/// Misura gli FPS di qualunque applicazione stia disegnando, senza agganciarsi a lei.
/// <para>
/// È lo stesso principio di PresentMon: Windows annuncia su <b>ETW</b> ogni volta che
/// un programma consegna un fotogramma alla scheda video, e qui ci si limita ad
/// ascoltare e contare. Nessuna libreria da iniettare nel gioco, nessun aggancio alle
/// sue funzioni di disegno: dall'interno del gioco questo programma è invisibile, il
/// che è esattamente quello che si vuole quando di mezzo c'è un anticheat.
/// </para>
/// <para>
/// Degli eventi non si decodifica il contenuto: servono solo il numero dell'evento e
/// il processo che l'ha emesso, e stanno tutti e due nell'intestazione.
/// </para>
/// </summary>
public sealed class FrameRateMonitor : IDisposable
{
    /// <summary>
    /// Microsoft-Windows-DxgKrnl: il driver di visualizzazione di Windows.
    /// <para>
    /// Si ascolta questo e non i provider di Direct3D perché qui passa <b>tutto</b>:
    /// Direct3D 9, 11 e 12, ma anche OpenGL e Vulkan, che dai provider di Direct3D non
    /// si vedono affatto. Misurato su questa macchina: un gioco in OpenGL non emetteva
    /// un solo evento DXGI, mentre l'evento 184 del driver lo seguiva fotogramma per
    /// fotogramma.
    /// </para>
    /// </summary>
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    /// <summary>
    /// La parola chiave "Base". È l'unica che consegni l'evento delle presentazioni:
    /// provate una per una, le altre o non lo portano o non portano niente.
    /// </summary>
    private const ulong DxgKrnlBase = 0x1;

    /// <summary>L'evento che batte una volta per fotogramma consegnato.</summary>
    private const int PresentInfo = 184;

    /// <summary>Su quanto tempo si calcola il valore: mezzo secondo è stabile e reattivo.</summary>
    private static readonly long WindowTicks = Stopwatch.Frequency / 2;

    /// <summary>Dopo tre secondi senza fotogrammi il processo non sta più disegnando.</summary>
    private static readonly long StaleTicks = Stopwatch.Frequency * 3;

    private sealed class Counter
    {
        public long WindowStart;
        public int Frames;
        public float Fps;
        public long LastSeen;
    }

    private readonly Dictionary<uint, Counter> _counters = [];
    private readonly Lock _gate = new();
    private readonly string _sessionName = "DiskTempMonitorFps";

    private Etw.Callback? _callback;      // va tenuto vivo: lo richiama il sistema
    private ulong _session;
    private ulong _consumer;
    private Thread? _pump;
    private bool _disposed;

    /// <summary>Vero se la misura è partita davvero.</summary>
    public bool Available { get; private set; }

    /// <summary>Perché non è partita, da mostrare all'utente.</summary>
    public string? Error { get; private set; }

    private long _seen;

    /// <summary>Quanti fotogrammi sono stati contati in tutto: serve a capire se arriva qualcosa.</summary>
    public long FramesSeen => Interlocked.Read(ref _seen);

    /// <summary>
    /// Avvia l'ascolto. Serve l'esecuzione come amministratore, che questo programma ha
    /// già per leggere i sensori: senza, la sessione non si apre e la funzione resta
    /// spenta senza disturbare il resto.
    /// </summary>
    public void Start()
    {
        if (Available || _disposed) return;

        try
        {
            Etw.Stop(_sessionName);     // resti di un avvio precedente andato male

            _session = Etw.Start(_sessionName);
            Etw.Enable(_session, DxgKrnlProvider, DxgKrnlBase);

            _callback = OnEvent;
            _consumer = Etw.Open(_sessionName, _callback, PresentInfo);

            _pump = new Thread(() => Etw.Process(_consumer))
            {
                IsBackground = true,
                Name = "Conteggio fotogrammi",
                Priority = ThreadPriority.BelowNormal,
            };
            _pump.Start();

            Available = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Available = false;
            Stop();
        }
    }

    private void OnEvent(uint processId)
    {
        Interlocked.Increment(ref _seen);

        long now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (!_counters.TryGetValue(processId, out var c))
                _counters[processId] = c = new Counter { WindowStart = now };

            c.Frames++;
            c.LastSeen = now;

            long elapsed = now - c.WindowStart;
            if (elapsed >= WindowTicks)
            {
                c.Fps = (float)(c.Frames * (double)Stopwatch.Frequency / elapsed);
                c.Frames = 0;
                c.WindowStart = now;
            }
        }
    }

    /// <summary>
    /// Fotogrammi al secondo del processo indicato, se sta disegnando.
    /// <para>
    /// Finché la prima mezza finestra di misura non si è chiusa non ci sarebbe ancora
    /// un valore, e la sovrimpressione — che compare solo quando un numero c'è — si
    /// faceva attendere: il tempo di aprire la sessione di ascolto più mezzo secondo,
    /// abbastanza perché sembrasse che non stesse comparendo affatto. Se qualche
    /// fotogramma è già arrivato si dà perciò una stima su quello che si ha: meno
    /// precisa per un attimo, ma la targhetta c'è subito e si assesta da sola.
    /// </para>
    /// </summary>
    public float? FpsFor(int processId)
    {
        if (!Available) return null;

        long now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (!_counters.TryGetValue((uint)processId, out var c)) return null;
            if (now - c.LastSeen > StaleTicks) return null;
            if (c.Fps > 0) return c.Fps;

            long trascorso = now - c.WindowStart;
            return c.Frames >= 3 && trascorso > Stopwatch.Frequency / 20
                ? (float)(c.Frames * (double)Stopwatch.Frequency / trascorso)
                : null;
        }
    }

    /// <summary>
    /// Il programma in primo piano, se sta disegnando fotogrammi: è quello di cui ha
    /// senso mostrare gli FPS. Restituisce null quando in primo piano non c'è niente
    /// che disegni — il desktop, un editor di testo, questa stessa finestra.
    /// </summary>
    public (int Pid, string Name, float Fps)? ForegroundApp()
    {
        if (!Available) return null;

        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        _ = Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;

        if (FpsFor((int)pid) is not float fps || fps < 1f) return null;

        string nome;
        try { nome = Process.GetProcessById((int)pid).ProcessName; }
        catch { return null; }

        // La shell e il compositore disegnano di continuo ma non sono giochi.
        if (Esclusi.Contains(nome)) return null;

        return ((int)pid, nome, fps);
    }

    private static readonly HashSet<string> Esclusi =
        new(StringComparer.OrdinalIgnoreCase) { "explorer", "dwm", "ShellExperienceHost",
                                                "SearchHost", "StartMenuExperienceHost",
                                                "TextInputHost", "ApplicationFrameHost" };

    /// <summary>Toglie dalla memoria i processi che non disegnano più.</summary>
    public void Prune()
    {
        if (!Available) return;

        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            foreach (var pid in _counters.Where(e => now - e.Value.LastSeen > StaleTicks * 4)
                                         .Select(e => e.Key).ToList())
                _counters.Remove(pid);
        }
    }

    private void Stop()
    {
        try { if (_session != 0) Etw.Stop(_sessionName); } catch { /* si stava già chiudendo */ }
        try { if (_consumer != 0) Etw.Close(_consumer); } catch { /* idem */ }

        _pump?.Join(2000);
        _session = _consumer = 0;
        _pump = null;
        Available = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}

/// <summary>
/// Il minimo indispensabile per aprire una sessione ETW in tempo reale e riceverne gli
/// eventi. Le librerie che fanno questo mestiere portano decine di megabyte perché sanno
/// anche decodificare il contenuto degli eventi; qui il contenuto non serve, quindi
/// bastano cinque chiamate di sistema.
/// </summary>
internal static class Etw
{
    private const int WnodeFlagTracedGuid = 0x00020000;
    private const int RealTimeMode = 0x00000100;
    private const int ProcessModeRealTime = 0x00000100;
    private const int ProcessModeEventRecord = 0x10000000;
    private const int ControlCodeEnableProvider = 1;
    private const int ControlStop = 1;
    private const byte LevelInformational = 4;

    public delegate void Callback(uint processId);

    // Il sistema richiama una funzione non gestita: delegato e puntatore vanno tenuti
    // in vita finché la sessione è aperta, altrimenti il garbage collector li sposta.
    private static Callback? _managed;
    private static EventRecordCallback? _thunk;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public short Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint code);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong handle, in Guid provider, uint controlCode,
        byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);

    /// <summary>
    /// Il blocco che descrive la sessione: la struttura, e subito dopo il nome, a cui la
    /// struttura rimanda con uno scostamento invece che con un puntatore.
    /// </summary>
    private static IntPtr AllocProperties(string name)
    {
        int strutturaSize = Marshal.SizeOf<EventTraceProperties>();
        int size = strutturaSize + (name.Length + 1) * 2 + 2;

        IntPtr blocco = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(blocco, i, 0);

        var props = new EventTraceProperties
        {
            Wnode = { BufferSize = (uint)size, ClientContext = 1, Flags = WnodeFlagTracedGuid },
            BufferSize = 64,            // KB per buffer
            MinimumBuffers = 4,
            MaximumBuffers = 16,
            LogFileMode = RealTimeMode,
            FlushTimer = 1,             // secondi: gli eventi devono arrivare subito
            LoggerNameOffset = (uint)strutturaSize,
        };

        Marshal.StructureToPtr(props, blocco, false);
        return blocco;
    }

    /// <summary>
    /// Il blocco delle proprietà non si libera qui: Windows ci scrive dentro le
    /// statistiche della sessione finché resta aperta, e liberarlo subito gli lascia
    /// in mano un puntatore a memoria non più nostra.
    /// </summary>
    private static IntPtr _properties;

    public static ulong Start(string name)
    {
        IntPtr props = AllocProperties(name);

        int esito = StartTraceW(out ulong handle, name, props);
        if (esito != 0)
        {
            Marshal.FreeHGlobal(props);
            throw new InvalidOperationException(esito == 5
                ? "servono i privilegi di amministratore"
                : $"apertura della sessione non riuscita (codice {esito})");
        }

        _properties = props;
        return handle;
    }

    public static void Enable(ulong handle, Guid provider, ulong keyword)
    {
        int esito = EnableTraceEx2(handle, provider, ControlCodeEnableProvider,
                                   LevelInformational, keyword, 0, 0, IntPtr.Zero);
        if (esito != 0) throw new InvalidOperationException($"provider non attivato (codice {esito})");
    }

    public static void Stop(string name)
    {
        IntPtr props = AllocProperties(name);
        try { ControlTraceW(0, name, props, ControlStop); }
        finally
        {
            Marshal.FreeHGlobal(props);
            if (_properties != IntPtr.Zero) { Marshal.FreeHGlobal(_properties); _properties = IntPtr.Zero; }
        }
    }

    public static ulong Open(string name, Callback callback, int eventId)
    {
        _managed = callback;
        _wanted = eventId;
        _thunk = Record;

        var logfile = new EventTraceLogfile
        {
            LoggerName = Marshal.StringToHGlobalUni(name),
            ProcessTraceMode = ProcessModeRealTime | ProcessModeEventRecord,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_thunk),
        };

        ulong h = OpenTraceW(ref logfile);
        if (h == ulong.MaxValue)
            throw new InvalidOperationException($"lettura della sessione non riuscita (codice {Marshal.GetLastWin32Error()})");
        return h;
    }

    /// <summary>Quale evento interessa: gli altri si scartano senza guardarli oltre.</summary>
    private static int _wanted;

    /// <summary>
    /// Nell'intestazione di ogni evento il numero dell'evento sta a 40 byte dall'inizio
    /// e il processo che l'ha emesso a 12.
    /// <para>
    /// Questa funzione viene chiamata per <b>ogni</b> evento del driver di
    /// visualizzazione — decine di migliaia al secondo mentre un gioco è in corso — e
    /// quindi fa il minimo indispensabile: legge due byte, e se non è l'evento giusto
    /// torna indietro subito. Solo per quello giusto legge anche il processo.
    /// </para>
    /// <para>
    /// Qualunque eccezione va fermata qui: la chiama il sistema, e lasciarla uscire
    /// chiuderebbe in silenzio il filo di esecuzione che riceve gli eventi.
    /// </para>
    /// </summary>
    private static void Record(IntPtr record)
    {
        try
        {
            if (Marshal.ReadInt16(record, 40) != _wanted) return;
            _managed?.Invoke((uint)Marshal.ReadInt32(record, 12));
        }
        catch { /* un evento perso non vale un blocco */ }
    }

    public static void Process(ulong handle) => ProcessTrace([handle], 1, IntPtr.Zero, IntPtr.Zero);

    public static void Close(ulong handle) => CloseTrace(handle);
}
