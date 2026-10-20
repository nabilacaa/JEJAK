using Jejak.Core.Models;

namespace Jejak.Core.Rules;

public enum StatusPengumpulan
{
    BelumMengumpulkan,
    TepatWaktu,
    Terlambat
}

public enum StatusPenugasan
{
    BelumDimulai,
    Berjalan,
    Selesai
}

/// <summary>Ambang kriteria kader berisiko (BR-05). Dapat diubah Admin.</summary>
public sealed record AmbangRisiko(int MinBelum, double MinKetuntasan, double MinNilai, int MinDinilai)
{
    public static AmbangRisiko Bawaan { get; } = new(2, 60, 60, 2);
}

/// <summary>Hasil BR-03 dan BR-04 untuk satu kader.</summary>
public sealed record RekapKader(
    int JumlahJatuhTempo,
    int JumlahKumpul,
    int JumlahBelum,
    int JumlahTerlambat,
    int JumlahDinilai,
    double? Ketuntasan,
    double? NilaiAkhir);

/// <summary>
/// Implementasi tunggal aturan bisnis PRD Bagian 7. Seluruh angka di dashboard,
/// tabel, dan ekspor dihitung dari kelas ini agar konsisten. Rumusnya identik
/// dengan view <c>v_rekap_kader</c> dan <c>v_kader_berisiko</c> di jejak.sql.
/// </summary>
public static class AturanBisnis
{
    public const string R1 = "R1";
    public const string R2 = "R2";
    public const string R3 = "R3";

    /// <summary>BR-02: status penugasan relatif terhadap waktu sekarang.</summary>
    public static StatusPenugasan StatusPenugasan(Penugasan p, DateTime sekarang)
    {
        if (sekarang < p.TanggalMulai) return Rules.StatusPenugasan.BelumDimulai;
        if (sekarang > p.Tenggat) return Rules.StatusPenugasan.Selesai;
        return Rules.StatusPenugasan.Berjalan;
    }

    /// <summary>BR-02: penugasan jatuh tempo bila tenggatnya telah lewat.</summary>
    public static bool JatuhTempo(Penugasan p, DateTime sekarang) => sekarang > p.Tenggat;

    /// <summary>BR-01: status pengumpulan ditentukan sistem dari waktu kumpul dan tenggat.</summary>
    public static StatusPengumpulan StatusPengumpulan(DateTime tenggat, DateTime? waktuKumpul)
    {
        if (waktuKumpul is null) return Rules.StatusPengumpulan.BelumMengumpulkan;
        return waktuKumpul.Value <= tenggat ? Rules.StatusPengumpulan.TepatWaktu : Rules.StatusPengumpulan.Terlambat;
    }

    /// <summary>
    /// BR-03 dan BR-04 untuk satu kader.
    /// </summary>
    /// <param name="penugasanAktif">Seluruh penugasan aktif.</param>
    /// <param name="pengumpulanKader">Pengumpulan milik kader ini, dikunci dengan id penugasan.</param>
    public static RekapKader HitungRekap(
        IEnumerable<Penugasan> penugasanAktif,
        IReadOnlyDictionary<int, Pengumpulan> pengumpulanKader,
        DateTime sekarang)
    {
        int jatuhTempo = 0, kumpul = 0, terlambat = 0, dinilai = 0;
        double pembilang = 0;
        int penyebut = 0;

        foreach (var p in penugasanAktif)
        {
            if (!JatuhTempo(p, sekarang)) continue;
            jatuhTempo++;

            if (!pengumpulanKader.TryGetValue(p.Id, out var s))
            {
                // Tidak dikumpulkan: dihitung bernilai 0.
                penyebut += p.Bobot;
                continue;
            }

            kumpul++;
            if (s.WaktuKumpul > p.Tenggat) terlambat++;
            if (s.Nilai is double nilai)
            {
                dinilai++;
                pembilang += nilai * p.Bobot;
                penyebut += p.Bobot;
            }
            // Dikumpulkan tetapi belum dinilai: diabaikan sampai nilainya diisi.
        }

        double? ketuntasan = jatuhTempo == 0 ? null : Bulatkan(100.0 * kumpul / jatuhTempo);
        double? nilaiAkhir = penyebut == 0 ? null : Bulatkan(pembilang / penyebut);
        return new RekapKader(jatuhTempo, kumpul, jatuhTempo - kumpul, terlambat, dinilai, ketuntasan, nilaiAkhir);
    }

    /// <summary>BR-05: kriteria risiko yang terpenuhi (kosong bila tidak berisiko).</summary>
    public static IReadOnlyList<string> KriteriaRisiko(RekapKader r, AmbangRisiko a)
    {
        var hasil = new List<string>(3);
        if (r.JumlahBelum >= a.MinBelum) hasil.Add(R1);
        if (r.Ketuntasan is double k && k < a.MinKetuntasan) hasil.Add(R2);
        if (r.NilaiAkhir is double n && n < a.MinNilai && r.JumlahDinilai >= a.MinDinilai) hasil.Add(R3);
        return hasil;
    }

    /// <summary>Penjelasan kriteria untuk ditampilkan ke pengguna.</summary>
    public static string JelaskanKriteria(string kode, RekapKader r, AmbangRisiko a) => kode switch
    {
        R1 => $"R1 · {r.JumlahBelum} penugasan belum dikumpulkan",
        R2 => $"R2 · Ketuntasan {Format(r.Ketuntasan, 0)}% (< {a.MinKetuntasan:0}%)",
        R3 => $"R3 · Nilai akhir {Format(r.NilaiAkhir, 1)} (< {a.MinNilai:0})",
        _ => kode
    };

    /// <summary>Rata-rata dari nilai yang tersedia, dibulatkan satu desimal (NULL diabaikan seperti AVG SQL).</summary>
    public static double? RataRata(IEnumerable<double?> nilai)
    {
        var ada = nilai.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return ada.Count == 0 ? null : Bulatkan(ada.Average());
    }

    /// <summary>Panjang maksimum tautan hasil tugas, sama dengan CHECK pada kolom pengumpulan.tautan.</summary>
    public const int PanjangMaksTautan = 500;

    /// <summary>
    /// FR-39: tautan bersifat opsional; bila diisi harus URL absolut http/https dengan host,
    /// tanpa spasi, dan maksimal 500 karakter. Aturan ini sedikit lebih ketat dari CHECK di database.
    /// </summary>
    public static bool TautanValid(string? tautan)
    {
        if (string.IsNullOrWhiteSpace(tautan)) return true;
        var s = tautan.Trim();
        if (s.Length > PanjangMaksTautan || s.Any(char.IsWhiteSpace)) return false;
        return Uri.TryCreate(s, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
               && u.Host.Length > 0;
    }

    /// <summary>Pembulatan satu desimal, setengah menjauhi nol (sama dengan ROUND di SQLite).</summary>
    public static double Bulatkan(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    private static string Format(double? v, int desimal) =>
        v is null ? "—" : v.Value.ToString(desimal == 0 ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("id-ID"));
}
