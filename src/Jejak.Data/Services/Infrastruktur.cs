using System.Globalization;
using System.Text;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jejak.Data.Services;

/// <summary>Registrasi lapisan data ke container DI aplikasi.</summary>
public static class RegistrasiData
{
    public static IServiceCollection AddJejakData(this IServiceCollection services, string pathDatabase)
    {
        services.AddDbContextFactory<JejakDbContext>(o => o.UseSqlite(DatabaseInitializer.ConnectionString(pathDatabase)));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISessionContext, SessionContext>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IRekapService, RekapService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IPengaturanService, PengaturanService>();
        services.AddSingleton<IKaderService, KaderService>();
        services.AddSingleton<IKelompokService, KelompokService>();
        services.AddSingleton<IPenugasanService, PenugasanService>();
        services.AddSingleton<IPenilaianService, PenilaianService>();
        services.AddSingleton<IPenggunaService, PenggunaService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IEksporService, EksporService>();
        return services;
    }
}

/// <summary>Hash password BCrypt (NFR-01). Kompatibel dengan hash $2b$ pada data contoh.</summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int Cost = 11;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, Cost);

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (Exception)
        {
            // Hash rusak atau format tidak dikenal: perlakukan sebagai password salah.
            return false;
        }
    }
}

internal static class DbExtensions
{
    /// <summary>Menambahkan entri log ke context yang sama agar tersimpan dalam transaksi yang sama (FR-33).</summary>
    public static void Catat(this JejakDbContext db, int? penggunaId, string aksi, string entitas, int? entitasId, string keterangan, DateTime waktu)
    {
        db.LogAktivitas.Add(new LogAktivitas
        {
            PenggunaId = penggunaId,
            Aksi = aksi,
            Entitas = entitas,
            EntitasId = entitasId,
            Keterangan = keterangan.Length > 500 ? keterangan[..500] : keterangan,
            Waktu = waktu
        });
    }

    public static async Task<AmbangRisiko> AmbangAsync(this JejakDbContext db, CancellationToken ct)
    {
        var d = await db.Pengaturan.AsNoTracking().ToDictionaryAsync(p => p.Kunci, p => p.Nilai, ct);
        var bawaan = AmbangRisiko.Bawaan;

        int Bulat(string kunci, int def) =>
            d.TryGetValue(kunci, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : def;
        double Desimal(string kunci, double def) =>
            d.TryGetValue(kunci, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : def;

        return new AmbangRisiko(
            Bulat(KunciPengaturan.R1MinBelum, bawaan.MinBelum),
            Desimal(KunciPengaturan.R2MinKetuntasan, bawaan.MinKetuntasan),
            Desimal(KunciPengaturan.R3MinNilai, bawaan.MinNilai),
            Bulat(KunciPengaturan.R3MinDinilai, bawaan.MinDinilai));
    }

    /// <summary>
    /// SaveChanges dengan penerjemahan pelanggaran constraint/trigger menjadi
    /// pesan yang dapat dipahami pengguna (NFR-04).
    /// </summary>
    public static async Task<int> SimpanAsync(this JejakDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sql)
        {
            throw new ValidasiException(TerjemahkanGalat(sql));
        }
    }

    internal static string TerjemahkanGalat(SqliteException ex)
    {
        var pesan = ex.Message;
        if (pesan.Contains("kader.nrp", StringComparison.OrdinalIgnoreCase)) return "NRP sudah terdaftar.";
        if (pesan.Contains("pengguna.username", StringComparison.OrdinalIgnoreCase)) return "Username sudah dipakai.";
        if (pesan.Contains("kelompok.nama", StringComparison.OrdinalIgnoreCase)) return "Nama kelompok sudah dipakai.";
        if (pesan.Contains("kelompok.nomor", StringComparison.OrdinalIgnoreCase)) return "Nomor kelompok sudah dipakai.";
        if (pesan.Contains("kelompok.pemandu_id", StringComparison.OrdinalIgnoreCase)) return "Pemandu tersebut sudah membimbing kelompok lain.";
        if (pesan.Contains("penugasan.kode", StringComparison.OrdinalIgnoreCase)) return "Kode penugasan sudah dipakai.";
        if (pesan.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) return "Data masih dipakai oleh data lain sehingga tidak dapat diubah atau dihapus.";
        if (pesan.Contains("CHECK constraint", StringComparison.OrdinalIgnoreCase)) return "Data tidak memenuhi aturan validasi database.";

        // Pesan RAISE(ABORT, ...) dari trigger sudah berbahasa Indonesia; ambil bagian setelah "SQLite Error 19: '".
        var awal = pesan.IndexOf('\'');
        var akhir = pesan.LastIndexOf('\'');
        return awal >= 0 && akhir > awal ? pesan[(awal + 1)..akhir] : "Gagal menyimpan data: " + pesan;
    }
}

/// <summary>Menyusun rekap dalam cakupan sesi. Dipakai bersama oleh beberapa layanan.</summary>
internal static class RekapBuilder
{
    public static async Task<RekapSnapshot> BangunAsync(JejakDbContext db, SesiPengguna sesi, DateTime sekarang, CancellationToken ct)
    {
        var ambang = await db.AmbangAsync(ct);
        var kelompokId = sesi.KelompokId ?? -1;

        var kelompokQ = db.Kelompok.AsNoTracking().Include(k => k.Pemandu).AsQueryable();
        var kaderQ = db.Kader.AsNoTracking().AsQueryable();
        var kumpulQ = db.Pengumpulan.AsNoTracking().AsQueryable();
        if (!sesi.IsAdmin)
        {
            kelompokQ = kelompokQ.Where(k => k.Id == kelompokId);
            kaderQ = kaderQ.Where(k => k.KelompokId == kelompokId);
            kumpulQ = kumpulQ.Where(s => s.Kader!.KelompokId == kelompokId);
        }

        var kelompok = await kelompokQ.OrderBy(k => k.Nomor).ToListAsync(ct);
        var kader = await kaderQ.OrderBy(k => k.Nrp).ToListAsync(ct);
        var penugasan = await db.Penugasan.AsNoTracking().Where(p => p.Aktif).ToListAsync(ct);
        penugasan = penugasan.OrderBy(p => p.TanggalMulai).ThenBy(p => p.Kode, StringComparer.OrdinalIgnoreCase).ToList();
        var kumpul = await kumpulQ.ToListAsync(ct);

        var perKader = kumpul
            .GroupBy(s => s.KaderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, Pengumpulan>)g.ToDictionary(s => s.PenugasanId));
        var kosong = new Dictionary<int, Pengumpulan>();
        var kelompokById = kelompok.ToDictionary(k => k.Id);

        var kaderRingkas = kader.Select(k =>
        {
            RekapKader? rekap = null;
            IReadOnlyList<string> kriteria = Array.Empty<string>();
            if (k.Aktif)
            {
                rekap = AturanBisnis.HitungRekap(penugasan, perKader.GetValueOrDefault(k.Id) ?? kosong, sekarang);
                kriteria = AturanBisnis.KriteriaRisiko(rekap, ambang);
            }
            kelompokById.TryGetValue(k.KelompokId, out var g);
            return new KaderRingkas(k.Id, k.Nrp, k.Nama, k.Angkatan, k.Kontak, k.KelompokId, g?.Nomor ?? 0, g?.Nama ?? "—",
                g?.Pemandu?.Nama, k.Aktif, k.DibuatPada, rekap, kriteria);
        }).ToList();

        var aktif = kaderRingkas.Where(k => k.Aktif).Select(k => k.Id).ToHashSet();
        var kumpulPerPenugasan = kumpul.Where(s => aktif.Contains(s.KaderId))
            .GroupBy(s => s.PenugasanId).ToDictionary(g => g.Key, g => g.Count());

        var penugasanRingkas = penugasan.Select(p => new PenugasanRingkas(
            p.Id, p.Kode, p.Judul, p.Deskripsi, p.Kategori, p.TanggalMulai, p.Tenggat, p.Bobot,
            AturanBisnis.StatusPenugasan(p, sekarang),
            kumpulPerPenugasan.GetValueOrDefault(p.Id),
            aktif.Count)).ToList();

        var kelompokRingkas = kelompok.Select(g =>
        {
            var anggota = kaderRingkas.Where(k => k.Aktif && k.KelompokId == g.Id).ToList();
            return new KelompokRingkas(g.Id, g.Nomor, g.Nama, g.Deskripsi, g.PemanduId, g.Pemandu?.Nama,
                anggota.Count,
                AturanBisnis.RataRata(anggota.Select(a => a.Rekap?.Ketuntasan)),
                AturanBisnis.RataRata(anggota.Select(a => a.Rekap?.NilaiAkhir)),
                anggota.Count(a => a.Berisiko));
        }).ToList();

        return new RekapSnapshot(sekarang, ambang, kaderRingkas, penugasanRingkas, kelompokRingkas);
    }
}

/// <summary>Penulis CSV sederhana; pemisah titik koma agar terbaca Excel berlokal Indonesia.</summary>
/// <summary>
/// Pemisah kolom dan format angka berkas CSV. Excel membaca CSV memakai "List separator" dan pemisah
/// desimal dari pengaturan regional Windows, sehingga aplikasi mengikuti pengaturan yang sama (FR-38).
/// </summary>
public sealed record FormatCsv(string Pemisah, CultureInfo Budaya)
{
    /// <summary>Bawaan bila pengaturan regional tidak diketahui: titik koma dan desimal koma.</summary>
    public static FormatCsv Indonesia { get; } = new(";", CultureInfo.GetCultureInfo("id-ID"));

    public static FormatCsv Dari(CultureInfo budaya)
    {
        var pemisah = budaya.TextInfo.ListSeparator;
        return new(string.IsNullOrEmpty(pemisah) ? "," : pemisah, budaya);
    }
}

internal sealed class CsvWriter
{
    private readonly FormatCsv _format;
    private readonly StringBuilder _sb = new();

    public CsvWriter(FormatCsv format) => _format = format;

    public CsvWriter Baris(params object?[] kolom)
    {
        _sb.AppendJoin(_format.Pemisah, kolom.Select(Format));
        _sb.Append("\r\n");
        return this;
    }

    public override string ToString() => _sb.ToString();

    private string Format(object? v)
    {
        var s = v switch
        {
            null => "",
            double d => d.ToString("0.0", _format.Budaya),
            DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, _format.Budaya) ?? ""
        };
        bool perluKutip = s.Contains(_format.Pemisah) || s.IndexOfAny(new[] { '"', '\n', '\r' }) >= 0;
        return perluKutip ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

internal static class Teks
{
    public static string? Bersihkan(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public static string Label(Kelompok g) => $"Kelompok {g.Nomor:00} · {g.Nama}";

    public static string LabelPendek(Kelompok g) => $"Kelompok {g.Nomor:00}";

    public static string Nilai(double? v) => v is null ? "—" : v.Value.ToString("0.#", CultureInfo.GetCultureInfo("id-ID"));
}
