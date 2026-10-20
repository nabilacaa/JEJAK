using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Jejak.Data;
using Jejak.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jejak.Tests;

/// <summary>
/// Uji integrasi layanan terhadap database SQLite in-memory yang dibuat dari jejak.sql
/// (skema, trigger, view, dan data contoh yang sama dengan aplikasi).
/// </summary>
public sealed class LayananTests : IDisposable
{
    private const string Password = "jejak123";
    private readonly SqliteConnection _koneksi;
    private readonly Pabrik _pabrik;
    private readonly SessionContext _sesi = new();
    private readonly SystemClock _clock = new();
    private readonly BcryptPasswordHasher _hasher = new();

    public LayananTests()
    {
        _koneksi = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _koneksi.Open();
        DatabaseInitializer.JalankanSkrip(_koneksi);
        _pabrik = new Pabrik(new DbContextOptionsBuilder<JejakDbContext>().UseSqlite(_koneksi).Options);
    }

    public void Dispose() => _koneksi.Dispose();

    private sealed class Pabrik : IDbContextFactory<JejakDbContext>
    {
        private readonly DbContextOptions<JejakDbContext> _opsi;
        public Pabrik(DbContextOptions<JejakDbContext> opsi) => _opsi = opsi;
        public JejakDbContext CreateDbContext() => new(_opsi);
    }

    private AuthService Auth => new(_pabrik, _sesi, _hasher, _clock);

    private async Task<SesiPengguna> Login(string username)
    {
        var s = await Auth.LoginAsync(username, Password);
        Assert.NotNull(s);
        return s!;
    }

    private List<(string Nrp, string Kriteria)> RisikoDariView()
    {
        using var cmd = _koneksi.CreateCommand();
        cmd.CommandText = "SELECT nrp, kriteria FROM v_kader_berisiko ORDER BY nrp";
        using var r = cmd.ExecuteReader();
        var hasil = new List<(string, string)>();
        while (r.Read()) hasil.Add((r.GetString(0), r.GetString(1)));
        return hasil;
    }

    // ---------------- Autentikasi (FR-01, NFR-01, NFR-02)

    [Fact]
    public async Task Login_PasswordBenar_Berhasil()
    {
        var s = await Login("raka.pratama");
        Assert.True(s.IsAdmin);
        Assert.Equal(s, _sesi.Current);
    }

    [Theory]
    [InlineData("raka.pratama", "salah")]
    [InlineData("' OR '1'='1", "x")]           // AT-11: injeksi SQL tidak berpengaruh
    [InlineData("tidak.ada", "jejak123")]
    [InlineData("elsa.maharani", "jejak123")]  // akun nonaktif
    public async Task Login_Gagal_TanpaSesi(string username, string password)
    {
        Assert.Null(await Auth.LoginAsync(username, password));
        Assert.Null(_sesi.Current);
        await using var db = _pabrik.CreateDbContext();
        Assert.True(await db.LogAktivitas.AnyAsync(l => l.Aksi == AksiLog.LoginGagal));
    }

    // ---------------- Paritas rumus C# dengan view SQL

    [Fact]
    public async Task Rekap_SamaDenganViewSql()
    {
        await Login("raka.pratama");
        var snap = await new RekapService(_pabrik, _sesi, _clock).AmbilAsync();
        var dariLayanan = snap.KaderBerisiko.Select(k => (k.Nrp, string.Join(" ", k.Kriteria))).OrderBy(x => x.Nrp).ToList();
        Assert.Equal(RisikoDariView(), dariLayanan);
        Assert.Equal(120, snap.KaderAktif.Count());
        Assert.Equal(12, snap.Kelompok.Count);
    }

    // ---------------- RBAC (PRD 5.2)

    [Fact]
    public async Task Pemandu_HanyaMelihatKelompoknya()
    {
        var s = await Login("daffa.prakoso");
        Assert.False(s.IsAdmin);
        var snap = await new RekapService(_pabrik, _sesi, _clock).AmbilAsync();
        Assert.All(snap.Kader, k => Assert.Equal(s.KelompokId, k.KelompokId));
        Assert.Single(snap.Kelompok);
    }

    [Fact]
    public async Task Pemandu_TidakDapatMengubahDataMaster()
    {
        await Login("daffa.prakoso");
        var svc = new KaderService(_pabrik, _sesi, _clock);
        await Assert.ThrowsAsync<AksesDitolakException>(() =>
            svc.SimpanAsync(new KaderInput(null, "5025259999", "Uji", 2025, null, 1, true)));
    }

    [Fact]
    public async Task Pemandu_TidakDapatMenilaiKelompokLain()
    {
        await Login("alya.rahmadani"); // pemandu Kelompok 01
        await using var db = _pabrik.CreateDbContext();
        var kaderK04 = await db.Kader.FirstAsync(k => k.Nrp == "5025251035");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        await Assert.ThrowsAsync<AksesDitolakException>(() =>
            svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(kaderK04.Id, DateTime.Now.AddDays(-2), null, 90, null) }));
    }

    // ---------------- Penilaian (FR-28 s.d. FR-30)

    [Fact]
    public async Task Penilaian_MenyimpanPenilaiDanMencatatLog()
    {
        var s = await Login("daffa.prakoso");
        await using var db = _pabrik.CreateDbContext();
        var erlangga = await db.Kader.FirstAsync(k => k.Nrp == "5025251035");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var lama = await db.Pengumpulan.AsNoTracking().FirstAsync(x => x.KaderId == erlangga.Id && x.PenugasanId == p5.Id);
        Assert.Null(lama.Nilai);

        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        var n = await svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(erlangga.Id, lama.WaktuKumpul, lama.Tautan, 85, "Baik") });
        Assert.Equal(1, n);

        await using var db2 = _pabrik.CreateDbContext();
        var baru = await db2.Pengumpulan.FirstAsync(x => x.Id == lama.Id);
        Assert.Equal(85, baru.Nilai);
        Assert.Equal(s.Id, baru.DinilaiOleh);
        Assert.True(await db2.LogAktivitas.AnyAsync(l => l.Aksi == AksiLog.Nilai && l.PenggunaId == s.Id));

        // FR-30: perubahan nilai mencatat nilai lama dan baru.
        await svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(erlangga.Id, lama.WaktuKumpul, lama.Tautan, 70, "Baik") });
        await using var db3 = _pabrik.CreateDbContext();
        Assert.True(await db3.LogAktivitas.AnyAsync(l => l.Keterangan.Contains("85 → 70")));
    }

    [Fact]
    public async Task Penilaian_StatusTerlambatDihitungOtomatis()
    {
        await Login("daffa.prakoso");
        await using var db = _pabrik.CreateDbContext();
        var dimas = await db.Kader.FirstAsync(k => k.Nrp == "5025251034");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        await svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(dimas.Id, p5.Tenggat.AddHours(3), null, null, null) });

        var lembar = await svc.AmbilAsync(p5.Id, dimas.KelompokId);
        var baris = lembar.Baris.First(b => b.KaderId == dimas.Id);
        Assert.Equal(StatusPengumpulan.Terlambat, AturanBisnis.StatusPengumpulan(p5.Tenggat, baris.WaktuKumpul)); // AT-06
    }

    [Fact]
    public async Task Penilaian_NilaiTanpaPengumpulanDitolak()
    {
        await Login("raka.pratama");
        await using var db = _pabrik.CreateDbContext();
        var dimas = await db.Kader.FirstAsync(k => k.Nrp == "5025251034");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        await Assert.ThrowsAsync<ValidasiException>(() =>
            svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(dimas.Id, null, null, 80, null) }));
    }

    // ---------------- Tautan hasil tugas (FR-39)

    [Fact]
    public async Task Tautan_DisimpanDibacaUlangDanDicatat()
    {
        await Login("daffa.prakoso");
        await using var db = _pabrik.CreateDbContext();
        var erlangga = await db.Kader.FirstAsync(k => k.Nrp == "5025251035");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var lama = await db.Pengumpulan.AsNoTracking().FirstAsync(x => x.KaderId == erlangga.Id && x.PenugasanId == p5.Id);
        Assert.NotNull(lama.Tautan); // data contoh

        const string url = "https://drive.google.com/file/d/abc123/view?usp=sharing";
        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        var n = await svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(erlangga.Id, lama.WaktuKumpul, "  " + url + " ", null, null) });
        Assert.Equal(1, n);

        var lembar = await svc.AmbilAsync(p5.Id, erlangga.KelompokId);
        Assert.Equal(url, lembar.Baris.First(b => b.KaderId == erlangga.Id).Tautan); // dipangkas
        await using var db2 = _pabrik.CreateDbContext();
        Assert.True(await db2.LogAktivitas.AnyAsync(l => l.Keterangan.Contains("Mengganti tautan tugas P5")));

        // Mengosongkan tautan menghapusnya (opsional).
        await svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(erlangga.Id, lama.WaktuKumpul, "", null, null) });
        await using var db3 = _pabrik.CreateDbContext();
        Assert.Null((await db3.Pengumpulan.FirstAsync(x => x.Id == lama.Id)).Tautan);
    }

    [Theory]
    [InlineData("drive saya")]
    [InlineData("drive.google.com/file/d/abc")]
    [InlineData("ftp://example.org/tugas")]
    [InlineData("https://")]
    [InlineData("https://example.org/a b")]
    public async Task Tautan_TidakValidDitolak(string tautan)
    {
        await Login("daffa.prakoso");
        await using var db = _pabrik.CreateDbContext();
        var erlangga = await db.Kader.FirstAsync(k => k.Nrp == "5025251035");
        var p5 = await db.Penugasan.FirstAsync(p => p.Kode == "P5");
        var lama = await db.Pengumpulan.AsNoTracking().FirstAsync(x => x.KaderId == erlangga.Id && x.PenugasanId == p5.Id);
        var svc = new PenilaianService(_pabrik, _sesi, _clock);
        await Assert.ThrowsAsync<ValidasiException>(() =>
            svc.SimpanAsync(p5.Id, new[] { new PerubahanPenilaian(erlangga.Id, lama.WaktuKumpul, tautan, null, null) }));
        await using var db2 = _pabrik.CreateDbContext();
        Assert.Equal(lama.Tautan, (await db2.Pengumpulan.FirstAsync(x => x.Id == lama.Id)).Tautan);
    }

    [Fact]
    public void Tautan_DatabaseLamaDitingkatkanTanpaKehilanganData()
    {
        using var k = new SqliteConnection("Data Source=:memory:");
        k.Open();
        using (var cmd = k.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE pengumpulan (id INTEGER PRIMARY KEY, waktu_kumpul TEXT NOT NULL);" +
                              "INSERT INTO pengumpulan VALUES (1, '2026-10-01 10:00:00');";
            cmd.ExecuteNonQuery();
        }
        DatabaseInitializer.Tingkatkan(k);
        DatabaseInitializer.Tingkatkan(k); // aman diulang

        using var baca = k.CreateCommand();
        baca.CommandText = "SELECT COUNT(*) FROM pengumpulan WHERE tautan IS NULL";
        Assert.Equal(1L, baca.ExecuteScalar());
        using var salah = k.CreateCommand();
        salah.CommandText = "UPDATE pengumpulan SET tautan = 'drive saya'";
        Assert.Throws<SqliteException>(() => salah.ExecuteNonQuery()); // CHECK ikut terpasang
    }

    // ---------------- Data master (FR-15, FR-16, FR-22)

    [Fact]
    public async Task Kader_NrpGanda_DitolakPadaKolomNrp()
    {
        await Login("raka.pratama");
        var svc = new KaderService(_pabrik, _sesi, _clock);
        var ex = await Assert.ThrowsAsync<ValidasiException>(() =>
            svc.SimpanAsync(new KaderInput(null, "5025251034", "Duplikat", 2025, null, 1, true)));
        Assert.Equal(nameof(KaderInput.Nrp), ex.Kolom); // AT-03
    }

    [Fact]
    public async Task Kader_BaruMenambahJumlahKaderAktif()
    {
        await Login("raka.pratama");
        var rekap = new RekapService(_pabrik, _sesi, _clock);
        var sebelum = (await rekap.AmbilAsync()).KaderAktif.Count();
        await new KaderService(_pabrik, _sesi, _clock).SimpanAsync(new KaderInput(null, "5025259001", "Kader Baru", 2025, null, 1, true));
        Assert.Equal(sebelum + 1, (await rekap.AmbilAsync()).KaderAktif.Count()); // AT-05
    }

    [Fact]
    public async Task Kader_DenganRiwayatHanyaDinonaktifkan()
    {
        await Login("raka.pratama");
        await using var db = _pabrik.CreateDbContext();
        var dimas = await db.Kader.FirstAsync(k => k.Nrp == "5025251034");
        var pesan = await new KaderService(_pabrik, _sesi, _clock).HapusAsync(dimas.Id);
        Assert.Contains("dinonaktifkan", pesan);
        await using var db2 = _pabrik.CreateDbContext();
        Assert.False((await db2.Kader.FirstAsync(k => k.Id == dimas.Id)).Aktif);
    }

    [Fact]
    public async Task Kelompok_BeranggotaTidakDapatDihapus()
    {
        await Login("raka.pratama");
        await Assert.ThrowsAsync<ValidasiException>(() => new KelompokService(_pabrik, _sesi, _clock).HapusAsync(1)); // AT-09
    }

    [Fact]
    public async Task Penugasan_TenggatSebelumMulaiDitolak()
    {
        await Login("raka.pratama");
        var ex = await Assert.ThrowsAsync<ValidasiException>(() => new PenugasanService(_pabrik, _sesi, _clock).SimpanAsync(
            new PenugasanInput(null, "P9", "Uji", null, KategoriPenugasan.Individu, new DateTime(2026, 11, 10), new DateTime(2026, 11, 1), 10)));
        Assert.Equal(nameof(PenugasanInput.Tenggat), ex.Kolom);
    }

    [Fact]
    public async Task Pengguna_AdminTidakDapatMenonaktifkanDiriSendiri()
    {
        var s = await Login("raka.pratama");
        await Assert.ThrowsAsync<ValidasiException>(() => new PenggunaService(_pabrik, _sesi, _hasher, _clock).SetAktifAsync(s.Id, false));
    }

    [Fact]
    public async Task Ambang_PerubahanMengubahDaftarBerisikoDanTercatat()
    {
        await Login("raka.pratama");
        var pengaturan = new PengaturanService(_pabrik, _sesi, _clock);
        var rekap = new RekapService(_pabrik, _sesi, _clock);
        var awal = (await rekap.AmbilAsync()).KaderBerisiko.Count;
        await pengaturan.SimpanAmbangAsync(new AmbangRisiko(5, 0, 0, 2)); // hampir mustahil berisiko
        Assert.True((await rekap.AmbilAsync()).KaderBerisiko.Count < awal);
        await using var db = _pabrik.CreateDbContext();
        Assert.True(await db.LogAktivitas.AnyAsync(l => l.Entitas == "Pengaturan" && l.Keterangan.Contains("R1: 2 → 5")));
    }

    // ---------------- Log & ekspor

    [Fact]
    public async Task Log_FilterAksiDanTeks()
    {
        await Login("raka.pratama");
        var log = new LogService(_pabrik, _sesi);
        var hasil = await log.CariAsync(new FilterLog(Aksi: AksiLog.Nilai, UkuranHalaman: 5));
        Assert.True(hasil.Total > 0);
        Assert.All(hasil.Item, l => Assert.Equal(AksiLog.Nilai, l.Aksi));
        var teks = await log.CariAsync(new FilterLog(Teks: "P5"));
        Assert.All(teks.Item, l => Assert.Contains("P5", l.Keterangan, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ekspor_RekapCsvBerisiHeaderDanSemuaKader()
    {
        await Login("raka.pratama");
        var csv = await new EksporService(_pabrik, _sesi, _clock).RekapNilaiCsvAsync();
        var baris = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("NRP;Nama;Kelompok", baris[0]);
        Assert.Equal(125, baris.Length); // header + 124 kader
    }

    [Fact]
    public async Task Ekspor_CsvMengikutiPengaturanRegional()
    {
        await Login("raka.pratama");
        var format = FormatCsv.Dari(new System.Globalization.CultureInfo("en-US"));
        Assert.Equal(",", format.Pemisah);

        var csv = await new EksporService(_pabrik, _sesi, _clock, format).KaderBerisikoCsvAsync();
        var baris = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("NRP,Nama,Kelompok,Pemandu", baris[0]);
        // Desimal memakai titik; teks yang mengandung koma dikutip agar tidak terbelah.
        var nadia = baris.First(b => b.StartsWith("5025251074"));
        Assert.Contains(",40.0,22.1,", nadia);
        Assert.EndsWith("\"", nadia);
    }
}
