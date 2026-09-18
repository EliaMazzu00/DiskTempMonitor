using System.Drawing.Drawing2D;
using DiskTempMonitor.Models;
using DiskTempMonitor.Services;

namespace DiskTempMonitor.UI;

/// <summary>
/// Grafico dell'andamento della temperatura nella sessione corrente, con area
/// sfumata, bande delle soglie e valore corrente in evidenza.
/// </summary>
public sealed class TempChart : Control
{
    private readonly AppSettings _settings;
    private DiskInfo? _disk;

    public TempChart(AppSettings settings)
    {
        _settings = settings;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetDisk(DiskInfo? disk)
    {
        _disk = disk;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Surface);

        var plot = new RectangleF(38, 10, Width - 52, Height - 34);
        if (plot.Width < 20 || plot.Height < 20) return;

        var samples = _disk?.History.ToArray() ?? [];
        if (samples.Length == 0)
        {
            using var mb = new SolidBrush(Theme.TextMuted);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("In attesa delle prime letture…", Theme.Small, mb, plot, fmt);
            return;
        }

        // Scala verticale: almeno 20 gradi di ampiezza, arrotondata a multipli di 5.
        int dataMin = samples.Min(), dataMax = samples.Max();

        // Se la soglia di attenzione è vicina, la si include per mostrare il margine.
        if (_settings.WarnTemp <= dataMax + 15) dataMax = Math.Max(dataMax, _settings.WarnTemp);

        int mid = (dataMin + dataMax) / 2;
        int span = Math.Max(20, dataMax - dataMin + 8);
        int min = (int)(Math.Floor((mid - span / 2.0) / 5) * 5);
        int max = min + (int)(Math.Ceiling(span / 5.0) * 5);
        if (max <= min) max = min + 20;

        float ToY(double celsius) =>
            plot.Bottom - (float)((celsius - min) / (max - min)) * plot.Height;

        // Griglia orizzontale con etichette
        using (var gridPen = new Pen(Theme.Divider, 1f) { DashStyle = DashStyle.Dot })
        using (var labelBrush = new SolidBrush(Theme.TextMuted))
        {
            int step = (max - min) / 4;
            if (step < 1) step = 1;
            for (int v = min; v <= max; v += step)
            {
                float y = ToY(v);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);

                string label = $"{Math.Round(_settings.ToDisplay(v)):0}";
                var size = g.MeasureString(label, Theme.Micro);
                g.DrawString(label, Theme.Micro, labelBrush, plot.Left - size.Width - 6, y - size.Height / 2);
            }
        }

        // Soglie: linee tratteggiate sottili, molto meno invadenti di una banda piena
        DrawThreshold(g, plot, _settings.WarnTemp, Theme.Warn, "attenzione", min, max, ToY);
        DrawThreshold(g, plot, _settings.CriticalTemp, Theme.Bad, "critica", min, max, ToY);

        // Serie distribuita su tutta la larghezza: l'asse X è "le ultime N letture",
        // così il grafico è leggibile già dal secondo campione.
        var points = new PointF[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples.Length == 1
                ? plot.Right
                : plot.Left + i * (plot.Width / (samples.Length - 1));
            points[i] = new PointF(x, ToY(samples[i]));
        }

        var current = samples[^1];
        var lineColor = Theme.OnSurface(IconRenderer.ColorForTemp(current, _settings));

        if (points.Length > 1)
        {
            // Area sfumata sotto la curva
            var area = new PointF[points.Length + 2];
            Array.Copy(points, area, points.Length);
            area[^2] = new PointF(points[^1].X, plot.Bottom);
            area[^1] = new PointF(points[0].X, plot.Bottom);

            using (var gradient = new LinearGradientBrush(
                       new RectangleF(plot.X, plot.Y, plot.Width, plot.Height + 1),
                       Theme.Alpha(lineColor, Theme.IsDark ? 90 : 70),
                       Theme.Alpha(lineColor, 0), 90f))
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(area);
                g.FillPath(gradient, path);
            }

            using var pen = new Pen(lineColor, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, points);
        }

        // Punto corrente
        var last = points[^1];
        using (var halo = new SolidBrush(Theme.Alpha(lineColor, 60)))
            g.FillEllipse(halo, last.X - 6, last.Y - 6, 12, 12);
        using (var dot = new SolidBrush(lineColor))
            g.FillEllipse(dot, last.X - 3.5f, last.Y - 3.5f, 7, 7);

        // Legenda in basso
        using (var b = new SolidBrush(Theme.TextMuted))
        {
            string span2 = $"{samples.Length} letture · ogni {_settings.RefreshSeconds}s";
            g.DrawString(span2, Theme.Micro, b, plot.Left, plot.Bottom + 6);

            string stats = $"min {_settings.FormatTemp(_disk?.SessionMinC)}   max {_settings.FormatTemp(_disk?.SessionMaxC)}";
            var size = g.MeasureString(stats, Theme.Micro);
            g.DrawString(stats, Theme.Micro, b, plot.Right - size.Width, plot.Bottom + 6);
        }
    }

    private static void DrawThreshold(Graphics g, RectangleF plot, int value, Color color, string label,
                                      int min, int max, Func<double, float> toY)
    {
        if (value < min || value > max) return;       // soglia fuori dalla scala corrente
        float y = toY(value);

        using (var pen = new Pen(Theme.Alpha(color, 150), 1f) { DashStyle = DashStyle.Dash, DashPattern = [4f, 3f] })
            g.DrawLine(pen, plot.Left, y, plot.Right, y);

        using var b = new SolidBrush(Theme.Alpha(color, 190));
        var size = g.MeasureString(label, Theme.Micro);
        g.DrawString(label, Theme.Micro, b, plot.Left + 4, y - size.Height - 1);
    }
}
