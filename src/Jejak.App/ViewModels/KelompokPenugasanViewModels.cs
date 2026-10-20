using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;

namespace Jejak.App.ViewModels;

public sealed record KelompokKartu(
    KelompokRingkas Data,
    string Pemandu,
    bool AdaPemandu,
    string JumlahKaderTeks,
    double Ketuntasan,
    string KetuntasanTeks,
    bool KetuntasanRendah,
    string NilaiTeks,
    string BerisikoTeks);

/// <summary>S-06 Kelompok (FR-19 s.d. FR-22). Khusus Admin.</summary>
public sealed partial class KelompokViewModel : HalamanViewModel
{
    private readonly IRekapService _rekap;
    private readonly IKelompokService _kelompok;
    private List<KelompokKartu> _semua = new();

    public KelompokViewModel(IUiService ui, IRekapService rekap, IKelompokService kelompok) : base(ui)
    {
        _rekap = rekap;
        _kelompok = kelompok;
        UrutanOpsi = new[]
        {
            new Opsi<string>("nomor", "Urutkan: nomor kelompok"),
            new Opsi<string>("ketuntasan", "Urutkan: ketuntasan terendah"),
            new Opsi<string>("risiko", "Urutkan: kader berisiko terbanyak")
        };
        _urutanDipilih = UrutanOpsi[0];
    }

    public override string Judul => "Kelompok";
    public override string KunciMenu => "kelompok";

    public IReadOnlyList<Opsi<string>> UrutanOpsi { get; }
    [ObservableProperty] private Opsi<string> _urutanDipilih;
    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<KelompokKartu> _kartu = Array.Empty<KelompokKartu>();

    partial void OnCariChanged(string value) => Saring();
    partial void OnUrutanDipilihChanged(Opsi<string> value) => Saring();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        Ui.PerbaruiJumlahBerisiko(snap.KaderBerisiko.Count);
        _semua = snap.Kelompok.Select(g => new KelompokKartu(
            g,
            g.PemanduNama ?? "Belum ada pemandu",
            g.PemanduNama is not null,
            $"{g.JumlahKader} kader",
            g.RataKetuntasan ?? 0,
            Fmt.Persen(g.RataKetuntasan),
            g.RataKetuntasan < 75,
            $"Nilai rata-rata {Fmt.Nilai(g.RataNilai)}",
            g.JumlahBerisiko > 0 ? $"{g.JumlahBerisiko} berisiko" : "")).ToList();
        Saring();
    });

    private void Saring()
    {
        IEnumerable<KelompokKartu> q = _semua;
        var t = Cari.Trim();
        if (t.Length > 0)
            q = q.Where(k => k.Data.Label.Contains(t, StringComparison.OrdinalIgnoreCase) || k.Pemandu.Contains(t, StringComparison.OrdinalIgnoreCase));
        q = UrutanDipilih.Nilai switch
        {
            "ketuntasan" => q.OrderBy(k => k.Data.RataKetuntasan ?? 101).ThenBy(k => k.Data.Nomor),
            "risiko" => q.OrderByDescending(k => k.Data.JumlahBerisiko).ThenBy(k => k.Data.Nomor),
            _ => q.OrderBy(k => k.Data.Nomor)
        };
        Kartu = q.ToList();
    }

    [RelayCommand]
    private async Task TambahAsync()
    {
        var nomor = _semua.Count == 0 ? 1 : _semua.Max(k => k.Data.Nomor) + 1;
        var vm = new KelompokFormViewModel(_kelompok, null, nomor);
        await vm.MuatAsync();
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Kelompok berhasil ditambahkan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task UbahAsync(KelompokKartu? kartu)
    {
        if (kartu is null) return;
        var vm = new KelompokFormViewModel(_kelompok, kartu.Data, kartu.Data.Nomor);
        await vm.MuatAsync();
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Perubahan kelompok disimpan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task HapusAsync(KelompokKartu? kartu)
    {
        if (kartu is null) return;
        if (!Ui.Konfirmasi("Hapus kelompok?", $"{kartu.Data.Label} akan dihapus. Kelompok yang masih memiliki kader tidak dapat dihapus.", "Hapus", bahaya: true))
            return;
        await JalankanAsync(async () =>
        {
            await _kelompok.HapusAsync(kartu.Data.Id);
            Ui.Toast($"{kartu.Data.Label} dihapus.");
        });
        await MuatAsync();
    }
}

public sealed partial class KelompokFormViewModel : DialogViewModel
{
    private readonly IKelompokService _svc;
    private readonly int? _id;
    private readonly int? _pemanduAwal;

    public KelompokFormViewModel(IKelompokService svc, KelompokRingkas? data, int nomor)
    {
        _svc = svc;
        _id = data?.Id;
        _pemanduAwal = data?.PemanduId;
        _nomorTeks = nomor.ToString();
        _nama = data?.Nama ?? "";
        _deskripsi = data?.Deskripsi ?? "";
    }

    public override string Judul => _id is null ? "Tambah Kelompok" : "Ubah Kelompok";
    public override string Subjudul => "Satu kelompok dibimbing oleh satu pemandu.";

    [ObservableProperty] private string _nomorTeks;
    [ObservableProperty] private string _nama;
    [ObservableProperty] private string _deskripsi;
    [ObservableProperty] private IReadOnlyList<Opsi<int?>> _pemanduOpsi = Array.Empty<Opsi<int?>>();
    [ObservableProperty] private Opsi<int?>? _pemanduDipilih;
    [ObservableProperty] private string? _nomorGalat;
    [ObservableProperty] private string? _namaGalat;
    [ObservableProperty] private string? _pemanduGalat;

    public async Task MuatAsync()
    {
        var daftar = await _svc.PemanduTersediaAsync(_id);
        PemanduOpsi = new[] { new Opsi<int?>(null, "Tanpa pemandu") }
            .Concat(daftar.Select(p => new Opsi<int?>(p.Id, $"{p.Nama} ({p.Username})"))).ToList();
        PemanduDipilih = PemanduOpsi.FirstOrDefault(o => o.Nilai == _pemanduAwal) ?? PemanduOpsi[0];
    }

    [RelayCommand]
    private async Task SimpanAsync()
    {
        GalatUmum = NomorGalat = NamaGalat = PemanduGalat = null;
        if (!Fmt.CobaBulat(NomorTeks, out var nomor) || nomor < 1) NomorGalat = "Nomor harus berupa angka positif.";
        if (string.IsNullOrWhiteSpace(Nama)) NamaGalat = "Nama kelompok wajib diisi.";
        if (NomorGalat is not null || NamaGalat is not null) return;

        SedangMenyimpan = true;
        try
        {
            await _svc.SimpanAsync(new KelompokInput(_id, nomor, Nama, Deskripsi, PemanduDipilih?.Nilai));
            Selesai(true);
        }
        catch (ValidasiException ex)
        {
            switch (ex.Kolom)
            {
                case nameof(KelompokInput.Nomor): NomorGalat = ex.Message; break;
                case nameof(KelompokInput.Nama): NamaGalat = ex.Message; break;
                case nameof(KelompokInput.PemanduId): PemanduGalat = ex.Message; break;
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

public sealed record PenugasanBaris(
    PenugasanRingkas Data,
    BadgeKind KategoriKind,
    string Mulai,
    string Tenggat,
    string StatusTeks,
    BadgeKind StatusKind,
    bool AdaPengumpulan,
    string KumpulTeks,
    bool KumpulRendah);

/// <summary>S-07 Penugasan (FR-23 s.d. FR-26).</summary>
public sealed partial class PenugasanViewModel : HalamanViewModel
{
    private readonly IRekapService _rekap;
    private readonly IPenugasanService _svc;
    private List<PenugasanBaris> _semua = new();

    public PenugasanViewModel(IUiService ui, IRekapService rekap, IPenugasanService svc, ISessionContext sesi) : base(ui)
    {
        _rekap = rekap;
        _svc = svc;
        IsAdmin = sesi.Wajib().IsAdmin;
    }

    public override string Judul => "Penugasan";
    public override string KunciMenu => "penugasan";
    public bool IsAdmin { get; }

    [ObservableProperty] private bool _tabSemua = true;
    [ObservableProperty] private bool _tabBerjalan;
    [ObservableProperty] private bool _tabSelesai;
    [ObservableProperty] private bool _tabBelum;
    [ObservableProperty] private int _jumlahSemua;
    [ObservableProperty] private int _jumlahBerjalan;
    [ObservableProperty] private int _jumlahSelesai;
    [ObservableProperty] private int _jumlahBelum;
    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<PenugasanBaris> _tampil = Array.Empty<PenugasanBaris>();

    partial void OnTabSemuaChanged(bool value) { if (value) Saring(); }
    partial void OnTabBerjalanChanged(bool value) { if (value) Saring(); }
    partial void OnTabSelesaiChanged(bool value) { if (value) Saring(); }
    partial void OnTabBelumChanged(bool value) { if (value) Saring(); }
    partial void OnCariChanged(string value) => Saring();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        _semua = snap.Penugasan.Select(p => new PenugasanBaris(
            p,
            p.Kategori == KategoriPenugasan.Individu ? BadgeKind.Accent : BadgeKind.Neutral,
            Fmt.TanggalPendek(p.TanggalMulai),
            p.Tenggat.ToString("d MMM HH.mm", Fmt.Id),
            Fmt.StatusPenugasan(p.Status),
            Fmt.KindPenugasan(p.Status),
            p.Status != StatusPenugasan.BelumDimulai,
            $"{p.JumlahKumpul}/{p.JumlahKader}",
            p.JatuhTempo && p.PersenKumpul < 90)).ToList();
        JumlahSemua = _semua.Count;
        JumlahBerjalan = _semua.Count(p => p.Data.Status == StatusPenugasan.Berjalan);
        JumlahSelesai = _semua.Count(p => p.Data.Status == StatusPenugasan.Selesai);
        JumlahBelum = _semua.Count(p => p.Data.Status == StatusPenugasan.BelumDimulai);
        Saring();
    });

    private void Saring()
    {
        IEnumerable<PenugasanBaris> q = _semua;
        if (TabBerjalan) q = q.Where(p => p.Data.Status == StatusPenugasan.Berjalan);
        else if (TabSelesai) q = q.Where(p => p.Data.Status == StatusPenugasan.Selesai);
        else if (TabBelum) q = q.Where(p => p.Data.Status == StatusPenugasan.BelumDimulai);
        var t = Cari.Trim();
        if (t.Length > 0)
            q = q.Where(p => p.Data.Judul.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Data.Kode.Contains(t, StringComparison.OrdinalIgnoreCase));
        Tampil = q.ToList();
    }

    [RelayCommand]
    private async Task TambahAsync()
    {
        if (!IsAdmin) return;
        var kode = $"P{_semua.Count + 1}";
        var vm = new PenugasanFormViewModel(_svc, null, kode);
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Penugasan berhasil ditambahkan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task UbahAsync(PenugasanBaris? baris)
    {
        if (!IsAdmin || baris is null) return;
        var vm = new PenugasanFormViewModel(_svc, baris.Data, baris.Data.Kode);
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Perubahan penugasan disimpan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task HapusAsync(PenugasanBaris? baris)
    {
        if (!IsAdmin || baris is null) return;
        var p = baris.Data;
        var pesan = p.JumlahKumpul > 0
            ? $"{p.Label} sudah memiliki {p.JumlahKumpul} pengumpulan, sehingga penugasan akan dinonaktifkan. Nilai akhir kader akan dihitung ulang tanpa penugasan ini."
            : $"{p.Label} akan dihapus.";
        if (!Ui.Konfirmasi("Hapus penugasan?", pesan, "Hapus", bahaya: true)) return;
        await JalankanAsync(async () => Ui.Toast(await _svc.HapusAsync(p.Id)));
        await MuatAsync();
    }

    [RelayCommand]
    private Task NilaiAsync(PenugasanBaris? baris) =>
        baris is null || !baris.AdaPengumpulan ? Task.CompletedTask : Ui.NavigasiAsync("penilaian", baris.Data.Id);
}

public sealed partial class PenugasanFormViewModel : DialogViewModel
{
    private readonly IPenugasanService _svc;
    private readonly int? _id;

    public PenugasanFormViewModel(IPenugasanService svc, PenugasanRingkas? data, string kode)
    {
        _svc = svc;
        _id = data?.Id;
        KategoriOpsi = new[]
        {
            new Opsi<KategoriPenugasan>(KategoriPenugasan.Individu, "Individu"),
            new Opsi<KategoriPenugasan>(KategoriPenugasan.Kelompok, "Kelompok")
        };
        var hariIni = DateTime.Today;
        _kode = kode;
        _judulPenugasan = data?.Judul ?? "";
        _deskripsi = data?.Deskripsi ?? "";
        _kategoriDipilih = KategoriOpsi.First(o => o.Nilai == (data?.Kategori ?? KategoriPenugasan.Individu));
        _tanggalMulai = data?.TanggalMulai.Date ?? hariIni;
        _tanggalTenggat = data?.Tenggat.Date ?? hariIni.AddDays(7);
        _jamTenggat = data is null ? "23:59" : data.Tenggat.ToString("HH:mm");
        _bobotTeks = (data?.Bobot ?? 10).ToString();
    }

    public override string Judul => _id is null ? "Tambah Penugasan" : "Ubah Penugasan";
    public override string Subjudul => "Status Tepat Waktu/Terlambat dihitung otomatis dari tenggat.";
    public IReadOnlyList<Opsi<KategoriPenugasan>> KategoriOpsi { get; }

    [ObservableProperty] private string _kode;
    [ObservableProperty] private string _judulPenugasan;
    [ObservableProperty] private string _deskripsi;
    [ObservableProperty] private Opsi<KategoriPenugasan> _kategoriDipilih;
    [ObservableProperty] private DateTime? _tanggalMulai;
    [ObservableProperty] private DateTime? _tanggalTenggat;
    [ObservableProperty] private string _jamTenggat;
    [ObservableProperty] private string _bobotTeks;

    [ObservableProperty] private string? _kodeGalat;
    [ObservableProperty] private string? _judulGalat;
    [ObservableProperty] private string? _tenggatGalat;
    [ObservableProperty] private string? _bobotGalat;

    [RelayCommand]
    private async Task SimpanAsync()
    {
        GalatUmum = KodeGalat = JudulGalat = TenggatGalat = BobotGalat = null;
        if (string.IsNullOrWhiteSpace(Kode)) KodeGalat = "Kode wajib diisi.";
        if (string.IsNullOrWhiteSpace(JudulPenugasan)) JudulGalat = "Judul wajib diisi.";
        if (!Fmt.CobaBulat(BobotTeks, out var bobot) || bobot is < 1 or > 100) BobotGalat = "Bobot harus bilangan 1–100.";
        DateTime mulai = default, tenggat = default;
        if (TanggalMulai is null || TanggalTenggat is null) TenggatGalat = "Tanggal mulai dan tenggat wajib diisi.";
        else if (!Fmt.CobaJam(JamTenggat, out var jam)) TenggatGalat = "Jam tenggat harus berformat JJ:MM, mis. 23:59.";
        else
        {
            mulai = TanggalMulai.Value.Date;
            tenggat = TanggalTenggat.Value.Date + jam;
            if (tenggat < mulai) TenggatGalat = "Tenggat tidak boleh lebih awal dari tanggal mulai.";
        }
        if ((KodeGalat ?? JudulGalat ?? BobotGalat ?? TenggatGalat) is not null) return;

        SedangMenyimpan = true;
        try
        {
            await _svc.SimpanAsync(new PenugasanInput(_id, Kode, JudulPenugasan, Deskripsi, KategoriDipilih.Nilai, mulai, tenggat, bobot));
            Selesai(true);
        }
        catch (ValidasiException ex)
        {
            switch (ex.Kolom)
            {
                case nameof(PenugasanInput.Kode): KodeGalat = ex.Message; break;
                case nameof(PenugasanInput.Judul): JudulGalat = ex.Message; break;
                case nameof(PenugasanInput.Tenggat): TenggatGalat = ex.Message; break;
                case nameof(PenugasanInput.Bobot): BobotGalat = ex.Message; break;
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
