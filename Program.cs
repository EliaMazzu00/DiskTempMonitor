using System.Diagnostics;
using DiskTempMonitor.Services;
using DiskTempMonitor.UI;

namespace DiskTempMonitor;

internal static class Program
{
    private const string MutexName = @"Local\DiskTempMonitor.SingleInstance";
    private const string ShowEventName = @"Local\DiskTempMonitor.ShowWindow";

    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        // Una sola istanza: altrimenti avremmo icone duplicate in area di notifica.
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Invece di un avviso, si chiede all'istanza già attiva di mostrarsi: è
            // quello che ci si aspetta riaprendo il programma. Si usa un evento con
            // nome e non un messaggio di finestra, perché il broadcast non raggiunge
            // le finestre nascoste in area di notifica.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                using (existing) existing.Set();
            }
            return;
        }

        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var settings = AppSettings.Load();

        // --tray forza la partenza in area di notifica, --show forza la finestra aperta
        // anche quando le impostazioni dicono di partire ridotto.
        bool startHidden = settings.StartMinimized || args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--show", StringComparer.OrdinalIgnoreCase)) startHidden = false;

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        using var form = new MainForm(settings, startHidden, showRequested);
        Application.Run(form);

        GC.KeepAlive(_singleInstance);
    }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string path = Path.Combine(
                Path.GetDirectoryName(AppSettings.SettingsPath)!, "errori.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");

            var result = MessageBox.Show(
                $"Si è verificato un errore imprevisto:\n\n{ex.Message}\n\n" +
                $"Dettagli salvati in:\n{path}\n\nVuoi continuare?",
                "Disk Temp Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Error);

            if (result == DialogResult.No) Application.Exit();
        }
        catch
        {
            Debug.WriteLine(ex);
        }
    }
}
