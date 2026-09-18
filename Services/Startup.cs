using System.Diagnostics;
using Microsoft.Win32;

namespace DiskTempMonitor.Services;

/// <summary>
/// Avvio automatico con Windows. Se l'app gira come amministratore usa l'Utilità di
/// pianificazione con privilegi elevati (nessun prompt UAC al login), altrimenti
/// ripiega sulla chiave Run del registro.
/// </summary>
internal static class Startup
{
    private const string TaskName = "DiskTempMonitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DiskTempMonitor";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DiskTempMonitor.exe");

    public static bool IsEnabled() => TaskExists() || RegistryEnabled();

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveTask();
                RemoveRegistry();
                return true;
            }

            // Solo l'attività pianificata funziona: l'applicazione richiede l'elevazione
            // e Windows non avvia al login i programmi elevati elencati nella chiave Run.
            if (CreateTask()) { RemoveRegistry(); return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Porta un avvio automatico configurato in passato dalla chiave Run all'Utilità di
    /// pianificazione. Serve perché, ora che l'applicazione richiede l'elevazione, la
    /// voce nel registro verrebbe semplicemente ignorata da Windows al login.
    /// </summary>
    public static bool MigrateRegistryToTask()
    {
        try
        {
            if (!RegistryEnabled() || TaskExists()) return false;
            if (!CreateTask()) return false;

            RemoveRegistry();
            return true;
        }
        catch { return false; }
    }

    // --------------------------------------------------------- Utilità di pianificazione

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static bool CreateTask() =>
        RunSchtasks($"/Create /F /SC ONLOGON /RL HIGHEST /TN \"{TaskName}\" /TR \"\\\"{ExecutablePath}\\\" --tray\"") == 0;

    private static void RemoveTask()
    {
        if (TaskExists()) RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ registro

    private static bool RegistryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static bool SetRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key?.SetValue(RunValue, $"\"{ExecutablePath}\" --tray");
            return true;
        }
        catch { return false; }
    }

    private static void RemoveRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is not null) key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { /* nulla da rimuovere */ }
    }

    // ------------------------------------------------------------- riavvio elevato

    public static bool RestartElevated(bool minimized)
    {
        try
        {
            var psi = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = minimized ? "--tray" : "",
            };
            Process.Start(psi);
            return true;
        }
        catch { return false; }     // l'utente ha annullato il prompt UAC
    }
}
