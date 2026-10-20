using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jejak.App.Controls;

/// <summary>Ikon garis 24×24 dari resource Geometry (Themes/Icons.xaml).</summary>
public class Icon : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0));

    static Icon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}

public enum BadgeKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
    Accent
}

/// <summary>Label status berbentuk pil; warna ditentukan oleh <see cref="Kind"/> dan selalu disertai teks.</summary>
public class Badge : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Badge), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(BadgeKind), typeof(Badge), new FrameworkPropertyMetadata(BadgeKind.Neutral));

    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot), typeof(bool), typeof(Badge), new FrameworkPropertyMetadata(true));

    static Badge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(typeof(Badge)));
        FocusableProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(false));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BadgeKind Kind
    {
        get => (BadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool ShowDot
    {
        get => (bool)GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }
}

/// <summary>Properti terlampir untuk TextBox/PasswordBox/ComboBox: placeholder, ikon, dan status galat.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Hint), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(Hint), new FrameworkPropertyMetadata(false));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);
    public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, Geometry? v) => o.SetValue(IconProperty, v);
    public static bool GetHasError(DependencyObject o) => (bool)o.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject o, bool v) => o.SetValue(HasErrorProperty, v);
}

/// <summary>
/// Mengikat <see cref="PasswordBox.Password"/> ke ViewModel. PasswordBox sengaja tidak
/// menyediakan binding; properti terlampir ini menjembataninya dua arah.
/// </summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        // Bawaan null agar binding awal ("") selalu memicu callback dan event terpasang.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    public static string? GetBoundPassword(DependencyObject o) => (string?)o.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject o, string? v) => o.SetValue(BoundPasswordProperty, v);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(IsHookedProperty))
        {
            box.SetValue(IsHookedProperty, true);
            box.PasswordChanged += (s, _) =>
            {
                var pb = (PasswordBox)s;
                pb.SetValue(IsUpdatingProperty, true);
                SetBoundPassword(pb, pb.Password);
                pb.SetValue(IsUpdatingProperty, false);
            };
        }
        if ((bool)box.GetValue(IsUpdatingProperty)) return;
        var baru = e.NewValue as string ?? "";
        if (box.Password != baru) box.Password = baru;
    }
}

/// <summary>Logo JEJAK (jejak tiga titik) dengan teks opsional.</summary>
public class Logo : Control
{
    public static readonly DependencyProperty UkuranProperty = DependencyProperty.Register(
        nameof(Ukuran), typeof(double), typeof(Logo), new FrameworkPropertyMetadata(36.0));

    public static readonly DependencyProperty TampilkanTeksProperty = DependencyProperty.Register(
        nameof(TampilkanTeks), typeof(bool), typeof(Logo), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty SubBrushProperty = DependencyProperty.Register(
        nameof(SubBrush), typeof(Brush), typeof(Logo), new FrameworkPropertyMetadata(Brushes.Gray));

    static Logo()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Logo), new FrameworkPropertyMetadata(typeof(Logo)));
        FocusableProperty.OverrideMetadata(typeof(Logo), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Logo), new FrameworkPropertyMetadata(false));
    }

    public double Ukuran { get => (double)GetValue(UkuranProperty); set => SetValue(UkuranProperty, value); }
    public bool TampilkanTeks { get => (bool)GetValue(TampilkanTeksProperty); set => SetValue(TampilkanTeksProperty, value); }
    public Brush SubBrush { get => (Brush)GetValue(SubBrushProperty); set => SetValue(SubBrushProperty, value); }
}

/// <summary>Tema gelap → ikon matahari (untuk beralih ke terang); tema terang → ikon bulan.</summary>
public sealed class PilihIkonTema : System.Windows.Data.IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Application.Current.TryFindResource(value is true ? "Icon.Sun" : "Icon.Moon");

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
