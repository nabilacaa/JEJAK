using System.Globalization;
using System.Text;
using Jejak.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jejak.Data;

/// <summary>
/// DbContext JEJAK. Skema dibuat oleh jejak.sql (bukan migrasi EF), sehingga
/// pemetaan di sini mengikuti nama tabel/kolom pada skrip tersebut.
/// </summary>
public class JejakDbContext : DbContext
{
    public JejakDbContext(DbContextOptions<JejakDbContext> options) : base(options) { }

    public DbSet<Pengguna> Pengguna => Set<Pengguna>();
    public DbSet<Kelompok> Kelompok => Set<Kelompok>();
    public DbSet<Kader> Kader => Set<Kader>();
    public DbSet<Penugasan> Penugasan => Set<Penugasan>();
    public DbSet<Pengumpulan> Pengumpulan => Set<Pengumpulan>();
    public DbSet<LogAktivitas> LogAktivitas => Set<LogAktivitas>();
    public DbSet<Pengaturan> Pengaturan => Set<Pengaturan>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Pengguna>(e =>
        {
            e.ToTable("pengguna");
            e.Property(x => x.Peran).HasConversion<string>();
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<Kelompok>(e =>
        {
            // Tabel dengan trigger diberi tahu ke EF agar tidak memakai klausa RETURNING.
            e.ToTable("kelompok", tb =>
            {
                tb.HasTrigger("trg_kelompok_pemandu_ins");
                tb.HasTrigger("trg_kelompok_pemandu_upd");
            });
            e.HasOne(x => x.Pemandu)
                .WithOne(p => p.KelompokBinaan)
                .HasForeignKey<Kelompok>(x => x.PemanduId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Kader>(e =>
        {
            e.ToTable("kader", tb => tb.HasTrigger("trg_kader_diubah"));
            e.HasIndex(x => x.Nrp).IsUnique();
            e.HasOne(x => x.Kelompok)
                .WithMany(k => k.Anggota)
                .HasForeignKey(x => x.KelompokId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Penugasan>(e =>
        {
            e.ToTable("penugasan");
            e.Property(x => x.Kategori).HasConversion<string>();
        });

        b.Entity<Pengumpulan>(e =>
        {
            e.ToTable("pengumpulan", tb =>
            {
                tb.HasTrigger("trg_pengumpulan_penugasan_aktif");
                tb.HasTrigger("trg_pengumpulan_kader_aktif");
                tb.HasTrigger("trg_pengumpulan_penilai_ins");
                tb.HasTrigger("trg_pengumpulan_penilai_upd");
            });
            e.HasIndex(x => new { x.KaderId, x.PenugasanId }).IsUnique();
            e.HasOne(x => x.Kader).WithMany(k => k.Pengumpulan).HasForeignKey(x => x.KaderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Penugasan).WithMany().HasForeignKey(x => x.PenugasanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Penilai).WithMany().HasForeignKey(x => x.DinilaiOleh).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LogAktivitas>(e =>
        {
            e.ToTable("log_aktivitas");
            e.HasOne(x => x.Pengguna).WithMany().HasForeignKey(x => x.PenggunaId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Pengaturan>(e =>
        {
            e.ToTable("pengaturan");
            e.HasKey(x => x.Kunci);
        });

        // Konvensi: kolom snake_case dan waktu sebagai TEXT 'yyyy-MM-dd HH:mm:ss'.
        var konverter = new DateTimeTextConverter();
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(KeSnakeCase(property.Name));
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(konverter);
            }
        }
    }

    internal static string KeSnakeCase(string nama)
    {
        var sb = new StringBuilder(nama.Length + 8);
        for (int i = 0; i < nama.Length; i++)
        {
            char c = nama[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Menyimpan DateTime sebagai TEXT dengan format yang sama dengan
/// <c>datetime('now','localtime')</c> SQLite, sehingga perbandingan teks di
/// view dan trigger (mis. waktu_kumpul &lt;= tenggat) tetap benar.
/// </summary>
public sealed class DateTimeTextConverter : ValueConverter<DateTime, string>
{
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    public DateTimeTextConverter()
        : base(v => KeTeks(v), v => DariTeks(v))
    {
    }

    public static string KeTeks(DateTime v) => v.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTime DariTeks(string v)
    {
        if (DateTime.TryParseExact(v, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hasil))
            return hasil;
        return DateTime.Parse(v, CultureInfo.InvariantCulture);
    }
}
