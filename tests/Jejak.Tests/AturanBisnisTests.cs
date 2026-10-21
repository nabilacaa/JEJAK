using Jejak.Core.Models;
using Jejak.Core.Rules;

namespace Jejak.Tests;

/// <summary>Unit test PRD Bagian 7 (BR-01 s.d. BR-05).</summary>
public class AturanBisnisTests
{
    private static readonly DateTime Sekarang = new(2026, 10, 19, 10, 0, 0);

    private static Penugasan Tugas(int id, int bobot, int hariTenggat) => new()
    {
        Id = id,
        Kode = $"P{id}",
        Bobot = bobot,
        TanggalMulai = Sekarang.AddDays(hariTenggat - 7),
        Tenggat = Sekarang.AddDays(hariTenggat)
    };

    private static Pengumpulan Kumpul(int penugasanId, DateTime waktu, double? nilai) =>
        new() { PenugasanId = penugasanId, WaktuKumpul = waktu, Nilai = nilai };

    [Fact]
    public void BR01_TepatPadaTenggat_DianggapTepatWaktu()
    {
        var tenggat = new DateTime(2026, 10, 17, 23, 59, 0);
        Assert.Equal(StatusPengumpulan.TepatWaktu, AturanBisnis.StatusPengumpulan(tenggat, tenggat));
        Assert.Equal(StatusPengumpulan.Terlambat, AturanBisnis.StatusPengumpulan(tenggat, tenggat.AddMinutes(1)));
        Assert.Equal(StatusPengumpulan.BelumMengumpulkan, AturanBisnis.StatusPengumpulan(tenggat, null));
    }

    [Fact]
    public void BR02_StatusPenugasanMengikutiWaktu()
    {
        Assert.Equal(StatusPenugasan.Selesai, AturanBisnis.StatusPenugasan(Tugas(1, 10, -1), Sekarang));
        Assert.Equal(StatusPenugasan.Berjalan, AturanBisnis.StatusPenugasan(Tugas(1, 10, 3), Sekarang));
        Assert.Equal(StatusPenugasan.BelumDimulai, AturanBisnis.StatusPenugasan(Tugas(1, 10, 10), Sekarang));
    }

    [Fact]
    public void BR03_TanpaPenugasanJatuhTempo_KetuntasanTidakDihitung()
    {
        var r = AturanBisnis.HitungRekap(new[] { Tugas(1, 10, 3) }, new Dictionary<int, Pengumpulan>(), Sekarang);
        Assert.Equal(0, r.JumlahJatuhTempo);
        Assert.Null(r.Ketuntasan);
        Assert.Null(r.NilaiAkhir);
    }

    [Fact]
    public void BR03_PenugasanBerjalanTidakMembuatKaderTertinggal()
    {
        var tugas = new[] { Tugas(1, 10, -2), Tugas(2, 10, 3) };
        var kumpul = new Dictionary<int, Pengumpulan> { [1] = Kumpul(1, Sekarang.AddDays(-3), 80) };
        var r = AturanBisnis.HitungRekap(tugas, kumpul, Sekarang);
        Assert.Equal(100, r.Ketuntasan);
        Assert.Equal(0, r.JumlahBelum);
    }

    [Fact]
    public void BR04_TidakDikumpulkanBernilaiNol_BelumDinilaiDiabaikan()
    {
        var tugas = new[] { Tugas(1, 20, -5), Tugas(2, 10, -3), Tugas(3, 30, -1) };
        var kumpul = new Dictionary<int, Pengumpulan>
        {
            [1] = Kumpul(1, Sekarang.AddDays(-6), 80),   // 80 × 20
            [3] = Kumpul(3, Sekarang.AddDays(-2), null)  // belum dinilai: diabaikan
        };
        // P2 tidak dikumpulkan: 0 × 10. Nilai akhir = 1600 / 30 = 53,3
        var r = AturanBisnis.HitungRekap(tugas, kumpul, Sekarang);
        Assert.Equal(53.3, r.NilaiAkhir);
        Assert.Equal(66.7, r.Ketuntasan);
        Assert.Equal(1, r.JumlahDinilai);
    }

    [Fact]
    public void BR04_PembulatanSetengahMenjauhiNol()
    {
        Assert.Equal(36.7, AturanBisnis.Bulatkan(36.65));
        Assert.Equal(92.8, AturanBisnis.RataRata(new double?[] { 92.75, null, 92.85 }));
    }

    [Fact]
    public void BR05_KriteriaR1R2R3()
    {
        var a = AmbangRisiko.Bawaan;
        var r = new RekapKader(5, 2, 3, 0, 2, 40, 35);
        Assert.Equal(new[] { "R1", "R2", "R3" }, AturanBisnis.KriteriaRisiko(r, a));

        var cukup = new RekapKader(5, 4, 1, 0, 4, 80, 75);
        Assert.Empty(AturanBisnis.KriteriaRisiko(cukup, a));
    }

    [Fact]
    public void BR05_R3HanyaBerlakuSetelahMinimalDuaPenugasanDinilai()
    {
        var a = AmbangRisiko.Bawaan;
        var satuNilai = new RekapKader(2, 2, 0, 0, 1, 100, 40);
        Assert.DoesNotContain("R3", AturanBisnis.KriteriaRisiko(satuNilai, a));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://drive.google.com/file/d/abc/view", true)]
    [InlineData("HTTP://example.org/x", true)]
    [InlineData("drive saya", false)]
    [InlineData("www.example.org", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("https://example.org/a b", false)]
    public void TautanValid_SesuaiFr39(string? tautan, bool valid) =>
        Assert.Equal(valid, AturanBisnis.TautanValid(tautan));

    [Fact]
    public void TautanValid_MenolakLebihDari500Karakter() =>
        Assert.False(AturanBisnis.TautanValid("https://example.org/" + new string('a', 481)));
}
