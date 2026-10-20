using System.Globalization;
using Jejak.Core.Models;
using Jejak.Core.Rules;
using Jejak.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jejak.Data.Services;

public sealed class RekapService : IRekapService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public RekapService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    public async Task<RekapSnapshot> AmbilAsync(CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        await using var db = await _db.CreateDbContextAsync(ct);
        return await RekapBuilder.BangunAsync(db, sesi, _clock.Now, ct);
    }
}

public sealed class AuthService : IAuthService
{
    public const int PanjangMinimumPassword = 8;

    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;

    public AuthService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IPasswordHasher hasher, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _hasher = hasher;
        _clock = clock;
    }

    public async Task<SesiPengguna?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var nama = (username ?? "").Trim();
        var sekarang = _clock.Now;
        await using var db = await _db.CreateDbContextAsync(ct);

        // Query berparameter melalui EF Core; kolom username ber-collation NOCASE (NFR-02).
        var u = nama.Length == 0
            ? null
            : await db.Pengguna.Include(p => p.KelompokBinaan).FirstOrDefaultAsync(p => p.Username == nama, ct);

        if (u is null || !u.Aktif || !_hasher.Verify(password ?? "", u.PasswordHash))
        {
            var alasan = u is null ? "username tidak dikenal" : !u.Aktif ? "akun nonaktif" : "password salah";
            db.Catat(u?.Id, AksiLog.LoginGagal, "Sesi", null, $"Login gagal ({alasan})", sekarang);
            await db.SimpanAsync(ct);
            return null;
        }

        u.LoginTerakhir = sekarang;
        db.Catat(u.Id, AksiLog.Login, "Sesi", null, "Login berhasil", sekarang);
        await db.SimpanAsync(ct);

        var g = u.KelompokBinaan;
        var sesi = new SesiPengguna(u.Id, u.Username, u.Nama, u.Peran, u.Tema, g?.Id, g is null ? null : Teks.LabelPendek(g));
        _sesi.Mulai(sesi);
        return sesi;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var sesi = _sesi.Current;
        if (sesi is null) return;
        await using var db = await _db.CreateDbContextAsync(ct);
        db.Catat(sesi.Id, AksiLog.Logout, "Sesi", null, "Logout", _clock.Now);
        await db.SimpanAsync(ct);
        _sesi.Akhiri();
    }

    public async Task GantiPasswordAsync(string passwordLama, string passwordBaru, CancellationToken ct = default)
    {
        var sesi = _sesi.Wajib();
        if (string.IsNullOrEmpty(passwordBaru) || passwordBaru.Length < PanjangMinimumPassword)
            throw new ValidasiException($"Password baru minimal {PanjangMinimumPassword} karakter.", "PasswordBaru");
        if (passwordBaru == passwordLama)
            throw new ValidasiException("Password baru harus berbeda dari password lama.", "PasswordBaru");

        await using var db = await _db.CreateDbContextAsync(ct);
        var u = await db.Pengguna.FirstAsync(p => p.Id == sesi.Id, ct);
        if (!_hasher.Verify(passwordLama ?? "", u.PasswordHash))
            throw new ValidasiException("Password lama tidak sesuai.", "PasswordLama");

        u.PasswordHash = _hasher.Hash(passwordBaru);
        db.Catat(u.Id, AksiLog.Ubah, "Pengguna", u.Id, "Mengganti password akun sendiri", _clock.Now);
        await db.SimpanAsync(ct);
    }

    public async Task SimpanTemaAsync(string tema, CancellationToken ct = default)
    {
        if (tema is not ("light" or "dark")) throw new ValidasiException("Tema tidak dikenal.");
        var sesi = _sesi.Wajib();
        await using var db = await _db.CreateDbContextAsync(ct);
        var u = await db.Pengguna.FirstAsync(p => p.Id == sesi.Id, ct);
        u.Tema = tema;
        await db.SimpanAsync(ct);
        _sesi.PerbaruiTema(tema);
    }
}

public sealed class PengaturanService : IPengaturanService
{
    private readonly IDbContextFactory<JejakDbContext> _db;
    private readonly ISessionContext _sesi;
    private readonly IClock _clock;

    public PengaturanService(IDbContextFactory<JejakDbContext> db, ISessionContext sesi, IClock clock)
    {
        _db = db;
        _sesi = sesi;
        _clock = clock;
    }

    public async Task<AmbangRisiko> AmbangAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.AmbangAsync(ct);
    }

    public async Task<string> NamaProgramAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var p = await db.Pengaturan.AsNoTracking().FirstOrDefaultAsync(x => x.Kunci == KunciPengaturan.NamaProgram, ct);
        return p?.Nilai ?? "Kaderisasi";
    }

    public async Task SimpanAmbangAsync(AmbangRisiko ambang, CancellationToken ct = default)
    {
        var sesi = _sesi.WajibAdmin();
        if (ambang.MinBelum is < 1 or > 20) throw new ValidasiException("Ambang R1 harus antara 1 dan 20 penugasan.", "MinBelum");
        if (ambang.MinKetuntasan is < 0 or > 100) throw new ValidasiException("Ambang R2 harus antara 0 dan 100%.", "MinKetuntasan");
        if (ambang.MinNilai is < 0 or > 100) throw new ValidasiException("Ambang R3 harus antara 0 dan 100.", "MinNilai");
        if (ambang.MinDinilai is < 1 or > 20) throw new ValidasiException("Syarat jumlah penugasan dinilai harus antara 1 dan 20.", "MinDinilai");

        await using var db = await _db.CreateDbContextAsync(ct);
        var lama = await db.AmbangAsync(ct);
        var sekarang = _clock.Now;
        var inv = CultureInfo.InvariantCulture;
        var perubahan = new List<string>();

        async Task Set(string kunci, string label, string nilaiLama, string nilaiBaru, string keterangan)
        {
            var row = await db.Pengaturan.FirstOrDefaultAsync(p => p.Kunci == kunci, ct);
            if (row is null)
            {
                row = new Pengaturan { Kunci = kunci, Keterangan = keterangan };
                db.Pengaturan.Add(row);
            }
            if (row.Nilai == nilaiBaru) return;
            row.Nilai = nilaiBaru;
            row.DiubahPada = sekarang;
            perubahan.Add($"{label}: {nilaiLama} → {nilaiBaru}");
        }

        await Set(KunciPengaturan.R1MinBelum, "R1", lama.MinBelum.ToString(inv), ambang.MinBelum.ToString(inv),
            "R1: jumlah penugasan jatuh tempo belum dikumpulkan (>=)");
        await Set(KunciPengaturan.R2MinKetuntasan, "R2", lama.MinKetuntasan.ToString("0.##", inv), ambang.MinKetuntasan.ToString("0.##", inv),
            "R2: ketuntasan minimum dalam persen (<)");
        await Set(KunciPengaturan.R3MinNilai, "R3", lama.MinNilai.ToString("0.##", inv), ambang.MinNilai.ToString("0.##", inv),
            "R3: nilai akhir minimum (<)");
        await Set(KunciPengaturan.R3MinDinilai, "Syarat R3", lama.MinDinilai.ToString(inv), ambang.MinDinilai.ToString(inv),
            "R3 berlaku bila penugasan yang sudah dinilai >= nilai ini");

        if (perubahan.Count == 0) return;
        db.Catat(sesi.Id, AksiLog.Ubah, "Pengaturan", null, "Ambang kader berisiko — " + string.Join(", ", perubahan), sekarang);
        await db.SimpanAsync(ct);
    }
}
