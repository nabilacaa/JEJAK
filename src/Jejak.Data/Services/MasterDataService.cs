using System.Text.RegularExpressions;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jejak.Data.Services;

public sealed partial class KaderService : IKaderService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public KaderService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex PolaNrp();

    /// <summary>Validasi format NRP (10 digit angka, BR-06). Publik agar formulir dapat memvalidasi langsung.</summary>
    public static string? ValidasiNrp(string? nrp)
    {
        var t = nrp?.Trim() ?? "";
        if (t.Length == 0) return "NRP wajib diisi.";
        if (!PolaNrp().IsMatch(t)) return "NRP harus terdiri dari 10 digit angka.";
        return null;
    }

    public async Task<KaderDetail> DetailAsync(int kaderId, CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);

        var kader = await db.Kader.AsNoTracking().FirstOrDefaultAsync(k => k.Id == kaderId, ct)
            ?? throw new ValidasiException("Kader tidak ditemukan.");
        sesi.WajibAksesKelompok(kader.KelompokId);

        var snapshot = await RekapBuilder.BangunAsync(db, sesi, sekarang, ct);
        var ringkas = snapshot.Kader.First(k => k.Id == kaderId);

        // Pengumpulan seluruh kader aktif sekelompok, untuk garis rata-rata kelompok.
        var kumpulKelompok = await db.Pengumpulan.AsNoTracking()
            .Where(s => s.Kader!.KelompokId == kader.KelompokId && s.Kader.Aktif)
            .ToListAsync(ct);
        var milikKader = await db.Pengumpulan.AsNoTracking().Where(s => s.KaderId == kaderId).ToDictionaryAsync(s => s.PenugasanId, ct);

        var jejak = snapshot.Penugasan
            .Where(p => p.Status != StatusPenugasan.BelumDimulai)
            .OrderByDescending(p => p.TanggalMulai).ThenByDescending(p => p.Kode)
            .Select(p =>
            {
                milikKader.TryGetValue(p.Id, out var s);
                var nilaiKelompok = kumpulKelompok.Where(x => x.PenugasanId == p.Id && x.Nilai.HasValue).Select(x => (double?)x.Nilai);
                return new JejakPenugasan(p, AturanBisnis.StatusPengumpulan(p.Tenggat, s?.WaktuKumpul),
                    s?.WaktuKumpul, s?.Tautan, s?.Nilai, s?.Catatan, AturanBisnis.RataRata(nilaiKelompok));
            })
            .ToList();

        return new KaderDetail(ringkas, snapshot.Ambang, jejak);
    }

    public async Task<int> SimpanAsync(KaderInput input, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var nrp = input.Nrp?.Trim() ?? "";
        var nama = Teks.Bersihkan(input.Nama) ?? "";
        var kontak = Teks.Bersihkan(input.Kontak);

        if (ValidasiNrp(nrp) is string galatNrp) throw new ValidasiException(galatNrp, nameof(KaderInput.Nrp));
        if (nama.Length == 0) throw new ValidasiException("Nama lengkap wajib diisi.", nameof(KaderInput.Nama));
        if (nama.Length > 100) throw new ValidasiException("Nama maksimal 100 karakter.", nameof(KaderInput.Nama));
        if (input.Angkatan is < 2000 or > 2100) throw new ValidasiException("Angkatan tidak valid.", nameof(KaderInput.Angkatan));
        if (kontak is { Length: > 50 }) throw new ValidasiException("Kontak maksimal 50 karakter.", nameof(KaderInput.Kontak));

        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);

        var kelompok = await db.Kelompok.FirstOrDefaultAsync(k => k.Id == input.KelompokId, ct)
            ?? throw new ValidasiException("Kelompok wajib dipilih.", nameof(KaderInput.KelompokId));

        var pemilikNrp = await db.Kader.AsNoTracking().FirstOrDefaultAsync(k => k.Nrp == nrp && k.Id != (input.Id ?? 0), ct);
        if (pemilikNrp is not null)
            throw new ValidasiException($"NRP sudah terdaftar atas nama {pemilikNrp.Nama}.", nameof(KaderInput.Nrp));

        if (input.Id is null)
        {
            var baru = new Kader
            {
                Nrp = nrp, Nama = nama, Angkatan = input.Angkatan, Kontak = kontak,
                KelompokId = kelompok.Id, Aktif = input.Aktif, DibuatPada = sekarang
            };
            db.Kader.Add(baru);
            await db.SimpanAsync(ct);
            db.Catat(sesi.Id, AksiLog.Tambah, "Kader", baru.Id, $"Menambahkan kader {nama} ({nrp}) ke {Teks.LabelPendek(kelompok)}", sekarang);
            await db.SimpanAsync(ct);
            return baru.Id;
        }

        var k = await db.Kader.Include(x => x.Kelompok).FirstOrDefaultAsync(x => x.Id == input.Id, ct)
            ?? throw new ValidasiException("Kader tidak ditemukan.");

        var catatan = new List<string>();
        if (k.KelompokId != kelompok.Id)
            catatan.Add($"memindahkan {k.Kelompok!.Nomor:00} → {kelompok.Nomor:00}");
        if (k.Aktif != input.Aktif)
            catatan.Add(input.Aktif ? "mengaktifkan kembali" : "menonaktifkan");
        if (k.Nrp != nrp || k.Nama != nama || k.Angkatan != input.Angkatan || k.Kontak != kontak)
            catatan.Add("memperbarui data diri");

        k.Nrp = nrp;
        k.Nama = nama;
        k.Angkatan = input.Angkatan;
        k.Kontak = kontak;
        k.KelompokId = kelompok.Id;
        k.Aktif = input.Aktif;
        k.DiubahPada = sekarang;

        if (catatan.Count > 0)
            db.Catat(sesi.Id, AksiLog.Ubah, "Kader", k.Id, $"Kader {nama}: {string.Join(", ", catatan)}", sekarang);
        await db.SimpanAsync(ct);
        return k.Id;
    }

    public async Task<string> HapusAsync(int kaderId, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);
        var k = await db.Kader.FirstOrDefaultAsync(x => x.Id == kaderId, ct) ?? throw new ValidasiException("Kader tidak ditemukan.");

        // FR-16 / BR-06: kader yang sudah memiliki pengumpulan hanya dinonaktifkan agar riwayat utuh.
        if (await db.Pengumpulan.AnyAsync(s => s.KaderId == kaderId, ct))
        {
            if (!k.Aktif) return $"{k.Nama} sudah nonaktif.";
            k.Aktif = false;
            k.DiubahPada = sekarang;
            db.Catat(sesi.Id, AksiLog.Hapus, "Kader", k.Id, $"Menonaktifkan {k.Nama} ({k.Nrp}) karena memiliki riwayat pengumpulan", sekarang);
            await db.SimpanAsync(ct);
            return $"{k.Nama} dinonaktifkan karena sudah memiliki riwayat pengumpulan.";
        }

        db.Kader.Remove(k);
        db.Catat(sesi.Id, AksiLog.Hapus, "Kader", null, $"Menghapus kader {k.Nama} ({k.Nrp})", sekarang);
        await db.SimpanAsync(ct);
        return $"{k.Nama} dihapus.";
    }
}

public sealed class KelompokService : IKelompokService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public KelompokService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PilihanPengguna>> PemanduTersediaAsync(int? kelompokId, CancellationToken ct = default)
    {
        _sesi.WajibAdmin();
        await using var db = await _db.CreateDbContextAsync(ct);
        var id = kelompokId ?? 0;
        return await db.Pengguna.AsNoTracking()
            .Where(p => p.Peran == Peran.Pemandu && p.Aktif && (p.KelompokBinaan == null || p.KelompokBinaan.Id == id))
            .OrderBy(p => p.Nama)
            .Select(p => new PilihanPengguna(p.Id, p.Nama, p.Username))
            .ToListAsync(ct);
    }

    public async Task<int> SimpanAsync(KelompokInput input, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var nama = Teks.Bersihkan(input.Nama) ?? "";
        var deskripsi = Teks.Bersihkan(input.Deskripsi);
        if (input.Nomor is < 1 or > 999) throw new ValidasiException("Nomor kelompok harus antara 1 dan 999.", nameof(KelompokInput.Nomor));
        if (nama.Length == 0) throw new ValidasiException("Nama kelompok wajib diisi.", nameof(KelompokInput.Nama));
        if (nama.Length > 50) throw new ValidasiException("Nama kelompok maksimal 50 karakter.", nameof(KelompokInput.Nama));

        var sekarang = _clock.Now;
        var idSendiri = input.Id ?? 0;
        await using var db = await _db.CreateDbContextAsync(ct);

        if (await db.Kelompok.AnyAsync(k => k.Nomor == input.Nomor && k.Id != idSendiri, ct))
            throw new ValidasiException($"Nomor {input.Nomor:00} sudah dipakai kelompok lain.", nameof(KelompokInput.Nomor));
        if (await db.Kelompok.AnyAsync(k => k.Nama == nama && k.Id != idSendiri, ct))
            throw new ValidasiException("Nama kelompok sudah dipakai.", nameof(KelompokInput.Nama));

        Pengguna? pemandu = null;
        if (input.PemanduId is int pid)
        {
            pemandu = await db.Pengguna.Include(p => p.KelompokBinaan).FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (pemandu is null || pemandu.Peran != Peran.Pemandu || !pemandu.Aktif)
                throw new ValidasiException("Pemandu harus akun Pemandu yang aktif.", nameof(KelompokInput.PemanduId));
            if (pemandu.KelompokBinaan is { } lain && lain.Id != idSendiri)
                throw new ValidasiException($"{pemandu.Nama} sudah membimbing {Teks.LabelPendek(lain)}.", nameof(KelompokInput.PemanduId));
        }

        if (input.Id is null)
        {
            var baru = new Kelompok { Nomor = input.Nomor, Nama = nama, Deskripsi = deskripsi, PemanduId = pemandu?.Id, DibuatPada = sekarang };
            db.Kelompok.Add(baru);
            await db.SimpanAsync(ct);
            db.Catat(sesi.Id, AksiLog.Tambah, "Kelompok", baru.Id,
                $"Membuat {Teks.Label(baru)}" + (pemandu is null ? "" : $" dengan pemandu {pemandu.Nama}"), sekarang);
            await db.SimpanAsync(ct);
            return baru.Id;
        }

        var g = await db.Kelompok.Include(x => x.Pemandu).FirstOrDefaultAsync(x => x.Id == input.Id, ct)
            ?? throw new ValidasiException("Kelompok tidak ditemukan.");
        var catatan = new List<string>();
        if (g.PemanduId != pemandu?.Id) catatan.Add($"pemandu {g.Pemandu?.Nama ?? "—"} → {pemandu?.Nama ?? "—"}");
        if (g.Nomor != input.Nomor || g.Nama != nama || g.Deskripsi != deskripsi) catatan.Add("memperbarui identitas");

        g.Nomor = input.Nomor;
        g.Nama = nama;
        g.Deskripsi = deskripsi;
        g.PemanduId = pemandu?.Id;
        if (catatan.Count > 0)
            db.Catat(sesi.Id, AksiLog.Ubah, "Kelompok", g.Id, $"{Teks.Label(g)}: {string.Join(", ", catatan)}", sekarang);
        await db.SimpanAsync(ct);
        return g.Id;
    }

    public async Task HapusAsync(int kelompokId, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        await using var db = await _db.CreateDbContextAsync(ct);
        var g = await db.Kelompok.FirstOrDefaultAsync(k => k.Id == kelompokId, ct) ?? throw new ValidasiException("Kelompok tidak ditemukan.");

        // FR-22: kelompok yang masih memiliki anggota tidak dapat dihapus.
        var anggota = await db.Kader.CountAsync(k => k.KelompokId == kelompokId, ct);
        if (anggota > 0)
            throw new ValidasiException($"{Teks.LabelPendek(g)} masih memiliki {anggota} kader. Pindahkan kader ke kelompok lain terlebih dahulu.");

        db.Kelompok.Remove(g);
        db.Catat(sesi.Id, AksiLog.Hapus, "Kelompok", null, $"Menghapus {Teks.Label(g)}", _clock.Now);
        await db.SimpanAsync(ct);
    }
}

public sealed class PenugasanService : IPenugasanService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public PenugasanService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    public async Task<int> SimpanAsync(PenugasanInput input, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var kode = Teks.Bersihkan(input.Kode)?.ToUpperInvariant() ?? "";
        var judul = Teks.Bersihkan(input.Judul) ?? "";
        var deskripsi = Teks.Bersihkan(input.Deskripsi);

        if (kode.Length == 0) throw new ValidasiException("Kode penugasan wajib diisi.", nameof(PenugasanInput.Kode));
        if (kode.Length > 10) throw new ValidasiException("Kode maksimal 10 karakter.", nameof(PenugasanInput.Kode));
        if (judul.Length == 0) throw new ValidasiException("Judul wajib diisi.", nameof(PenugasanInput.Judul));
        if (judul.Length > 100) throw new ValidasiException("Judul maksimal 100 karakter.", nameof(PenugasanInput.Judul));
        if (input.Tenggat < input.TanggalMulai) // FR-24
            throw new ValidasiException("Tenggat tidak boleh lebih awal dari tanggal mulai.", nameof(PenugasanInput.Tenggat));
        if (input.Bobot is < 1 or > 100)
            throw new ValidasiException("Bobot harus bernilai 1–100.", nameof(PenugasanInput.Bobot));

        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);
        if (await db.Penugasan.AnyAsync(p => p.Kode == kode && p.Id != (input.Id ?? 0), ct))
            throw new ValidasiException($"Kode {kode} sudah dipakai.", nameof(PenugasanInput.Kode));

        if (input.Id is null)
        {
            var baru = new Penugasan
            {
                Kode = kode, Judul = judul, Deskripsi = deskripsi, Kategori = input.Kategori,
                TanggalMulai = input.TanggalMulai, Tenggat = input.Tenggat, Bobot = input.Bobot, Aktif = true, DibuatPada = sekarang
            };
            db.Penugasan.Add(baru);
            await db.SimpanAsync(ct);
            db.Catat(sesi.Id, AksiLog.Tambah, "Penugasan", baru.Id, $"Menambahkan {kode} · {judul} (bobot {input.Bobot})", sekarang);
            await db.SimpanAsync(ct);
            return baru.Id;
        }

        var p = await db.Penugasan.FirstOrDefaultAsync(x => x.Id == input.Id, ct) ?? throw new ValidasiException("Penugasan tidak ditemukan.");
        var catatan = new List<string>();
        if (p.Tenggat != input.Tenggat) catatan.Add($"tenggat {p.Tenggat:dd/MM HH.mm} → {input.Tenggat:dd/MM HH.mm}");
        if (p.Bobot != input.Bobot) catatan.Add($"bobot {p.Bobot} → {input.Bobot}");
        if (p.Kode != kode || p.Judul != judul || p.Deskripsi != deskripsi || p.Kategori != input.Kategori || p.TanggalMulai != input.TanggalMulai)
            catatan.Add("memperbarui detail");

        p.Kode = kode;
        p.Judul = judul;
        p.Deskripsi = deskripsi;
        p.Kategori = input.Kategori;
        p.TanggalMulai = input.TanggalMulai;
        p.Tenggat = input.Tenggat;
        p.Bobot = input.Bobot;
        if (catatan.Count > 0)
            db.Catat(sesi.Id, AksiLog.Ubah, "Penugasan", p.Id, $"{kode}: {string.Join(", ", catatan)}", sekarang);
        await db.SimpanAsync(ct);
        return p.Id;
    }

    public async Task<string> HapusAsync(int penugasanId, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);
        var p = await db.Penugasan.FirstOrDefaultAsync(x => x.Id == penugasanId, ct) ?? throw new ValidasiException("Penugasan tidak ditemukan.");

        if (await db.Pengumpulan.AnyAsync(s => s.PenugasanId == penugasanId, ct))
        {
            p.Aktif = false;
            db.Catat(sesi.Id, AksiLog.Hapus, "Penugasan", p.Id, $"Menonaktifkan {p.Kode} · {p.Judul} karena memiliki pengumpulan", sekarang);
            await db.SimpanAsync(ct);
            return $"{p.Kode} dinonaktifkan karena sudah memiliki pengumpulan. Riwayat nilainya tetap tersimpan.";
        }

        db.Penugasan.Remove(p);
        db.Catat(sesi.Id, AksiLog.Hapus, "Penugasan", null, $"Menghapus {p.Kode} · {p.Judul}", sekarang);
        await db.SimpanAsync(ct);
        return $"{p.Kode} dihapus.";
    }
}
