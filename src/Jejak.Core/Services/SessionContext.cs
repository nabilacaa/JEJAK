namespace Jejak.Core.Services;

/// <summary>Menyimpan sesi pengguna yang sedang login selama aplikasi berjalan.</summary>
public sealed class SessionContext : ISessionContext
{
    public SesiPengguna? Current { get; private set; }

    public void Mulai(SesiPengguna sesi) => Current = sesi;

    public void Akhiri() => Current = null;

    public void PerbaruiTema(string tema)
    {
        if (Current is not null) Current = Current with { Tema = tema };
    }

    public SesiPengguna Wajib() => Current ?? throw new AksesDitolakException("Sesi berakhir. Silakan login kembali.");
}

/// <summary>Pemeriksaan hak akses yang dipakai seluruh layanan (lapis kedua setelah UI).</summary>
public static class Otorisasi
{
    public static SesiPengguna WajibAdmin(this ISessionContext sesi)
    {
        var s = sesi.Wajib();
        if (!s.IsAdmin) throw new AksesDitolakException();
        return s;
    }

    /// <summary>Admin boleh mengakses semua kelompok; Pemandu hanya kelompok binaannya.</summary>
    public static void WajibAksesKelompok(this SesiPengguna s, int kelompokId)
    {
        if (s.IsAdmin) return;
        if (s.KelompokId != kelompokId)
            throw new AksesDitolakException("Pemandu hanya dapat mengelola kader di kelompoknya sendiri.");
    }
}
