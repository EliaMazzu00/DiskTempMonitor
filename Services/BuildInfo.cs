using System.Reflection;

namespace DiskTempMonitor.Services;

/// <summary>
/// Versione e momento di compilazione di questa copia del programma.
/// <para>
/// Serve a sapere a colpo d'occhio se quello che si sta guardando è la build appena
/// prodotta o una copia più vecchia rimasta in giro: il progetto viene eseguito ora da
/// <c>bin\Release\</c>, ora da <c>publish\</c>, e le due possono divergere.
/// </para>
/// <para>
/// Il momento di compilazione viene inciso nel file dal progetto (vedi la destinazione
/// <c>MarcaOraDiCompilazione</c> nel <c>.csproj</c>). Se per qualche motivo non ci fosse,
/// si ripiega sulla data dell'eseguibile, che è comunque quella giusta.
/// </para>
/// </summary>
public static class BuildInfo
{
    static BuildInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();

        Version = assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
        BuildTime = ReadStampedTime(assembly) ?? ReadFileTime();
    }

    public static string Version { get; }
    public static DateTime? BuildTime { get; }

    /// <summary>
    /// Come compare nella barra di stato: solo <c>v1.1.0</c>. L'ora di compilazione resta
    /// disponibile nel suggerimento del mouse e nei rapporti esportati, dove non ingombra.
    /// </summary>
    public static string Display => $"v{Version}";

    /// <summary>Versione estesa, per i rapporti esportati.</summary>
    public static string Full => BuildTime is DateTime t
        ? $"versione {Version}, compilata il {t:dd/MM/yyyy alle HH:mm}"
        : $"versione {Version}";

    /// <summary>
    /// L'ora incisa nella versione informativa, nella forma <c>1.1.0+2026-09-14T19:04</c>.
    /// </summary>
    private static DateTime? ReadStampedTime(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        int plus = informational?.IndexOf('+') ?? -1;
        if (informational is null || plus < 0) return null;

        return DateTime.TryParse(informational[(plus + 1)..], out var stamped) ? stamped : null;
    }

    private static DateTime? ReadFileTime()
    {
        try
        {
            string? path = Environment.ProcessPath;
            return path is not null && File.Exists(path) ? File.GetLastWriteTime(path) : null;
        }
        catch { return null; }
    }
}
