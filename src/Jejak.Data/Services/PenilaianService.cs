using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jejak.Data.Services;

public sealed class PenilaianService : IPenilaianService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public PenilaianService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    public async Task<LembarPenilaian> AmbilAsync(int penugasanId, int kelompokId, CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        sesi.WajibAksesKelompok(kelompokId);
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);

        var p = await db.Penugasan.AsNoTracking().FirstOrDefaultAsync(x => x.Id == penugasanId && x.Aktif, ct)
            ?? throw new ValidasiException("Penugasan tidak ditemukan.");
        var g = await db.Kelompok.AsNoTracking().Include(x => x.Pemandu).FirstOrDefaultAsync(x => x.Id == kelompokId, ct)
            ?? throw new ValidasiException("Kelompok tidak ditemukan.");

        var kader = await db.Kader.AsNoTracking().Where(k => k.KelompokId == kelompokId && k.Aktif).OrderBy(k => k.Nrp).ToListAsync(ct);
        var kumpul = await db.Pengumpulan.AsNoTracking().Include(s => s.Penilai)
            .Where(s => s.PenugasanId == penugasanId && s.Kader!.KelompokId == kelompokId && s.Kader.Aktif)
            .ToDictionaryAsync(s => s.KaderId, ct);

        var baris = kader.Select(k =>
        {
            kumpul.TryGetValue(k.Id, out var s);
            return new BarisPenilaian(k.Id, k.Nrp, k.Nama, s?.Id, s?.WaktuKumpul, s?.Tautan, s?.Nilai, s?.Catatan, s?.Penilai?.Nama);
        }).ToList();

        var ringkasPenugasan = new PenugasanRingkas(p.Id, p.Kode, p.Judul, p.Deskripsi, p.Kategori, p.TanggalMulai, p.Tenggat, p.Bobot,
            AturanBisnis.StatusPenugasan(p, sekarang), kumpul.Count, kader.Count);
        var ringkasKelompok = new KelompokRingkas(g.Id, g.Nomor, g.Nama, g.Deskripsi, g.PemanduId, g.Pemandu?.Nama, kader.Count, null, null, 0);
        return new LembarPenilaian(ringkasPenugasan, ringkasKelompok, baris);
    }

    public async Task<int> SimpanAsync(int penugasanId, IReadOnlyList<PerubahanPenilaian> perubahan, CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        if (perubahan.Count == 0) return 0;
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);

        var p = await db.Penugasan.FirstOrDefaultAsync(x => x.Id == penugasanId && x.Aktif, ct)
            ?? throw new ValidasiException("Penugasan tidak ditemukan atau sudah dinonaktifkan.");
        if (AturanBisnis.StatusPenugasan(p, sekarang) == StatusPenugasan.BelumDimulai)
            throw new ValidasiException($"{p.Kode} belum dimulai sehingga belum dapat menerima pengumpulan.");

        var idKader = perubahan.Select(x => x.KaderId).Distinct().ToList();
        var kaderMap = await db.Kader.Include(k => k.Kelompok).Where(k => idKader.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        var adaMap = await db.Pengumpulan.Where(s => s.PenugasanId == penugasanId && idKader.Contains(s.KaderId)).ToDictionaryAsync(s => s.KaderId, ct);

        int berubah = 0, dinilaiBaru = 0;
        var kelompokDinilai = new SortedSet<int>();

        foreach (var u in perubahan)
        {
            if (!kaderMap.TryGetValue(u.KaderId, out var k)) throw new ValidasiException("Kader tidak ditemukan.");
            if (!k.Aktif) throw new ValidasiException($"{k.Nama} sudah nonaktif.");
            sesi.WajibAksesKelompok(k.KelompokId);
            ValidasiBaris(u, p, k, sekarang);

            adaMap.TryGetValue(u.KaderId, out var s);
            var catatan = Teks.Bersihkan(u.Catatan);
            var tautan = Teks.Bersihkan(u.Tautan);
            bool barisBerubah = false;

            if (!u.Terkumpul)
            {
                if (s is null) continue;
                db.Pengumpulan.Remove(s);
                db.Catat(sesi.Id, AksiLog.Hapus, "Pengumpulan", s.Id, $"Membatalkan pengumpulan {p.Kode} {k.Nama}", sekarang);
                berubah++;
                continue;
            }

            if (s is null)
            {
                s = new Pengumpulan { KaderId = k.Id, PenugasanId = p.Id, WaktuKumpul = u.WaktuKumpul!.Value, Tautan = tautan, Catatan = catatan };
                db.Pengumpulan.Add(s);
                var status = AturanBisnis.StatusPengumpulan(p.Tenggat, s.WaktuKumpul) == StatusPengumpulan.Terlambat ? "terlambat" : "tepat waktu";
                db.Catat(sesi.Id, AksiLog.Ubah, "Pengumpulan", null, $"Mencatat pengumpulan {p.Kode} {k.Nama} ({status})", sekarang);
                barisBerubah = true;
            }
            else
            {
                if (s.WaktuKumpul != u.WaktuKumpul!.Value)
                {
                    db.Catat(sesi.Id, AksiLog.Ubah, "Pengumpulan", s.Id,
                        $"Waktu kumpul {p.Kode} {k.Nama}: {s.WaktuKumpul:dd/MM HH.mm} → {u.WaktuKumpul.Value:dd/MM HH.mm}", sekarang);
                    s.WaktuKumpul = u.WaktuKumpul.Value;
                    barisBerubah = true;
                }
                if (s.Tautan != tautan)
                {
                    var ket = tautan is null ? "Menghapus" : s.Tautan is null ? "Menambahkan" : "Mengganti";
                    db.Catat(sesi.Id, AksiLog.Ubah, "Pengumpulan", s.Id, $"{ket} tautan tugas {p.Kode} {k.Nama}", sekarang);
                    s.Tautan = tautan;
                    barisBerubah = true;
                }
                if (s.Catatan != catatan)
                {
                    s.Catatan = catatan;
                    barisBerubah = true;
                }
            }

            if (s.Nilai != u.Nilai)
            {
                if (u.Nilai is null)
                {
                    db.Catat(sesi.Id, AksiLog.Ubah, "Pengumpulan", s.Id == 0 ? null : s.Id,
                        $"Menghapus nilai {p.Kode} {k.Nama} ({Teks.Nilai(s.Nilai)})", sekarang);
                    s.Nilai = null;
                    s.DinilaiOleh = null;
                    s.DinilaiPada = null;
                }
                else
                {
                    if (s.Nilai is double lama) // FR-30: catat nilai lama dan nilai baru
                        db.Catat(sesi.Id, AksiLog.Ubah, "Pengumpulan", s.Id,
                            $"Nilai {p.Kode} {k.Nama}: {Teks.Nilai(lama)} → {Teks.Nilai(u.Nilai)}", sekarang);
                    else
                    {
                        dinilaiBaru++;
                        kelompokDinilai.Add(k.Kelompok!.Nomor);
                    }
                    s.Nilai = u.Nilai;
                    s.DinilaiOleh = sesi.Id;
                    s.DinilaiPada = sekarang;
                }
                barisBerubah = true;
            }

            if (barisBerubah) berubah++;
        }

        if (dinilaiBaru > 0)
        {
            var kel = string.Join(", ", kelompokDinilai.Select(n => $"{n:00}"));
            db.Catat(sesi.Id, AksiLog.Nilai, "Pengumpulan", null, $"Menilai {p.Kode} untuk {dinilaiBaru} kader Kelompok {kel}", sekarang);
        }

        // Satu SaveChanges = satu transaksi (NFR-05).
        await db.SimpanAsync(ct);
        return berubah;
    }

    private static void ValidasiBaris(PerubahanPenilaian u, Penugasan p, Kader k, DateTime sekarang)
    {
        if (u.WaktuKumpul is DateTime w)
        {
            if (w > sekarang.AddMinutes(1))
                throw new ValidasiException($"Waktu kumpul {k.Nama} tidak boleh di masa depan.");
            if (w < p.TanggalMulai)
                throw new ValidasiException($"Waktu kumpul {k.Nama} lebih awal dari tanggal mulai {p.Kode}.");
        }
        else if (u.Nilai is not null)
        {
            throw new ValidasiException($"Nilai {k.Nama} hanya dapat diisi setelah pengumpulan dicatat."); // FR-29
        }

        if (u.Nilai is double n && (double.IsNaN(n) || n < 0 || n > 100))
            throw new ValidasiException($"Nilai {k.Nama} harus antara 0 dan 100.");
        if (u.Tautan is not null && u.WaktuKumpul is null && u.Tautan.Trim().Length > 0)
            throw new ValidasiException($"Tautan tugas {k.Nama} hanya dapat diisi setelah pengumpulan dicatat.");
        if (!AturanBisnis.TautanValid(u.Tautan))
            throw new ValidasiException($"Tautan tugas {k.Nama} tidak valid. Gunakan alamat lengkap yang diawali http:// atau https://.");
        if (u.Catatan is { Length: > 500 })
            throw new ValidasiException($"Catatan {k.Nama} maksimal 500 karakter.");
    }
}
