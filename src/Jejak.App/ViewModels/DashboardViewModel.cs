using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Models;
using Jejak.Core.Services;

namespace Jejak.App.ViewModels;

public sealed record DistribusiItem(string Label, int Jumlah, int Sisa, bool Bahaya);

public sealed record KaderBerisikoItem(int Id, string Nama, string Keterangan, IReadOnlyList<KriteriaChip> Kriteria);

public sealed record AktivitasItem(string Nama, string Keterangan, string Waktu, string Ikon);

/// <summary>S-02 Dashboard (FR-05 s.d. FR-10). Seluruh angka dihitung dari database.</summary>
public sealed partial class DashboardViewModel : HalamanViewModel
{
    private readonly IRekapService _rekap;
    private readonly ILogService _log;
    private readonly IEksporService _ekspor;
    private readonly IClock _clock;

    public DashboardViewModel(IUiService ui, IRekapService rekap, ILogService log, IEksporService ekspor, ISessionContext sesi, IClock clock)
        : base(ui)
    {
        _rekap = rekap;
        _log = log;
        _ekspor = ekspor;
        _clock = clock;
        var s = sesi.Wajib();
        IsAdmin = s.IsAdmin;
        Sapaan = $"{Fmt.Sapaan(clock.Now)}, {s.Nama.Split(' ')[0]}";
        Tanggal = $"Ringkasan progres kaderisasi per {clock.Now.ToString("dddd, d MMMM yyyy", Fmt.Id)}" +
                  (s.IsAdmin ? "" : $" · {s.KelompokNama ?? "belum memiliki kelompok"}");
    }

    public override string Judul => "Dashboard";
    public override string KunciMenu => "dashboard";
    public bool IsAdmin { get; }
    public string Sapaan { get; }
    public string Tanggal { get; }

    [ObservableProperty] private int _kaderAktif;
    [ObservableProperty] private string _subKader = "";
    [ObservableProperty] private int _jumlahKelompok;
    [ObservableProperty] private string _subKelompok = "";
    [ObservableProperty] private int _penugasanAktif;
    [ObservableProperty] private string _subPenugasan = "";
    [ObservableProperty] private string _rataKetuntasanTeks = "—";
    [ObservableProperty] private double _rataKetuntasan;

    [ObservableProperty] private string _judulGrafik = "";
    [ObservableProperty] private string _subGrafik = "";
    [ObservableProperty] private IReadOnlyList<TitikGrafik> _grafik = Array.Empty<TitikGrafik>();

    [ObservableProperty] private IReadOnlyList<DistribusiItem> _distribusi = Array.Empty<DistribusiItem>();
    [ObservableProperty] private string _subDistribusi = "";

    [ObservableProperty] private IReadOnlyList<KaderBerisikoItem> _berisiko = Array.Empty<KaderBerisikoItem>();
    [ObservableProperty] private string _subBerisiko = "";

    [ObservableProperty] private IReadOnlyList<AktivitasItem> _aktivitas = Array.Empty<AktivitasItem>();

    public override Task MuatAsync() => JalankanAsync(async () =>
    {
        var snap = await _rekap.AmbilAsync();
        var aktif = snap.KaderAktif.ToList();
        var sekarang = _clock.Now;

        KaderAktif = aktif.Count;
        SubKader = $"dari {snap.Kader.Count} terdaftar";
        JumlahKelompok = snap.Kelompok.Count;
        SubKelompok = snap.Kelompok.Count == 0 ? "" : $"± {Math.Round((double)aktif.Count / snap.Kelompok.Count)} kader per kelompok";
        PenugasanAktif = snap.Penugasan.Count;
        SubPenugasan = $"{snap.Penugasan.Count(p => p.JatuhTempo)} jatuh tempo · {snap.Penugasan.Count(p => p.Status == Core.Rules.StatusPenugasan.Berjalan)} berjalan";
        RataKetuntasan = snap.RataKetuntasan ?? 0;
        RataKetuntasanTeks = Fmt.Persen(snap.RataKetuntasan, 1);

        if (IsAdmin)
        {
            JudulGrafik = "Ketuntasan per Kelompok";
            SubGrafik = "Persentase penugasan jatuh tempo yang dikumpulkan";
            Grafik = snap.Kelompok.Select(k => new TitikGrafik($"K{k.Nomor:00}", k.RataKetuntasan, k.RataKetuntasan < 75)).ToList();
        }
        else
        {
            // Pemandu hanya memiliki satu kelompok, sehingga grafik dipecah per penugasan.
            JudulGrafik = "Pengumpulan per Penugasan";
            SubGrafik = "Persentase kader kelompok Anda yang sudah mengumpulkan";
            Grafik = snap.Penugasan.Where(p => p.Status != Core.Rules.StatusPenugasan.BelumDimulai)
                .Select(p => new TitikGrafik(p.Kode, p.PersenKumpul, p.JatuhTempo && p.PersenKumpul < 75)).ToList();
        }

        var nilai = aktif.Where(k => k.Rekap?.NilaiAkhir is not null).Select(k => k.Rekap!.NilaiAkhir!.Value).ToList();
        var rentang = new (string Label, double Bawah, double Atas, bool Bahaya)[]
        {
            ("90–100", 90, 101, false), ("80–89", 80, 90, false), ("70–79", 70, 80, false), ("60–69", 60, 70, false), ("0–59", 0, 60, true)
        };
        var jumlah = rentang.Select(r => nilai.Count(n => n >= r.Bawah && n < r.Atas)).ToList();
        var maks = Math.Max(1, jumlah.DefaultIfEmpty(0).Max());
        Distribusi = rentang.Select((r, i) => new DistribusiItem(r.Label, jumlah[i], maks - jumlah[i], r.Bahaya)).ToList();
        SubDistribusi = $"{nilai.Count} kader aktif yang sudah memiliki nilai";

        var risiko = snap.KaderBerisiko;
        Ui.PerbaruiJumlahBerisiko(risiko.Count);
        SubBerisiko = risiko.Count == 0 ? "Tidak ada kader yang memenuhi kriteria" : $"{risiko.Count} kader memenuhi kriteria";
        Berisiko = risiko.Take(4).Select(k => new KaderBerisikoItem(k.Id, k.Nama,
            $"Kelompok {k.KelompokNomor:00} · ketuntasan {Fmt.Persen(k.Rekap?.Ketuntasan)}", Fmt.Chip(k.Kriteria))).ToList();

        var log = await _log.TerbaruAsync(4);
        Aktivitas = log.Select(l => new AktivitasItem(l.PenggunaNama ?? "Sistem", Huruf(l.Keterangan), Fmt.Relatif(l.Waktu, sekarang), IkonAksi(l.Aksi))).ToList();
    });

    private static string Huruf(string s) => s.Length == 0 ? s : char.ToLower(s[0], Fmt.Id) + s[1..];

    internal static string IkonAksi(string aksi) => aksi switch
    {
        AksiLog.Nilai => "Icon.Check",
        AksiLog.Tambah => "Icon.Plus",
        AksiLog.Hapus => "Icon.Trash",
        AksiLog.Ubah => "Icon.Edit",
        _ => "Icon.Clock"
    };

    [RelayCommand]
    private Task LihatBerisikoAsync() => Ui.NavigasiAsync("risiko");

    [RelayCommand]
    private Task LihatLogAsync() => Ui.NavigasiAsync("log");

    [RelayCommand]
    private Task LihatKaderAsync(KaderBerisikoItem? item) =>
        item is null ? Task.CompletedTask : Ui.NavigasiAsync("kader-detail", new BukaDetailKader(item.Id, "dashboard"));

    [RelayCommand]
    private Task EksporAsync() => JalankanAsync(async () =>
    {
        var csv = await _ekspor.RekapNilaiCsvAsync();
        Ui.SimpanCsv($"rekap-nilai-{_clock.Now:yyyyMMdd}.csv", csv);
    });
}
