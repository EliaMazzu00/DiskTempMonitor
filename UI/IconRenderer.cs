using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Genera al volo le icone numeriche per l'area di notifica, nello stile di Core Temp:
/// sfondo trasparente (o pieno) e la temperatura scritta grande, colorata per soglia.
/// </summary>
internal static class IconRenderer
{
    public static int IconSizeForDpi(int dpi) => dpi switch
    {
        <= 96 => 16,
        <= 120 => 20,
        <= 144 => 24,
        <= 192 => 32,
        _ => 40,
    };

    public static Color ParseColor(string value, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return ColorTranslator.FromHtml(value.Trim());
        }
        catch { return fallback; }
    }

    public static Color ColorForTemp(int? celsius, AppSettings s) =>
        ColorForTemp(celsius, s, s.WarnTemp, s.CriticalTemp);

    /// <summary>Le soglie dei dischi non valgono per processore e scheda video.</summary>
    public static Color ColorForCpu(int? celsius, AppSettings s) =>
        ColorForTemp(celsius, s, s.CpuWarnTemp, s.CpuCriticalTemp);

    public static Color ColorForGpu(int? celsius, AppSettings s) =>
        ColorForTemp(celsius, s, s.GpuWarnTemp, s.GpuCriticalTemp);

    /// <summary>
    /// Colore per una percentuale di utilizzo. Le soglie delle temperature non c'entrano:
    /// qui conta quanto è impegnata la risorsa, e la scala è la stessa per processore,
    /// scheda video e memoria.
    /// </summary>
    public static Color ColorForLoad(float? percent, AppSettings s)
    {
        if (percent is not float p) return Color.FromArgb(150, 150, 150);
        if (p >= 90) return ParseColor(s.ColorCritical, Color.OrangeRed);
        if (p >= 70) return ParseColor(s.ColorWarn, Color.Orange);
        return ParseColor(s.ColorNormal, Color.LimeGreen);
    }

    public static Color ColorForTemp(int? celsius, AppSettings s, int warn, int critical)
    {
        if (celsius is not int c) return Color.FromArgb(150, 150, 150);
        if (c >= critical) return ParseColor(s.ColorCritical, Color.OrangeRed);
        if (c >= warn) return ParseColor(s.ColorWarn, Color.Orange);
        return ParseColor(s.ColorNormal, Color.LimeGreen);
    }

    /// <summary>
    /// Crea un'icona con il testo indicato. È una risorsa gestita: va liberata con
    /// <see cref="Icon.Dispose"/> quando viene sostituita.
    /// </summary>
    public static Icon Create(string text, Color foreground, AppSettings s, int size, string? subscript = null)
    {
        size = Math.Clamp(size, 16, 64);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Con l'antialiasing agganciato alla griglia le cifre restano separate anche
            // a otto pixel: senza, a quella misura "43" diventava un unico blocco.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (!s.IconTransparentBackground)
            {
                using var back = new SolidBrush(ParseColor(s.IconBackgroundColor, Color.Black));
                float radius = size * 0.22f;
                using var path = RoundedRect(new RectangleF(0, 0, size, size), radius);
                g.FillPath(back, path);
            }
            else
            {
                g.Clear(Color.Transparent);
            }

            bool hasSub = !string.IsNullOrEmpty(subscript);

            // Con la sigla il valore lascia in fondo una striscia: su un'icona da 16 px
            // sono sei pixel scarsi, e vanno sfruttati tutti. Fra le due bande resta
            // un margine: senza, le cifre e la sigla si toccavano e diventavano una
            // macchia sola.
            // Almeno un pixel di stacco fra numero e sigla: attaccate, a sedici pixel,
            // si leggevano come una macchia sola.
            //
            // La striscia della sigla non scende mai sotto i sette pixel: e' l'altezza
            // dell'alfabeto disegnato a mano, e sotto quella misura la sigla c'e' ma non
            // si distingue. Il numero cede il pixel che serve — su un'icona da 16 px
            // passa da nove pixel a otto, che restano ben leggibili, e la sigla in
            // cambio cresce del quaranta per cento.
            float stacco = Math.Max(1f, size * 0.04f);
            float sigla = Math.Max(GlyphH, size * 0.345f);

            var textArea = hasSub
                ? new RectangleF(0, 0, size, size - sigla - stacco)
                : new RectangleF(0, 0, size, size);

            DrawValue(g, text, textArea, foreground, s);

            if (hasSub)
                DrawLabel(g, subscript!, new RectangleF(0, size - sigla, size, sigla), foreground, s);
        }

        return IconFromBitmap(bmp);
    }

    /// <summary>
    /// Quanta parte del corpo del carattere occupano davvero le cifre maiuscole. Il resto
    /// dell'em sono spalle e discendenti, che una cifra non usa.
    /// </summary>
    private const float CapHeightRatio = 0.72f;

    /// <summary>
    /// Quanto si possono stringere le cifre prima che diventino illeggibili. Sotto
    /// questa soglia conviene rimpicciolire il corpo invece di schiacciare oltre.
    /// </summary>
    private const float MinSqueeze = 0.78f;

    /// <summary>
    /// Il numero, grande quanto lo spazio permette davvero.
    /// <para>
    /// Il punto delicato è come si misura "quanto è alto". <c>MeasureString</c> restituisce
    /// l'altezza della <b>riga di testo</b>, che comprende lo spazio per gli accenti sopra
    /// e per le code sotto: su cifre, che non hanno né gli uni né le altre, è circa il 40%
    /// in più di quanto serva. Pretendere che quella riga stesse dentro l'icona lasciava
    /// perciò quasi metà dell'altezza inutilizzata, ed è il motivo per cui i numeri
    /// risultavano minuti. Qui invece si ragiona sull'altezza delle cifre, e la posizione
    /// verticale si calcola dalla linea di base.
    /// </para>
    /// </summary>
    private static void DrawValue(Graphics g, string text, RectangleF area, Color color, AppSettings s)
    {
        var style = s.IconBold ? FontStyle.Bold : FontStyle.Regular;

        // Sotto i dieci pixel di banda — cioe' sull'icona da 16 px — anche le cifre
        // vanno disegnate a mano. Con un carattere vero a quella misura o si sgranano
        // (senza antialiasing "43" diventava un blocco unico) o sbiadiscono (con
        // l'antialiasing restano grigie e molli): l'alfabeto a pixel invece e' netto,
        // e un numero netto di sette pixel si legge meglio di uno sfocato di otto.
        // Se pero' l'utente ha chiesto un corpo diverso dal predefinito si torna al
        // carattere vero, che e' l'unico che quella richiesta la sa onorare.
        if (area.Height < 10f && s.IconFontSizeOffset == 0 && DrawPixelLabel(g, text, area, color)) return;

        // Si parte dal corpo che fa toccare alle cifre il bordo superiore e inferiore.
        float emSize = area.Height / CapHeightRatio + s.IconFontSizeOffset;

        for (int attempt = 0; attempt < 16 && emSize > 4f; attempt++)
        {
            using (var font = MakeFont(s.IconFontFamily, emSize, style))
            {
                float width = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

                if (width <= area.Width)
                {
                    DrawOnBaseline(g, text, font, color, area, style, 1f);
                    return;
                }

                // Troppo largo: prima di rinunciare all'altezza si stringono le cifre.
                // A sedici pixel un numero di tre cifre non ci sta in larghezza, e
                // rimpicciolirlo lo riduceva a sette pixel su dieci disponibili;
                // stretto invece li occupa tutti, e si legge molto meglio.
                float squeeze = area.Width / width;
                if (squeeze >= MinSqueeze)
                {
                    DrawOnBaseline(g, text, font, color, area, style, squeeze);
                    return;
                }
            }

            emSize -= Math.Max(0.5f, emSize * 0.06f);
        }
    }

    /// <summary>
    /// Disegna centrando le <b>cifre</b> nello spazio dato, non la riga di testo che le
    /// contiene: si calcola dove cade la linea di base e da lì si risale all'origine che
    /// <see cref="Graphics.DrawString(string, Font, Brush, PointF, StringFormat)"/> si aspetta.
    /// </summary>
    private static void DrawOnBaseline(Graphics g, string text, Font font, Color color,
                                       RectangleF area, FontStyle style, float squeeze)
    {
        var family = font.FontFamily;
        float ascent = family.GetCellAscent(style) / (float)family.GetEmHeight(style) * font.Size;
        float capHeight = CapHeightRatio * font.Size;

        float top = area.Y + (area.Height - capHeight) / 2f;
        float origin = top + capHeight - ascent;      // dove comincia la riga di testo
        float centre = area.X + area.Width / 2f;

        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        if (squeeze >= 0.999f)
        {
            g.DrawString(text, font, brush, new PointF(centre, origin), fmt);
            return;
        }

        // La compressione agisce solo in orizzontale, e attorno alla mezzeria: si
        // sposta l'origine sul centro, si scala, e si disegna sull'asse.
        var stato = g.Save();
        g.TranslateTransform(centre, 0);
        g.ScaleTransform(squeeze, 1f);
        g.DrawString(text, font, brush, new PointF(0, origin), fmt);
        g.Restore(stato);
    }

    /// <summary>
    /// La sigla sotto il valore: sempre in grassetto e sempre grande quanto la striscia
    /// che la ospita, perché a queste dimensioni ogni frazione di pixel conta.
    /// <para>
    /// Qui non si applica la dimensione scelta per il valore, e il limite inferiore è più
    /// basso: usando gli stessi criteri del numero, su un'icona da 16 px il corpo
    /// risultava di meno di tre pixel, la ricerca si fermava subito e la sigla non veniva
    /// disegnata affatto. Era il motivo per cui la lettera del disco non si vedeva.
    /// </para>
    /// <para>
    /// L'unico vincolo è la larghezza: in altezza un filo di debordo non si nota, mentre
    /// rimpicciolire ancora renderebbe la sigla illeggibile.
    /// </para>
    /// </summary>
    private static void DrawLabel(Graphics g, string text, RectangleF area, Color color, AppSettings s)
    {
        // Sotto i dieci pixel di striscia nessun carattere vero regge: la "C"
        // diventava una macchia storta. Lì si disegna a mano, pixel per pixel.
        if (area.Height < 10f && DrawPixelLabel(g, text, area, color)) return;

        float emSize = Math.Max(4f, area.Height / CapHeightRatio);

        for (int attempt = 0; attempt < 12 && emSize >= 3f; attempt++)
        {
            using (var font = MakeFont(s.IconFontFamily, emSize, FontStyle.Bold))
            {
                float width = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

                if (width <= area.Width)
                {
                    DrawOnBaseline(g, text, font, color, area, FontStyle.Bold, 1f);
                    return;
                }

                float squeeze = area.Width / width;
                if (squeeze >= MinSqueeze)
                {
                    DrawOnBaseline(g, text, font, color, area, FontStyle.Bold, squeeze);
                    return;
                }
            }

            emSize -= Math.Max(0.5f, emSize * 0.1f);
        }
    }

    // ------------------------------------------------- sigla disegnata a pixel

    /// <summary>
    /// Alfabeto 5x7 per la sigla delle icone piccole. A questa misura un carattere vero
    /// viene sfocato dall'antialiasing e non si riconosce piu'; queste forme invece
    /// cadono esattamente sui pixel e restano nitide.
    /// <para>
    /// Sette pixel e non cinque: con cinque la sigla ci stava comodamente, ma sulla
    /// barra vera — dove non c'e' nessun ingrandimento — era troppo minuta per
    /// distinguere un'icona dall'altra, che e' tutto il suo scopo.
    /// </para>
    /// </summary>
    private static readonly Dictionary<char, string> PixelGlyphs = new()
    {
        ['0'] = ".###." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
        ['1'] = "..#.." + ".##.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
        ['2'] = ".###." + "#...#" + "....#" + "...#." + "..#.." + ".#..." + "#####",
        ['3'] = ".###." + "#...#" + "....#" + "..##." + "....#" + "#...#" + ".###.",
        ['4'] = "...#." + "..##." + ".#.#." + "#..#." + "#####" + "...#." + "...#.",
        ['5'] = "#####" + "#...." + "####." + "....#" + "....#" + "#...#" + ".###.",
        ['6'] = "..##." + ".#..." + "#...." + "####." + "#...#" + "#...#" + ".###.",
        ['7'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + ".#..." + ".#...",
        ['8'] = ".###." + "#...#" + "#...#" + ".###." + "#...#" + "#...#" + ".###.",
        ['9'] = ".###." + "#...#" + "#...#" + ".####" + "....#" + "...#." + ".##..",
        ['A'] = ".###." + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
        ['B'] = "####." + "#...#" + "#...#" + "####." + "#...#" + "#...#" + "####.",
        ['C'] = ".###." + "#...#" + "#...." + "#...." + "#...." + "#...#" + ".###.",
        ['D'] = "####." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "####.",
        ['E'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#####",
        ['F'] = "#####" + "#...." + "#...." + "####." + "#...." + "#...." + "#....",
        ['G'] = ".###." + "#...#" + "#...." + "#.###" + "#...#" + "#...#" + ".###.",
        ['H'] = "#...#" + "#...#" + "#...#" + "#####" + "#...#" + "#...#" + "#...#",
        ['I'] = ".###." + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + ".###.",
        ['J'] = "..###" + "...#." + "...#." + "...#." + "...#." + "#..#." + ".##..",
        ['K'] = "#...#" + "#..#." + "#.#.." + "##..." + "#.#.." + "#..#." + "#...#",
        ['L'] = "#...." + "#...." + "#...." + "#...." + "#...." + "#...." + "#####",
        ['M'] = "#...#" + "##.##" + "#.#.#" + "#.#.#" + "#...#" + "#...#" + "#...#",
        ['N'] = "#...#" + "##..#" + "##..#" + "#.#.#" + "#..##" + "#..##" + "#...#",
        ['O'] = ".###." + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
        ['P'] = "####." + "#...#" + "#...#" + "####." + "#...." + "#...." + "#....",
        ['Q'] = ".###." + "#...#" + "#...#" + "#...#" + "#.#.#" + "#..#." + ".##.#",
        ['R'] = "####." + "#...#" + "#...#" + "####." + "#.#.." + "#..#." + "#...#",
        ['S'] = ".###." + "#...#" + "#...." + ".###." + "....#" + "#...#" + ".###.",
        ['T'] = "#####" + "..#.." + "..#.." + "..#.." + "..#.." + "..#.." + "..#..",
        ['U'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".###.",
        ['V'] = "#...#" + "#...#" + "#...#" + "#...#" + "#...#" + ".#.#." + "..#..",
        ['W'] = "#...#" + "#...#" + "#...#" + "#.#.#" + "#.#.#" + "##.##" + "#...#",
        ['X'] = "#...#" + "#...#" + ".#.#." + "..#.." + ".#.#." + "#...#" + "#...#",
        ['Y'] = "#...#" + "#...#" + ".#.#." + "..#.." + "..#.." + "..#.." + "..#..",
        ['Z'] = "#####" + "....#" + "...#." + "..#.." + ".#..." + "#...." + "#####",
        ['%'] = "##..#" + "##..#" + "...#." + "..#.." + ".#..." + "#..##" + "#..##",

        // Segni stretti: due punti per i dischi, grado per le temperature.
        [':'] = ".." + "##" + "##" + ".." + "##" + "##" + "..",
        ['°'] = "###" + "#.#" + "###" + "..." + "..." + "..." + "...",
    };

    /// <summary>Larghezza dei glifi normali; alcuni segni sono piu' stretti.</summary>
    private const int GlyphW = 5;
    private const int GlyphH = 7;

    /// <summary>Colonne di un glifo: le righe sono sempre sette, la larghezza si deduce.</summary>
    private static int WidthOf(string glifo) => glifo.Length / GlyphH;

    /// <summary>
    /// Disegna la sigla con l'alfabeto a pixel, centrata nella striscia e allineata ai
    /// pixel dell'icona. Restituisce falso se un carattere non e' previsto o se non
    /// c'e' posto: in quel caso decide il chiamante.
    /// </summary>
    private static bool DrawPixelLabel(Graphics g, string text, RectangleF area, Color color)
    {
        if (text.Length == 0) return false;

        var glifi = new List<string>(text.Length);
        foreach (char c in text.ToUpperInvariant())
        {
            if (!PixelGlyphs.TryGetValue(c, out var glifo)) return false;
            glifi.Add(glifo);
        }

        int scala = Math.Max(1, (int)(area.Height / GlyphH));
        int colonne = glifi.Sum(WidthOf);

        // Fra un glifo e l'altro si lascia un pixel; se cosi' non ci sta si toglie,
        // tanto quasi tutte queste forme hanno gia' una colonna vuota ai lati.
        int spazio = scala;
        int larghezza = Larghezza(colonne, glifi.Count, scala, spazio);
        if (larghezza > area.Width)
        {
            spazio = 0;
            larghezza = Larghezza(colonne, glifi.Count, scala, spazio);
            if (larghezza > area.Width) return false;
        }

        int x0 = (int)Math.Round(area.X + (area.Width - larghezza) / 2f);
        int y0 = (int)Math.Round(area.Y + (area.Height - GlyphH * scala) / 2f);

        // Niente antialiasing: si riempiono rettangoli interi, cosi' i bordi restano netti.
        var modo = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var brush = new SolidBrush(color))
        {
            int x = x0;
            foreach (var glifo in glifi)
            {
                int w = WidthOf(glifo);
                for (int r = 0; r < GlyphH; r++)
                    for (int c = 0; c < w; c++)
                        if (glifo[r * w + c] == '#')
                            g.FillRectangle(brush, x + c * scala, y0 + r * scala, scala, scala);

                x += w * scala + spazio;
            }
        }
        g.SmoothingMode = modo;
        return true;

        static int Larghezza(int colonne, int glifi, int scala, int spazio) =>
            colonne * scala + (glifi - 1) * spazio;
    }

    private static Font MakeFont(string family, float emSize, FontStyle style)
    {
        try { return new Font(family, emSize, style, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, emSize, style, GraphicsUnit.Pixel); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Icona generica dell'applicazione (usata per la finestra e i messaggi).</summary>
    public static Icon CreateAppIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
            using (var back = new LinearGradientBrush(rect, Color.FromArgb(38, 48, 62),
                                                      Color.FromArgb(18, 24, 32), 60f))
            using (var path = RoundedRect(rect, size * 0.2f))
                g.FillPath(back, path);

            // Tre piatti/strati del disco
            using var pen = new Pen(Color.FromArgb(120, 200, 255), Math.Max(1.4f, size * 0.055f));
            for (int i = 0; i < 3; i++)
            {
                float y = size * (0.30f + i * 0.18f);
                g.DrawLine(pen, size * 0.24f, y, size * 0.62f, y);
            }

            // Indicatore di temperatura
            using var hot = new SolidBrush(Color.FromArgb(255, 120, 60));
            g.FillEllipse(hot, size * 0.66f, size * 0.55f, size * 0.22f, size * 0.22f);
            using var stem = new Pen(Color.FromArgb(255, 120, 60), Math.Max(1.4f, size * 0.07f));
            g.DrawLine(stem, size * 0.77f, size * 0.22f, size * 0.77f, size * 0.62f);
        }

        return IconFromBitmap(bmp);
    }

    /// <summary>
    /// Costruisce un'icona a 32 bit con canale alfa scrivendo un file ICO in memoria.
    /// Si evita così <c>Bitmap.GetHicon</c>, che perde la trasparenza e restituisce un
    /// handle nativo che <see cref="Icon.Clone"/> si limita a condividere.
    /// </summary>
    private static Icon IconFromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;

        var pixels = new byte[w * h * 4];
        var locked = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(locked.Scan0 + y * locked.Stride, pixels, y * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(locked); }

        int maskStride = (w + 31) / 32 * 4;
        int xorSize = w * h * 4;
        int andSize = maskStride * h;

        using var ms = new MemoryStream(22 + 40 + xorSize + andSize);
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            // ICONDIR
            bw.Write((ushort)0);                 // riservato
            bw.Write((ushort)1);                 // tipo: icona
            bw.Write((ushort)1);                 // numero di immagini

            // ICONDIRENTRY
            bw.Write((byte)(w >= 256 ? 0 : w));
            bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0);                   // colori della palette
            bw.Write((byte)0);                   // riservato
            bw.Write((ushort)1);                 // piani
            bw.Write((ushort)32);                // bit per pixel
            bw.Write((uint)(40 + xorSize + andSize));
            bw.Write((uint)22);                  // offset dei dati

            // BITMAPINFOHEADER (altezza doppia: bitmap XOR + maschera AND)
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);                          // BI_RGB
            bw.Write(xorSize + andSize);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

            // Bitmap dei colori, dal basso verso l'alto
            for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * w * 4, w * 4);

            // Maschera AND: bit a 1 dove il pixel è completamente trasparente
            var maskRow = new byte[maskStride];
            for (int y = h - 1; y >= 0; y--)
            {
                Array.Clear(maskRow);
                for (int x = 0; x < w; x++)
                    if (pixels[y * w * 4 + x * 4 + 3] == 0)
                        maskRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                bw.Write(maskRow);
            }
        }

        ms.Position = 0;
        return new Icon(ms);
    }
}
