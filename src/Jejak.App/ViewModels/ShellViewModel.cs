using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Services;
using Jejak.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jejak.App.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly ThemeService _tema;
    private readonly Preferensi _preferensi;

    public LoginViewModel(IAuthService auth, ThemeService tema, Preferensi preferensi)
    {
        _auth = auth;
        _tema = tema;
        _preferensi = preferensi;
        _username = preferensi.UsernameDiingat ?? "";
        _ingatSaya = preferensi.UsernameDiingat is not null;
    }

    public event EventHandler? Berhasil;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _ingatSaya;

    [ObservableProperty]
    private string? _galat;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MasukCommand))]
    private bool _sedangMasuk;

    public bool Gelap => _tema.Gelap;

    partial void OnUsernameChanged(string value) => Galat = null;

    partial void OnPasswordChanged(string value) => Galat = null;

    private bool BisaMasuk() => !SedangMasuk;

    [RelayCommand(CanExecute = nameof(BisaMasuk))]
    private async Task MasukAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            Galat = "Masukkan username dan password.";
            return;
        }

        SedangMasuk = true;
        try
        {
            var sesi = await _auth.LoginAsync(Username, Password);
            if (sesi is null)
            {
                Password = "";
                Galat = "Username atau password salah."; // pesan umum (FR-01)
                return;
            }
            _preferensi.UsernameDiingat = IngatSaya ? sesi.Username : null;
            _tema.Terapkan(sesi.Tema);
            Berhasil?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Galat = "Tidak dapat membuka database: " + ex.Message;
        }
        finally
        {
            SedangMasuk = false;
        }
    }

    [RelayCommand]
    private void GantiTema()
    {
        _tema.Terapkan(_tema.Gelap ? "light" : "dark");
        OnPropertyChanged(nameof(Gelap));
    }
}

public sealed partial class MenuItemViewModel : ObservableObject
{
    public MenuItemViewModel(string kunci, string label, string ikon)
    {
        Kunci = kunci;
        Label = label;
        Ikon = ikon;
    }

    public string Kunci { get; }
    public string Label { get; }

    /// <summary>Kunci resource ikon, mis. "Icon.Grid".</summary>
    public string Ikon { get; }

    [ObservableProperty]
    private bool _aktif;

    [ObservableProperty]
    private int _badge;
}

/// <summary>Jendela utama: sidebar, header, area halaman, toast, dan overlay dialog.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _sp;
    private readonly IAuthService _auth;
    private readonly IRekapService _rekap;
    private readonly IPengaturanService _pengaturan;
    private readonly ThemeService _tema;
    private readonly UiService _ui;
    private readonly DispatcherTimer _timerToast;

    public MainViewModel(IServiceProvider sp, ISessionContext sesi, IAuthService auth, IRekapService rekap,
        IPengaturanService pengaturan, ThemeService tema, UiService ui)
    {
        _sp = sp;
        _auth = auth;
        _rekap = rekap;
        _pengaturan = pengaturan;
        _tema = tema;
        _ui = ui;
        _ui.Utama = this;

        var s = sesi.Wajib();
        NamaPengguna = s.Nama;
        LabelPeran = s.LabelPeran;
        IsAdmin = s.IsAdmin;

        MenuUtama.Add(new MenuItemViewModel("dashboard", "Dashboard", "Icon.Grid"));
        MenuUtama.Add(new MenuItemViewModel("kader", "Kader", "Icon.Users"));
        if (IsAdmin) MenuUtama.Add(new MenuItemViewModel("kelompok", "Kelompok", "Icon.Layers"));
        MenuUtama.Add(new MenuItemViewModel("penugasan", "Penugasan", "Icon.Clipboard"));
        MenuUtama.Add(new MenuItemViewModel("penilaian", "Penilaian", "Icon.Check"));
        MenuUtama.Add(new MenuItemViewModel("risiko", "Kader Berisiko", "Icon.Alert"));
        if (IsAdmin)
        {
            MenuAdministrasi.Add(new MenuItemViewModel("log", "Log Aktivitas", "Icon.History"));
            MenuAdministrasi.Add(new MenuItemViewModel("pengguna", "Pengguna", "Icon.Shield"));
        }
        MenuAdministrasi.Add(new MenuItemViewModel("pengaturan", "Pengaturan", "Icon.Gear"));

        _timerToast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _timerToast.Tick += (_, _) =>
        {
            _timerToast.Stop();
            ToastTampil = false;
        };
        _tema.Berubah += (_, _) => OnPropertyChanged(nameof(Gelap));
    }

    public event EventHandler? Keluar;

    public ObservableCollection<MenuItemViewModel> MenuUtama { get; } = new();
    public ObservableCollection<MenuItemViewModel> MenuAdministrasi { get; } = new();
    private IEnumerable<MenuItemViewModel> SemuaMenu => MenuUtama.Concat(MenuAdministrasi);

    public string NamaPengguna { get; }
    public string LabelPeran { get; }
    public bool IsAdmin { get; }
    public bool Gelap => _tema.Gelap;

    [ObservableProperty]
    private HalamanViewModel? _halaman;

    [ObservableProperty]
    private string _namaProgram = "Kaderisasi";

    [ObservableProperty]
    private string _periodeProgram = "";

    [ObservableProperty]
    private int _jumlahBerisiko;

    [ObservableProperty]
    private string _pencarian = "";

    [ObservableProperty]
    private bool _overlayTampil;

    [ObservableProperty]
    private string? _toastPesan;

    [ObservableProperty]
    private bool _toastGalat;

    [ObservableProperty]
    private bool _toastTampil;

    partial void OnJumlahBerisikoChanged(int value)
    {
        foreach (var m in SemuaMenu.Where(m => m.Kunci == "risiko")) m.Badge = value;
    }

    public async Task MulaiAsync()
    {
        try
        {
            NamaProgram = await _pengaturan.NamaProgramAsync();
            var snap = await _rekap.AmbilAsync();
            if (snap.Penugasan.Count > 0)
                PeriodeProgram = $"Periode {Fmt.TanggalPendek(snap.Penugasan.Min(p => p.TanggalMulai))} – {Fmt.TanggalPendek(snap.Penugasan.Max(p => p.Tenggat))}";
            JumlahBerisiko = snap.KaderBerisiko.Count;
        }
        catch (Exception ex)
        {
            _ui.TanganiGalat(ex);
        }
        await BukaAsync("dashboard");
    }

    public async Task BukaAsync(string kunci, object? parameter = null)
    {
        if (Halaman is IKonfirmasiKeluar k && !k.BolehKeluar()) return;

        HalamanViewModel? vm = kunci switch
        {
            "dashboard" => Buat<DashboardViewModel>(),
            "kader" => Buat<KaderListViewModel>(),
            "kader-detail" => Buat<KaderDetailViewModel>(),
            "kelompok" when IsAdmin => Buat<KelompokViewModel>(),
            "penugasan" => Buat<PenugasanViewModel>(),
            "penilaian" => Buat<PenilaianViewModel>(),
            "risiko" => Buat<RisikoViewModel>(),
            "log" when IsAdmin => Buat<LogViewModel>(),
            "pengguna" when IsAdmin => Buat<PenggunaViewModel>(),
            "pengaturan" => Buat<PengaturanViewModel>(),
            _ => null
        };
        if (vm is null) return;

        if (parameter is not null && vm is IMenerimaParameter p) p.Terima(parameter);
        Halaman = vm;
        foreach (var m in SemuaMenu) m.Aktif = m.Kunci == vm.KunciMenu;
        await vm.MuatAsync();
    }

    private T Buat<T>() where T : HalamanViewModel => ActivatorUtilities.CreateInstance<T>(_sp);

    [RelayCommand]
    private Task NavigasiAsync(string kunci) => BukaAsync(kunci);

    [RelayCommand]
    private Task CariAsync() => BukaAsync("kader", new PencarianKader(Pencarian.Trim()));

    [RelayCommand]
    private async Task GantiTemaAsync()
    {
        var baru = _tema.Gelap ? "light" : "dark";
        _tema.Terapkan(baru);
        try
        {
            await _auth.SimpanTemaAsync(baru);
        }
        catch (Exception ex)
        {
            _ui.TanganiGalat(ex);
        }
    }

    [RelayCommand]
    private async Task KeluarAsync()
    {
        if (Halaman is IKonfirmasiKeluar k && !k.BolehKeluar()) return;
        if (!_ui.Konfirmasi("Keluar dari JEJAK?", "Anda akan kembali ke halaman login.", "Keluar")) return;
        try
        {
            await _auth.LogoutAsync();
        }
        catch (Exception)
        {
            // Logout tetap dilanjutkan walaupun log gagal ditulis.
        }
        Keluar?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dipakai halaman Pengaturan setelah data contoh dipulihkan.</summary>
    public void KeluarPaksa() => Keluar?.Invoke(this, EventArgs.Empty);

    public void TampilkanToast(string pesan, bool galat)
    {
        ToastPesan = pesan;
        ToastGalat = galat;
        ToastTampil = true;
        _timerToast.Stop();
        _timerToast.Interval = TimeSpan.FromSeconds(galat ? 5 : 3.5);
        _timerToast.Start();
    }

    [RelayCommand]
    private void TutupToast()
    {
        _timerToast.Stop();
        ToastTampil = false;
    }
}

/// <summary>Parameter navigasi ke daftar kader dengan kata kunci pencarian.</summary>
public sealed record PencarianKader(string Teks);

/// <summary>Parameter navigasi ke detail kader; Asal menentukan tujuan tombol kembali.</summary>
public sealed record BukaDetailKader(int Id, string Asal = "kader");
