using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Jejak.App.ViewModels;
using Jejak.App.Views;
using Jejak.Core.Services;
using Microsoft.Win32;

namespace Jejak.App.Services;

/// <summary>Preferensi lokal per komputer (tema terakhir dan username yang diingat).</summary>
public sealed class Preferensi
{
    private static readonly string Lokasi = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JEJAK", "preferensi.json");

    public string Tema { get; set; } = "light";
    public string? UsernameDiingat { get; set; }

    public static Preferensi Muat()
    {
        try
        {
            if (File.Exists(Lokasi))
                return JsonSerializer.Deserialize<Preferensi>(File.ReadAllText(Lokasi)) ?? new Preferensi();
        }
        catch (Exception)
        {
            // Berkas rusak: kembali ke bawaan.
        }
        return new Preferensi();
    }

    public void Simpan()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Lokasi)!);
            File.WriteAllText(Lokasi, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Preferensi bersifat opsional; kegagalan menulis tidak menghentikan aplikasi.
        }
    }
}

/// <summary>Mengganti kamus warna Light/Dark saat aplikasi berjalan.</summary>
public sealed class ThemeService
{
    private readonly Preferensi _preferensi;

    public ThemeService(Preferensi preferensi) => _preferensi = preferensi;

    public string Saatini { get; private set; } = "light";
    public bool Gelap => Saatini == "dark";

    public event EventHandler? Berubah;

    public void Terapkan(string tema)
    {
        tema = tema == "dark" ? "dark" : "light";
        var kamus = Application.Current.Resources.MergedDictionaries;
        var baru = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(tema == "dark" ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        var lama = kamus.FirstOrDefault(d => d.Source is not null &&
            (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        if (lama is null) kamus.Insert(0, baru);
        else kamus[kamus.IndexOf(lama)] = baru;

        Saatini = tema;
        _preferensi.Tema = tema;
        _preferensi.Simpan();
        foreach (Window w in Application.Current.Windows) JudulGelap.Terapkan(w, Gelap);
        Berubah?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Menyelaraskan warna bilah judul Windows 10/11 dengan tema.</summary>
public static class JudulGelap
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Terapkan(Window w, bool gelap)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int nilai = gelap ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref nilai, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref nilai, sizeof(int)); // Windows 10 versi lama
        }
        catch (Exception)
        {
            // Tidak tersedia di versi Windows tertentu; abaikan.
        }
    }
}

public interface IUiService
{
    void Toast(string pesan, bool galat = false);
    bool Konfirmasi(string judul, string pesan, string tombolYa = "Ya, lanjutkan", bool bahaya = false);
    bool TampilkanDialog(DialogViewModel vm);
    Task NavigasiAsync(string kunci, object? parameter = null);
    void PerbaruiJumlahBerisiko(int jumlah);

    /// <summary>Meminta lokasi lalu menyimpan teks CSV (UTF-8 dengan BOM agar terbaca Excel).</summary>
    void SimpanCsv(string namaBawaan, string isi);

    /// <summary>Membuka tautan hasil tugas di browser bawaan (FR-39). Hanya URL http/https yang dibuka.</summary>
    void BukaTautan(string? tautan);

    void TanganiGalat(Exception ex);
}

public sealed class UiService : IUiService
{
    public MainViewModel? Utama { get; set; }

    public void Toast(string pesan, bool galat = false) => Utama?.TampilkanToast(pesan, galat);

    public void BukaTautan(string? tautan)
    {
        var url = tautan?.Trim();
        // Divalidasi lagi di sini: UseShellExecute akan menjalankan berkas/perintah lokal bila diberi selain URL.
        if (string.IsNullOrEmpty(url) || !Jejak.Core.Rules.AturanBisnis.TautanValid(url))
        {
            Toast("Tautan tugas tidak valid. Gunakan alamat yang diawali http:// atau https://.", galat: true);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast("Tautan tidak dapat dibuka: " + ex.Message, galat: true);
        }
    }

    public bool Konfirmasi(string judul, string pesan, string tombolYa = "Ya, lanjutkan", bool bahaya = false) =>
        TampilkanDialog(new KonfirmasiViewModel(judul, pesan, tombolYa, bahaya));

    public bool TampilkanDialog(DialogViewModel vm)
    {
        var pemilik = Application.Current.MainWindow;
        var jendela = new DialogWindow { DataContext = vm };
        if (pemilik is not null && pemilik.IsVisible && pemilik != jendela) jendela.Owner = pemilik;
        else jendela.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        void Tutup(bool hasil)
        {
            jendela.DialogResult = hasil;
        }

        vm.Tutup += Tutup;
        if (Utama is not null) Utama.OverlayTampil = true;
        try
        {
            return jendela.ShowDialog() == true;
        }
        finally
        {
            vm.Tutup -= Tutup;
            if (Utama is not null) Utama.OverlayTampil = false;
        }
    }

    public Task NavigasiAsync(string kunci, object? parameter = null) =>
        Utama?.BukaAsync(kunci, parameter) ?? Task.CompletedTask;

    public void PerbaruiJumlahBerisiko(int jumlah)
    {
        if (Utama is not null) Utama.JumlahBerisiko = jumlah;
    }

    public void SimpanCsv(string namaBawaan, string isi)
    {
        var dialog = new SaveFileDialog
        {
            FileName = namaBawaan,
            DefaultExt = ".csv",
            Filter = "Berkas CSV (*.csv)|*.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, isi, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Toast($"Tersimpan: {Path.GetFileName(dialog.FileName)}");
        }
        catch (IOException ex)
        {
            Toast("Gagal menyimpan berkas: " + ex.Message, galat: true);
        }
        catch (UnauthorizedAccessException)
        {
            Toast("Tidak memiliki izin menulis ke lokasi tersebut.", galat: true);
        }
    }

    public void TanganiGalat(Exception ex)
    {
        switch (ex)
        {
            case ValidasiException v:
                Toast(v.Message, galat: true);
                break;
            case AksesDitolakException a:
                Toast(a.Message, galat: true);
                break;
            case OperationCanceledException:
                break;
            default:
                Toast("Terjadi kesalahan: " + ex.Message, galat: true);
                break;
        }
    }
}
