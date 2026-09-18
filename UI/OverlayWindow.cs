using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Sovrimpressione di gioco: una targhetta con FPS, temperature e carichi che resta
/// sopra a tutto, compreso il gioco.
/// <para>
/// <b>Come sta sopra al gioco senza entrarci.</b> È una normale finestra di Windows,
/// solo dichiarata come strato trasparente (<c>WS_EX_LAYERED</c>), fuori dalla catena
/// dei clic (<c>WS_EX_TRANSPARENT</c>), che non prende mai il fuoco
/// (<c>WS_EX_NOACTIVATE</c>) e non compare fra le finestre con Alt+Tab
/// (<c>WS_EX_TOOLWINDOW</c>). Non inietta niente nel gioco e non si aggancia alle sue
/// funzioni di disegno: dal punto di vista del gioco — e del suo anticheat — non
/// esiste. Il prezzo è che funziona sopra ai giochi <i>a finestra</i> e <i>a finestra
/// senza bordi</i>, che oggi sono la quasi totalità, ma non sopra a quelli in
/// schermo intero esclusivo, dove nessuna finestra può comparire.
/// </para>
/// <para>
/// Il disegno passa da <c>UpdateLayeredWindow</c> invece che dal normale ciclo di
/// ridisegno: così ogni pixel ha la sua trasparenza, e si ottiene lo sfondo scuro
/// translucido con sopra il testo pieno. Con la trasparenza di finestra, che è uguale
/// per tutto, anche le cifre sarebbero sbiadite.
/// </para>
/// </summary>
public sealed class OverlayWindow : Form
{
    private readonly AppSettings _settings;
    private IReadOnlyList<DiskInfo> _disks = [];
    private SystemMetrics? _metrics;
    private float? _fps;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Enabled = false;            // non deve reagire a niente
        Size = new Size(240, 160);  // provvisoria: la detta il contenuto
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT |
                          Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Nuovi dati: si ridisegna e si rimette al suo posto.</summary>
    public void Update(IReadOnlyList<DiskInfo> disks, SystemMetrics? metrics, float? fps)
    {
        _disks = disks;
        _metrics = metrics;
        _fps = fps;

        if (!IsHandleCreated) return;
        Render();
    }

    // ------------------------------------------------------------- disegno

    private void Render()
    {
        using var bmp = OverlayRenderer.Draw(_settings, _disks, _metrics, _fps);
        if (bmp is null) { Native.Hide(Handle); return; }

        var posto = Posiziona(bmp.Width, bmp.Height);
        Native.Apply(Handle, bmp, posto);
    }

    /// <summary>
    /// Si aggancia all'angolo scelto dello schermo su cui sta il gioco, con un margine
    /// che non copra la grafica di bordo.
    /// <para>
    /// Lo schermo è quello della finestra in primo piano, non quello dove sta il
    /// puntatore: durante una partita il mouse può trovarsi ovunque — su un secondo
    /// monitor, fermo da mezz'ora in un angolo — e la targhetta finirebbe lì invece
    /// che davanti agli occhi.
    /// </para>
    /// </summary>
    private Point Posiziona(int larghezza, int altezza)
    {
        IntPtr davanti = Native.ForegroundWindow();
        var monitor = davanti != IntPtr.Zero ? Screen.FromHandle(davanti) : Screen.PrimaryScreen;
        var schermo = (monitor ?? Screen.PrimaryScreen!).WorkingArea;
        int margine = Math.Max(8, _settings.OverlayMargin);

        int x = _settings.OverlayCorner is OverlayCorner.AltoSinistra or OverlayCorner.BassoSinistra
            ? schermo.Left + margine
            : schermo.Right - larghezza - margine;

        int y = _settings.OverlayCorner is OverlayCorner.AltoSinistra or OverlayCorner.AltoDestra
            ? schermo.Top + margine
            : schermo.Bottom - altezza - margine;

        // Ritocco fine: l'angolo dà il punto di partenza, lo scostamento permette di
        // scansare quello che il gioco disegna proprio lì.
        x += _settings.OverlayOffsetX;
        y += _settings.OverlayOffsetY;

        Bounds = new Rectangle(x, y, larghezza, altezza);
        return new Point(x, y);
    }

    /// <summary>Toglie la targhetta dallo schermo senza chiuderla.</summary>
    public void HideOverlay()
    {
        if (IsHandleCreated) Native.Hide(Handle);
    }

    /// <summary>
    /// Rimette la targhetta in cima alla pila. I giochi, quando prendono il primo piano,
    /// spingono la loro finestra sopra a tutte le altre: senza questa spinta periodica
    /// la sovrimpressione sparirebbe dietro.
    /// </summary>
    public void BringToFront() => Native.KeepOnTop(Handle);

    private static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
                           SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040,
                           SWP_HIDEWINDOW = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        private struct BlendFunction
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointS { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SizeS { public int Cx, Cy; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PointS dst,
            ref SizeS size, IntPtr hdcSrc, ref PointS src, int key, ref BlendFunction blend, int flags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public static IntPtr ForegroundWindow() => GetForegroundWindow();

        /// <summary>
        /// Riversa il disegno nella finestra, trasparenza per trasparenza.
        /// <para>
        /// La posizione la detta questa chiamata e non <c>SetWindowPos</c>: passando a
        /// <c>UpdateLayeredWindow</c> un punto di destinazione, la finestra ci viene
        /// <b>spostata</b>. Passandogli (0,0) — come si farebbe pensando che sia
        /// l'origine del disegno — la targhetta tornava nell'angolo in alto a sinistra
        /// dello schermo a ogni aggiornamento, qualunque angolo fosse stato scelto.
        /// </para>
        /// </summary>
        public static void Apply(IntPtr hwnd, Bitmap bmp, Point dove)
        {
            IntPtr schermo = GetDC(IntPtr.Zero);
            IntPtr memoria = CreateCompatibleDC(schermo);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr precedente = IntPtr.Zero;

            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                precedente = SelectObject(memoria, hBitmap);

                var dimensione = new SizeS { Cx = bmp.Width, Cy = bmp.Height };
                var origine = new PointS { X = 0, Y = 0 };
                var posizione = new PointS { X = dove.X, Y = dove.Y };
                var blend = new BlendFunction
                {
                    BlendOp = AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };

                UpdateLayeredWindow(hwnd, IntPtr.Zero, ref posizione, ref dimensione,
                                    memoria, ref origine, 0, ref blend, ULW_ALPHA);

                // E ogni volta si torna in cima: un gioco che prende il primo piano
                // spinge la propria finestra sopra a tutte, anche sopra alle "sempre
                // in primo piano".
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            finally
            {
                if (precedente != IntPtr.Zero) SelectObject(memoria, precedente);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memoria);
                ReleaseDC(IntPtr.Zero, schermo);
            }
        }

        public static void Hide(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);

        public static void KeepOnTop(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
