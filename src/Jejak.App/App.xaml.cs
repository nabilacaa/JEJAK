using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Jejak.App.Services;
using Jejak.App.ViewModels;
using Jejak.App.Views;
using Jejak.Core.Services;
using Jejak.Data;
using Jejak.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Jejak.App;

public partial class App : Application
{
    private static ServiceProvider? _layanan;
    private static string _lokasiDatabase = "";

    public static IServiceProvider Layanan => _layanan ?? throw new InvalidOperationException("Aplikasi belum diinisialisasi.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Disimpan sebelum budaya diganti: format regional Windows pengguna, dipakai untuk ekspor CSV
        // agar Excel di komputer tersebut langsung memisahkan kolom dengan benar.
        var budayaSistem = CultureInfo.CurrentCulture;

        // Lokal Indonesia: desimal koma dan nama bulan Indonesia di seluruh aplikasi.
        var budaya = CultureInfo.GetCultureInfo("id-ID");
        CultureInfo.DefaultThreadCurrentCulture = budaya;
        CultureInfo.DefaultThreadCurrentUICulture = budaya;
        Thread.CurrentThread.CurrentCulture = budaya;
        Thread.CurrentThread.CurrentUICulture = budaya;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(budaya.IetfLanguageTag)));

        DispatcherUnhandledException += GalatTakTertangani;

        // Argumen opsional --db <path> untuk memakai berkas database lain.
        _lokasiDatabase = DatabaseInitializer.LokasiBawaan;
        var idx = Array.IndexOf(e.Args, "--db");
        if (idx >= 0 && idx + 1 < e.Args.Length) _lokasiDatabase = System.IO.Path.GetFullPath(e.Args[idx + 1]);

        try
        {
            DatabaseInitializer.Pastikan(_lokasiDatabase);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Database tidak dapat dibuat di:\n{_lokasiDatabase}\n\n{ex.Message}", "JEJAK",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var preferensi = Preferensi.Muat();
        var sc = new ServiceCollection();
        sc.AddJejakData(_lokasiDatabase);
        sc.AddSingleton(FormatCsv.Dari(budayaSistem));
        sc.AddSingleton(preferensi);
        sc.AddSingleton(new LokasiData(_lokasiDatabase));
        sc.AddSingleton<ThemeService>();
        sc.AddSingleton<UiService>();
        sc.AddSingleton<IUiService>(sp => sp.GetRequiredService<UiService>());
        sc.AddTransient<LoginViewModel>();
        sc.AddTransient<MainViewModel>();
        _layanan = sc.BuildServiceProvider();

        Layanan.GetRequiredService<ThemeService>().Terapkan(preferensi.Tema);
        TampilkanLogin();
    }

    public static void TampilkanLogin()
    {
        var vm = Layanan.GetRequiredService<LoginViewModel>();
        var jendela = new LoginWindow { DataContext = vm };
        vm.Berhasil += (_, _) =>
        {
            TampilkanUtama();
            jendela.Close();
        };
        var lama = Current.MainWindow;
        Current.MainWindow = jendela;
        jendela.Show();
        if (lama is not null && lama != jendela) lama.Close();
    }

    private static void TampilkanUtama()
    {
        var vm = Layanan.GetRequiredService<MainViewModel>();
        var jendela = new MainWindow { DataContext = vm };
        vm.Keluar += (_, _) =>
        {
            Layanan.GetRequiredService<UiService>().Utama = null;
            Layanan.GetRequiredService<ISessionContext>().Akhiri();
            TampilkanLogin();
        };
        Current.MainWindow = jendela;
        jendela.Show();
        _ = vm.MulaiAsync();
    }

    /// <summary>Menghapus database lalu membuat ulang dari data contoh, kemudian kembali ke login.</summary>
    public static void PulihkanDataContoh()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            DatabaseInitializer.BuatUlang(_lokasiDatabase);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal memulihkan data contoh: " + ex.Message, "JEJAK", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var ui = Layanan.GetRequiredService<UiService>();
        var utama = ui.Utama;
        ui.Utama = null;
        Layanan.GetRequiredService<ISessionContext>().Akhiri();
        MessageBox.Show("Data contoh berhasil dipulihkan. Silakan login kembali.", "JEJAK", MessageBoxButton.OK, MessageBoxImage.Information);
        if (utama is not null) utama.KeluarPaksa();
        else TampilkanLogin();
    }

    private void GalatTakTertangani(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var ui = _layanan?.GetService<UiService>();
        if (ui?.Utama is not null)
        {
            ui.TanganiGalat(e.Exception);
            return;
        }
        MessageBox.Show("Terjadi kesalahan: " + e.Exception.Message, "JEJAK", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _layanan?.Dispose();
        base.OnExit(e);
    }
}
