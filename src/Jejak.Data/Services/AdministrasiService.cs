using System.Text.RegularExpressions;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jejak.Data.Services;

public sealed partial class PenggunaService : IPenggunaService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;

    public PenggunaService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IPasswordHasher hasher, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _hasher = hasher;
        _clock = clock;
    }

    [GeneratedRegex(@"^[a-z0-9._]{3,50}$")]
    private static partial Regex PolaUsername();

    public async Task<IReadOnlyList<PenggunaItem>> DaftarAsync(CancellationToken ct = default)
    {
        _sesi.WajibAdmin();
        await using var db = await _db.CreateDbContextAsync(ct);
        var data = await db.Pengguna.AsNoTracking().Include(p => p.KelompokBinaan).ToListAsync(ct);
        return data
            .OrderBy(p => p.Peran)
            .ThenBy(p => p.KelompokBinaan?.Nomor ?? int.MaxValue)
            .ThenBy(p => p.Nama)
            .Select(p => new PenggunaItem(p.Id, p.Username, p.Nama, p.Peran, p.Aktif, p.LoginTerakhir,
                p.KelompokBinaan is null ? null : Teks.Label(p.KelompokBinaan)))
            .ToList();
    }

    public async Task<int> SimpanAsync(PenggunaInput input, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var username = (input.Username ?? "").Trim().ToLowerInvariant();
        var nama = Teks.Bersihkan(input.Nama) ?? "";
        var password = input.PasswordBaru ?? "";

        if (!PolaUsername().IsMatch(username))
            throw new ValidasiException("Username 3–50 karakter: huruf kecil, angka, titik, atau garis bawah.", nameof(PenggunaInput.Username));
        if (nama.Length == 0) throw new ValidasiException("Nama wajib diisi.", nameof(PenggunaInput.Nama));
        if (input.Id is null && password.Length == 0)
            throw new ValidasiException("Password awal wajib diisi.", nameof(PenggunaInput.PasswordBaru));
        if (password.Length > 0 && password.Length < AuthService.PanjangMinimumPassword)
            throw new ValidasiException($"Password minimal {AuthService.PanjangMinimumPassword} karakter.", nameof(PenggunaInput.PasswordBaru));

        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);
        if (await db.Pengguna.AnyAsync(p => p.Username == username && p.Id != (input.Id ?? 0), ct))
            throw new ValidasiException("Username sudah dipakai.", nameof(PenggunaInput.Username));

        if (input.Id is null)
        {
            var baru = new Pengguna
            {
                Username = username, Nama = nama, Peran = input.Peran, Aktif = input.Aktif,
                PasswordHash = _hasher.Hash(password), Tema = "light", DibuatPada = sekarang
            };
            db.Pengguna.Add(baru);
            await db.SimpanAsync(ct);
            db.Catat(sesi.Id, AksiLog.Tambah, "Pengguna", baru.Id,
                $"Membuat akun {(input.Peran == Peran.Admin ? "admin" : "pemandu")} {username}", sekarang);
            await db.SimpanAsync(ct);
            return baru.Id;
        }

        var u = await db.Pengguna.Include(p => p.KelompokBinaan).FirstOrDefaultAsync(p => p.Id == input.Id, ct)
            ?? throw new ValidasiException("Pengguna tidak ditemukan.");
        if (u.Id == sesi.Id && !input.Aktif) // FR-35
            throw new ValidasiException("Anda tidak dapat menonaktifkan akun sendiri.", nameof(PenggunaInput.Aktif));
        if (u.Id == sesi.Id && input.Peran != u.Peran)
            throw new ValidasiException("Anda tidak dapat mengubah peran akun sendiri.", nameof(PenggunaInput.Peran));
        if (u.Peran == Peran.Pemandu && input.Peran == Peran.Admin && u.KelompokBinaan is not null)
            throw new ValidasiException($"Lepaskan {u.Nama} dari {Teks.LabelPendek(u.KelompokBinaan)} sebelum menjadikannya Admin.", nameof(PenggunaInput.Peran));

        var catatan = new List<string>();
        if (u.Username != username || u.Nama != nama) catatan.Add("memperbarui identitas");
        if (u.Peran != input.Peran) catatan.Add($"peran {u.Peran} → {input.Peran}");
        if (u.Aktif != input.Aktif) catatan.Add(input.Aktif ? "mengaktifkan" : "menonaktifkan");
        if (password.Length > 0) catatan.Add("mengatur ulang password");

        u.Username = username;
        u.Nama = nama;
        u.Peran = input.Peran;
        u.Aktif = input.Aktif;
        if (password.Length > 0) u.PasswordHash = _hasher.Hash(password);

        if (catatan.Count > 0)
            db.Catat(sesi.Id, AksiLog.Ubah, "Pengguna", u.Id, $"Akun {username}: {string.Join(", ", catatan)}", sekarang);
        await db.SimpanAsync(ct);
        return u.Id;
    }

    public async Task SetAktifAsync(int penggunaId, bool aktif, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        if (penggunaId == sesi.Id && !aktif) throw new ValidasiException("Anda tidak dapat menonaktifkan akun sendiri.");
        await using var db = await _db.CreateDbContextAsync(ct);
        var u = await db.Pengguna.FirstOrDefaultAsync(p => p.Id == penggunaId, ct) ?? throw new ValidasiException("Pengguna tidak ditemukan.");
        if (u.Aktif == aktif) return;
        u.Aktif = aktif;
        db.Catat(sesi.Id, AksiLog.Ubah, "Pengguna", u.Id, $"{(aktif ? "Mengaktifkan" : "Menonaktifkan")} akun {u.Username}", _clock.Now);
        await db.SimpanAsync(ct);
    }
}

public sealed class LogService : ILogService
{
    private static readonly string[] AksiSesi = { AksiLog.Login, AksiLog.LoginGagal, AksiLog.Logout };

    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;

    public LogService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi)
    {
        _db = db;
        _sesi = sesi;
    }

    public async Task<HalamanLog> CariAsync(FilterLog filter, CancellationToken ct = default)
    {
        _sesi.WajibAdmin();
        await using var db = await _db.CreateDbContextAsync(ct);
        var q = Saring(db, filter);
        var total = await q.CountAsync(ct);
        var ukuran = Math.Clamp(filter.UkuranHalaman, 1, 100_000);
        var halaman = Math.Max(1, filter.Halaman);
        var item = await q
            .OrderByDescending(l => l.Waktu).ThenByDescending(l => l.Id)
            .Skip((halaman - 1) * ukuran).Take(ukuran)
            .Select(l => new LogItem(l.Id, l.Waktu, l.Pengguna != null ? l.Pengguna.Nama : null, l.Aksi, l.Entitas, l.Keterangan))
            .ToListAsync(ct);
        return new HalamanLog(item, total);
    }

    public async Task<IReadOnlyList<LogItem>> TerbaruAsync(int jumlah, CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        await using var db = await _db.CreateDbContextAsync(ct);
        var q = db.LogAktivitas.AsNoTracking().Where(l => !AksiSesi.Contains(l.Aksi));
        if (!sesi.IsAdmin) q = q.Where(l => l.PenggunaId == sesi.Id);
        return await q
            .OrderByDescending(l => l.Waktu).ThenByDescending(l => l.Id)
            .Take(jumlah)
            .Select(l => new LogItem(l.Id, l.Waktu, l.Pengguna != null ? l.Pengguna.Nama : null, l.Aksi, l.Entitas, l.Keterangan))
            .ToListAsync(ct);
    }

    internal static IQueryable<LogAktivitas> Saring(JejakDbContext db, FilterLog f)
    {
        var q = db.LogAktivitas.AsNoTracking().AsQueryable();
        if (Teks.Bersihkan(f.Teks) is string t)
        {
            var pola = "%" + t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(l => EF.Functions.Like(l.Keterangan, pola, "\\"));
        }
        if (f.PenggunaId is int pid) q = q.Where(l => l.PenggunaId == pid);
        if (!string.IsNullOrEmpty(f.Aksi)) q = q.Where(l => l.Aksi == f.Aksi);
        if (f.Dari is DateTime dari)
        {
            var awal = dari.Date;
            q = q.Where(l => l.Waktu >= awal);
        }
        if (f.Sampai is DateTime sampai)
        {
            var akhir = sampai.Date.AddDays(1);
            q = q.Where(l => l.Waktu < akhir);
        }
        return q;
    }
}

public sealed class EksporService : IEksporService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;
    private readonly FormatCsv _format;

    public EksporService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock, FormatCsv? format = null)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
        _format = format ?? FormatCsv.Indonesia;
    }

    public async Task<string> RekapNilaiCsvAsync(CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        await using var db = await _db.CreateDbContextAsync(ct);
        var snap = await RekapBuilder.BangunAsync(db, sesi, _clock.Now, ct);

        var jatuhTempo = snap.Penugasan.Where(p => p.JatuhTempo).ToList();
        var idKader = snap.Kader.Select(k => k.Id).ToList();
        var kumpul = await db.Pengumpulan.AsNoTracking().Where(s => idKader.Contains(s.KaderId)).ToListAsync(ct);
        var nilai = kumpul.ToDictionary(s => (s.KaderId, s.PenugasanId));

        var csv = new CsvWriter(_format);
        var header = new List<object?> { "NRP", "Nama", "Kelompok", "Status", "Ketuntasan (%)", "Nilai Akhir", "Belum Dikumpulkan", "Terlambat", "Kriteria Risiko" };
        header.AddRange(jatuhTempo.Select(p => (object?)$"{p.Kode} (bobot {p.Bobot})"));
        csv.Baris(header.ToArray());

        foreach (var k in snap.Kader.OrderBy(k => k.KelompokNomor).ThenBy(k => k.Nrp))
        {
            var baris = new List<object?>
            {
                k.Nrp, k.Nama, k.KelompokLabel, k.Status, k.Rekap?.Ketuntasan, k.Rekap?.NilaiAkhir,
                k.Rekap?.JumlahBelum, k.Rekap?.JumlahTerlambat, string.Join(" ", k.Kriteria)
            };
            foreach (var p in jatuhTempo)
            {
                if (!nilai.TryGetValue((k.Id, p.Id), out var s)) baris.Add("Belum");
                else baris.Add(s.Nilai is double n ? n : "Belum dinilai");
            }
            csv.Baris(baris.ToArray());
        }
        return csv.ToString();
    }

    public async Task<string> KaderBerisikoCsvAsync(CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        await using var db = await _db.CreateDbContextAsync(ct);
        var snap = await RekapBuilder.BangunAsync(db, sesi, _clock.Now, ct);
        var csv = new CsvWriter(_format).Baris("NRP", "Nama", "Kelompok", "Pemandu", "Belum Dikumpulkan", "Ketuntasan (%)", "Nilai Akhir", "Kriteria", "Keterangan");
        foreach (var k in snap.KaderBerisiko)
        {
            var ket = string.Join("; ", k.Kriteria.Select(c => AturanBisnis.JelaskanKriteria(c, k.Rekap!, snap.Ambang)));
            csv.Baris(k.Nrp, k.Nama, k.KelompokLabel, k.PemanduNama, k.Rekap?.JumlahBelum, k.Rekap?.Ketuntasan, k.Rekap?.NilaiAkhir,
                string.Join(" ", k.Kriteria), ket);
        }
        return csv.ToString();
    }

    public async Task<string> LogCsvAsync(FilterLog filter, CancellationToken ct = default)
    {
        _sesi.WajibAdmin();
        await using var db = await _db.CreateDbContextAsync(ct);
        var data = await LogService.Saring(db, filter)
            .OrderByDescending(l => l.Waktu).ThenByDescending(l => l.Id)
            .Select(l => new { l.Waktu, Nama = l.Pengguna != null ? l.Pengguna.Nama : null, l.Aksi, l.Entitas, l.Keterangan })
            .ToListAsync(ct);
        var csv = new CsvWriter(_format).Baris("Waktu", "Pengguna", "Aksi", "Entitas", "Keterangan");
        foreach (var l in data) csv.Baris(l.Waktu, l.Nama, l.Aksi, l.Entitas, l.Keterangan);
        return csv.ToString();
    }
}
