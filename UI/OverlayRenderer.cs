using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>Come si dispongono i valori nella targhetta.</summary>
public enum OverlayLayout
{
    /// <summary>Uno per riga: colonna stretta e alta, da tenere lungo un bordo.</summary>
    Verticale,
    /// <summary>Tre per riga: striscia bassa e larga, da tenere sopra o sotto.</summary>
    Orizzontale,
}

/// <summary>In quale angolo dello schermo sta la sovrimpressione.</summary>
public enum OverlayCorner
{
    AltoSinistra,
    AltoDestra,
    BassoSinistra,
    BassoDestra,
}

/// <summary>
/// Disegna la targhetta della sovrimpressione. Il disegno sta qui, separato dalla
/// finestra che la mostra, perche' lo usano sia la sovrimpressione vera sia l'anteprima
/// della finestra di posizionamento.
/// </summary>
internal static class OverlayRenderer
{
    private readonly record struct Riga(string Sigla, string Valore, string Unita, Color Colore);

    /// <summary>Le righe da mostrare, nell'ordine: prima gli FPS, che sono il motivo per cui la si apre.</summary>
    private static List<Riga> BuildRows(AppSettings _settings, IReadOnlyList<DiskInfo> _disks,
                                        SystemMetrics? _metrics, float? _fps)
    {
        var righe = new List<Riga>();
        var cpu = _metrics?.Cpu;
        var gpu = _metrics?.PrimaryGpu;

        if (_settings.OverlayShowFps)
            righe.Add(new Riga("FPS",
                _fps is float f ? f.ToString("0") : "—", "",
                _fps is float v ? ColorePerFps(_settings, v) : Theme.TextSecondary));

        if (_settings.OverlayShowCpu)
        {
            int? t = _metrics?.CpuTemperature(_settings.OverlayCpuMode);
            righe.Add(new Riga("CPU", Temperatura(_settings, t), _settings.UnitSuffix,
                               IconRenderer.ColorForCpu(t, _settings)));
        }

        if (_settings.OverlayShowGpu)
        {
            int? t = _metrics?.GpuTemperature;
            righe.Add(new Riga("GPU", Temperatura(_settings, t), _settings.UnitSuffix,
                               IconRenderer.ColorForGpu(t, _settings)));
        }

        if (_settings.OverlayShowDisks)
        {
            foreach (var d in _disks.Where(d => d.TemperatureC is not null))
                righe.Add(new Riga(d.PrimaryLetter ?? $"D{d.Index}",
                                   Temperatura(_settings, d.TemperatureC), _settings.UnitSuffix,
                                   IconRenderer.ColorForTemp(d.TemperatureC, _settings)));
        }

        if (_settings.OverlayShowCpuLoad) righe.Add(Carico(_settings, "CPU", cpu?.AverageLoadPercent));
        if (_settings.OverlayShowGpuLoad) righe.Add(Carico(_settings, "GPU", gpu?.CoreLoadPercent));
        if (_settings.OverlayShowGpuVram) righe.Add(Carico(_settings, "VRAM", gpu?.VramUsedPercent));
        if (_settings.OverlayShowMemory) righe.Add(Carico(_settings, "RAM", _metrics?.MemoryUsedPercent));

        return righe;
    }

    /// <summary>
    /// Riga di percentuale. La sigla porta gia' il segno di percento e il valore no:
    /// serve a distinguerla dalla riga di temperatura della stessa cosa — "CPU 71 °C"
    /// contro "CPU % 46" — senza scrivere due volte lo stesso segno.
    /// </summary>
    private static Riga Carico(AppSettings _settings, string sigla, float? percent) => new(
        sigla + " %",
        percent is float p ? Math.Clamp(p, 0, 100).ToString("0") : "—",
        "",
        IconRenderer.ColorForLoad(percent, _settings));

    private static string Temperatura(AppSettings _settings, int? celsius) =>
        celsius is int c ? Math.Round(_settings.ToDisplay(c)).ToString("0") : "—";

    /// <summary>
    /// Sotto i trenta fotogrammi il gioco scatta, sotto i cinquanta non è fluido: le
    /// soglie delle temperature qui non c'entrano, e la scala va al contrario.
    /// </summary>
    private static Color ColorePerFps(AppSettings _settings, float fps) =>
        fps < 30 ? IconRenderer.ParseColor(_settings.ColorCritical, Color.OrangeRed)
        : fps < 50 ? IconRenderer.ParseColor(_settings.ColorWarn, Color.Orange)
        : IconRenderer.ParseColor(_settings.ColorNormal, Color.LimeGreen);

    // ------------------------------------------------------------- disegno

    /// <summary>
    /// Disegna la targhetta e restituisce l'immagine, con la trasparenza gia' dentro.
    /// Restituisce null quando non c'e' niente da mostrare.
    /// <para>
    /// Sta qui e non nella finestra perche' la usano in due: la sovrimpressione vera,
    /// che la riversa sullo schermo, e l'anteprima nella finestra di posizionamento.
    /// Se fossero due disegni diversi, l'anteprima prometterebbe qualcosa di diverso da
    /// quello che si vede poi in partita.
    /// </para>
    /// </summary>
    public static Bitmap? Draw(AppSettings _settings, IReadOnlyList<DiskInfo> _disks,
                               SystemMetrics? _metrics, float? _fps)
    {
        var righe = BuildRows(_settings, _disks, _metrics, _fps);
        if (righe.Count == 0) return null;

        float scala = Math.Clamp(_settings.OverlayScalePercent, 60, 250) / 100f;

        using var fontSigla = new Font("Segoe UI", 9f * scala, FontStyle.Bold, GraphicsUnit.Point);
        using var fontValore = new Font("Segoe UI", 12f * scala, FontStyle.Bold, GraphicsUnit.Point);
        using var fontUnita = new Font("Segoe UI", 7.5f * scala, FontStyle.Bold, GraphicsUnit.Point);

        int padding = (int)(10 * scala);
        int rigaH = (int)(22 * scala);

        // La colonna delle sigle è larga quanto la sigla più lunga: così i numeri
        // restano incolonnati e si leggono con la coda dell'occhio.
        int larghezzaSigla = 0, larghezzaValore = 0;
        using (var misura = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(misura))
        {
            foreach (var r in righe)
            {
                larghezzaSigla = Math.Max(larghezzaSigla, (int)Math.Ceiling(g.MeasureString(r.Sigla, fontSigla).Width));
                larghezzaValore = Math.Max(larghezzaValore, (int)Math.Ceiling(
                    g.MeasureString(r.Valore, fontValore).Width + g.MeasureString(r.Unita, fontUnita).Width));
            }
        }

        int gap = (int)(10 * scala);
        int gapColonne = (int)(18 * scala);

        // Una colonna sola, oppure tre affiancate. Le celle sono tutte larghe uguale:
        // è quello che tiene i numeri incolonnati anche quando le sigle sono di
        // lunghezza diversa.
        int perRiga = _settings.OverlayLayout == OverlayLayout.Orizzontale
            ? Math.Min(3, righe.Count)
            : 1;

        int righeGriglia = (righe.Count + perRiga - 1) / perRiga;
        int cella = larghezzaSigla + gap + larghezzaValore;

        int larghezza = padding * 2 + perRiga * cella + (perRiga - 1) * gapColonne;
        int altezza = padding * 2 + righeGriglia * rigaH;

        var bmp = new Bitmap(larghezza, altezza, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            int opacita = Math.Clamp(_settings.OverlayOpacityPercent, 10, 100);
            int velo = opacita * 255 / 100;

            // Il contorno è più scuro dello sfondo, non più chiaro. Prima era una riga
            // bianca a bassa opacità: su sfondo chiaro non si notava, ma appena il gioco
            // diventava scuro restava un filo bianco tutto intorno alla targhetta.
            using (var sfondo = new SolidBrush(Color.FromArgb(velo, 8, 10, 14)))
            using (var bordo = new Pen(Color.FromArgb(Math.Min(255, velo + 45), 0, 0, 0), 1f))
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, larghezza - 1, altezza - 1), 8 * scala))
            {
                g.FillPath(sfondo, path);
                g.DrawPath(bordo, path);
            }

            using var siglaBrush = new SolidBrush(Color.FromArgb(215, 196, 204, 218));
            using var alCentro = new StringFormat { LineAlignment = StringAlignment.Center };

            for (int i = 0; i < righe.Count; i++)
            {
                var r = righe[i];
                int colonna = i % perRiga;
                int riga = i / perRiga;

                int x0 = padding + colonna * (cella + gapColonne);
                int y = padding + riga * rigaH;

                g.DrawString(r.Sigla, fontSigla, siglaBrush,
                             new RectangleF(x0, y, larghezzaSigla, rigaH), alCentro);

                // Valore e unità si scrivono da destra verso sinistra, così i numeri
                // di una e due cifre restano allineati sulla stessa colonna.
                float destra = x0 + cella;

                if (r.Unita.Length > 0)
                {
                    float wUnita = g.MeasureString(r.Unita, fontUnita).Width;
                    destra -= wUnita;
                    using var b = new SolidBrush(Color.FromArgb(190, r.Colore));
                    g.DrawString(r.Unita, fontUnita, b,
                                 new RectangleF(destra, y + rigaH * 0.18f, wUnita, rigaH), alCentro);
                }

                float wValore = g.MeasureString(r.Valore, fontValore).Width;
                using var pennello = new SolidBrush(r.Colore);
                g.DrawString(r.Valore, fontValore, pennello,
                             new RectangleF(destra - wValore, y, wValore, rigaH), alCentro);
            }
        }

        return bmp;
    }


    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1f, radius) * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
