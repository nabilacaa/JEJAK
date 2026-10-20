using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jejak.App.Controls;
using Jejak.App.Services;
using Jejak.Core.Rules;

namespace Jejak.App.ViewModels;

/// <summary>Dasar semua halaman yang ditampilkan di area konten jendela utama.</summary>
public abstract partial class HalamanViewModel : ObservableObject
{
    protected HalamanViewModel(IUiService ui) => Ui = ui;

    protected IUiService Ui { get; }

    public abstract string Judul { get; }
    public virtual string Breadcrumb => $"JEJAK / {Judul}";

    /// <summary>Kunci menu sidebar yang disorot saat halaman ini aktif.</summary>
    public abstract string KunciMenu { get; }

    [ObservableProperty]
    private bool _sedangMemuat;

    public abstract Task MuatAsync();

    /// <summary>Menjalankan aksi dengan indikator memuat dan penanganan galat terpusat (NFR-04).</summary>
    protected async Task JalankanAsync(Func<Task> aksi)
    {
        try
        {
            SedangMemuat = true;
            await aksi();
        }
        catch (Exception ex)
        {
            Ui.TanganiGalat(ex);
        }
        finally
        {
            SedangMemuat = false;
        }
    }
}

/// <summary>Halaman yang dapat dibuka dengan parameter (mis. id kader).</summary>
public interface IMenerimaParameter
{
    void Terima(object parameter);
}

/// <summary>Halaman yang perlu konfirmasi sebelum ditinggalkan (perubahan belum disimpan).</summary>
public interface IKonfirmasiKeluar
{
    bool BolehKeluar();
}

/// <summary>Dasar semua dialog formulir.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    public event Action<bool>? Tutup;

    public abstract string Judul { get; }
    public virtual string? Subjudul => null;
    public virtual double Lebar => 560;

    [ObservableProperty]
    private string? _galatUmum;

    [ObservableProperty]
    private bool _sedangMenyimpan;

    protected void Selesai(bool hasil) => Tutup?.Invoke(hasil);

    [RelayCommand]
    private void Batal() => Selesai(false);
}

public sealed partial class KonfirmasiViewModel : DialogViewModel
{
    public KonfirmasiViewModel(string judul, string pesan, string tombolYa, bool bahaya)
    {
        Judul = judul;
        Pesan = pesan;
        TombolYa = tombolYa;
        Bahaya = bahaya;
    }

    public override string Judul { get; }
    public override double Lebar => 440;
    public string Pesan { get; }
    public string TombolYa { get; }
    public bool Bahaya { get; }

    [RelayCommand]
    private void Ya() => Selesai(true);
}

/// <summary>Pilihan ComboBox. Nilai null berarti "semua"/tidak dipilih.</summary>
public sealed record Opsi<T>(T Nilai, string Label)
{
    public override string ToString() => Label;
}

public sealed record KriteriaChip(string Kode, BadgeKind Kind);

/// <summary>Pemformatan angka dan waktu berlokal Indonesia.</summary>
public static class Fmt
{
    public static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");

    public static string Persen(double? v, int desimal = 0) =>
        v is null ? "—" : v.Value.ToString(desimal == 0 ? "0" : "0.0", Id) + "%";

    public static string Nilai(double? v) => v is null ? "—" : v.Value.ToString("0.0", Id);

    public static string NilaiBulat(double? v) => v is null ? "—" : v.Value.ToString("0.#", Id);

    public static string Tanggal(DateTime t) => t.ToString("d MMM yyyy", Id);

    public static string TanggalPendek(DateTime t) => t.ToString("d MMM", Id);

    public static string TanggalJam(DateTime t) => t.ToString("d MMM yyyy, HH.mm", Id);

    public static string TanggalJamPendek(DateTime t) => t.ToString("d MMM, HH.mm", Id);

    public static string Relatif(DateTime t, DateTime sekarang)
    {
        var d = sekarang - t;
        if (d.TotalSeconds < 0) return TanggalJamPendek(t);
        if (d.TotalMinutes < 1) return "Baru saja";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} menit lalu";
        if (d.TotalHours < 24 && t.Date == sekarang.Date) return $"{(int)d.TotalHours} jam lalu";
        if (t.Date == sekarang.Date.AddDays(-1)) return $"Kemarin, {t:HH.mm}";
        if (d.TotalDays < 7) return $"{(int)Math.Ceiling(d.TotalDays)} hari lalu";
        return TanggalJamPendek(t);
    }

    public static string StatusPengumpulan(StatusPengumpulan s) => s switch
    {
        Core.Rules.StatusPengumpulan.TepatWaktu => "Tepat Waktu",
        Core.Rules.StatusPengumpulan.Terlambat => "Terlambat",
        _ => "Belum Mengumpulkan"
    };

    public static BadgeKind KindPengumpulan(StatusPengumpulan s) => s switch
    {
        Core.Rules.StatusPengumpulan.TepatWaktu => BadgeKind.Success,
        Core.Rules.StatusPengumpulan.Terlambat => BadgeKind.Warning,
        _ => BadgeKind.Danger
    };

    public static string StatusPenugasan(StatusPenugasan s) => s switch
    {
        Core.Rules.StatusPenugasan.BelumDimulai => "Belum Dimulai",
        Core.Rules.StatusPenugasan.Berjalan => "Berjalan",
        _ => "Selesai"
    };

    public static BadgeKind KindPenugasan(StatusPenugasan s) => s switch
    {
        Core.Rules.StatusPenugasan.BelumDimulai => BadgeKind.Warning,
        Core.Rules.StatusPenugasan.Berjalan => BadgeKind.Info,
        _ => BadgeKind.Neutral
    };

    public static BadgeKind KindStatusKader(string status) => status switch
    {
        "Aktif" => BadgeKind.Success,
        "Berisiko" => BadgeKind.Danger,
        _ => BadgeKind.Neutral
    };

    public static IReadOnlyList<KriteriaChip> Chip(IEnumerable<string> kriteria) =>
        kriteria.Select(k => new KriteriaChip(k, k == AturanBisnis.R1 ? BadgeKind.Danger : BadgeKind.Warning)).ToList();

    /// <summary>Membaca angka dengan koma atau titik sebagai pemisah desimal.</summary>
    public static bool CobaAngka(string? teks, out double hasil)
    {
        hasil = 0;
        var t = teks?.Trim().Replace(',', '.');
        return !string.IsNullOrEmpty(t) &&
               double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out hasil) &&
               !double.IsNaN(hasil) && !double.IsInfinity(hasil);
    }

    public static bool CobaBulat(string? teks, out int hasil) =>
        int.TryParse(teks?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hasil);

    private static readonly string[] FormatWaktu =
    {
        "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm", "dd/MM/yyyy HH.mm", "d/M/yyyy HH.mm", "dd/MM/yyyy H:mm", "d/M/yyyy H:mm"
    };

    public const string PolaWaktu = "dd/MM/yyyy HH:mm";

    public static bool CobaWaktu(string? teks, out DateTime hasil) =>
        DateTime.TryParseExact(teks?.Trim(), FormatWaktu, CultureInfo.InvariantCulture, DateTimeStyles.None, out hasil);

    public static string Waktu(DateTime t) => t.ToString(PolaWaktu, CultureInfo.InvariantCulture);

    public static bool CobaJam(string? teks, out TimeSpan hasil)
    {
        hasil = default;
        var t = teks?.Trim().Replace('.', ':');
        return TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out hasil) && hasil < TimeSpan.FromDays(1);
    }

    public static string Sapaan(DateTime t) => t.Hour switch
    {
        < 11 => "Selamat pagi",
        < 15 => "Selamat siang",
        < 18 => "Selamat sore",
        _ => "Selamat malam"
    };
}
