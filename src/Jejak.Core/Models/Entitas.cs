namespace Jejak.Core.Models;

/// <summary>Peran pengguna aplikasi (PRD 5.1).</summary>
public enum Peran
{
    Admin,
    Pemandu
}

/// <summary>Kategori penugasan.</summary>
public enum KategoriPenugasan
{
    Individu,
    Kelompok
}

/// <summary>Akun yang dapat login: Admin (SC) atau Pemandu. Tabel <c>pengguna</c>.</summary>
public class Pengguna
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Nama { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Peran Peran { get; set; }
    public string Tema { get; set; } = "light";
    public bool Aktif { get; set; } = true;
    public DateTime? LoginTerakhir { get; set; }
    public DateTime DibuatPada { get; set; }

    /// <summary>Kelompok yang dibimbing (hanya untuk Pemandu).</summary>
    public Kelompok? KelompokBinaan { get; set; }
}

/// <summary>Kelompok kader. Tabel <c>kelompok</c>.</summary>
public class Kelompok
{
    public int Id { get; set; }
    public int Nomor { get; set; }
    public string Nama { get; set; } = "";
    public string? Deskripsi { get; set; }
    public int? PemanduId { get; set; }
    public Pengguna? Pemandu { get; set; }
    public DateTime DibuatPada { get; set; }

    public List<Kader> Anggota { get; set; } = new();
}

/// <summary>Peserta kaderisasi. Tabel <c>kader</c>.</summary>
public class Kader
{
    public int Id { get; set; }
    public string Nrp { get; set; } = "";
    public string Nama { get; set; } = "";
    public int Angkatan { get; set; }
    public string? Kontak { get; set; }
    public int KelompokId { get; set; }
    public Kelompok? Kelompok { get; set; }
    public bool Aktif { get; set; } = true;
    public DateTime DibuatPada { get; set; }
    public DateTime? DiubahPada { get; set; }

    public List<Pengumpulan> Pengumpulan { get; set; } = new();
}

/// <summary>Penugasan kaderisasi. Tabel <c>penugasan</c>.</summary>
public class Penugasan
{
    public int Id { get; set; }
    public string Kode { get; set; } = "";
    public string Judul { get; set; } = "";
    public string? Deskripsi { get; set; }
    public KategoriPenugasan Kategori { get; set; }
    public DateTime TanggalMulai { get; set; }
    public DateTime Tenggat { get; set; }
    public int Bobot { get; set; }
    public bool Aktif { get; set; } = true;
    public DateTime DibuatPada { get; set; }
}

/// <summary>
/// Satu kader telah mengumpulkan satu penugasan. Tabel <c>pengumpulan</c>.
/// Tidak adanya baris berarti "Belum Mengumpulkan".
/// </summary>
public class Pengumpulan
{
    public int Id { get; set; }
    public int KaderId { get; set; }
    public Kader? Kader { get; set; }
    public int PenugasanId { get; set; }
    public Penugasan? Penugasan { get; set; }
    public DateTime WaktuKumpul { get; set; }

    /// <summary>URL hasil tugas (mis. berkas GDrive); berkasnya tidak disimpan di database (FR-39).</summary>
    public string? Tautan { get; set; }

    public double? Nilai { get; set; }
    public string? Catatan { get; set; }
    public int? DinilaiOleh { get; set; }
    public Pengguna? Penilai { get; set; }
    public DateTime? DinilaiPada { get; set; }
}

/// <summary>Jejak audit perubahan data dan sesi. Tabel <c>log_aktivitas</c>.</summary>
public class LogAktivitas
{
    public int Id { get; set; }
    public int? PenggunaId { get; set; }
    public Pengguna? Pengguna { get; set; }
    public string Aksi { get; set; } = "";
    public string Entitas { get; set; } = "";
    public int? EntitasId { get; set; }
    public string Keterangan { get; set; } = "";
    public DateTime Waktu { get; set; }
}

/// <summary>Pengaturan global berbentuk kunci–nilai. Tabel <c>pengaturan</c>.</summary>
public class Pengaturan
{
    public string Kunci { get; set; } = "";
    public string Nilai { get; set; } = "";
    public string? Keterangan { get; set; }
    public DateTime DiubahPada { get; set; }
}

/// <summary>Nilai kolom <c>log_aktivitas.aksi</c> yang diizinkan CHECK constraint.</summary>
public static class AksiLog
{
    public const string Login = "Login";
    public const string LoginGagal = "Login Gagal";
    public const string Logout = "Logout";
    public const string Tambah = "Tambah";
    public const string Ubah = "Ubah";
    public const string Hapus = "Hapus";
    public const string Nilai = "Nilai";

    public static readonly IReadOnlyList<string> Semua = new[] { Login, LoginGagal, Logout, Tambah, Ubah, Hapus, Nilai };
}

/// <summary>Kunci pada tabel <c>pengaturan</c>.</summary>
public static class KunciPengaturan
{
    public const string R1MinBelum = "risiko_r1_min_belum";
    public const string R2MinKetuntasan = "risiko_r2_min_ketuntasan";
    public const string R3MinNilai = "risiko_r3_min_nilai";
    public const string R3MinDinilai = "risiko_r3_min_dinilai";
    public const string NamaProgram = "program_nama";
}
