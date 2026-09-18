using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiskTempMonitor.UI;

public enum ThemeMode
{
    /// <summary>Segue l'impostazione chiaro/scuro di Windows.</summary>
    Auto,
    Light,
    Dark,
}

/// <summary>
/// Tavolozza, tipografia e primitive di disegno condivise da tutta l'interfaccia.
/// Un unico punto da cui dipendono i colori, così il tema si cambia a caldo.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;

    public static event EventHandler? Changed;

    // ---------------------------------------------------------------- colori
    public static Color Background { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceAlt { get; private set; }
    public static Color SurfaceHover { get; private set; }
    public static Color Border { get; private set; }
    public static Color Divider { get; private set; }
    public static Color TextPrimary { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextMuted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color Good { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Bad { get; private set; }
    public static Color Neutral { get; private set; }
    public static Color Track { get; private set; }
    public static Color BannerBack { get; private set; }
    public static Color BannerText { get; private set; }

    // ------------------------------------------------------------ tipografia
    public static Font Display { get; private set; } = null!;
    public static Font Title { get; private set; } = null!;
    public static Font Section { get; private set; } = null!;
    public static Font Body { get; private set; } = null!;
    public static Font BodyStrong { get; private set; } = null!;
    public static Font Small { get; private set; } = null!;
    public static Font SmallStrong { get; private set; } = null!;
    public static Font Micro { get; private set; } = null!;
    public static Font Mono { get; private set; } = null!;

    private static string _uiFamily = "Segoe UI";
    private static string _displayFamily = "Segoe UI";

    static Theme()
    {
        PickFontFamilies();
        BuildFonts();
        Apply(ThemeMode.Auto);

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.Auto)
                Apply(ThemeMode.Auto);
        };
    }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        bool dark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => WindowsUsesDarkTheme(),
        };

        IsDark = dark;

        if (dark)
        {
            Background = FromHex("#16181D");
            Surface = FromHex("#1F2229");
            SurfaceAlt = FromHex("#262A32");
            SurfaceHover = FromHex("#2D323B");
            Border = FromHex("#333944");
            Divider = FromHex("#2A2F38");
            TextPrimary = FromHex("#EDEFF3");
            TextSecondary = FromHex("#A8AEBA");
            TextMuted = FromHex("#767D8B");
            Accent = FromHex("#4CC2FF");
            AccentSoft = FromHex("#20364A");
            Good = FromHex("#4ADE80");
            Warn = FromHex("#FBBF24");
            Bad = FromHex("#F87171");
            Neutral = FromHex("#8A919F");
            Track = FromHex("#2E333C");
            BannerBack = FromHex("#3A2E12");
            BannerText = FromHex("#F5CE6B");
        }
        else
        {
            // Niente bianco pieno e niente nero quasi pieno: le superfici sono un grigio
            // perla e il testo un grigio ardesia chiaro. Il contrasto del testo scende a
            // circa 6:1 — un monitor luminoso stanca molto meno — e resta comunque ben
            // oltre il 4,5:1 considerato il minimo leggibile. Anche i colori di stato
            // sono nelle versioni smorzate, ciascuna verificata sopra quel minimo.
            Background = FromHex("#D3D8DF");
            Surface = FromHex("#E2E5EA");
            SurfaceAlt = FromHex("#DCE0E6");
            SurfaceHover = FromHex("#D5DAE1");
            Border = FromHex("#BFC6D1");
            Divider = FromHex("#CDD3DB");
            TextPrimary = FromHex("#4C5462");
            TextSecondary = FromHex("#6B7382");
            TextMuted = FromHex("#8E95A1");
            Accent = FromHex("#366587");
            AccentSoft = FromHex("#CEDBE6");
            Good = FromHex("#326B49");
            Warn = FromHex("#7A580D");
            Bad = FromHex("#94473F");
            Neutral = FromHex("#8E95A1");
            Track = FromHex("#CBD1D9");
            BannerBack = FromHex("#E7DFCC");
            BannerText = FromHex("#6B5520");
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ tipografia

    private static void PickFontFamilies()
    {
        var available = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Su Windows 11 la famiglia "Variable" ha un disegno più moderno e meglio spaziato.
        _uiFamily = available.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text"
                  : available.Contains("Segoe UI") ? "Segoe UI"
                  : FontFamily.GenericSansSerif.Name;

        _displayFamily = available.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display"
                       : _uiFamily;
    }

    private static void BuildFonts()
    {
        Display = new Font(_displayFamily, 21f, FontStyle.Bold, GraphicsUnit.Point);
        Title = new Font(_displayFamily, 13f, FontStyle.Bold, GraphicsUnit.Point);
        Section = new Font(_uiFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        Body = new Font(_uiFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);
        BodyStrong = new Font(_uiFamily, 9f, FontStyle.Bold, GraphicsUnit.Point);
        Small = new Font(_uiFamily, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
        SmallStrong = new Font(_uiFamily, 8.25f, FontStyle.Bold, GraphicsUnit.Point);
        Micro = new Font(_uiFamily, 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        Mono = new Font(FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas",
                        9f, FontStyle.Regular, GraphicsUnit.Point);
    }

    // ------------------------------------------------------------- primitive

    public static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Riempie una superficie arrotondata con il bordo sottile del tema.</summary>
    public static void DrawSurface(Graphics g, RectangleF r, float radius,
                                   Color? fill = null, Color? border = null, float borderWidth = 1f)
    {
        using var path = RoundedRect(r, radius);
        using (var b = new SolidBrush(fill ?? Surface)) g.FillPath(b, path);
        if (borderWidth > 0)
        {
            using var p = new Pen(border ?? Border, borderWidth);
            g.DrawPath(p, path);
        }
    }

    /// <summary>Etichetta a pillola, usata per lo stato di salute e per i badge.</summary>
    public static void DrawPill(Graphics g, RectangleF r, string text, Color foreground, Color background, Font font)
    {
        using var path = RoundedRect(r, r.Height / 2f);
        using (var b = new SolidBrush(background)) g.FillPath(b, path);

        using var brush = new SolidBrush(foreground);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, r, fmt);
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(alpha, c);

    /// <summary>
    /// Adatta al tema un colore vivo scelto dall'utente — quelli delle soglie di
    /// temperatura — prima di disegnarlo dentro la finestra.
    /// <para>
    /// Sul fondo chiaro un verde o un rosso a piena saturazione stridono e si leggono
    /// male: vengono scuriti e smorzati verso il colore del testo, restando riconoscibili
    /// ma senza abbagliare. Sul fondo scuro invece servono accesi, e restano intatti.
    /// </para>
    /// <para>
    /// Non passano di qui le icone in area di notifica: lì il colore deve reggere su una
    /// barra delle applicazioni di tinta ignota, e serve tutta la sua forza.
    /// </para>
    /// </summary>
    public static Color OnSurface(Color c) => IsDark ? c : Mix(c, TextPrimary, 0.45f);

    /// <summary>Tronca il testo con i puntini di sospensione per stare nella larghezza data.</summary>
    public static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (g.MeasureString(text, font).Width <= maxWidth) return text;

        for (int len = text.Length - 1; len > 1; len--)
        {
            string candidate = text[..len].TrimEnd() + "…";
            if (g.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "…";
    }

    // ------------------------------------------- barra del titolo scura (DWM)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void ApplyToWindow(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* versioni di Windows che non espongono l'attributo */ }
    }

    // ------------------------------- barre di scorrimento native in tema scuro

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    /// <summary>
    /// Porta nel tema corrente le parti disegnate da Windows e non da noi: in pratica
    /// le barre di scorrimento di pannelli e griglie, che altrimenti restano chiare.
    /// </summary>
    public static void ApplyNativeTheme(Control root)
    {
        try { SetPreferredAppMode(IsDark ? 2 : 1); }      // 1 = consenti scuro, 2 = forza scuro
        catch { /* build di Windows senza questo ordinale */ }

        Walk(root);

        static void Walk(Control c)
        {
            if (c.IsHandleCreated)
            {
                try { SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null); }
                catch { /* controllo che non accetta il tema */ }
            }
            foreach (Control child in c.Controls) Walk(child);
        }
    }
}
