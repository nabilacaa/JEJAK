using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Jejak.App.Converters;

/// <summary>true → Visible; false → Collapsed. Parameter "balik" membalik logika.</summary>
public sealed class BoolKeVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter is string p && p == "balik") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nilai null atau string kosong → Collapsed. Parameter "balik" membalik logika; "sembunyi" memakai Hidden.</summary>
public sealed class AdaKeVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool ada = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true
        };
        if (parameter is string p && p == "balik") ada = !ada;
        // "sembunyi" → Hidden: ruang tetap dipakai agar tinggi kartu seragam.
        var kosong = parameter is string q && q == "sembunyi" ? Visibility.Hidden : Visibility.Collapsed;
        return ada ? Visibility.Visible : kosong;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolBalik : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>Angka → lebar kolom bintang (untuk batang horizontal tanpa kode).</summary>
public sealed class KeBintang : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0;
        return new GridLength(Math.Max(0, v), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nama → inisial dua huruf untuk avatar.</summary>
public sealed class KeInisial : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var bagian = (value as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (bagian.Length == 0) return "?";
        var s = bagian[0][..1] + (bagian.Length > 1 ? bagian[1][..1] : "");
        return s.ToUpperInvariant();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nama → warna avatar yang stabil (sama dengan palet desain).</summary>
public sealed class KeWarnaAvatar : IValueConverter
{
    private static readonly SolidColorBrush[] Palet = new[]
    {
        "#3B66E0", "#0E9384", "#C2410C", "#7C3AED", "#DB2777", "#0891B2", "#65A30D", "#B45309"
    }.Select(h =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        b.Freeze();
        return b;
    }).ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        int h = s.Sum(ch => ch);
        return Palet[h % Palet.Length];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Kunci resource (mis. "Icon.Check") → objek resource.</summary>
public sealed class KeResource : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string kunci ? Application.Current.TryFindResource(kunci) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nilai tidak null/kosong → true (mis. teks galat → status galat input).</summary>
public sealed class AdaKeBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Skor kekuatan password → isi segmen ke-n (parameter) indikator: 100 atau 0.</summary>
public sealed class SegmenKekuatan : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int skor = value is int i ? i : 0;
        int n = int.TryParse(parameter as string, out var p) ? p : 1;
        return skor >= n ? 100.0 : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
