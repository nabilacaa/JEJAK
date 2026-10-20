using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Jejak.App.Controls;

/// <summary>Satu titik/batang grafik.</summary>
public sealed record TitikGrafik(string Label, double? Nilai, bool Peringatan = false);

/// <summary>Dasar grafik yang digambar langsung (OnRender) agar ringan dan mengikuti tema.</summary>
public abstract class GrafikDasar : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object? def = null) =>
        new(def, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(GrafikDasar), Render());
    public static readonly DependencyProperty MaksimumProperty = DependencyProperty.Register(nameof(Maksimum), typeof(double), typeof(GrafikDasar), Render(100.0));
    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(nameof(SeriesBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.RoyalBlue));
    public static readonly DependencyProperty WarnBrushProperty = DependencyProperty.Register(nameof(WarnBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.Orange));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.Black));
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(nameof(MutedBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.Gray));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.LightGray));
    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(nameof(SurfaceBrush), typeof(Brush), typeof(GrafikDasar), Render(Brushes.White));
    public static readonly DependencyProperty KosongTextProperty = DependencyProperty.Register(nameof(KosongText), typeof(string), typeof(GrafikDasar), Render("Belum ada data untuk ditampilkan."));

    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double Maksimum { get => (double)GetValue(MaksimumProperty); set => SetValue(MaksimumProperty, value); }
    public Brush SeriesBrush { get => (Brush)GetValue(SeriesBrushProperty); set => SetValue(SeriesBrushProperty, value); }
    public Brush WarnBrush { get => (Brush)GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public string KosongText { get => (string)GetValue(KosongTextProperty); set => SetValue(KosongTextProperty, value); }

    protected List<TitikGrafik> Titik => Items?.OfType<TitikGrafik>().ToList() ?? new List<TitikGrafik>();

    protected FormattedText Teks(string s, double ukuran, Brush brush, bool tebal = false)
    {
        var family = TextElement.GetFontFamily(this);
        var typeface = new Typeface(family, FontStyles.Normal, tebal ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, ukuran, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected void TulisTengah(DrawingContext dc, FormattedText t, double cx, double y) => dc.DrawText(t, new Point(cx - t.Width / 2, y));

    protected void GambarGaris(DrawingContext dc, double kiri, double kanan, double atas, double bawah, double[] skala)
    {
        var solid = new Pen(GridBrush, 1);
        var putus = new Pen(GridBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        foreach (var t in skala)
        {
            var y = Math.Round(bawah - (bawah - atas) * t / Maksimum) + 0.5;
            dc.DrawLine(t == 0 ? solid : putus, new Point(kiri, y), new Point(kanan, y));
            var label = Teks(t.ToString("0", CultureInfo.CurrentCulture), 11, MutedBrush);
            dc.DrawText(label, new Point(kiri - 8 - label.Width, y - label.Height / 2));
        }
    }

    protected bool GambarKosong(DrawingContext dc)
    {
        var t = Teks(KosongText, 13, MutedBrush);
        dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
        return true;
    }
}

/// <summary>Grafik batang vertikal dengan garis ambang (dashboard: ketuntasan per kelompok).</summary>
public sealed class BarChart : GrafikDasar
{
    public static readonly DependencyProperty AmbangProperty = DependencyProperty.Register(
        nameof(Ambang), typeof(double?), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Ambang { get => (double?)GetValue(AmbangProperty); set => SetValue(AmbangProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var data = Titik;
        if (data.Count == 0) { GambarKosong(dc); return; }

        double kiri = 34, kanan = ActualWidth, atas = 18, bawah = ActualHeight - 24;
        if (kanan - kiri < 40 || bawah - atas < 20) return;
        GambarGaris(dc, kiri, kanan, atas, bawah, new double[] { 0, 25, 50, 75, 100 });

        if (Ambang is double a)
        {
            var y = Math.Round(bawah - (bawah - atas) * a / Maksimum) + 0.5;
            var pen = new Pen(WarnBrush, 1) { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) };
            dc.DrawLine(pen, new Point(kiri, y), new Point(kanan, y));
        }

        double slot = (kanan - kiri) / data.Count;
        double lebar = Math.Min(slot * 0.52, 44);
        for (int i = 0; i < data.Count; i++)
        {
            var d = data[i];
            double cx = kiri + slot * i + slot / 2;
            var nilai = Math.Clamp(d.Nilai ?? 0, 0, Maksimum);
            double tinggi = (bawah - atas) * nilai / Maksimum;
            if (d.Nilai is not null && tinggi > 0)
            {
                var rect = new Rect(cx - lebar / 2, bawah - tinggi, lebar, tinggi);
                dc.DrawRoundedRectangle(d.Peringatan ? WarnBrush : SeriesBrush, null, rect, Math.Min(5, lebar / 2), Math.Min(5, tinggi / 2));
            }
            var label = d.Nilai is null ? "—" : d.Nilai.Value.ToString("0", CultureInfo.CurrentCulture);
            TulisTengah(dc, Teks(label, 11.5, TextBrush, tebal: true), cx, bawah - tinggi - 17);
            TulisTengah(dc, Teks(d.Label, 11.5, MutedBrush), cx, bawah + 6);
        }
    }
}

/// <summary>Grafik garis dua seri (nilai kader dan rata-rata kelompok) pada halaman detail kader.</summary>
public sealed class LineChart : GrafikDasar
{
    public static readonly DependencyProperty PembandingProperty = DependencyProperty.Register(
        nameof(Pembanding), typeof(IEnumerable), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PembandingBrushProperty = DependencyProperty.Register(
        nameof(PembandingBrush), typeof(Brush), typeof(LineChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Pembanding { get => (IEnumerable?)GetValue(PembandingProperty); set => SetValue(PembandingProperty, value); }
    public Brush PembandingBrush { get => (Brush)GetValue(PembandingBrushProperty); set => SetValue(PembandingBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var data = Titik;
        if (data.Count == 0) { GambarKosong(dc); return; }
        var pembanding = Pembanding?.OfType<TitikGrafik>().ToList() ?? new List<TitikGrafik>();

        double kiri = 30, kanan = ActualWidth - 8, atas = 22, bawah = ActualHeight - 24;
        if (kanan - kiri < 40 || bawah - atas < 20) return;
        GambarGaris(dc, kiri, kanan, atas, bawah, new double[] { 0, 50, 100 });

        double x0 = kiri + 20, x1 = kanan - 20;
        double langkah = data.Count > 1 ? (x1 - x0) / (data.Count - 1) : 0;
        Point P(int i, double v) => new(data.Count > 1 ? x0 + langkah * i : (x0 + x1) / 2, bawah - (bawah - atas) * Math.Clamp(v, 0, Maksimum) / Maksimum);

        GambarSeri(dc, pembanding.Select(t => t.Nilai).ToList(), P, new Pen(PembandingBrush, 2));
        GambarSeri(dc, data.Select(t => t.Nilai).ToList(), P, new Pen(SeriesBrush, 2.5));

        for (int i = 0; i < data.Count; i++)
        {
            var d = data[i];
            double cx = data.Count > 1 ? x0 + langkah * i : (x0 + x1) / 2;
            if (d.Nilai is double v)
            {
                var p = P(i, v);
                var isi = d.Peringatan ? WarnBrush : SeriesBrush;
                dc.DrawEllipse(isi, new Pen(SurfaceBrush, 2), p, 5, 5);
                TulisTengah(dc, Teks(v.ToString("0", CultureInfo.CurrentCulture), 12, d.Peringatan ? WarnBrush : TextBrush, tebal: true), p.X, p.Y - 22);
            }
            TulisTengah(dc, Teks(d.Label, 11.5, MutedBrush), cx, bawah + 6);
        }
    }

    private static void GambarSeri(DrawingContext dc, IReadOnlyList<double?> nilai, Func<int, double, Point> titik, Pen pen)
    {
        pen.LineJoin = PenLineJoin.Round;
        Point? sebelum = null;
        for (int i = 0; i < nilai.Count; i++)
        {
            if (nilai[i] is not double v) { sebelum = null; continue; }
            var p = titik(i, v);
            if (sebelum is Point s) dc.DrawLine(pen, s, p);
            sebelum = p;
        }
    }
}
