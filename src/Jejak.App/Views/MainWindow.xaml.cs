using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Jejak.App.Services;
using Jejak.App.ViewModels;

namespace Jejak.App.Views;

public partial class MainWindow : Window
{
    private static readonly Brush Sukses = Beku("#4ADE80");
    private static readonly Brush Galat = Beku("#F87171");

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => JudulGelap.Terapkan(this, (App.Layanan.GetService(typeof(ThemeService)) as ThemeService)?.Gelap == true);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged lama) lama.PropertyChanged -= VmBerubah;
            if (e.NewValue is INotifyPropertyChanged baru) baru.PropertyChanged += VmBerubah;
        };
    }

    private void VmBerubah(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ToastGalat) && sender is MainViewModel vm)
        {
            IkonToast.Data = (Geometry)FindResource(vm.ToastGalat ? "Icon.Alert" : "Icon.Checkmark");
            IkonToast.Foreground = vm.ToastGalat ? Galat : Sukses;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+K memfokuskan pencarian global.
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            KotakCari.Focus();
            KotakCari.SelectAll();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private static Brush Beku(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
