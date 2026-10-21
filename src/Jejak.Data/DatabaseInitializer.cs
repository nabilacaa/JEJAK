using Microsoft.Data.Sqlite;

namespace Jejak.Data;

/// <summary>
/// Membuat database SQLite dari skrip jejak.sql (skema + data contoh) saat
/// aplikasi pertama kali dijalankan, sehingga .exe dapat langsung dipakai
/// tanpa server database (NFR-10).
/// </summary>
public static class DatabaseInitializer
{
    private const string NamaResource = "Jejak.Data.jejak.sql";

    /// <summary>Lokasi bawaan: %LOCALAPPDATA%\JEJAK\jejak.db (NFR-11).</summary>
    public static string LokasiBawaan =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JEJAK", "jejak.db");

    /// <summary>Connection string dengan foreign key aktif. SQLite mematikannya per koneksi secara bawaan.</summary>
    public static string ConnectionString(string path, bool pooling = true) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = pooling
    }.ToString();

    /// <summary>
    /// Membuat database bila belum ada, atau meningkatkan database lama ke skema terbaru.
    /// Mengembalikan true bila database baru dibuat.
    /// </summary>
    public static bool Pastikan(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            using var koneksi = new SqliteConnection(ConnectionString(path, pooling: false));
            koneksi.Open();
            Tingkatkan(koneksi);
            return false;
        }
        Buat(path);
        return true;
    }

    /// <summary>
    /// Menambahkan kolom yang belum ada pada database dari versi aplikasi sebelumnya tanpa menghapus data.
    /// Saat ini: kolom pengumpulan.tautan (PRD v1.2, FR-39). Aman dijalankan berulang kali.
    /// </summary>
    public static void Tingkatkan(SqliteConnection koneksi)
    {
        if (AdaKolom(koneksi, "pengumpulan", "tautan")) return;
        using var cmd = koneksi.CreateCommand();
        cmd.CommandText =
            "ALTER TABLE pengumpulan ADD COLUMN tautan TEXT CHECK (tautan IS NULL OR " +
            "((tautan LIKE 'http://_%' OR tautan LIKE 'https://_%') AND length(tautan) <= 500 AND instr(tautan, ' ') = 0));";
        cmd.ExecuteNonQuery();
    }

    private static bool AdaKolom(SqliteConnection koneksi, string tabel, string kolom)
    {
        using var cmd = koneksi.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tabel}') WHERE name = $kolom;";
        cmd.Parameters.AddWithValue("$kolom", kolom);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Menghapus database lalu membuat ulang dengan data contoh.</summary>
    public static void BuatUlang(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        Buat(path);
    }

    /// <summary>Menjalankan skrip pada koneksi yang sudah terbuka (dipakai juga oleh unit test in-memory).</summary>
    public static void JalankanSkrip(SqliteConnection koneksi)
    {
        using var cmd = koneksi.CreateCommand();
        cmd.CommandText = BacaSkrip();
        cmd.ExecuteNonQuery();
    }

    public static string BacaSkrip()
    {
        using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream(NamaResource)
            ?? throw new InvalidOperationException($"Resource {NamaResource} tidak ditemukan.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Buat(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Dibuat di berkas sementara agar kegagalan di tengah tidak meninggalkan database setengah jadi.
        var sementara = path + ".baru";
        if (File.Exists(sementara)) File.Delete(sementara);
        try
        {
            using (var koneksi = new SqliteConnection(ConnectionString(sementara, pooling: false)))
            {
                koneksi.Open();
                JalankanSkrip(koneksi);
            }
            File.Move(sementara, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(sementara)) File.Delete(sementara);
            throw;
        }
    }
}
