using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Rules;
using Jejak.Core.Services;

namespace Jejak.App.ViewModels;

/// <summary>Satu baris lembar penilaian yang dapat diedit langsung di tabel.</summary>
public sealed partial class BarisPenilaianViewModel : ObservableObject
{
    private readonly DateTime _tenggat;
    private readonly IClock _clock;
    private readonly bool _awalTerkumpul;
    private readonly string _awalWaktu;
    private readonly string _awalNilai;
    private readonly string _awalCatatan;
    private readonly string _awalTautan;

    public BarisPenilaianViewModel(BarisPenilaian b, DateTime tenggat, IClock clock)
    {
        _tenggat = tenggat;
        _clock = clock;
        KaderId = b.KaderId;
        Nrp = b.Nrp;
        Nama = b.Nama;
        Penilai = b.PenilaiNama;

        _terkumpul = _awalTerkumpul = b.WaktuKumpul.HasValue;
        _waktuTeks = _awalWaktu = b.WaktuKumpul is DateTime w ? Fmt.Waktu(w) : "";
        _nilaiTeks = _awalNilai = b.Nilai is double n ? n.ToString("0.##", Fmt.Id) : "";
        _catatan = _awalCatatan = b.Catatan ?? "";
        _tautanTeks = _awalTautan = b.Tautan ?? "";
    }

    public event EventHandler? Berubah;

    public int KaderId { get; }
    public string Nrp { get; }
    public string Nama { get; }
    public string? Penilai { get; }
    public string PenilaiTeks => Penilai is null ? "" : $"Dinilai oleh {Penilai}";

    [ObservableProperty] private bool _terkumpul;
    [ObservableProperty] private string _waktuTeks;
    [ObservableProperty] private string _nilaiTeks;
    [ObservableProperty] private string _catatan;
    [ObservableProperty] private string _tautanTeks;
    [ObservableProperty] private bool _dipilih;

    public DateTime? Waktu => Terkumpul && Fmt.CobaWaktu(WaktuTeks, out var w) ? w : null;

    public double? Nilai => Terkumpul && Fmt.CobaAngka(NilaiTeks, out var n) ? n : null;

    public bool WaktuGalat => Terkumpul && !Fmt.CobaWaktu(WaktuTeks, out _);

    public bool NilaiGalat => Terkumpul && NilaiTeks.Trim().Length > 0 &&
                              (!Fmt.CobaAngka(NilaiTeks, out var n) || n < 0 || n > 100);

    /// <summary>FR-39: tautan opsional, tetapi bila diisi harus URL http/https yang valid.</summary>
    public bool TautanGalat => Terkumpul && !AturanBisnis.TautanValid(TautanTeks);

    public bool TautanBisaDibuka => Terkumpul && TautanTeks.Trim().Length > 0 && !TautanGalat;

    public string TautanTooltip => TautanGalat
        ? "Tautan harus diawali http:// atau https:// dan tanpa spasi."
        : TautanTeks.Trim().Length > 0 ? TautanTeks.Trim() : "Tempel tautan hasil tugas (mis. Google Drive)";

    public StatusPengumpulan Status => AturanBisnis.StatusPengumpulan(_tenggat, Terkumpul ? Waktu ?? _clock.Now : null);
    public string StatusTeks => Fmt.StatusPengumpulan(Status);
    public BadgeKind StatusKind => Fmt.KindPengumpulan(Status);
    public bool BelumDinilai => Terkumpul && NilaiTeks.Trim().Length == 0;

    public bool Diubah =>
        Terkumpul != _awalTerkumpul ||
        (Terkumpul && (WaktuTeks.Trim() != _awalWaktu || NormalNilai(NilaiTeks) != NormalNilai(_awalNilai) || Catatan.Trim() != _awalCatatan.Trim() ||
                       TautanTeks.Trim() != _awalTautan.Trim()));

    private static string NormalNilai(string s) => Fmt.CobaAngka(s, out var n) ? n.ToString("0.##", Fmt.Id) : s.Trim();

    partial void OnTerkumpulChanged(bool value)
    {
        if (value)
        {
            if (WaktuTeks.Trim().Length == 0) WaktuTeks = Fmt.Waktu(_clock.Now);
            Dipilih = false;
        }
        else
        {
            WaktuTeks = "";
            NilaiTeks = "";
            Catatan = "";
            TautanTeks = "";
        }
        Kabari();
    }

    partial void OnWaktuTeksChanged(string value) => Kabari();
    partial void OnNilaiTeksChanged(string value) => Kabari();
    partial void OnCatatanChanged(string value) => Kabari();
    partial void OnTautanTeksChanged(string value) => Kabari();
    partial void OnDipilihChanged(bool value) => Berubah?.Invoke(this, EventArgs.Empty);

    private void Kabari()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusTeks));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(WaktuGalat));
        OnPropertyChanged(nameof(NilaiGalat));
        OnPropertyChanged(nameof(TautanGalat));
        OnPropertyChanged(nameof(TautanBisaDibuka));
        OnPropertyChanged(nameof(TautanTooltip));
        OnPropertyChanged(nameof(BelumDinilai));
        OnPropertyChanged(nameof(Diubah));
        Berubah?.Invoke(this, EventArgs.Empty);
    }

    public PerubahanPenilaian KePerubahan() =>
        new(KaderId, Terkumpul ? Waktu : null, Terkumpul ? TautanTeks : null, Terkumpul ? Nilai : null, Terkumpul ? Catatan : null);
}

/// <summary>S-08 Pengumpulan dan Penilaian (FR-27 s.d. FR-31).</summary>
public sealed partial class PenilaianViewModel : HalamanViewModel, IMenerimaParameter, IKonfirmasiKeluar
{
    private readonly IRekapService _rekap;
    private readonly IPenilaianService _svc;
    private readonly IClock _clock;
    private readonly SesiPengguna _sesi;
    private int? _penugasanAwal;
    private bool _abaikanPilihan;

    public PenilaianViewModel(IUiService ui, IRekapService rekap, IPenilaianService svc, ISessionContext sesi, IClock clock) : base(ui)
    {
        _rekap = rekap;
        _svc = svc;
        _clock = clock;
        _sesi = sesi.Wajib();
    }

    public override string Judul => "Pengumpulan & Penilaian";
    public override string KunciMenu => "penilaian";
    public bool IsAdmin => _sesi.IsAdmin;

    public ObservableCollection<BarisPenilaianViewModel> Baris { get; } = new();

    [ObservableProperty] private IReadOnlyList<Opsi<int>> _penugasanOpsi = Array.Empty<Opsi<int>>();
    [ObservableProperty] private Opsi<int>? _penugasanDipilih;
    [ObservableProperty] private IReadOnlyList<Opsi<int>> _kelompokOpsi = Array.Empty<Opsi<int>>();
    [ObservableProperty] private Opsi<int>? _kelompokDipilih;

    [ObservableProperty] private string _infoTenggat = "—";
    [ObservableProperty] private string _infoBobot = "—";
    [ObservableProperty] private string _infoKategori = "—";
    [ObservableProperty] private string _infoStatus = "—";
    [ObservableProperty] private bool _infoJatuhTempo;
    [ObservableProperty] private string _pesanKosong = "";

    [ObservableProperty] private int _jumlahTepat;
    [ObservableProperty] private int _jumlahTerlambat;
    [ObservableProperty] private int _jumlahBelum;
    [ObservableProperty] private int _jumlahBelumDinilai;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TandaiTerkumpulCommand))]
    private int _jumlahDipilih;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimpanCommand), nameof(BatalkanCommand))]
    private int _jumlahPerubahan;

    public string TeksTandai => JumlahDipilih > 0 ? $"Tandai Terkumpul ({JumlahDipilih})" : "Tandai Terkumpul";

    public string InfoSimpan => JumlahPerubahan == 0
        ? "Status Tepat Waktu/Terlambat ditentukan otomatis. Klik ikon ↗ untuk membuka tautan tugas di browser."
        : $"{JumlahPerubahan} baris diubah dan belum disimpan.";

    partial void OnJumlahDipilihChanged(int value) => OnPropertyChanged(nameof(TeksTandai));
    partial void OnJumlahPerubahanChanged(int value) => OnPropertyChanged(nameof(InfoSimpan));

    public void Terima(object parameter)
    {
        if (parameter is int id) _penugasanAwal = id;
    }

    public bool BolehKeluar() =>
        JumlahPerubahan == 0 ||
        Ui.Konfirmasi("Buang perubahan?", $"Ada {JumlahPerubahan} baris yang belum disimpan. Perubahan tersebut akan hilang.", "Buang perubahan", bahaya: true);

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        var tersedia = snap.Penugasan.Where(p => p.Status != StatusPenugasan.BelumDimulai)
            .OrderByDescending(p => p.TanggalMulai).ToList();
        _abaikanPilihan = true;
        PenugasanOpsi = tersedia.Select(p => new Opsi<int>(p.Id, p.Label)).ToList();
        KelompokOpsi = snap.Kelompok.Select(g => new Opsi<int>(g.Id, g.Label)).ToList();

        // Bawaan: penugasan jatuh tempo terbaru (paling relevan untuk dinilai).
        var awal = PenugasanOpsi.FirstOrDefault(o => o.Nilai == _penugasanAwal)
                   ?? PenugasanOpsi.FirstOrDefault(o => tersedia.First(p => p.Id == o.Nilai).JatuhTempo)
                   ?? PenugasanOpsi.FirstOrDefault();
        PenugasanDipilih = awal;
        KelompokDipilih = _sesi.KelompokId is int kid
            ? KelompokOpsi.FirstOrDefault(o => o.Nilai == kid) ?? KelompokOpsi.FirstOrDefault()
            : KelompokOpsi.FirstOrDefault();
        _abaikanPilihan = false;

        if (PenugasanDipilih is null)
            PesanKosong = "Belum ada penugasan yang dimulai.";
        else if (KelompokDipilih is null)
            PesanKosong = "Anda belum ditetapkan sebagai pemandu kelompok mana pun.";
        else
            await MuatLembarAsync();
    });

    partial void OnPenugasanDipilihChanged(Opsi<int>? oldValue, Opsi<int>? newValue) => GantiPilihan(() => PenugasanDipilih = oldValue);

    partial void OnKelompokDipilihChanged(Opsi<int>? oldValue, Opsi<int>? newValue) => GantiPilihan(() => KelompokDipilih = oldValue);

    private void GantiPilihan(Action kembalikan)
    {
        if (_abaikanPilihan) return;
        if (!BolehKeluar())
        {
            // Kembalikan pilihan setelah ComboBox selesai memperbarui dirinya.
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _abaikanPilihan = true;
                kembalikan();
                _abaikanPilihan = false;
            });
            return;
        }
        _ = JalankanAsync(MuatLembarAsync);
    }

    private async Task MuatLembarAsync()
    {
        if (PenugasanDipilih is null || KelompokDipilih is null) return;
        var lembar = await _svc.AmbilAsync(PenugasanDipilih.Nilai, KelompokDipilih.Nilai);
        var p = lembar.Penugasan;
        InfoTenggat = p.Tenggat.ToString("d MMM, HH.mm", Fmt.Id);
        InfoBobot = $"{p.Bobot}%";
        InfoKategori = p.Kategori.ToString();
        InfoStatus = p.JatuhTempo ? "Jatuh tempo" : Fmt.StatusPenugasan(p.Status);
        InfoJatuhTempo = p.JatuhTempo;

        foreach (var b in Baris) b.Berubah -= BarisBerubah;
        Baris.Clear();
        foreach (var b in lembar.Baris)
        {
            var vm = new BarisPenilaianViewModel(b, p.Tenggat, _clock);
            vm.Berubah += BarisBerubah;
            Baris.Add(vm);
        }
        PesanKosong = Baris.Count == 0 ? "Kelompok ini belum memiliki kader aktif." : "";
        HitungUlang();
    }

    private void BarisBerubah(object? sender, EventArgs e) => HitungUlang();

    private void HitungUlang()
    {
        JumlahTepat = Baris.Count(b => b.Status == StatusPengumpulan.TepatWaktu);
        JumlahTerlambat = Baris.Count(b => b.Status == StatusPengumpulan.Terlambat);
        JumlahBelum = Baris.Count(b => b.Status == StatusPengumpulan.BelumMengumpulkan);
        JumlahBelumDinilai = Baris.Count(b => b.BelumDinilai);
        JumlahDipilih = Baris.Count(b => b.Dipilih && !b.Terkumpul);
        JumlahPerubahan = Baris.Count(b => b.Diubah);
    }

    private bool BisaTandai() => JumlahDipilih > 0;

    [RelayCommand(CanExecute = nameof(BisaTandai))]
    private void TandaiTerkumpul()
    {
        foreach (var b in Baris.Where(b => b.Dipilih && !b.Terkumpul).ToList()) b.Terkumpul = true;
    }

    [RelayCommand]
    private void BukaTautan(BarisPenilaianViewModel? baris)
    {
        if (baris is { TautanBisaDibuka: true }) Ui.BukaTautan(baris.TautanTeks);
    }

    [RelayCommand]
    private void BatalKumpul(BarisPenilaianViewModel? baris)
    {
        if (baris is not null) baris.Terkumpul = false;
    }

    private bool AdaPerubahan() => JumlahPerubahan > 0;

    [RelayCommand(CanExecute = nameof(AdaPerubahan))]
    private async Task SimpanAsync()
    {
        var berubah = Baris.Where(b => b.Diubah).ToList();
        var salahWaktu = berubah.FirstOrDefault(b => b.WaktuGalat);
        if (salahWaktu is not null)
        {
            Ui.Toast($"Waktu kumpul {salahWaktu.Nama} tidak valid. Gunakan format {Fmt.PolaWaktu.ToUpperInvariant()}.", galat: true);
            return;
        }
        var salahNilai = berubah.FirstOrDefault(b => b.NilaiGalat);
        if (salahNilai is not null)
        {
            Ui.Toast($"Nilai {salahNilai.Nama} harus angka 0–100.", galat: true);
            return;
        }
        var salahTautan = berubah.FirstOrDefault(b => b.TautanGalat);
        if (salahTautan is not null)
        {
            Ui.Toast($"Tautan tugas {salahTautan.Nama} tidak valid. Gunakan alamat lengkap yang diawali https://.", galat: true);
            return;
        }
        if (PenugasanDipilih is null) return;

        await JalankanAsync(async () =>
        {
            var n = await _svc.SimpanAsync(PenugasanDipilih.Nilai, berubah.Select(b => b.KePerubahan()).ToList());
            Ui.Toast($"{n} perubahan disimpan.");
            await MuatLembarAsync();
            var snap = await _rekap.AmbilAsync();
            Ui.PerbaruiJumlahBerisiko(snap.KaderBerisiko.Count);
        });
    }

    [RelayCommand(CanExecute = nameof(AdaPerubahan))]
    private Task BatalkanAsync() => JalankanAsync(MuatLembarAsync);
}
