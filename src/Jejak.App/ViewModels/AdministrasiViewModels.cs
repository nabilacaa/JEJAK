using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Jejak.Data.Services;

// MVVMTK0034 sengaja diabaikan: beberapa field [ObservableProperty] ditulis langsung
// agar handler perubahan (penyaringan/pemuatan ulang) tidak terpicu saat inisialisasi.
#pragma warning disable MVVMTK0034

namespace Jejak.App.ViewModels;

public sealed record RisikoBaris(
    KaderRingkas Data,
    string Belum,
    bool BelumBahaya,
    string KetuntasanTeks,
    bool KetuntasanBahaya,
    string NilaiTeks,
    bool NilaiBahaya,
    IReadOnlyList<KriteriaChip> Kriteria);

/// <summary>S-09 Kader Berisiko (FR-32, BR-05).</summary>
public sealed partial class RisikoViewModel : HalamanViewModel
{
    private readonly IRekapService _rekap;
    private readonly IEksporService _ekspor;
    private readonly IClock _clock;
    private List<RisikoBaris> _semua = new();
    private bool _opsiSiap;

    public RisikoViewModel(IUiService ui, IRekapService rekap, IEksporService ekspor, IClock clock) : base(ui)
    {
        _rekap = rekap;
        _ekspor = ekspor;
        _clock = clock;
        KriteriaOpsi = new[]
        {
            new Opsi<string?>(null, "Semua kriteria"), new Opsi<string?>(AturanBisnis.R1, "R1 · Belum mengumpulkan"),
            new Opsi<string?>(AturanBisnis.R2, "R2 · Ketuntasan rendah"), new Opsi<string?>(AturanBisnis.R3, "R3 · Nilai rendah")
        };
        _kriteriaDipilih = KriteriaOpsi[0];
    }

    public override string Judul => "Kader Berisiko";
    public override string KunciMenu => "risiko";

    [ObservableProperty] private string _keteranganAmbang = "";
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _jumlahR1;
    [ObservableProperty] private int _jumlahR2;
    [ObservableProperty] private int _jumlahR3;
    [ObservableProperty] private string _subTotal = "";
    [ObservableProperty] private string _subR1 = "";
    [ObservableProperty] private string _subR2 = "";
    [ObservableProperty] private string _subR3 = "";

    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<Opsi<int?>> _kelompokOpsi = Array.Empty<Opsi<int?>>();
    [ObservableProperty] private Opsi<int?>? _kelompokDipilih;
    public IReadOnlyList<Opsi<string?>> KriteriaOpsi { get; }
    [ObservableProperty] private Opsi<string?> _kriteriaDipilih;
    [ObservableProperty] private IReadOnlyList<RisikoBaris> _tampil = Array.Empty<RisikoBaris>();
    [ObservableProperty] private string _pesanKosong = "";

    partial void OnCariChanged(string value) => Saring();
    partial void OnKelompokDipilihChanged(Opsi<int?>? value) => Saring();
    partial void OnKriteriaDipilihChanged(Opsi<string?> value) => Saring();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        var a = snap.Ambang;
        var risiko = snap.KaderBerisiko;
        Ui.PerbaruiJumlahBerisiko(risiko.Count);

        KeteranganAmbang = $"R1: ≥ {a.MinBelum} penugasan jatuh tempo belum dikumpulkan  ·  R2: ketuntasan < {a.MinKetuntasan:0}%  ·  " +
                           $"R3: nilai akhir < {a.MinNilai:0} (minimal {a.MinDinilai} penugasan sudah dinilai)";
        Total = risiko.Count;
        JumlahR1 = risiko.Count(k => k.Kriteria.Contains(AturanBisnis.R1));
        JumlahR2 = risiko.Count(k => k.Kriteria.Contains(AturanBisnis.R2));
        JumlahR3 = risiko.Count(k => k.Kriteria.Contains(AturanBisnis.R3));
        SubTotal = $"dari {snap.KaderAktif.Count()} kader aktif";
        SubR1 = $"≥ {a.MinBelum} penugasan";
        SubR2 = $"< {a.MinKetuntasan:0}%";
        SubR3 = $"nilai akhir < {a.MinNilai:0}";

        _semua = risiko.Select(k => new RisikoBaris(k,
            (k.Rekap?.JumlahBelum ?? 0).ToString(), k.Rekap?.JumlahBelum >= a.MinBelum,
            Fmt.Persen(k.Rekap?.Ketuntasan), k.Rekap?.Ketuntasan < a.MinKetuntasan,
            Fmt.Nilai(k.Rekap?.NilaiAkhir), k.Rekap?.NilaiAkhir < a.MinNilai,
            Fmt.Chip(k.Kriteria))).ToList();

        if (!_opsiSiap)
        {
            _opsiSiap = true;
            KelompokOpsi = new[] { new Opsi<int?>(null, "Semua kelompok") }
                .Concat(snap.Kelompok.Select(g => new Opsi<int?>(g.Id, g.Label))).ToList();
            _kelompokDipilih = KelompokOpsi[0];
            OnPropertyChanged(nameof(KelompokDipilih));
        }
        Saring();
    });

    private void Saring()
    {
        IEnumerable<RisikoBaris> q = _semua;
        var t = Cari.Trim();
        if (t.Length > 0)
            q = q.Where(k => k.Data.Nama.Contains(t, StringComparison.OrdinalIgnoreCase) || k.Data.Nrp.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (KelompokDipilih?.Nilai is int g) q = q.Where(k => k.Data.KelompokId == g);
        if (KriteriaDipilih.Nilai is string c) q = q.Where(k => k.Data.Kriteria.Contains(c));
        Tampil = q.ToList();
        PesanKosong = _semua.Count == 0
            ? "Tidak ada kader yang memenuhi kriteria berisiko."
            : Tampil.Count == 0 ? "Tidak ada kader yang cocok dengan filter." : "";
    }

    [RelayCommand]
    private Task LihatAsync(RisikoBaris? baris) =>
        baris is null ? Task.CompletedTask : Ui.NavigasiAsync("kader-detail", new BukaDetailKader(baris.Data.Id, "risiko"));

    [RelayCommand]
    private Task EksporAsync() => JalankanAsync(async () =>
    {
        var csv = await _ekspor.KaderBerisikoCsvAsync();
        Ui.SimpanCsv($"kader-berisiko-{_clock.Now:yyyyMMdd}.csv", csv);
    });
}

public sealed record LogBaris(string Waktu, string Nama, string Aksi, BadgeKind AksiKind, string Entitas, string Keterangan);

/// <summary>S-10 Log Aktivitas (FR-33, FR-34). Khusus Admin.</summary>
public sealed partial class LogViewModel : HalamanViewModel
{
    private const int UkuranHalaman = 12;
    private readonly ILogService _log;
    private readonly IPenggunaService _pengguna;
    private readonly IEksporService _ekspor;
    private readonly IClock _clock;
    private CancellationTokenSource? _cts;
    private bool _siap;

    public LogViewModel(IUiService ui, ILogService log, IPenggunaService pengguna, IEksporService ekspor, IClock clock) : base(ui)
    {
        _log = log;
        _pengguna = pengguna;
        _ekspor = ekspor;
        _clock = clock;
        AksiOpsi = new[] { new Opsi<string?>(null, "Semua aksi") }
            .Concat(AksiLog.Semua.Select(a => new Opsi<string?>(a, a))).ToList();
        _aksiDipilih = AksiOpsi[0];
    }

    public override string Judul => "Log Aktivitas";
    public override string KunciMenu => "log";

    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<Opsi<int?>> _penggunaOpsi = Array.Empty<Opsi<int?>>();
    [ObservableProperty] private Opsi<int?>? _penggunaDipilih;
    public IReadOnlyList<Opsi<string?>> AksiOpsi { get; }
    [ObservableProperty] private Opsi<string?> _aksiDipilih;
    [ObservableProperty] private DateTime? _dari;
    [ObservableProperty] private DateTime? _sampai;
    [ObservableProperty] private IReadOnlyList<LogBaris> _tampil = Array.Empty<LogBaris>();
    [ObservableProperty] private string _infoHalaman = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SebelumnyaCommand), nameof(BerikutnyaCommand))]
    private int _halamanKe = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SebelumnyaCommand), nameof(BerikutnyaCommand))]
    private int _totalHalaman = 1;

    partial void OnCariChanged(string value) => MuatUlang(true);
    partial void OnPenggunaDipilihChanged(Opsi<int?>? value) => MuatUlang(true);
    partial void OnAksiDipilihChanged(Opsi<string?> value) => MuatUlang(true);
    partial void OnDariChanged(DateTime? value) => MuatUlang(true);
    partial void OnSampaiChanged(DateTime? value) => MuatUlang(true);

    private void MuatUlang(bool reset)
    {
        if (!_siap) return;
        if (reset) _halamanKe = 1;
        _ = JalankanAsync(MuatHalamanAsync);
    }

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var daftar = await _pengguna.DaftarAsync();
        PenggunaOpsi = new[] { new Opsi<int?>(null, "Semua pengguna") }
            .Concat(daftar.Select(p => new Opsi<int?>(p.Id, p.Nama))).ToList();
        _penggunaDipilih = PenggunaOpsi[0];
        OnPropertyChanged(nameof(PenggunaDipilih));
        _siap = true;
        await MuatHalamanAsync();
    });

    private FilterLog Filter(int halaman, int ukuran) =>
        new(Cari, PenggunaDipilih?.Nilai, AksiDipilih.Nilai, Dari, Sampai, halaman, ukuran);

    private async Task MuatHalamanAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var hasil = await _log.CariAsync(Filter(HalamanKe, UkuranHalaman), cts.Token);
        if (cts.IsCancellationRequested) return;

        TotalHalaman = Math.Max(1, (int)Math.Ceiling(hasil.Total / (double)UkuranHalaman));
        OnPropertyChanged(nameof(HalamanKe));
        SebelumnyaCommand.NotifyCanExecuteChanged();
        BerikutnyaCommand.NotifyCanExecuteChanged();
        Tampil = hasil.Item.Select(l => new LogBaris(Fmt.TanggalJam(l.Waktu), l.PenggunaNama ?? "—", l.Aksi, KindAksi(l.Aksi), l.Entitas, l.Keterangan)).ToList();
        var awal = (HalamanKe - 1) * UkuranHalaman;
        InfoHalaman = hasil.Total == 0 ? "Tidak ada entri yang cocok" : $"Menampilkan {awal + 1}–{awal + Tampil.Count} dari {hasil.Total} entri";
    }

    private static BadgeKind KindAksi(string aksi) => aksi switch
    {
        AksiLog.Nilai => BadgeKind.Accent,
        AksiLog.Ubah => BadgeKind.Info,
        AksiLog.Tambah => BadgeKind.Success,
        AksiLog.Hapus => BadgeKind.Danger,
        AksiLog.LoginGagal => BadgeKind.Warning,
        _ => BadgeKind.Neutral
    };

    private bool BisaSebelumnya() => HalamanKe > 1;
    private bool BisaBerikutnya() => HalamanKe < TotalHalaman;

    [RelayCommand(CanExecute = nameof(BisaSebelumnya))]
    private void Sebelumnya()
    {
        HalamanKe--;
        MuatUlang(false);
    }

    [RelayCommand(CanExecute = nameof(BisaBerikutnya))]
    private void Berikutnya()
    {
        HalamanKe++;
        MuatUlang(false);
    }

    [RelayCommand]
    private void ResetFilter()
    {
        _siap = false;
        Cari = "";
        PenggunaDipilih = PenggunaOpsi.FirstOrDefault();
        AksiDipilih = AksiOpsi[0];
        Dari = null;
        Sampai = null;
        _siap = true;
        MuatUlang(true);
    }

    [RelayCommand]
    private Task EksporAsync() => JalankanAsync(async () =>
    {
        var csv = await _ekspor.LogCsvAsync(Filter(1, int.MaxValue));
        Ui.SimpanCsv($"log-aktivitas-{_clock.Now:yyyyMMdd}.csv", csv);
    });
}

public sealed record PenggunaBaris(PenggunaItem Data, string PeranTeks, BadgeKind PeranKind, string Kelompok, string LoginTeks, bool DiriSendiri);

/// <summary>S-11 Pengguna (FR-35). Khusus Admin.</summary>
public sealed partial class PenggunaViewModel : HalamanViewModel
{
    private readonly IPenggunaService _svc;
    private readonly IClock _clock;
    private readonly SesiPengguna _sesi;
    private List<PenggunaBaris> _semua = new();

    public PenggunaViewModel(IUiService ui, IPenggunaService svc, ISessionContext sesi, IClock clock) : base(ui)
    {
        _svc = svc;
        _clock = clock;
        _sesi = sesi.Wajib();
        PeranOpsi = new[]
        {
            new Opsi<Peran?>(null, "Semua peran"), new Opsi<Peran?>(Peran.Admin, "Admin (SC)"), new Opsi<Peran?>(Peran.Pemandu, "Pemandu")
        };
        _peranDipilih = PeranOpsi[0];
    }

    public override string Judul => "Pengguna";
    public override string KunciMenu => "pengguna";

    public IReadOnlyList<Opsi<Peran?>> PeranOpsi { get; }
    [ObservableProperty] private Opsi<Peran?> _peranDipilih;
    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<PenggunaBaris> _tampil = Array.Empty<PenggunaBaris>();
    [ObservableProperty] private string _info = "";

    partial void OnCariChanged(string value) => Saring();
    partial void OnPeranDipilihChanged(Opsi<Peran?> value) => Saring();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var daftar = await _svc.DaftarAsync();
        var sekarang = _clock.Now;
        _semua = daftar.Select(p => new PenggunaBaris(p,
            p.Peran == Peran.Admin ? "Admin (SC)" : "Pemandu",
            p.Peran == Peran.Admin ? BadgeKind.Accent : BadgeKind.Neutral,
            p.KelompokBinaan ?? "—",
            p.LoginTerakhir is DateTime t ? Fmt.Relatif(t, sekarang) : "Belum pernah",
            p.Id == _sesi.Id)).ToList();
        Saring();
    });

    private void Saring()
    {
        IEnumerable<PenggunaBaris> q = _semua;
        var t = Cari.Trim();
        if (t.Length > 0)
            q = q.Where(p => p.Data.Nama.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Data.Username.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (PeranDipilih.Nilai is Peran r) q = q.Where(p => p.Data.Peran == r);
        Tampil = q.ToList();
        Info = $"{Tampil.Count} dari {_semua.Count} pengguna · {_semua.Count(p => p.Data.Aktif)} aktif";
    }

    [RelayCommand]
    private async Task TambahAsync()
    {
        if (Ui.TampilkanDialog(new PenggunaFormViewModel(_svc, null, false)))
        {
            Ui.Toast("Akun berhasil dibuat.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task UbahAsync(PenggunaBaris? baris)
    {
        if (baris is null) return;
        if (Ui.TampilkanDialog(new PenggunaFormViewModel(_svc, baris.Data, baris.DiriSendiri)))
        {
            Ui.Toast("Perubahan akun disimpan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task UbahAktifAsync(PenggunaBaris? baris)
    {
        if (baris is null) return;
        var p = baris.Data;
        var aktifkan = !p.Aktif;
        var pesan = aktifkan
            ? $"{p.Nama} akan dapat login kembali."
            : $"{p.Nama} tidak akan dapat login. Riwayat nilai dan log yang dibuatnya tetap tersimpan.";
        if (Ui.Konfirmasi(aktifkan ? "Aktifkan akun?" : "Nonaktifkan akun?", pesan, aktifkan ? "Aktifkan" : "Nonaktifkan", bahaya: !aktifkan))
        {
            await JalankanAsync(async () =>
            {
                await _svc.SetAktifAsync(p.Id, aktifkan);
                Ui.Toast(aktifkan ? $"{p.Nama} diaktifkan." : $"{p.Nama} dinonaktifkan.");
            });
        }
        await MuatAsync(); // menyelaraskan posisi toggle dengan data
    }
}

public sealed partial class PenggunaFormViewModel : DialogViewModel
{
    private readonly IPenggunaService _svc;
    private readonly int? _id;

    public PenggunaFormViewModel(IPenggunaService svc, PenggunaItem? data, bool diriSendiri)
    {
        _svc = svc;
        _id = data?.Id;
        DiriSendiri = diriSendiri;
        PeranOpsi = new[] { new Opsi<Peran>(Peran.Pemandu, "Pemandu"), new Opsi<Peran>(Peran.Admin, "Admin (SC)") };
        _username = data?.Username ?? "";
        _nama = data?.Nama ?? "";
        _aktif = data?.Aktif ?? true;
        _peranDipilih = PeranOpsi.First(o => o.Nilai == (data?.Peran ?? Peran.Pemandu));
    }

    public bool IsBaru => _id is null;
    public bool DiriSendiri { get; }
    public bool BisaUbahPeran => !DiriSendiri;
    public override string Judul => IsBaru ? "Tambah Pengguna" : "Ubah Pengguna";
    public override string Subjudul => IsBaru ? "Password awal dapat diganti pengguna setelah login." : "Kosongkan password bila tidak ingin mengubahnya.";
    public string LabelPassword => IsBaru ? "Password awal" : "Password baru (opsional)";
    public IReadOnlyList<Opsi<Peran>> PeranOpsi { get; }

    [ObservableProperty] private string _username;
    [ObservableProperty] private string _nama;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _aktif;
    [ObservableProperty] private Opsi<Peran> _peranDipilih;
    [ObservableProperty] private string? _usernameGalat;
    [ObservableProperty] private string? _namaGalat;
    [ObservableProperty] private string? _passwordGalat;

    [RelayCommand]
    private async Task SimpanAsync()
    {
        GalatUmum = UsernameGalat = NamaGalat = PasswordGalat = null;
        if (string.IsNullOrWhiteSpace(Username)) UsernameGalat = "Username wajib diisi.";
        if (string.IsNullOrWhiteSpace(Nama)) NamaGalat = "Nama wajib diisi.";
        if (IsBaru && Password.Length == 0) PasswordGalat = "Password awal wajib diisi.";
        else if (Password.Length is > 0 and < AuthService.PanjangMinimumPassword)
            PasswordGalat = $"Password minimal {AuthService.PanjangMinimumPassword} karakter.";
        if ((UsernameGalat ?? NamaGalat ?? PasswordGalat) is not null) return;

        SedangMenyimpan = true;
        try
        {
            await _svc.SimpanAsync(new PenggunaInput(_id, Username, Nama, PeranDipilih.Nilai, Aktif, Password.Length == 0 ? null : Password));
            Selesai(true);
        }
        catch (ValidasiException ex)
        {
            switch (ex.Kolom)
            {
                case nameof(PenggunaInput.Username): UsernameGalat = ex.Message; break;
                case nameof(PenggunaInput.Nama): NamaGalat = ex.Message; break;
                case nameof(PenggunaInput.PasswordBaru): PasswordGalat = ex.Message; break;
                default: GalatUmum = ex.Message; break;
            }
        }
        catch (Exception ex)
        {
            GalatUmum = ex.Message;
        }
        finally
        {
            SedangMenyimpan = false;
        }
    }
}

/// <summary>Lokasi berkas database aktif (didaftarkan di DI).</summary>
public sealed record LokasiData(string Path);

/// <summary>S-12 Pengaturan: tema, ambang kader berisiko, ganti password, tentang aplikasi.</summary>
public sealed partial class PengaturanViewModel : HalamanViewModel
{
    private readonly IAuthService _auth;
    private readonly IPengaturanService _pengaturan;
    private readonly ThemeService _tema;
    private readonly LokasiData _lokasi;
    private readonly SesiPengguna _sesi;
    private bool _siap;

    public PengaturanViewModel(IUiService ui, IAuthService auth, IPengaturanService pengaturan, ThemeService tema,
        ISessionContext sesi, LokasiData lokasi) : base(ui)
    {
        _auth = auth;
        _pengaturan = pengaturan;
        _tema = tema;
        _lokasi = lokasi;
        _sesi = sesi.Wajib();
        _temaGelap = tema.Gelap;
        _temaTerang = !tema.Gelap;
        _siap = true;
    }

    public override string Judul => "Pengaturan";
    public override string KunciMenu => "pengaturan";
    public bool IsAdmin => _sesi.IsAdmin;
    public string Versi => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
    public string LokasiDatabase => _lokasi.Path;
    public string Akun => $"{_sesi.Nama} · {(_sesi.IsAdmin ? "Admin (SC)" : "Pemandu")}";

    [ObservableProperty] private bool _temaTerang;
    [ObservableProperty] private bool _temaGelap;

    [ObservableProperty] private string _r1 = "";
    [ObservableProperty] private string _r2 = "";
    [ObservableProperty] private string _r3 = "";
    [ObservableProperty] private string _r3Min = "";
    [ObservableProperty] private string? _galatAmbang;

    [ObservableProperty] private string _passwordLama = "";
    [ObservableProperty] private string _passwordBaru = "";
    [ObservableProperty] private string _konfirmasi = "";
    [ObservableProperty] private string? _galatPasswordLama;
    [ObservableProperty] private string? _galatPasswordBaru;
    [ObservableProperty] private string? _galatKonfirmasi;
    [ObservableProperty] private int _kekuatan;
    [ObservableProperty] private string _kekuatanTeks = "";

    partial void OnTemaGelapChanged(bool value)
    {
        if (!_siap || !value) return;
        _ = GantiTemaAsync("dark");
    }

    partial void OnTemaTerangChanged(bool value)
    {
        if (!_siap || !value) return;
        _ = GantiTemaAsync("light");
    }

    private async Task GantiTemaAsync(string tema)
    {
        _tema.Terapkan(tema);
        try
        {
            await _auth.SimpanTemaAsync(tema);
        }
        catch (Exception ex)
        {
            Ui.TanganiGalat(ex);
        }
    }

    partial void OnPasswordBaruChanged(string value)
    {
        int skor = 0;
        if (value.Length >= AuthService.PanjangMinimumPassword) skor++;
        if (value.Length >= 12) skor++;
        if (value.Any(char.IsDigit) && value.Any(char.IsLetter)) skor++;
        if (value.Any(c => !char.IsLetterOrDigit(c)) || (value.Any(char.IsUpper) && value.Any(char.IsLower))) skor++;
        Kekuatan = value.Length == 0 ? 0 : Math.Max(1, skor);
        KekuatanTeks = value.Length == 0 ? "" : Kekuatan switch
        {
            1 => "Kekuatan password: lemah",
            2 => "Kekuatan password: cukup",
            3 => "Kekuatan password: kuat",
            _ => "Kekuatan password: sangat kuat"
        };
    }

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        TampilkanAmbang(await _pengaturan.AmbangAsync());
    });

    private void TampilkanAmbang(AmbangRisiko a)
    {
        R1 = a.MinBelum.ToString();
        R2 = a.MinKetuntasan.ToString("0.##", Fmt.Id);
        R3 = a.MinNilai.ToString("0.##", Fmt.Id);
        R3Min = a.MinDinilai.ToString();
        GalatAmbang = null;
    }

    [RelayCommand]
    private async Task SimpanAmbangAsync()
    {
        GalatAmbang = null;
        if (!Fmt.CobaBulat(R1, out var r1) || !Fmt.CobaAngka(R2, out var r2) || !Fmt.CobaAngka(R3, out var r3) || !Fmt.CobaBulat(R3Min, out var r3Min))
        {
            GalatAmbang = "Semua ambang harus berupa angka.";
            return;
        }
        try
        {
            await _pengaturan.SimpanAmbangAsync(new AmbangRisiko(r1, r2, r3, r3Min));
            Ui.Toast("Ambang kader berisiko disimpan. Daftar kader berisiko dihitung ulang.");
            await MuatAsync();
        }
        catch (ValidasiException ex)
        {
            GalatAmbang = ex.Message;
        }
        catch (Exception ex)
        {
            Ui.TanganiGalat(ex);
        }
    }

    [RelayCommand]
    private void KembalikanBawaan() => TampilkanAmbang(AmbangRisiko.Bawaan);

    [RelayCommand]
    private async Task GantiPasswordAsync()
    {
        GalatPasswordLama = GalatPasswordBaru = GalatKonfirmasi = null;
        if (PasswordLama.Length == 0) GalatPasswordLama = "Masukkan password lama.";
        if (PasswordBaru.Length < AuthService.PanjangMinimumPassword) GalatPasswordBaru = $"Password baru minimal {AuthService.PanjangMinimumPassword} karakter.";
        if (Konfirmasi != PasswordBaru) GalatKonfirmasi = "Konfirmasi tidak sama dengan password baru.";
        if ((GalatPasswordLama ?? GalatPasswordBaru ?? GalatKonfirmasi) is not null) return;

        try
        {
            await _auth.GantiPasswordAsync(PasswordLama, PasswordBaru);
            PasswordLama = PasswordBaru = Konfirmasi = "";
            Ui.Toast("Password berhasil diperbarui.");
        }
        catch (ValidasiException ex)
        {
            if (ex.Kolom == "PasswordLama") GalatPasswordLama = ex.Message;
            else GalatPasswordBaru = ex.Message;
        }
        catch (Exception ex)
        {
            Ui.TanganiGalat(ex);
        }
    }

    [RelayCommand]
    private void BukaFolderData()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_lokasi.Path);
            if (folder is not null && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.TanganiGalat(ex);
        }
    }

    [RelayCommand]
    private void PulihkanDataContoh()
    {
        if (!IsAdmin) return;
        if (!Ui.Konfirmasi("Pulihkan data contoh?",
                "Seluruh data saat ini (kader, nilai, akun, dan log) akan dihapus dan diganti data contoh awal. Tindakan ini tidak dapat dibatalkan.",
                "Hapus dan pulihkan", bahaya: true))
            return;
        App.PulihkanDataContoh();
    }
}
