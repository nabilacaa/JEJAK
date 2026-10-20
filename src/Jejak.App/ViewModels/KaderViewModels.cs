using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Jejak.Data.Services;

// MVVMTK0034 sengaja diabaikan: beberapa field [ObservableProperty] ditulis langsung
// agar handler perubahan (penyaringan/pemuatan ulang) tidak terpicu saat inisialisasi.
#pragma warning disable MVVMTK0034

namespace Jejak.App.ViewModels;

public sealed record KaderBaris(
    KaderRingkas Data,
    string KetuntasanTeks,
    double Ketuntasan,
    bool AdaKetuntasan,
    bool KetuntasanRendah,
    string NilaiTeks,
    bool NilaiRendah,
    BadgeKind StatusKind)
{
    public static KaderBaris Dari(KaderRingkas k) => new(
        k,
        Fmt.Persen(k.Rekap?.Ketuntasan),
        k.Rekap?.Ketuntasan ?? 0,
        k.Rekap?.Ketuntasan is not null,
        k.Rekap?.Ketuntasan < 75,
        Fmt.Nilai(k.Rekap?.NilaiAkhir),
        k.Rekap?.NilaiAkhir < 60,
        Fmt.KindStatusKader(k.Status));
}

/// <summary>S-03 Daftar Kader (FR-12 s.d. FR-18).</summary>
public sealed partial class KaderListViewModel : HalamanViewModel, IMenerimaParameter
{
    private const int UkuranHalaman = 10;

    private readonly IRekapService _rekap;
    private readonly IKaderService _kader;
    private readonly IEksporService _ekspor;
    private readonly IClock _clock;
    private List<KaderBaris> _semua = new();
    private IReadOnlyList<KelompokRingkas> _kelompok = Array.Empty<KelompokRingkas>();
    private List<KaderBaris> _tersaring = new();
    private bool _opsiSiap;

    public KaderListViewModel(IUiService ui, IRekapService rekap, IKaderService kader, IEksporService ekspor, ISessionContext sesi, IClock clock)
        : base(ui)
    {
        _rekap = rekap;
        _kader = kader;
        _ekspor = ekspor;
        _clock = clock;
        IsAdmin = sesi.Wajib().IsAdmin;
        StatusOpsi = new[]
        {
            new Opsi<string?>(null, "Semua status"), new Opsi<string?>("Aktif", "Aktif"),
            new Opsi<string?>("Berisiko", "Berisiko"), new Opsi<string?>("Nonaktif", "Nonaktif")
        };
        _statusDipilih = StatusOpsi[0];
    }

    public override string Judul => "Data Kader";
    public override string KunciMenu => "kader";
    public bool IsAdmin { get; }

    [ObservableProperty] private string _cari = "";
    [ObservableProperty] private IReadOnlyList<Opsi<int?>> _kelompokOpsi = Array.Empty<Opsi<int?>>();
    [ObservableProperty] private Opsi<int?>? _kelompokDipilih;
    [ObservableProperty] private IReadOnlyList<Opsi<int?>> _angkatanOpsi = Array.Empty<Opsi<int?>>();
    [ObservableProperty] private Opsi<int?>? _angkatanDipilih;
    public IReadOnlyList<Opsi<string?>> StatusOpsi { get; }
    [ObservableProperty] private Opsi<string?> _statusDipilih;

    [ObservableProperty] private IReadOnlyList<KaderBaris> _tampil = Array.Empty<KaderBaris>();
    [ObservableProperty] private string _infoHalaman = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SebelumnyaCommand), nameof(BerikutnyaCommand))]
    private int _halamanKe = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SebelumnyaCommand), nameof(BerikutnyaCommand))]
    private int _totalHalaman = 1;

    [ObservableProperty] private int _jumlahTotal;
    [ObservableProperty] private int _jumlahAktif;
    [ObservableProperty] private int _jumlahBerisiko;
    [ObservableProperty] private int _jumlahNonaktif;

    public void Terima(object parameter)
    {
        if (parameter is PencarianKader p) _cari = p.Teks;
    }

    partial void OnCariChanged(string value) => Saring(resetHalaman: true);
    partial void OnKelompokDipilihChanged(Opsi<int?>? value) => Saring(resetHalaman: true);
    partial void OnAngkatanDipilihChanged(Opsi<int?>? value) => Saring(resetHalaman: true);
    partial void OnStatusDipilihChanged(Opsi<string?> value) => Saring(resetHalaman: true);
    partial void OnHalamanKeChanged(int value) => TerapkanHalaman();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        _kelompok = snap.Kelompok;
        _semua = snap.Kader.Select(KaderBaris.Dari).ToList();
        Ui.PerbaruiJumlahBerisiko(snap.KaderBerisiko.Count);

        JumlahTotal = _semua.Count;
        JumlahAktif = _semua.Count(k => k.Data.Status == "Aktif");
        JumlahBerisiko = _semua.Count(k => k.Data.Status == "Berisiko");
        JumlahNonaktif = _semua.Count(k => k.Data.Status == "Nonaktif");

        if (!_opsiSiap)
        {
            _opsiSiap = true;
            KelompokOpsi = new[] { new Opsi<int?>(null, "Semua kelompok") }
                .Concat(snap.Kelompok.Select(g => new Opsi<int?>(g.Id, g.Label))).ToList();
            AngkatanOpsi = new[] { new Opsi<int?>(null, "Semua angkatan") }
                .Concat(_semua.Select(k => k.Data.Angkatan).Distinct().OrderByDescending(a => a).Select(a => new Opsi<int?>(a, a.ToString()))).ToList();
            _kelompokDipilih = KelompokOpsi[0];
            _angkatanDipilih = AngkatanOpsi[0];
            OnPropertyChanged(nameof(KelompokDipilih));
            OnPropertyChanged(nameof(AngkatanDipilih));
        }
        Saring(resetHalaman: false);
    });

    private void Saring(bool resetHalaman)
    {
        IEnumerable<KaderBaris> q = _semua;
        var t = Cari.Trim();
        if (t.Length > 0)
            q = q.Where(k => k.Data.Nrp.Contains(t, StringComparison.OrdinalIgnoreCase) || k.Data.Nama.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (KelompokDipilih?.Nilai is int g) q = q.Where(k => k.Data.KelompokId == g);
        if (AngkatanDipilih?.Nilai is int a) q = q.Where(k => k.Data.Angkatan == a);
        if (StatusDipilih.Nilai is string s) q = q.Where(k => k.Data.Status == s);

        _tersaring = q.OrderBy(k => k.Data.KelompokNomor).ThenBy(k => k.Data.Nrp).ToList();
        TotalHalaman = Math.Max(1, (int)Math.Ceiling(_tersaring.Count / (double)UkuranHalaman));
        if (resetHalaman || HalamanKe > TotalHalaman)
        {
            if (HalamanKe != 1) { HalamanKe = 1; return; } // OnHalamanKeChanged memanggil TerapkanHalaman
        }
        TerapkanHalaman();
    }

    private void TerapkanHalaman()
    {
        var awal = (HalamanKe - 1) * UkuranHalaman;
        Tampil = _tersaring.Skip(awal).Take(UkuranHalaman).ToList();
        InfoHalaman = _tersaring.Count == 0
            ? "Tidak ada kader yang cocok dengan filter"
            : $"Menampilkan {awal + 1}–{awal + Tampil.Count} dari {_tersaring.Count} kader";
    }

    private bool BisaSebelumnya() => HalamanKe > 1;
    private bool BisaBerikutnya() => HalamanKe < TotalHalaman;

    [RelayCommand(CanExecute = nameof(BisaSebelumnya))]
    private void Sebelumnya() => HalamanKe--;

    [RelayCommand(CanExecute = nameof(BisaBerikutnya))]
    private void Berikutnya() => HalamanKe++;

    [RelayCommand]
    private Task LihatAsync(KaderBaris? baris) =>
        baris is null ? Task.CompletedTask : Ui.NavigasiAsync("kader-detail", new BukaDetailKader(baris.Data.Id));

    [RelayCommand]
    private async Task TambahAsync()
    {
        if (!IsAdmin) return;
        var vm = new KaderFormViewModel(_kader, _kelompok, null, _clock.Now.Year);
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Kader berhasil ditambahkan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task UbahAsync(KaderBaris? baris)
    {
        if (!IsAdmin || baris is null) return;
        var vm = new KaderFormViewModel(_kader, _kelompok, baris.Data, _clock.Now.Year);
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Perubahan data kader disimpan.");
            await MuatAsync();
        }
    }

    [RelayCommand]
    private async Task HapusAsync(KaderBaris? baris)
    {
        if (!IsAdmin || baris is null) return;
        var k = baris.Data;
        var pesan = k.Rekap is { JumlahKumpul: > 0 } || !k.Aktif
            ? $"{k.Nama} sudah memiliki riwayat pengumpulan, sehingga kader akan dinonaktifkan (bukan dihapus permanen)."
            : $"Data {k.Nama} ({k.Nrp}) akan dihapus.";
        if (!Ui.Konfirmasi("Hapus kader?", pesan, "Hapus", bahaya: true)) return;
        await JalankanAsync(async () =>
        {
            var hasil = await _kader.HapusAsync(k.Id);
            Ui.Toast(hasil);
        });
        await MuatAsync();
    }

    [RelayCommand]
    private Task EksporAsync() => JalankanAsync(async () =>
    {
        var csv = await _ekspor.RekapNilaiCsvAsync();
        Ui.SimpanCsv($"rekap-kader-{_clock.Now:yyyyMMdd}.csv", csv);
    });
}

/// <summary>S-04 Formulir tambah/ubah kader dengan validasi per kolom (FR-15).</summary>
public sealed partial class KaderFormViewModel : DialogViewModel
{
    private readonly IKaderService _svc;
    private readonly int? _id;
    private bool _sudahDicoba;

    public KaderFormViewModel(IKaderService svc, IReadOnlyList<KelompokRingkas> kelompok, KaderRingkas? data, int tahunIni)
    {
        _svc = svc;
        _id = data?.Id;
        KelompokOpsi = kelompok.Select(g => new Opsi<int>(g.Id, g.Label)).ToList();
        var tahun = Enumerable.Range(tahunIni - 6, 8).Reverse().ToList();
        if (data is not null && !tahun.Contains(data.Angkatan)) tahun.Add(data.Angkatan);
        AngkatanOpsi = tahun.Select(t => new Opsi<int>(t, t.ToString())).ToList();

        _nrp = data?.Nrp ?? "";
        _nama = data?.Nama ?? "";
        _kontak = data?.Kontak ?? "";
        _aktif = data?.Aktif ?? true;
        _angkatanDipilih = AngkatanOpsi.FirstOrDefault(o => o.Nilai == (data?.Angkatan ?? tahunIni - 1)) ?? AngkatanOpsi[0];
        _kelompokDipilih = data is null ? null : KelompokOpsi.FirstOrDefault(o => o.Nilai == data.KelompokId);
    }

    public bool IsBaru => _id is null;
    public override string Judul => IsBaru ? "Tambah Kader" : "Ubah Data Kader";
    public override string Subjudul => "Lengkapi data kader. Kolom bertanda * wajib diisi.";
    public string TombolSimpan => IsBaru ? "Simpan Kader" : "Simpan Perubahan";

    public IReadOnlyList<Opsi<int>> KelompokOpsi { get; }
    public IReadOnlyList<Opsi<int>> AngkatanOpsi { get; }

    [ObservableProperty] private string _nrp;
    [ObservableProperty] private string _nama;
    [ObservableProperty] private string _kontak;
    [ObservableProperty] private bool _aktif;
    [ObservableProperty] private Opsi<int>? _angkatanDipilih;
    [ObservableProperty] private Opsi<int>? _kelompokDipilih;

    [ObservableProperty] private string? _nrpGalat;
    [ObservableProperty] private string? _namaGalat;
    [ObservableProperty] private string? _kelompokGalat;
    [ObservableProperty] private string? _kontakGalat;

    partial void OnNrpChanged(string value)
    {
        if (_sudahDicoba || value.Length >= 10) NrpGalat = KaderService.ValidasiNrp(value);
        else NrpGalat = null;
    }

    partial void OnNamaChanged(string value)
    {
        if (_sudahDicoba) NamaGalat = string.IsNullOrWhiteSpace(value) ? "Nama lengkap wajib diisi." : null;
    }

    partial void OnKelompokDipilihChanged(Opsi<int>? value) => KelompokGalat = null;

    private bool Validasi()
    {
        NrpGalat = KaderService.ValidasiNrp(Nrp);
        NamaGalat = string.IsNullOrWhiteSpace(Nama) ? "Nama lengkap wajib diisi." : null;
        KelompokGalat = KelompokDipilih is null ? "Kelompok wajib dipilih." : null;
        KontakGalat = Kontak.Length > 50 ? "Kontak maksimal 50 karakter." : null;
        return NrpGalat is null && NamaGalat is null && KelompokGalat is null && KontakGalat is null;
    }

    [RelayCommand]
    private async Task SimpanAsync()
    {
        _sudahDicoba = true;
        GalatUmum = null;
        if (!Validasi()) return;

        SedangMenyimpan = true;
        try
        {
            await _svc.SimpanAsync(new KaderInput(_id, Nrp, Nama, AngkatanDipilih?.Nilai ?? DateTime.Now.Year, Kontak, KelompokDipilih!.Nilai, Aktif));
            Selesai(true);
        }
        catch (ValidasiException ex)
        {
            switch (ex.Kolom)
            {
                case nameof(KaderInput.Nrp): NrpGalat = ex.Message; break;
                case nameof(KaderInput.Nama): NamaGalat = ex.Message; break;
                case nameof(KaderInput.KelompokId): KelompokGalat = ex.Message; break;
                case nameof(KaderInput.Kontak): KontakGalat = ex.Message; break;
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

public sealed record JejakItem(string Judul, string Keterangan, string StatusTeks, BadgeKind StatusKind, string NilaiTeks, bool NilaiBahaya, string? Tautan);

/// <summary>S-05 Detail Kader: profil, ringkasan, grafik tren, dan linimasa jejak penugasan (FR-17).</summary>
public sealed partial class KaderDetailViewModel : HalamanViewModel, IMenerimaParameter
{
    private readonly IKaderService _kader;
    private readonly IRekapService _rekap;
    private readonly IClock _clock;
    private BukaDetailKader? _param;
    private KaderRingkas? _data;

    public KaderDetailViewModel(IUiService ui, IKaderService kader, IRekapService rekap, ISessionContext sesi, IClock clock) : base(ui)
    {
        _kader = kader;
        _rekap = rekap;
        _clock = clock;
        IsAdmin = sesi.Wajib().IsAdmin;
    }

    public override string Judul => "Detail Kader";
    public override string Breadcrumb => $"JEJAK / Kader / {Nama}";
    public override string KunciMenu => _param?.Asal == "risiko" ? "risiko" : "kader";
    public bool IsAdmin { get; }
    public string TeksKembali => _param?.Asal switch
    {
        "risiko" => "Kembali ke kader berisiko",
        "dashboard" => "Kembali ke dashboard",
        _ => "Kembali ke daftar kader"
    };

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Breadcrumb))] private string _nama = "";
    [ObservableProperty] private string _nrpAngkatan = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private BadgeKind _statusKind;
    [ObservableProperty] private string _ketuntasanTeks = "—";
    [ObservableProperty] private bool _ketuntasanBahaya;
    [ObservableProperty] private string _nilaiTeks = "—";
    [ObservableProperty] private bool _nilaiBahaya;
    [ObservableProperty] private string _terlambatTeks = "0";
    [ObservableProperty] private string _kelompok = "";
    [ObservableProperty] private string _pemandu = "";
    [ObservableProperty] private string _kontak = "";
    [ObservableProperty] private string _terdaftar = "";
    [ObservableProperty] private IReadOnlyList<string> _alasan = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<TitikGrafik> _grafik = Array.Empty<TitikGrafik>();
    [ObservableProperty] private IReadOnlyList<TitikGrafik> _pembanding = Array.Empty<TitikGrafik>();
    [ObservableProperty] private IReadOnlyList<JejakItem> _jejak = Array.Empty<JejakItem>();
    [ObservableProperty] private string _subJejak = "";
    [ObservableProperty] private string _labelSeri = "Kader";

    public void Terima(object parameter)
    {
        _param = parameter switch
        {
            BukaDetailKader b => b,
            int id => new BukaDetailKader(id),
            _ => _param
        };
    }

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        if (_param is null) return;
        var d = await _kader.DetailAsync(_param.Id);
        var k = d.Kader;
        _data = k;

        Nama = k.Nama;
        LabelSeri = k.Nama.Split(' ')[0];
        NrpAngkatan = $"{k.Nrp} · Angkatan {k.Angkatan}";
        Status = k.Status;
        StatusKind = Fmt.KindStatusKader(k.Status);
        KetuntasanTeks = Fmt.Persen(k.Rekap?.Ketuntasan);
        KetuntasanBahaya = k.Rekap?.Ketuntasan < d.Ambang.MinKetuntasan;
        NilaiTeks = Fmt.Nilai(k.Rekap?.NilaiAkhir);
        NilaiBahaya = k.Rekap?.NilaiAkhir < d.Ambang.MinNilai;
        TerlambatTeks = (k.Rekap?.JumlahTerlambat ?? 0).ToString();
        Kelompok = k.KelompokLabel;
        Pemandu = k.PemanduNama ?? "—";
        Kontak = string.IsNullOrWhiteSpace(k.Kontak) ? "—" : k.Kontak;
        Terdaftar = Fmt.Tanggal(k.DibuatPada);
        Alasan = k.Rekap is null ? Array.Empty<string>() : k.Kriteria.Select(c => AturanBisnis.JelaskanKriteria(c, k.Rekap, d.Ambang)).ToList();

        var jatuhTempo = d.Jejak.Where(j => j.Penugasan.JatuhTempo).OrderBy(j => j.Penugasan.TanggalMulai).ToList();
        Grafik = jatuhTempo.Select(j => new TitikGrafik(j.Penugasan.Kode,
            j.Status == StatusPengumpulan.BelumMengumpulkan ? 0 : j.Nilai,
            j.Status == StatusPengumpulan.BelumMengumpulkan)).ToList();
        Pembanding = jatuhTempo.Select(j => new TitikGrafik(j.Penugasan.Kode, j.RataKelompok)).ToList();

        var total = (await _rekap.AmbilAsync()).Penugasan.Count;
        SubJejak = $"{jatuhTempo.Count} dari {total} penugasan telah jatuh tempo";
        Jejak = d.Jejak.Select(j =>
        {
            var p = j.Penugasan;
            string ket = j.WaktuKumpul is DateTime w
                ? $"Dikumpulkan {Fmt.TanggalJamPendek(w)} · bobot {p.Bobot}"
                : $"Tenggat {Fmt.TanggalJamPendek(p.Tenggat)} · bobot {p.Bobot}";
            var belumTempo = !p.JatuhTempo && j.Status == StatusPengumpulan.BelumMengumpulkan;
            string statusTeks = belumTempo ? "Berjalan" : Fmt.StatusPengumpulan(j.Status);
            var kind = belumTempo ? BadgeKind.Info : Fmt.KindPengumpulan(j.Status);
            string nilai = j.Status == StatusPengumpulan.BelumMengumpulkan ? (p.JatuhTempo ? "0" : "—") : Fmt.NilaiBulat(j.Nilai);
            return new JejakItem($"{p.Kode} · {p.Judul}", ket, statusTeks, kind, nilai, p.JatuhTempo && j.Status == StatusPengumpulan.BelumMengumpulkan,
                j.Tautan);
        }).ToList();
    });

    [RelayCommand]
    private void BukaTautan(string? tautan) => Ui.BukaTautan(tautan); // FR-39

    [RelayCommand]
    private Task KembaliAsync() => Ui.NavigasiAsync(_param?.Asal ?? "kader");

    [RelayCommand]
    private async Task UbahAsync()
    {
        if (!IsAdmin || _data is null) return;
        var snap = await _rekap.AmbilAsync();
        var vm = new KaderFormViewModel(_kader, snap.Kelompok, _data, _clock.Now.Year);
        if (Ui.TampilkanDialog(vm))
        {
            Ui.Toast("Perubahan data kader disimpan.");
            await MuatAsync();
        }
    }
}
