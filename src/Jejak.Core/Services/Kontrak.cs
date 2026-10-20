using Jejak.Core.Models;
using Jejak.Core.Rules;

namespace Jejak.Core.Services;

// =====================================================================
//  Infrastruktur umum
// =====================================================================

/// <summary>Sumber waktu; diabstraksikan agar aturan berbasis waktu dapat diuji.</summary>
public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

/// <summary>Identitas pengguna yang sedang login.</summary>
public sealed record SesiPengguna(int Id, string Username, string Nama, Peran Peran, string Tema, int? KelompokId, string? KelompokNama)
{
    public bool IsAdmin => Peran == Peran.Admin;
    public string LabelPeran => IsAdmin ? "Admin · SC" : KelompokNama is null ? "Pemandu" : $"Pemandu · {KelompokNama}";
}

public interface ISessionContext
{
    SesiPengguna? Current { get; }
    void Mulai(SesiPengguna sesi);
    void Akhiri();
    void PerbaruiTema(string tema);

    /// <summary>Sesi aktif; melempar <see cref="AksesDitolakException"/> bila belum login.</summary>
    SesiPengguna Wajib();
}

/// <summary>Kesalahan validasi yang aman ditampilkan ke pengguna.</summary>
public class ValidasiException : Exception
{
    public ValidasiException(string pesan, string? kolom = null) : base(pesan) => Kolom = kolom;

    /// <summary>Nama properti formulir yang bermasalah (opsional).</summary>
    public string? Kolom { get; }
}

/// <summary>Operasi tidak diizinkan untuk peran pengguna saat ini (PRD 5.2).</summary>
public class AksesDitolakException : Exception
{
    public AksesDitolakException(string pesan = "Anda tidak memiliki akses untuk tindakan ini.") : base(pesan) { }
}

// =====================================================================
//  Ringkasan data (read model)
// =====================================================================

public sealed record KaderRingkas(
    int Id,
    string Nrp,
    string Nama,
    int Angkatan,
    string? Kontak,
    int KelompokId,
    int KelompokNomor,
    string KelompokNama,
    string? PemanduNama,
    bool Aktif,
    DateTime DibuatPada,
    RekapKader? Rekap,
    IReadOnlyList<string> Kriteria)
{
    public bool Berisiko => Kriteria.Count > 0;
    public string Status => !Aktif ? "Nonaktif" : Berisiko ? "Berisiko" : "Aktif";
    public string KelompokLabel => $"Kelompok {KelompokNomor:00} · {KelompokNama}";
}

public sealed record PenugasanRingkas(
    int Id,
    string Kode,
    string Judul,
    string? Deskripsi,
    KategoriPenugasan Kategori,
    DateTime TanggalMulai,
    DateTime Tenggat,
    int Bobot,
    StatusPenugasan Status,
    int JumlahKumpul,
    int JumlahKader)
{
    public bool JatuhTempo => Status == StatusPenugasan.Selesai;
    public double PersenKumpul => JumlahKader == 0 ? 0 : 100.0 * JumlahKumpul / JumlahKader;
    public string Label => $"{Kode} · {Judul}";
}

public sealed record KelompokRingkas(
    int Id,
    int Nomor,
    string Nama,
    string? Deskripsi,
    int? PemanduId,
    string? PemanduNama,
    int JumlahKader,
    double? RataKetuntasan,
    double? RataNilai,
    int JumlahBerisiko)
{
    public string Label => $"Kelompok {Nomor:00} · {Nama}";
    public string LabelPendek => $"Kelompok {Nomor:00}";
}

/// <summary>
/// Potret data dalam cakupan pengguna saat ini: Admin melihat semua,
/// Pemandu hanya kelompoknya (PRD 5.2, FR-10).
/// </summary>
public sealed record RekapSnapshot(
    DateTime Waktu,
    AmbangRisiko Ambang,
    IReadOnlyList<KaderRingkas> Kader,
    IReadOnlyList<PenugasanRingkas> Penugasan,
    IReadOnlyList<KelompokRingkas> Kelompok)
{
    public IEnumerable<KaderRingkas> KaderAktif => Kader.Where(k => k.Aktif);

    public IReadOnlyList<KaderRingkas> KaderBerisiko => KaderAktif
        .Where(k => k.Berisiko)
        .OrderByDescending(k => k.Kriteria.Count)
        .ThenBy(k => k.Rekap?.Ketuntasan ?? -1)
        .ThenBy(k => k.Rekap?.NilaiAkhir ?? -1)
        .ToList();

    public double? RataKetuntasan => AturanBisnis.RataRata(KaderAktif.Select(k => k.Rekap?.Ketuntasan));
}

public interface IRekapService
{
    Task<RekapSnapshot> AmbilAsync(CancellationToken ct = default);
}

// =====================================================================
//  Autentikasi dan pengaturan
// =====================================================================

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IAuthService
{
    /// <summary>Login; mengembalikan null bila kredensial salah (pesan umum, FR-01).</summary>
    Task<SesiPengguna?> LoginAsync(string username, string password, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task GantiPasswordAsync(string passwordLama, string passwordBaru, CancellationToken ct = default);
    Task SimpanTemaAsync(string tema, CancellationToken ct = default);
}

public interface IPengaturanService
{
    Task<AmbangRisiko> AmbangAsync(CancellationToken ct = default);
    Task SimpanAmbangAsync(AmbangRisiko ambang, CancellationToken ct = default);
    Task<string> NamaProgramAsync(CancellationToken ct = default);
}

// =====================================================================
//  Kader, kelompok, penugasan
// =====================================================================

public sealed record KaderInput(int? Id, string Nrp, string Nama, int Angkatan, string? Kontak, int KelompokId, bool Aktif);

public sealed record JejakPenugasan(
    PenugasanRingkas Penugasan,
    StatusPengumpulan Status,
    DateTime? WaktuKumpul,
    string? Tautan,
    double? Nilai,
    string? Catatan,
    double? RataKelompok);

public sealed record KaderDetail(KaderRingkas Kader, AmbangRisiko Ambang, IReadOnlyList<JejakPenugasan> Jejak);

public interface IKaderService
{
    Task<KaderDetail> DetailAsync(int kaderId, CancellationToken ct = default);
    Task<int> SimpanAsync(KaderInput input, CancellationToken ct = default);

    /// <summary>Menghapus kader; bila sudah memiliki pengumpulan, kader dinonaktifkan (FR-16). Mengembalikan pesan hasil.</summary>
    Task<string> HapusAsync(int kaderId, CancellationToken ct = default);
}

public sealed record KelompokInput(int? Id, int Nomor, string Nama, string? Deskripsi, int? PemanduId);

public sealed record PilihanPengguna(int Id, string Nama, string Username);

public interface IKelompokService
{
    Task<int> SimpanAsync(KelompokInput input, CancellationToken ct = default);
    Task HapusAsync(int kelompokId, CancellationToken ct = default);

    /// <summary>Pemandu aktif yang belum membimbing kelompok lain (ditambah pemandu kelompok ini).</summary>
    Task<IReadOnlyList<PilihanPengguna>> PemanduTersediaAsync(int? kelompokId, CancellationToken ct = default);
}

public sealed record PenugasanInput(
    int? Id,
    string Kode,
    string Judul,
    string? Deskripsi,
    KategoriPenugasan Kategori,
    DateTime TanggalMulai,
    DateTime Tenggat,
    int Bobot);

public interface IPenugasanService
{
    Task<int> SimpanAsync(PenugasanInput input, CancellationToken ct = default);
    Task<string> HapusAsync(int penugasanId, CancellationToken ct = default);
}

// =====================================================================
//  Pengumpulan dan penilaian
// =====================================================================

public sealed record BarisPenilaian(
    int KaderId,
    string Nrp,
    string Nama,
    int? PengumpulanId,
    DateTime? WaktuKumpul,
    string? Tautan,
    double? Nilai,
    string? Catatan,
    string? PenilaiNama);

public sealed record LembarPenilaian(PenugasanRingkas Penugasan, KelompokRingkas Kelompok, IReadOnlyList<BarisPenilaian> Baris);

/// <summary>Keadaan akhir yang diinginkan untuk satu kader pada satu penugasan.</summary>
public sealed record PerubahanPenilaian(int KaderId, DateTime? WaktuKumpul, string? Tautan, double? Nilai, string? Catatan)
{
    public bool Terkumpul => WaktuKumpul.HasValue;
}

public interface IPenilaianService
{
    Task<LembarPenilaian> AmbilAsync(int penugasanId, int kelompokId, CancellationToken ct = default);

    /// <summary>Menyimpan perubahan dalam satu transaksi (NFR-05). Mengembalikan jumlah baris yang berubah.</summary>
    Task<int> SimpanAsync(int penugasanId, IReadOnlyList<PerubahanPenilaian> perubahan, CancellationToken ct = default);
}

// =====================================================================
//  Pengguna, log, ekspor
// =====================================================================

public sealed record PenggunaItem(
    int Id,
    string Username,
    string Nama,
    Peran Peran,
    bool Aktif,
    DateTime? LoginTerakhir,
    string? KelompokBinaan);

public sealed record PenggunaInput(int? Id, string Username, string Nama, Peran Peran, bool Aktif, string? PasswordBaru);

public interface IPenggunaService
{
    Task<IReadOnlyList<PenggunaItem>> DaftarAsync(CancellationToken ct = default);
    Task<int> SimpanAsync(PenggunaInput input, CancellationToken ct = default);
    Task SetAktifAsync(int penggunaId, bool aktif, CancellationToken ct = default);
}

public sealed record LogItem(int Id, DateTime Waktu, string? PenggunaNama, string Aksi, string Entitas, string Keterangan);

public sealed record FilterLog(
    string? Teks = null,
    int? PenggunaId = null,
    string? Aksi = null,
    DateTime? Dari = null,
    DateTime? Sampai = null,
    int Halaman = 1,
    int UkuranHalaman = 15);

public sealed record HalamanLog(IReadOnlyList<LogItem> Item, int Total);

public interface ILogService
{
    Task<HalamanLog> CariAsync(FilterLog filter, CancellationToken ct = default);

    /// <summary>Aktivitas terbaru dalam cakupan pengguna (Admin: semua, Pemandu: miliknya sendiri).</summary>
    Task<IReadOnlyList<LogItem>> TerbaruAsync(int jumlah, CancellationToken ct = default);
}

public interface IEksporService
{
    /// <summary>CSV rekap nilai sesuai cakupan akses pengguna (FR-38).</summary>
    Task<string> RekapNilaiCsvAsync(CancellationToken ct = default);

    Task<string> KaderBerisikoCsvAsync(CancellationToken ct = default);

    Task<string> LogCsvAsync(FilterLog filter, CancellationToken ct = default);
}
