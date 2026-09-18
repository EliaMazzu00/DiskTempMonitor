using System.Text;
using DiskTempMonitor.Models;

namespace DiskTempMonitor.Services;

/// <summary>Rapporto testuale con tutti i dati raccolti, in stile CrystalDiskInfo.</summary>
internal static class Report
{
    /// <summary>
    /// Riepilogo breve: solo i dati del disco, senza informazioni sul computer che lo
    /// ospita e senza la tabella degli attributi. Pensato per essere incollato altrove.
    /// </summary>
    public static string BuildSummary(IReadOnlyList<DiskInfo> disks, AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(disks.Count == 1
            ? "Disk Temp Monitor — riepilogo disco"
            : "Disk Temp Monitor — riepilogo dischi");
        sb.AppendLine(DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        sb.AppendLine();

        foreach (var d in disks)
        {
            sb.AppendLine(d.Model.Length > 0 ? d.Model : $"Disco {d.Index}");

            Short(sb, "Numero di serie", d.SerialNumber);
            Short(sb, "Firmware", d.Firmware);
            Short(sb, "Interfaccia", $"{d.InterfaceDisplay} · {d.KindDisplay}");

            string capacity = d.SizeDisplay;
            if (d.DriveLetters.Count > 0) capacity += $" ({d.LettersDisplay})";
            if (d.UsedFraction is double uf) capacity += $" · {uf:P0} occupato";
            Short(sb, "Capacità", capacity);

            string temp = settings.FormatTemp(d.TemperatureC);
            if (d.SessionMinC is not null || d.SessionMaxC is not null)
                temp += $"   (sessione: min {settings.FormatTemp(d.SessionMinC)}, max {settings.FormatTemp(d.SessionMaxC)})";
            Short(sb, "Temperatura", temp);

            string health = HealthLabel(d.Health);
            if (d.HealthDetail.Length > 0) health += $" — {d.HealthDetail}";
            Short(sb, "Stato di salute", health);

            if (d.LifePercent is int lp) Short(sb, "Vita residua", $"{lp}%");
            if (d.PowerOnHours is ulong h) Short(sb, "Ore di accensione", $"{h:N0} h");
            if (d.PowerOnCount is ulong pc) Short(sb, "Accensioni", $"{pc:N0}");
            if (d.HostReadsBytes is ulong r) Short(sb, "Totale letto", DiskInfo.FormatBytes(r));
            if (d.HostWritesBytes is ulong w) Short(sb, "Totale scritto", DiskInfo.FormatBytes(w));

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void Short(StringBuilder sb, string key, string value) =>
        sb.AppendLine($"  {key,-18}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");

    /// <summary>Rapporto completo: contesto del computer e tutti gli attributi.</summary>
    public static string Build(IReadOnlyList<DiskInfo> disks, AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Disk Temp Monitor — rapporto dischi ({BuildInfo.Full})");
        sb.AppendLine($"Generato il {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine($"Computer: {Environment.MachineName}   Sistema: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"Privilegi: {(DiskScanner.IsAdministrator ? "amministratore" : "utente standard")}");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine();

        foreach (var d in disks)
        {
            sb.AppendLine($"--- Disco {d.Index} ------------------------------------------------------");
            Line(sb, "Modello", d.Model);
            Line(sb, "Numero di serie", d.SerialNumber);
            Line(sb, "Firmware", d.Firmware);
            Line(sb, "Interfaccia", d.InterfaceDisplay);
            Line(sb, "Tipo", d.KindDisplay);
            Line(sb, "Capacità", d.SizeDisplay);
            Line(sb, "Lettere di unità", d.LettersDisplay);
            Line(sb, "Temperatura", settings.FormatTemp(d.TemperatureC));
            if (d.TemperatureMaxC is int tm) Line(sb, "Massimo firmware", settings.FormatTemp(tm));
            foreach (var (label, value) in d.ExtraSensors)
                Line(sb, label, settings.FormatTemp(value));
            Line(sb, "Stato di salute", $"{HealthLabel(d.Health)} {d.HealthDetail}".Trim());
            if (d.LifePercent is int lp) Line(sb, "Vita residua", $"{lp}%");
            if (d.PowerOnHours is ulong h) Line(sb, "Ore di accensione", $"{h:N0} h");
            if (d.PowerOnCount is ulong pc) Line(sb, "Accensioni", $"{pc:N0}");
            if (d.HostReadsBytes is ulong r) Line(sb, "Totale letto", DiskInfo.FormatBytes(r));
            if (d.HostWritesBytes is ulong w) Line(sb, "Totale scritto", DiskInfo.FormatBytes(w));
            Line(sb, "Origine dati", d.DataSource);
            if (d.LastError is not null) Line(sb, "Note", d.LastError);
            sb.AppendLine();

            if (d.Attributes.Count > 0)
            {
                sb.AppendLine("  ID  Cur Wor Thr  Raw            Attributo");
                foreach (var a in d.Attributes)
                {
                    sb.AppendLine($"  {a.IdHex}  {Pad(a.Current)} {Pad(a.Worst)} {Pad(a.Threshold)}  " +
                                  $"{a.RawHex,-14} {a.Name}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Pad(byte v) => v == 0 ? "  -" : v.ToString().PadLeft(3);

    private static void Line(StringBuilder sb, string key, string value) =>
        sb.AppendLine($"  {key,-22}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");

    private static string HealthLabel(HealthState s) => s switch
    {
        HealthState.Good => "Buono",
        HealthState.Caution => "Attenzione",
        HealthState.Bad => "Critico",
        _ => "Sconosciuto",
    };
}
