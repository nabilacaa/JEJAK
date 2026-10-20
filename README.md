# JEJAK — Jurnal Evaluasi dan Jejak Aktivitas Kader

Aplikasi desktop C# WPF (.NET 8, MVVM) untuk memantau penugasan, pengumpulan, dan penilaian kader. Tugas ETS PBKK — Rich UI.

## Struktur

```
Jejak.sln
database/            jejak.sql (skema + data contoh), PDM_JEJAK.png
src/Jejak.Core       entitas, aturan bisnis (BR-01..05), kontrak layanan
src/Jejak.Data       EF Core SQLite, implementasi layanan, inisialisasi database
src/Jejak.App        WPF: Views (XAML), ViewModels, tema Light/Dark, kontrol & grafik kustom
tests/Jejak.Tests    xUnit (aturan bisnis, layanan, RBAC, paritas dengan view SQL)
```

## Menjalankan

Kebutuhan: Windows 10/11, Visual Studio 2022 (workload ".NET desktop development") atau .NET 8 SDK.

- Visual Studio: buka `Jejak.sln`, jadikan `Jejak.App` sebagai startup project, lalu tekan F5.
- CLI: `dotnet run --project src/Jejak.App`

Saat pertama kali dijalankan, database dibuat otomatis dari `database/jejak.sql` di `%LOCALAPPDATA%\JEJAK\jejak.db`. Data contoh dapat dipulihkan dari menu Pengaturan.

Akun contoh (password `jejak123`):
- Admin (SC): `raka.pratama`
- Pemandu Kelompok 04: `daffa.prakoso`

## Membuat .exe

```
dotnet publish src/Jejak.App -p:PublishProfile=win-x64
```

Hasilnya adalah satu berkas `publish/JEJAK.exe` (self-contained, tidak memerlukan instalasi .NET).

## Pengujian

```
dotnet test tests/Jejak.Tests
```
