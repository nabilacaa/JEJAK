-- =====================================================================
--  JEJAK — Jurnal Evaluasi dan Jejak Aktivitas Kader
--  Skrip database (SQLite 3.35+)
--  ETS Pemrograman Berbasis Kerangka Kerja (PBKK) 2026
--
--  Isi berkas:
--    1. Skema tabel, constraint, indeks, dan trigger
--    2. View perhitungan aturan bisnis (PRD Bagian 7, BR-01 s.d. BR-05)
--    3. Data contoh (seed) yang konsisten dengan desain UI/UX
--
--  Konvensi:
--    * Waktu disimpan sebagai TEXT ISO-8601 waktu lokal 'YYYY-MM-DD HH:MM:SS'.
--    * Boolean disimpan sebagai INTEGER 0/1.
--    * Status pengumpulan, status penugasan, ketuntasan, nilai akhir, dan
--      status berisiko TIDAK disimpan sebagai kolom; semuanya dihitung
--      (lihat bagian 2) agar tidak pernah tidak sinkron dengan data sumber.
--
--  Cara pakai:
--    sqlite3 jejak.db < jejak.sql
-- =====================================================================

PRAGMA foreign_keys = ON;

DROP VIEW  IF EXISTS v_dashboard;
DROP VIEW  IF EXISTS v_rekap_kelompok;
DROP VIEW  IF EXISTS v_kader_berisiko;
DROP VIEW  IF EXISTS v_rekap_kader;
DROP VIEW  IF EXISTS v_status_pengumpulan;
DROP VIEW  IF EXISTS v_penugasan_status;
DROP TABLE IF EXISTS log_aktivitas;
DROP TABLE IF EXISTS pengaturan;
DROP TABLE IF EXISTS pengumpulan;
DROP TABLE IF EXISTS penugasan;
DROP TABLE IF EXISTS kader;
DROP TABLE IF EXISTS kelompok;
DROP TABLE IF EXISTS pengguna;

-- =====================================================================
-- 1. SKEMA
-- =====================================================================

-- Akun yang dapat login: Admin (SC) dan Pemandu.
CREATE TABLE pengguna (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    username        TEXT    NOT NULL UNIQUE COLLATE NOCASE
                            CHECK (length(username) BETWEEN 3 AND 50),
    nama            TEXT    NOT NULL CHECK (length(trim(nama)) > 0),
    password_hash   TEXT    NOT NULL,                       -- BCrypt, tidak pernah teks asli
    peran           TEXT    NOT NULL CHECK (peran IN ('Admin', 'Pemandu')),
    tema            TEXT    NOT NULL DEFAULT 'light' CHECK (tema IN ('light', 'dark')),
    aktif           INTEGER NOT NULL DEFAULT 1 CHECK (aktif IN (0, 1)),
    login_terakhir  TEXT,
    dibuat_pada     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

-- Kelompok kader; satu kelompok dibimbing tepat satu pemandu (BR-06).
CREATE TABLE kelompok (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    nomor           INTEGER NOT NULL UNIQUE CHECK (nomor > 0),
    nama            TEXT    NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(nama)) > 0),
    deskripsi       TEXT,
    pemandu_id      INTEGER UNIQUE
                            REFERENCES pengguna (id) ON UPDATE CASCADE ON DELETE SET NULL,
    dibuat_pada     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

-- Peserta kaderisasi. Kader yang sudah punya pengumpulan tidak bisa
-- dihapus permanen (FK RESTRICT); gunakan aktif = 0 (BR-06, FR-16).
CREATE TABLE kader (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    nrp             TEXT    NOT NULL UNIQUE
                            CHECK (length(nrp) = 10 AND nrp NOT GLOB '*[^0-9]*'),
    nama            TEXT    NOT NULL CHECK (length(trim(nama)) > 0),
    angkatan        INTEGER NOT NULL CHECK (angkatan BETWEEN 2000 AND 2100),
    kontak          TEXT,
    kelompok_id     INTEGER NOT NULL
                            REFERENCES kelompok (id) ON UPDATE CASCADE ON DELETE RESTRICT,
    aktif           INTEGER NOT NULL DEFAULT 1 CHECK (aktif IN (0, 1)),
    dibuat_pada     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    diubah_pada     TEXT
);

-- Penugasan kaderisasi. Bobot dipakai pada nilai akhir berbobot (BR-04).
CREATE TABLE penugasan (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    kode            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    judul           TEXT    NOT NULL CHECK (length(trim(judul)) > 0),
    deskripsi       TEXT,
    kategori        TEXT    NOT NULL CHECK (kategori IN ('Individu', 'Kelompok')),
    tanggal_mulai   TEXT    NOT NULL,
    tenggat         TEXT    NOT NULL,
    bobot           INTEGER NOT NULL CHECK (bobot BETWEEN 1 AND 100),
    aktif           INTEGER NOT NULL DEFAULT 1 CHECK (aktif IN (0, 1)),
    dibuat_pada     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    CHECK (tenggat >= tanggal_mulai)                                   -- FR-24
);

-- Satu baris = satu kader telah mengumpulkan satu penugasan.
-- Tidak ada baris = "Belum Mengumpulkan". Tepat Waktu/Terlambat dihitung
-- dari waktu_kumpul dibanding tenggat (BR-01), bukan disimpan.
-- tautan menyimpan URL hasil tugas (mis. berkas GDrive) yang dibuka pemandu
-- sebelum menilai; berkasnya sendiri tidak disimpan di database (FR-39).
-- dinilai_oleh menyimpan penanggung jawab setiap nilai. Siapa yang mencatat
-- pengumpulan tidak disimpan di sini karena sudah terekam di log_aktivitas.
CREATE TABLE pengumpulan (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    kader_id        INTEGER NOT NULL
                            REFERENCES kader (id) ON UPDATE CASCADE ON DELETE RESTRICT,
    penugasan_id    INTEGER NOT NULL
                            REFERENCES penugasan (id) ON UPDATE CASCADE ON DELETE RESTRICT,
    waktu_kumpul    TEXT    NOT NULL,
    tautan          TEXT    CHECK (tautan IS NULL OR                                -- FR-39
                                   ((tautan LIKE 'http://_%' OR tautan LIKE 'https://_%')
                                    AND length(tautan) <= 500 AND instr(tautan, ' ') = 0)),
    nilai           REAL    CHECK (nilai IS NULL OR nilai BETWEEN 0 AND 100),   -- FR-29
    catatan         TEXT,
    dinilai_oleh    INTEGER REFERENCES pengguna (id) ON UPDATE CASCADE ON DELETE RESTRICT,
    dinilai_pada    TEXT,
    UNIQUE (kader_id, penugasan_id),                                   -- BR-06
    -- nilai, penilai, dan waktu penilaian selalu terisi bersamaan
    CHECK ((nilai IS NULL) = (dinilai_oleh IS NULL) AND (nilai IS NULL) = (dinilai_pada IS NULL))
);

-- Ambang kriteria kader berisiko (BR-05) dan pengaturan global lain.
-- Siapa yang mengubah pengaturan dicatat di log_aktivitas.
CREATE TABLE pengaturan (
    kunci           TEXT    PRIMARY KEY,
    nilai           TEXT    NOT NULL,
    keterangan      TEXT,
    diubah_pada     TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

-- Jejak audit seluruh perubahan data dan sesi (FR-33).
CREATE TABLE log_aktivitas (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    pengguna_id     INTEGER REFERENCES pengguna (id) ON UPDATE CASCADE ON DELETE SET NULL,
    aksi            TEXT    NOT NULL
                            CHECK (aksi IN ('Login', 'Login Gagal', 'Logout', 'Tambah', 'Ubah', 'Hapus', 'Nilai')),
    entitas         TEXT    NOT NULL,
    entitas_id      INTEGER,
    keterangan      TEXT    NOT NULL,
    waktu           TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

-- Indeks untuk pencarian, filter, dan agregasi dashboard.
CREATE INDEX ix_kader_kelompok        ON kader (kelompok_id);
CREATE INDEX ix_kader_nama            ON kader (nama COLLATE NOCASE);
CREATE INDEX ix_kader_angkatan        ON kader (angkatan);
CREATE INDEX ix_pengumpulan_penugasan ON pengumpulan (penugasan_id);
CREATE INDEX ix_pengumpulan_penilai   ON pengumpulan (dinilai_oleh);
CREATE INDEX ix_log_waktu             ON log_aktivitas (waktu DESC);
CREATE INDEX ix_log_pengguna          ON log_aktivitas (pengguna_id);

-- Pemandu kelompok harus akun berperan 'Pemandu' (BR-06).
CREATE TRIGGER trg_kelompok_pemandu_ins
BEFORE INSERT ON kelompok
WHEN NEW.pemandu_id IS NOT NULL
 AND (SELECT peran FROM pengguna WHERE id = NEW.pemandu_id) IS NOT 'Pemandu'
BEGIN
    SELECT RAISE(ABORT, 'Pemandu kelompok harus akun dengan peran Pemandu.');
END;

CREATE TRIGGER trg_kelompok_pemandu_upd
BEFORE UPDATE OF pemandu_id ON kelompok
WHEN NEW.pemandu_id IS NOT NULL
 AND (SELECT peran FROM pengguna WHERE id = NEW.pemandu_id) IS NOT 'Pemandu'
BEGIN
    SELECT RAISE(ABORT, 'Pemandu kelompok harus akun dengan peran Pemandu.');
END;

-- Nilai hanya boleh diisi pada penugasan yang aktif.
CREATE TRIGGER trg_pengumpulan_penugasan_aktif
BEFORE INSERT ON pengumpulan
WHEN (SELECT aktif FROM penugasan WHERE id = NEW.penugasan_id) = 0
BEGIN
    SELECT RAISE(ABORT, 'Penugasan sudah dinonaktifkan.');
END;

-- Pengumpulan hanya untuk kader aktif.
CREATE TRIGGER trg_pengumpulan_kader_aktif
BEFORE INSERT ON pengumpulan
WHEN (SELECT aktif FROM kader WHERE id = NEW.kader_id) = 0
BEGIN
    SELECT RAISE(ABORT, 'Kader nonaktif tidak dapat menerima pengumpulan baru.');
END;

-- Penilai harus Admin aktif atau Pemandu aktif dari kelompok kader tersebut
-- (matriks hak akses PRD 5.2). Lapis ketiga setelah UI dan lapisan Service.
CREATE TRIGGER trg_pengumpulan_penilai_ins
BEFORE INSERT ON pengumpulan
WHEN NEW.dinilai_oleh IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM pengguna u
    WHERE u.id = NEW.dinilai_oleh AND u.aktif = 1
      AND (u.peran = 'Admin'
           OR u.id = (SELECT g.pemandu_id FROM kader k JOIN kelompok g ON g.id = k.kelompok_id
                      WHERE k.id = NEW.kader_id)))
BEGIN
    SELECT RAISE(ABORT, 'Nilai hanya dapat diberikan oleh Admin atau pemandu kelompok kader.');
END;

CREATE TRIGGER trg_pengumpulan_penilai_upd
BEFORE UPDATE OF nilai, dinilai_oleh ON pengumpulan
WHEN NEW.dinilai_oleh IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM pengguna u
    WHERE u.id = NEW.dinilai_oleh AND u.aktif = 1
      AND (u.peran = 'Admin'
           OR u.id = (SELECT g.pemandu_id FROM kader k JOIN kelompok g ON g.id = k.kelompok_id
                      WHERE k.id = NEW.kader_id)))
BEGIN
    SELECT RAISE(ABORT, 'Nilai hanya dapat diberikan oleh Admin atau pemandu kelompok kader.');
END;

-- Cap waktu perubahan data kader.
CREATE TRIGGER trg_kader_diubah
AFTER UPDATE OF nrp, nama, angkatan, kontak, kelompok_id, aktif ON kader
BEGIN
    UPDATE kader SET diubah_pada = datetime('now', 'localtime') WHERE id = NEW.id;
END;

-- =====================================================================
-- 2. VIEW ATURAN BISNIS (PRD Bagian 7)
--    Lapisan Service tetap menjadi acuan utama dan diuji dengan unit test;
--    view berikut memakai rumus yang sama sehingga dapat dipakai untuk
--    laporan, verifikasi data, atau dipetakan sebagai keyless entity EF Core.
-- =====================================================================

-- BR-02: status dan jatuh tempo penugasan relatif terhadap waktu sekarang.
CREATE VIEW v_penugasan_status AS
SELECT p.id, p.kode, p.judul, p.kategori, p.tanggal_mulai, p.tenggat, p.bobot,
       CASE
           WHEN datetime('now', 'localtime') <  p.tanggal_mulai THEN 'Belum Dimulai'
           WHEN datetime('now', 'localtime') >  p.tenggat       THEN 'Selesai'
           ELSE 'Berjalan'
       END                                              AS status,
       (datetime('now', 'localtime') > p.tenggat)       AS jatuh_tempo
FROM penugasan p
WHERE p.aktif = 1;

-- BR-01: status setiap pasangan kader aktif x penugasan aktif.
CREATE VIEW v_status_pengumpulan AS
SELECT k.id            AS kader_id,
       p.id            AS penugasan_id,
       p.kode, p.bobot, p.tenggat, p.jatuh_tempo,
       s.id            AS pengumpulan_id,
       s.waktu_kumpul, s.nilai, s.catatan,
       CASE
           WHEN s.id IS NULL              THEN 'Belum Mengumpulkan'
           WHEN s.waktu_kumpul <= p.tenggat THEN 'Tepat Waktu'
           ELSE 'Terlambat'
       END             AS status
FROM kader k
CROSS JOIN v_penugasan_status p
LEFT JOIN pengumpulan s ON s.kader_id = k.id AND s.penugasan_id = p.id
WHERE k.aktif = 1;

-- BR-03 dan BR-04: ketuntasan dan nilai akhir berbobot per kader aktif.
--   ketuntasan  = dikumpulkan / jatuh tempo x 100   (NULL bila belum ada jatuh tempo)
--   nilai akhir = sum(nilai x bobot) / sum(bobot) atas penugasan jatuh tempo;
--                 tidak dikumpulkan = 0, dikumpulkan tetapi belum dinilai = diabaikan.
CREATE VIEW v_rekap_kader AS
SELECT k.id AS kader_id, k.nrp, k.nama, k.angkatan,
       g.id AS kelompok_id, g.nomor AS kelompok_nomor, g.nama AS kelompok_nama,
       COALESCE(SUM(sp.jatuh_tempo), 0)                                              AS jml_jatuh_tempo,
       COALESCE(SUM(sp.jatuh_tempo AND sp.pengumpulan_id IS NOT NULL), 0)            AS jml_kumpul,
       COALESCE(SUM(sp.jatuh_tempo AND sp.pengumpulan_id IS NULL), 0)                AS jml_belum,
       COALESCE(SUM(sp.jatuh_tempo AND sp.status = 'Terlambat'), 0)                  AS jml_terlambat,
       COALESCE(SUM(sp.jatuh_tempo AND sp.nilai IS NOT NULL), 0)                     AS jml_dinilai,
       CASE WHEN COALESCE(SUM(sp.jatuh_tempo), 0) = 0 THEN NULL
            ELSE ROUND(100.0 * SUM(sp.jatuh_tempo AND sp.pengumpulan_id IS NOT NULL)
                             / SUM(sp.jatuh_tempo), 1)
       END                                                                           AS ketuntasan,
       ROUND(
           SUM(CASE WHEN sp.jatuh_tempo AND sp.pengumpulan_id IS NULL THEN 0
                    WHEN sp.jatuh_tempo AND sp.nilai IS NOT NULL      THEN sp.nilai * sp.bobot
               END) * 1.0
           / NULLIF(SUM(CASE WHEN sp.jatuh_tempo AND (sp.pengumpulan_id IS NULL OR sp.nilai IS NOT NULL)
                             THEN sp.bobot END), 0)
       , 1)                                                                          AS nilai_akhir
FROM kader k
JOIN kelompok g              ON g.id = k.kelompok_id
LEFT JOIN v_status_pengumpulan sp ON sp.kader_id = k.id
WHERE k.aktif = 1
GROUP BY k.id;

-- BR-05: kader berisiko beserta kriteria yang terpenuhi (ambang dari tabel pengaturan).
CREATE VIEW v_kader_berisiko AS
WITH ambang AS (
    SELECT (SELECT CAST(nilai AS INTEGER) FROM pengaturan WHERE kunci = 'risiko_r1_min_belum')     AS r1,
           (SELECT CAST(nilai AS REAL)    FROM pengaturan WHERE kunci = 'risiko_r2_min_ketuntasan') AS r2,
           (SELECT CAST(nilai AS REAL)    FROM pengaturan WHERE kunci = 'risiko_r3_min_nilai')      AS r3,
           (SELECT CAST(nilai AS INTEGER) FROM pengaturan WHERE kunci = 'risiko_r3_min_dinilai')    AS r3_min
),
flag AS (
    SELECT r.*,
           COALESCE(r.jml_belum >= a.r1, 0)                                AS r1,
           COALESCE(r.ketuntasan < a.r2, 0)                                AS r2,
           COALESCE(r.nilai_akhir < a.r3 AND r.jml_dinilai >= a.r3_min, 0) AS r3
    FROM v_rekap_kader r CROSS JOIN ambang a
)
SELECT f.*, (f.r1 + f.r2 + f.r3) AS jml_kriteria,
       trim((CASE WHEN f.r1 THEN 'R1 ' ELSE '' END) ||
            (CASE WHEN f.r2 THEN 'R2 ' ELSE '' END) ||
            (CASE WHEN f.r3 THEN 'R3'  ELSE '' END)) AS kriteria
FROM flag f
WHERE f.r1 OR f.r2 OR f.r3
ORDER BY jml_kriteria DESC, f.ketuntasan ASC, f.nilai_akhir ASC;

-- Ringkasan per kelompok (layar Kelompok dan grafik dashboard).
CREATE VIEW v_rekap_kelompok AS
SELECT g.id AS kelompok_id, g.nomor, g.nama,
       u.nama                                                 AS pemandu,
       COUNT(r.kader_id)                                      AS jml_kader,
       ROUND(AVG(r.ketuntasan), 1)                            AS rata_ketuntasan,
       ROUND(AVG(r.nilai_akhir), 1)                           AS rata_nilai,
       (SELECT COUNT(*) FROM v_kader_berisiko b WHERE b.kelompok_id = g.id) AS jml_berisiko
FROM kelompok g
LEFT JOIN pengguna u     ON u.id = g.pemandu_id
LEFT JOIN v_rekap_kader r ON r.kelompok_id = g.id
GROUP BY g.id
ORDER BY g.nomor;

-- Kartu statistik dashboard (FR-05).
CREATE VIEW v_dashboard AS
SELECT (SELECT COUNT(*) FROM kader WHERE aktif = 1)               AS kader_aktif,
       (SELECT COUNT(*) FROM kader)                               AS kader_terdaftar,
       (SELECT COUNT(*) FROM kelompok)                            AS jml_kelompok,
       (SELECT COUNT(*) FROM penugasan WHERE aktif = 1)           AS penugasan_aktif,
       (SELECT COUNT(*) FROM v_penugasan_status WHERE jatuh_tempo = 1)        AS penugasan_jatuh_tempo,
       (SELECT COUNT(*) FROM v_penugasan_status WHERE status = 'Berjalan')    AS penugasan_berjalan,
       (SELECT ROUND(AVG(ketuntasan), 1) FROM v_rekap_kader)      AS rata_ketuntasan,
       (SELECT COUNT(*) FROM v_kader_berisiko)                    AS kader_berisiko;

-- =====================================================================
-- 3. DATA CONTOH (SEED)
--    Semua tanggal ditulis RELATIF terhadap hari skrip dijalankan, dengan
--    "hari ini" = Senin 19 Oktober 2026 pada desain. Akibatnya status
--    penugasan (Selesai/Berjalan/Belum Dimulai) dan dashboard selalu
--    tampak seperti di desain, kapan pun database dibuat.
--    Password semua akun contoh: jejak123  (hash BCrypt, cost 11)
-- =====================================================================

BEGIN TRANSACTION;

INSERT INTO pengguna (id, username, nama, password_hash, peran, tema, aktif, login_terakhir, dibuat_pada) VALUES
  (1, 'raka.pratama', 'Raka Pratama', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Admin', 'light', 1, datetime('now','localtime','start of day','+0 days','+9 hours','+55 minutes'), datetime('now','localtime','start of day','-39 days','+8 hours','+0 minutes')),
  (2, 'sinta.dewi', 'Sinta Dewi', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Admin', 'dark', 1, datetime('now','localtime','start of day','-1 days','+13 hours','+20 minutes'), datetime('now','localtime','start of day','-39 days','+8 hours','+5 minutes')),
  (3, 'alya.rahmadani', 'Alya Rahmadani', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'dark', 1, datetime('now','localtime','start of day','+0 days','+9 hours','+48 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (4, 'bima.saputra', 'Bima Saputra', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','+0 days','+7 hours','+2 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (5, 'citra.lestari', 'Citra Lestari', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','-1 days','+9 hours','+30 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (6, 'daffa.prakoso', 'Daffa Prakoso', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'dark', 1, datetime('now','localtime','start of day','+0 days','+7 hours','+48 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (7, 'elsa.maharani', 'Elsa Maharani', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 0, datetime('now','localtime','start of day','-2 days','+9 hours','+10 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (8, 'fikri.hidayat', 'Fikri Hidayat', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','+0 days','+5 hours','+0 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (9, 'gita.puspita', 'Gita Puspita', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'dark', 1, datetime('now','localtime','start of day','+0 days','+8 hours','+10 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (10, 'hafiz.ramadhan', 'Hafiz Ramadhan', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','-1 days','+16 hours','+0 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (11, 'intan.permata', 'Intan Permata', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','-1 days','+20 hours','+5 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (12, 'jovan.kurniawan', 'Jovan Kurniawan', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'dark', 1, datetime('now','localtime','start of day','+0 days','+6 hours','+40 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (13, 'kirana.ayu', 'Kirana Ayu', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','-1 days','+21 hours','+15 minutes'), datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (14, 'lutfi.hakim', 'Lutfi Hakim', '$2b$11$XI.EJP0Lu6qlezXp1tnXO.YdRu5KHJ2qa7weK97LBdUfiIvnoc2Bi', 'Pemandu', 'light', 1, datetime('now','localtime','start of day','-1 days','+10 hours','+45 minutes'), datetime('now','localtime','start of day','-1 days','+10 hours','+40 minutes'));

INSERT INTO kelompok (id, nomor, nama, deskripsi, pemandu_id, dibuat_pada) VALUES
  (1, 1, 'Arunika', 'Fajar yang baru terbit', 3, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (2, 2, 'Bhaskara', 'Matahari', 4, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (3, 3, 'Candramawa', 'Cahaya bulan', 5, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (4, 4, 'Dirgantara', 'Langit luas', 6, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (5, 5, 'Elakshi', 'Bermata indah', 7, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (6, 6, 'Gemintang', 'Bertabur bintang', 8, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (7, 7, 'Harsa', 'Kebahagiaan', 9, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (8, 8, 'Indurasmi', 'Sinar bulan', 10, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (9, 9, 'Jumantara', 'Angkasa', 11, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (10, 10, 'Kalandra', 'Pemimpin', 12, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (11, 11, 'Lembayung', 'Senja', 13, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  (12, 12, 'Mahika', 'Bumi', 14, datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes'));

INSERT INTO kader (id, nrp, nama, angkatan, kontak, kelompok_id, aktif, dibuat_pada) VALUES
  (1, '5025251001', 'Adinda Salsabila', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (2, '5025251002', 'Bagas Wicaksono', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (3, '5025251003', 'Dika Salsabila', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (4, '5025251004', 'Dewi Utama', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (5, '5025251005', 'Yudha Rizaldi', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (6, '5025251006', 'Zaki Ramadhani', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (7, '5025251007', 'Adi Santoso', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (8, '5025251008', 'Dian Susanto', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (9, '5025251009', 'Dani Kurnia', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (10, '5025251010', 'Agung Rizaldi', 2025, NULL, 1, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (11, '5025251011', 'Clarissa Wijaya', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (12, '5025251012', 'Daniel Simanjuntak', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (13, '5025251013', 'Rani Harahap', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (14, '5025251014', 'Vina Anggraini', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (15, '5025251015', 'Sari Hasibuan', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (16, '5025251016', 'Tiara Oktaviani', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (17, '5025251017', 'Bayu Fauzi', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (18, '5025251018', 'Benny Handayani', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (19, '5025251019', 'Oki Susanto', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (20, '5025251020', 'Doni Setiawan', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (21, '5025251021', 'Eka Nurhaliza', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (22, '5025251022', 'Raditya Saputra', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (23, '5025251023', 'Teguh Pangestu', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (24, '5025251024', 'Feby Aditya', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (25, '5025251025', 'Benny Febrianti', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (26, '5025251026', 'Mutiara Nugroho', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (27, '5025251027', 'Yuni Oktaviani', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (28, '5025251028', 'Kezia Febrianti', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (29, '5025251029', 'Nabil Syahputra', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (30, '5025251030', 'Ikhsan Oktaviani', 2025, NULL, 3, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (31, '5025251031', 'Anisa Rahma', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (32, '5025251032', 'Bintang Saputra', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (33, '5025251033', 'Cahya Ningrum', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (34, '5025251034', 'Dimas Arya Nugraha', 2025, '0812-3456-7890', 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (35, '5025251035', 'Erlangga Putra', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (36, '5025251036', 'Farah Aulia', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (37, '5025251037', 'Rizky Maulana', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (38, '5025251038', 'Galih Permana', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (39, '5025251039', 'Putri Ayu Lestari', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (40, '5025251040', 'Ilham Fadillah', 2025, NULL, 4, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (41, '5025251041', 'Fajar Ramadhan', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (42, '5025251042', 'Alif Utama', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (43, '5025251043', 'Eko Kusuma', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (44, '5025251044', 'Dewi Aditya', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (45, '5025251045', 'Gading Setiawan', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (46, '5025251046', 'Ikhsan Utama', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (47, '5025251047', 'Dika Tanjung', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (48, '5025251048', 'Gading Handayani', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (49, '5025251049', 'Mira Febrianti', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (50, '5025251050', 'Raditya Oktaviani', 2025, NULL, 5, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (51, '5025251051', 'Ikhsan Febrianti', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (52, '5025251052', 'Gilang Mahendra', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (53, '5025251053', 'Zahra Nasution', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (54, '5025251054', 'Oki Firmansyah', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (55, '5025251055', 'Agung Wijaya', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (56, '5025251056', 'Fitri Tanjung', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (57, '5025251057', 'Kezia Pangestu', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (58, '5025251058', 'Kevin Halim', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (59, '5025251059', 'Jihan Anggraini', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (60, '5025251060', 'Kezia Tanjung', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (61, '5025251061', 'Mutiara Aditya', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (62, '5025251062', 'Amanda Susanto', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (63, '5025251063', 'Rani Tanjung', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (64, '5025251064', 'Nabil Kurnia', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (65, '5025251065', 'Indah Aditya', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (66, '5025251066', 'Ikhsan Lestari', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (67, '5025251067', 'Naufal Rizaldi', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (68, '5025251068', 'Teguh Wibowo', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (69, '5025251069', 'Irfan Syahputra', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (70, '5025251070', 'Fani Kurnia', 2025, NULL, 7, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (71, '5025251071', 'Agung Santoso', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (72, '5025251072', 'Adi Kurnia', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (73, '5025251073', 'Bella Nasution', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (74, '5025251074', 'Nadia Putri Kusuma', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (75, '5025251075', 'Maya Nugroho', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (76, '5025251076', 'Andi Handayani', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (77, '5025251077', 'Yoga Pratama', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (78, '5025251078', 'Naufal Hasibuan', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (79, '5025251079', 'Fitri Pratama', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (80, '5025251080', 'Rani Rizaldi', 2025, NULL, 8, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (81, '5025251081', 'Fadli Handayani', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (82, '5025251082', 'Kemal Simatupang', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (83, '5025251083', 'Eko Oktaviani', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (84, '5025251084', 'Oki Santoso', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (85, '5025251085', 'Yudha Purnama', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (86, '5025251086', 'Fadli Gunawan', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (87, '5025251087', 'Wulan Wardhana', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (88, '5025251088', 'Fitri Prasetyo', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (89, '5025251089', 'Jihan Wardhana', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (90, '5025251090', 'Agung Wibowo', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (91, '5025251091', 'Umar Lestari', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (92, '5025251092', 'Anggun Hasibuan', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (93, '5025251093', 'Satria Saputra', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (94, '5025251094', 'Alif Oktaviani', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (95, '5025251095', 'Gading Salsabila', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (96, '5025251096', 'Pandu Kurnia', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (97, '5025251097', 'Reza Nugroho', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (98, '5025251098', 'Mutiara Tanjung', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (99, '5025251099', 'Hana Setiawan', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (100, '5025251100', 'Joko Aditya', 2025, NULL, 10, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (101, '5025251101', 'Rafi Tanjung', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (102, '5025251102', 'Lia Lestari', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (103, '5025251103', 'Wahyu Wijaya', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (104, '5025251104', 'Jihan Siregar', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (105, '5025251105', 'Salma Azzahra', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (106, '5025251106', 'Anggun Kurnia', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (107, '5025251107', 'Irfan Simatupang', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (108, '5025251108', 'Laras Sekar Arum', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (109, '5025251109', 'Aisyah Rahman', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (110, '5025251110', 'Kartika Prasetyo', 2025, NULL, 11, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (111, '5025251111', 'Fadli Purnama', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (112, '5025251112', 'Indah Wardhana', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (113, '5025251113', 'Naufal Aditya', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (114, '5025251114', 'Yudha Santoso', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (115, '5025251115', 'Umar Gunawan', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (116, '5025251116', 'Arif Budiman', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (117, '5025251117', 'Indah Hasibuan', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (118, '5025251118', 'Hendra Anggraini', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (119, '5025251119', 'Feby Rizaldi', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (120, '5025251120', 'Yudha Susanto', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (121, '5025251121', 'Feby Siregar', 2025, NULL, 2, 1, datetime('now','localtime','start of day','-18 days','+10 hours','+0 minutes')),
  (122, '5025251122', 'Reza Prasetyo', 2025, NULL, 6, 1, datetime('now','localtime','start of day','-18 days','+10 hours','+0 minutes')),
  (123, '5025251123', 'Ikhsan Kurnia', 2025, NULL, 9, 1, datetime('now','localtime','start of day','-18 days','+10 hours','+0 minutes')),
  (124, '5025251124', 'Dika Prasetyo', 2025, NULL, 12, 1, datetime('now','localtime','start of day','-18 days','+10 hours','+0 minutes'));

INSERT INTO penugasan (id, kode, judul, deskripsi, kategori, tanggal_mulai, tenggat, bobot, aktif, dibuat_pada) VALUES
  (1, 'P1', 'Esai Refleksi Diri', 'Refleksi motivasi mengikuti kaderisasi', 'Individu', datetime('now','localtime','start of day','-27 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','-20 days','+23 hours','+59 minutes'), 10, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (2, 'P2', 'Profil Angkatan', 'Infografis profil dan nilai angkatan', 'Kelompok', datetime('now','localtime','start of day','-20 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','-16 days','+23 hours','+59 minutes'), 10, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (3, 'P3', 'Video Perkenalan Kelompok', 'Video 3 menit identitas kelompok', 'Kelompok', datetime('now','localtime','start of day','-17 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','-11 days','+23 hours','+59 minutes'), 15, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (4, 'P4', 'Laporan Kunjungan Lab', 'Laporan kunjungan ke laboratorium', 'Individu', datetime('now','localtime','start of day','-13 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','-7 days','+23 hours','+59 minutes'), 15, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (5, 'P5', 'Studi Kasus Organisasi', 'Analisis studi kasus kepemimpinan', 'Individu', datetime('now','localtime','start of day','-9 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','-2 days','+23 hours','+59 minutes'), 20, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (6, 'P6', 'Proyek Sosial Kelompok', 'Rancangan dan pelaksanaan aksi sosial', 'Kelompok', datetime('now','localtime','start of day','-4 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','+7 days','+23 hours','+59 minutes'), 10, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (7, 'P7', 'Esai Visi Angkatan', 'Esai visi angkatan untuk himpunan', 'Individu', datetime('now','localtime','start of day','+8 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','+14 days','+23 hours','+59 minutes'), 10, 1, datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (8, 'P8', 'Presentasi Akhir', 'Presentasi hasil proyek kelompok', 'Kelompok', datetime('now','localtime','start of day','+15 days','+0 hours','+0 minutes'), datetime('now','localtime','start of day','+21 days','+23 hours','+59 minutes'), 10, 1, datetime('now','localtime','start of day','+0 days','+9 hours','+2 minutes'));

INSERT INTO pengumpulan (kader_id, penugasan_id, waktu_kumpul, tautan, nilai, catatan, dinilai_oleh, dinilai_pada) VALUES
  (1, 1, datetime('now','localtime','start of day','-20 days','+16 hours','+19 minutes'), 'https://example.org/jejak/P1/5025251001', 78, NULL, 3, datetime('now','localtime','start of day','-19 days','+16 hours','+19 minutes')),
  (1, 2, datetime('now','localtime','start of day','-19 days','+5 hours','+43 minutes'), 'https://example.org/jejak/P2/kelompok-01', 85, NULL, 3, datetime('now','localtime','start of day','-18 days','+8 hours','+43 minutes')),
  (1, 3, datetime('now','localtime','start of day','-15 days','+2 hours','+39 minutes'), 'https://example.org/jejak/P3/kelompok-01', 84, NULL, 3, datetime('now','localtime','start of day','-14 days','+2 hours','+39 minutes')),
  (1, 4, datetime('now','localtime','start of day','-13 days','+14 hours','+0 minutes'), 'https://example.org/jejak/P4/5025251001', 86, NULL, 3, datetime('now','localtime','start of day','-12 days','+0 hours','+0 minutes')),
  (1, 5, datetime('now','localtime','start of day','-4 days','+15 hours','+44 minutes'), 'https://example.org/jejak/P5/5025251001', 91, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (2, 1, datetime('now','localtime','start of day','-22 days','+16 hours','+39 minutes'), 'https://example.org/jejak/P1/5025251002', 83, NULL, 3, datetime('now','localtime','start of day','-21 days','+23 hours','+39 minutes')),
  (2, 2, datetime('now','localtime','start of day','-16 days','+15 hours','+49 minutes'), 'https://example.org/jejak/P2/kelompok-01', 83, NULL, 3, datetime('now','localtime','start of day','-14 days','+3 hours','+49 minutes')),
  (2, 3, datetime('now','localtime','start of day','-11 days','+15 hours','+2 minutes'), 'https://example.org/jejak/P3/kelompok-01', 91, NULL, 3, datetime('now','localtime','start of day','-10 days','+5 hours','+2 minutes')),
  (2, 4, datetime('now','localtime','start of day','-7 days','+20 hours','+31 minutes'), 'https://example.org/jejak/P4/5025251002', 80, NULL, 3, datetime('now','localtime','start of day','-6 days','+11 hours','+31 minutes')),
  (2, 5, datetime('now','localtime','start of day','-2 days','+19 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251002', 74, NULL, 3, datetime('now','localtime','start of day','-1 days','+20 hours','+16 minutes')),
  (3, 1, datetime('now','localtime','start of day','-20 days','+11 hours','+16 minutes'), 'https://example.org/jejak/P1/5025251003', 77, NULL, 3, datetime('now','localtime','start of day','-19 days','+2 hours','+16 minutes')),
  (3, 2, datetime('now','localtime','start of day','-16 days','+17 hours','+44 minutes'), 'https://example.org/jejak/P2/kelompok-01', 88, NULL, 3, datetime('now','localtime','start of day','-15 days','+22 hours','+44 minutes')),
  (3, 3, datetime('now','localtime','start of day','-15 days','+23 hours','+37 minutes'), 'https://example.org/jejak/P3/kelompok-01', 84, NULL, 3, datetime('now','localtime','start of day','-14 days','+10 hours','+37 minutes')),
  (3, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P4/5025251003', 83, NULL, 3, datetime('now','localtime','start of day','-5 days','+5 hours','+57 minutes')),
  (3, 5, datetime('now','localtime','start of day','-5 days','+12 hours','+14 minutes'), 'https://example.org/jejak/P5/5025251003', 83, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (4, 1, datetime('now','localtime','start of day','-22 days','+7 hours','+54 minutes'), 'https://example.org/jejak/P1/5025251004', 69, NULL, 3, datetime('now','localtime','start of day','-21 days','+10 hours','+54 minutes')),
  (4, 2, datetime('now','localtime','start of day','-17 days','+6 hours','+47 minutes'), 'https://example.org/jejak/P2/kelompok-01', 80, NULL, 3, datetime('now','localtime','start of day','-16 days','+3 hours','+47 minutes')),
  (4, 3, datetime('now','localtime','start of day','-12 days','+17 hours','+9 minutes'), 'https://example.org/jejak/P3/kelompok-01', 73, NULL, 3, datetime('now','localtime','start of day','-11 days','+2 hours','+9 minutes')),
  (4, 4, datetime('now','localtime','start of day','-12 days','+17 hours','+41 minutes'), 'https://example.org/jejak/P4/5025251004', 71, NULL, 3, datetime('now','localtime','start of day','-10 days','+5 hours','+41 minutes')),
  (4, 5, datetime('now','localtime','start of day','-8 days','+6 hours','+44 minutes'), 'https://example.org/jejak/P5/5025251004', NULL, NULL, NULL, NULL),
  (5, 1, datetime('now','localtime','start of day','-21 days','+16 hours','+57 minutes'), 'https://example.org/jejak/P1/5025251005', 86, NULL, 3, datetime('now','localtime','start of day','-20 days','+21 hours','+57 minutes')),
  (5, 2, datetime('now','localtime','start of day','-15 days','+11 hours','+45 minutes'), 'https://example.org/jejak/P2/kelompok-01', 73, NULL, 3, datetime('now','localtime','start of day','-13 days','+3 hours','+45 minutes')),
  (5, 3, datetime('now','localtime','start of day','-11 days','+22 hours','+19 minutes'), 'https://example.org/jejak/P3/kelompok-01', 91, NULL, 3, datetime('now','localtime','start of day','-9 days','+2 hours','+19 minutes')),
  (5, 4, datetime('now','localtime','start of day','-7 days','+10 hours','+23 minutes'), 'https://example.org/jejak/P4/5025251005', 83, NULL, 3, datetime('now','localtime','start of day','-6 days','+11 hours','+23 minutes')),
  (5, 5, datetime('now','localtime','start of day','-4 days','+18 hours','+11 minutes'), 'https://example.org/jejak/P5/5025251005', 72, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (6, 1, datetime('now','localtime','start of day','-20 days','+21 hours','+58 minutes'), 'https://example.org/jejak/P1/5025251006', 83, NULL, 3, datetime('now','localtime','start of day','-18 days','+1 hours','+58 minutes')),
  (6, 2, datetime('now','localtime','start of day','-17 days','+1 hours','+48 minutes'), 'https://example.org/jejak/P2/kelompok-01', 85, NULL, 3, datetime('now','localtime','start of day','-16 days','+15 hours','+48 minutes')),
  (6, 3, datetime('now','localtime','start of day','-11 days','+6 hours','+40 minutes'), 'https://example.org/jejak/P3/kelompok-01', 84, NULL, 3, datetime('now','localtime','start of day','-11 days','+15 hours','+40 minutes')),
  (6, 4, datetime('now','localtime','start of day','-7 days','+16 hours','+45 minutes'), 'https://example.org/jejak/P4/5025251006', 82, NULL, 3, datetime('now','localtime','start of day','-6 days','+0 hours','+45 minutes')),
  (6, 5, datetime('now','localtime','start of day','-4 days','+21 hours','+53 minutes'), 'https://example.org/jejak/P5/5025251006', 88, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (7, 1, datetime('now','localtime','start of day','-20 days','+14 hours','+29 minutes'), 'https://example.org/jejak/P1/5025251007', 68, NULL, 3, datetime('now','localtime','start of day','-19 days','+20 hours','+29 minutes')),
  (7, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P2/kelompok-01', 79, NULL, 3, datetime('now','localtime','start of day','-14 days','+4 hours','+57 minutes')),
  (7, 3, datetime('now','localtime','start of day','-11 days','+10 hours','+46 minutes'), 'https://example.org/jejak/P3/kelompok-01', 87, NULL, 3, datetime('now','localtime','start of day','-10 days','+20 hours','+46 minutes')),
  (7, 4, datetime('now','localtime','start of day','-9 days','+18 hours','+38 minutes'), 'https://example.org/jejak/P4/5025251007', 84, NULL, 3, datetime('now','localtime','start of day','-8 days','+10 hours','+38 minutes')),
  (7, 5, datetime('now','localtime','start of day','-9 days','+15 hours','+57 minutes'), 'https://example.org/jejak/P5/5025251007', 73, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (8, 1, datetime('now','localtime','start of day','-20 days','+8 hours','+35 minutes'), 'https://example.org/jejak/P1/5025251008', 82, NULL, 3, datetime('now','localtime','start of day','-18 days','+0 hours','+35 minutes')),
  (8, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P2/kelompok-01', 89, NULL, 3, datetime('now','localtime','start of day','-15 days','+19 hours','+58 minutes')),
  (8, 3, datetime('now','localtime','start of day','-13 days','+0 hours','+16 minutes'), 'https://example.org/jejak/P3/kelompok-01', 81, NULL, 3, datetime('now','localtime','start of day','-13 days','+16 hours','+16 minutes')),
  (8, 4, datetime('now','localtime','start of day','-12 days','+1 hours','+43 minutes'), 'https://example.org/jejak/P4/5025251008', 87, NULL, 3, datetime('now','localtime','start of day','-11 days','+8 hours','+43 minutes')),
  (8, 5, datetime('now','localtime','start of day','-9 days','+20 hours','+5 minutes'), 'https://example.org/jejak/P5/5025251008', 79, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (9, 1, datetime('now','localtime','start of day','-20 days','+3 hours','+48 minutes'), 'https://example.org/jejak/P1/5025251009', 98, NULL, 3, datetime('now','localtime','start of day','-20 days','+11 hours','+48 minutes')),
  (9, 2, datetime('now','localtime','start of day','-17 days','+15 hours','+56 minutes'), 'https://example.org/jejak/P2/kelompok-01', 77, NULL, 3, datetime('now','localtime','start of day','-15 days','+6 hours','+56 minutes')),
  (9, 3, datetime('now','localtime','start of day','-11 days','+19 hours','+44 minutes'), 'https://example.org/jejak/P3/kelompok-01', 89, NULL, 3, datetime('now','localtime','start of day','-10 days','+15 hours','+44 minutes')),
  (9, 4, datetime('now','localtime','start of day','-7 days','+21 hours','+15 minutes'), 'https://example.org/jejak/P4/5025251009', 98, NULL, 3, datetime('now','localtime','start of day','-5 days','+11 hours','+15 minutes')),
  (9, 5, datetime('now','localtime','start of day','-7 days','+14 hours','+50 minutes'), 'https://example.org/jejak/P5/5025251009', 78, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (10, 1, datetime('now','localtime','start of day','-20 days','+2 hours','+4 minutes'), 'https://example.org/jejak/P1/5025251010', 73, NULL, 3, datetime('now','localtime','start of day','-19 days','+5 hours','+4 minutes')),
  (10, 2, datetime('now','localtime','start of day','-16 days','+22 hours','+26 minutes'), 'https://example.org/jejak/P2/kelompok-01', 82, NULL, 3, datetime('now','localtime','start of day','-15 days','+4 hours','+26 minutes')),
  (10, 3, datetime('now','localtime','start of day','-10 days','+7 hours','+53 minutes'), 'https://example.org/jejak/P3/kelompok-01', 61, NULL, 3, datetime('now','localtime','start of day','-10 days','+20 hours','+53 minutes')),
  (10, 4, datetime('now','localtime','start of day','-10 days','+20 hours','+23 minutes'), 'https://example.org/jejak/P4/5025251010', 83, NULL, 3, datetime('now','localtime','start of day','-9 days','+10 hours','+23 minutes')),
  (10, 5, datetime('now','localtime','start of day','-4 days','+22 hours','+41 minutes'), 'https://example.org/jejak/P5/5025251010', 85, NULL, 3, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (10, 6, datetime('now','localtime','start of day','-3 days','+12 hours','+43 minutes'), 'https://example.org/jejak/P6/kelompok-01', NULL, NULL, NULL, NULL),
  (11, 1, datetime('now','localtime','start of day','-20 days','+22 hours','+38 minutes'), 'https://example.org/jejak/P1/5025251011', 89, NULL, 4, datetime('now','localtime','start of day','-19 days','+18 hours','+38 minutes')),
  (11, 2, datetime('now','localtime','start of day','-16 days','+8 hours','+56 minutes'), 'https://example.org/jejak/P2/kelompok-02', 86, NULL, 4, datetime('now','localtime','start of day','-15 days','+9 hours','+56 minutes')),
  (11, 3, datetime('now','localtime','start of day','-13 days','+4 hours','+8 minutes'), 'https://example.org/jejak/P3/kelompok-02', 89, NULL, 4, datetime('now','localtime','start of day','-12 days','+1 hours','+8 minutes')),
  (11, 4, datetime('now','localtime','start of day','-12 days','+11 hours','+6 minutes'), 'https://example.org/jejak/P4/5025251011', 90, NULL, 4, datetime('now','localtime','start of day','-11 days','+11 hours','+6 minutes')),
  (11, 5, datetime('now','localtime','start of day','-3 days','+23 hours','+59 minutes'), 'https://example.org/jejak/P5/5025251011', 96, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (12, 1, datetime('now','localtime','start of day','-26 days','+0 hours','+44 minutes'), 'https://example.org/jejak/P1/5025251012', 88, NULL, 4, datetime('now','localtime','start of day','-25 days','+4 hours','+44 minutes')),
  (12, 2, datetime('now','localtime','start of day','-18 days','+4 hours','+0 minutes'), 'https://example.org/jejak/P2/kelompok-02', 91, NULL, 4, datetime('now','localtime','start of day','-17 days','+6 hours','+0 minutes')),
  (12, 3, datetime('now','localtime','start of day','-14 days','+13 hours','+56 minutes'), 'https://example.org/jejak/P3/kelompok-02', 77, NULL, 4, datetime('now','localtime','start of day','-13 days','+7 hours','+56 minutes')),
  (12, 4, datetime('now','localtime','start of day','-6 days','+11 hours','+13 minutes'), 'https://example.org/jejak/P4/5025251012', 85, NULL, 4, datetime('now','localtime','start of day','-5 days','+3 hours','+13 minutes')),
  (12, 5, datetime('now','localtime','start of day','-3 days','+3 hours','+23 minutes'), 'https://example.org/jejak/P5/5025251012', 88, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (12, 6, datetime('now','localtime','start of day','-1 days','+18 hours','+37 minutes'), 'https://example.org/jejak/P6/kelompok-02', NULL, NULL, NULL, NULL),
  (13, 1, datetime('now','localtime','start of day','-20 days','+17 hours','+22 minutes'), 'https://example.org/jejak/P1/5025251013', 81, NULL, 4, datetime('now','localtime','start of day','-19 days','+1 hours','+22 minutes')),
  (13, 2, datetime('now','localtime','start of day','-17 days','+7 hours','+32 minutes'), 'https://example.org/jejak/P2/kelompok-02', 98, NULL, 4, datetime('now','localtime','start of day','-16 days','+15 hours','+32 minutes')),
  (13, 3, datetime('now','localtime','start of day','-16 days','+19 hours','+4 minutes'), 'https://example.org/jejak/P3/kelompok-02', 98, NULL, 4, datetime('now','localtime','start of day','-15 days','+13 hours','+4 minutes')),
  (13, 5, datetime('now','localtime','start of day','-2 days','+0 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251013', 81, NULL, 4, datetime('now','localtime','start of day','-1 days','+10 hours','+58 minutes')),
  (14, 1, datetime('now','localtime','start of day','-20 days','+22 hours','+8 minutes'), 'https://example.org/jejak/P1/5025251014', 66, NULL, 4, datetime('now','localtime','start of day','-19 days','+4 hours','+8 minutes')),
  (14, 2, datetime('now','localtime','start of day','-16 days','+2 hours','+40 minutes'), 'https://example.org/jejak/P2/kelompok-02', 76, NULL, 4, datetime('now','localtime','start of day','-16 days','+23 hours','+40 minutes')),
  (14, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+17 minutes'), 'https://example.org/jejak/P3/kelompok-02', 78, NULL, 4, datetime('now','localtime','start of day','-9 days','+4 hours','+17 minutes')),
  (14, 4, datetime('now','localtime','start of day','-7 days','+11 hours','+1 minutes'), 'https://example.org/jejak/P4/5025251014', 75, NULL, 4, datetime('now','localtime','start of day','-6 days','+19 hours','+1 minutes')),
  (14, 5, datetime('now','localtime','start of day','-2 days','+16 hours','+41 minutes'), 'https://example.org/jejak/P5/5025251014', 74, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (15, 1, datetime('now','localtime','start of day','-26 days','+13 hours','+3 minutes'), 'https://example.org/jejak/P1/5025251015', 85, NULL, 4, datetime('now','localtime','start of day','-24 days','+2 hours','+3 minutes')),
  (15, 2, datetime('now','localtime','start of day','-16 days','+21 hours','+19 minutes'), 'https://example.org/jejak/P2/kelompok-02', 76, NULL, 4, datetime('now','localtime','start of day','-14 days','+5 hours','+19 minutes')),
  (16, 1, datetime('now','localtime','start of day','-24 days','+6 hours','+34 minutes'), 'https://example.org/jejak/P1/5025251016', 79, NULL, 4, datetime('now','localtime','start of day','-23 days','+8 hours','+34 minutes')),
  (16, 2, datetime('now','localtime','start of day','-19 days','+21 hours','+53 minutes'), 'https://example.org/jejak/P2/kelompok-02', 81, NULL, 4, datetime('now','localtime','start of day','-18 days','+16 hours','+53 minutes')),
  (16, 3, datetime('now','localtime','start of day','-13 days','+5 hours','+21 minutes'), 'https://example.org/jejak/P3/kelompok-02', 86, NULL, 4, datetime('now','localtime','start of day','-12 days','+17 hours','+21 minutes')),
  (16, 4, datetime('now','localtime','start of day','-7 days','+14 hours','+44 minutes'), 'https://example.org/jejak/P4/5025251016', 86, NULL, 4, datetime('now','localtime','start of day','-6 days','+3 hours','+44 minutes')),
  (16, 5, datetime('now','localtime','start of day','-3 days','+15 hours','+44 minutes'), 'https://example.org/jejak/P5/5025251016', NULL, NULL, NULL, NULL),
  (17, 1, datetime('now','localtime','start of day','-22 days','+18 hours','+5 minutes'), 'https://example.org/jejak/P1/5025251017', 93, NULL, 4, datetime('now','localtime','start of day','-21 days','+15 hours','+5 minutes')),
  (17, 2, datetime('now','localtime','start of day','-16 days','+15 hours','+53 minutes'), 'https://example.org/jejak/P2/kelompok-02', 80, NULL, 4, datetime('now','localtime','start of day','-15 days','+8 hours','+53 minutes')),
  (17, 3, datetime('now','localtime','start of day','-10 days','+11 hours','+43 minutes'), 'https://example.org/jejak/P3/kelompok-02', 88, NULL, 4, datetime('now','localtime','start of day','-9 days','+23 hours','+43 minutes')),
  (17, 4, datetime('now','localtime','start of day','-7 days','+8 hours','+39 minutes'), 'https://example.org/jejak/P4/5025251017', 80, NULL, 4, datetime('now','localtime','start of day','-6 days','+5 hours','+39 minutes')),
  (17, 5, datetime('now','localtime','start of day','-7 days','+10 hours','+37 minutes'), 'https://example.org/jejak/P5/5025251017', 74, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (17, 6, datetime('now','localtime','start of day','-1 days','+13 hours','+7 minutes'), 'https://example.org/jejak/P6/kelompok-02', NULL, NULL, NULL, NULL),
  (18, 1, datetime('now','localtime','start of day','-20 days','+4 hours','+39 minutes'), 'https://example.org/jejak/P1/5025251018', 86, NULL, 4, datetime('now','localtime','start of day','-20 days','+11 hours','+39 minutes')),
  (18, 2, datetime('now','localtime','start of day','-20 days','+19 hours','+25 minutes'), 'https://example.org/jejak/P2/kelompok-02', 89, NULL, 4, datetime('now','localtime','start of day','-19 days','+1 hours','+25 minutes')),
  (18, 3, datetime('now','localtime','start of day','-11 days','+15 hours','+52 minutes'), 'https://example.org/jejak/P3/kelompok-02', 88, NULL, 4, datetime('now','localtime','start of day','-10 days','+0 hours','+52 minutes')),
  (18, 4, datetime('now','localtime','start of day','-9 days','+8 hours','+6 minutes'), 'https://example.org/jejak/P4/5025251018', 80, NULL, 4, datetime('now','localtime','start of day','-8 days','+14 hours','+6 minutes')),
  (18, 5, datetime('now','localtime','start of day','-8 days','+19 hours','+42 minutes'), 'https://example.org/jejak/P5/5025251018', 94, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (19, 1, datetime('now','localtime','start of day','-25 days','+9 hours','+3 minutes'), 'https://example.org/jejak/P1/5025251019', 83, NULL, 4, datetime('now','localtime','start of day','-24 days','+5 hours','+3 minutes')),
  (19, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P2/kelompok-02', 83, NULL, 4, datetime('now','localtime','start of day','-14 days','+7 hours','+57 minutes')),
  (19, 3, datetime('now','localtime','start of day','-13 days','+11 hours','+9 minutes'), 'https://example.org/jejak/P3/kelompok-02', 88, NULL, 4, datetime('now','localtime','start of day','-12 days','+10 hours','+9 minutes')),
  (19, 4, datetime('now','localtime','start of day','-10 days','+21 hours','+8 minutes'), 'https://example.org/jejak/P4/5025251019', 80, NULL, 4, datetime('now','localtime','start of day','-9 days','+13 hours','+8 minutes')),
  (19, 5, datetime('now','localtime','start of day','-2 days','+7 hours','+14 minutes'), 'https://example.org/jejak/P5/5025251019', 81, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (19, 6, datetime('now','localtime','start of day','-3 days','+18 hours','+33 minutes'), 'https://example.org/jejak/P6/kelompok-02', NULL, NULL, NULL, NULL),
  (20, 1, datetime('now','localtime','start of day','-20 days','+9 hours','+26 minutes'), 'https://example.org/jejak/P1/5025251020', 90, NULL, 4, datetime('now','localtime','start of day','-20 days','+15 hours','+26 minutes')),
  (20, 2, datetime('now','localtime','start of day','-18 days','+15 hours','+11 minutes'), 'https://example.org/jejak/P2/kelompok-02', 80, NULL, 4, datetime('now','localtime','start of day','-17 days','+10 hours','+11 minutes')),
  (20, 3, datetime('now','localtime','start of day','-11 days','+14 hours','+29 minutes'), 'https://example.org/jejak/P3/kelompok-02', 76, NULL, 4, datetime('now','localtime','start of day','-11 days','+22 hours','+29 minutes')),
  (20, 4, datetime('now','localtime','start of day','-7 days','+19 hours','+52 minutes'), 'https://example.org/jejak/P4/5025251020', 81, NULL, 4, datetime('now','localtime','start of day','-6 days','+7 hours','+52 minutes')),
  (20, 5, datetime('now','localtime','start of day','-8 days','+11 hours','+47 minutes'), 'https://example.org/jejak/P5/5025251020', 65, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (21, 1, datetime('now','localtime','start of day','-20 days','+14 hours','+27 minutes'), 'https://example.org/jejak/P1/5025251021', 78, NULL, 5, datetime('now','localtime','start of day','-19 days','+1 hours','+27 minutes')),
  (21, 2, datetime('now','localtime','start of day','-19 days','+14 hours','+26 minutes'), 'https://example.org/jejak/P2/kelompok-03', 89, NULL, 5, datetime('now','localtime','start of day','-18 days','+4 hours','+26 minutes')),
  (21, 3, datetime('now','localtime','start of day','-10 days','+12 hours','+58 minutes'), 'https://example.org/jejak/P3/kelompok-03', 84, NULL, 5, datetime('now','localtime','start of day','-9 days','+15 hours','+58 minutes')),
  (21, 4, datetime('now','localtime','start of day','-10 days','+5 hours','+28 minutes'), 'https://example.org/jejak/P4/5025251021', 84, NULL, 5, datetime('now','localtime','start of day','-9 days','+7 hours','+28 minutes')),
  (21, 5, datetime('now','localtime','start of day','-3 days','+22 hours','+25 minutes'), 'https://example.org/jejak/P5/5025251021', 87, NULL, 5, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (22, 1, datetime('now','localtime','start of day','-19 days','+17 hours','+51 minutes'), 'https://example.org/jejak/P1/5025251022', 87, NULL, 5, datetime('now','localtime','start of day','-18 days','+20 hours','+51 minutes')),
  (22, 2, datetime('now','localtime','start of day','-18 days','+5 hours','+16 minutes'), 'https://example.org/jejak/P2/kelompok-03', 83, NULL, 5, datetime('now','localtime','start of day','-17 days','+12 hours','+16 minutes')),
  (22, 3, datetime('now','localtime','start of day','-12 days','+9 hours','+51 minutes'), 'https://example.org/jejak/P3/kelompok-03', 92, NULL, 5, datetime('now','localtime','start of day','-11 days','+13 hours','+51 minutes')),
  (22, 4, datetime('now','localtime','start of day','-7 days','+9 hours','+48 minutes'), 'https://example.org/jejak/P4/5025251022', 83, NULL, 5, datetime('now','localtime','start of day','-6 days','+0 hours','+48 minutes')),
  (22, 5, datetime('now','localtime','start of day','-9 days','+4 hours','+40 minutes'), 'https://example.org/jejak/P5/5025251022', 90, NULL, 5, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (23, 1, datetime('now','localtime','start of day','-25 days','+2 hours','+6 minutes'), 'https://example.org/jejak/P1/5025251023', 84, NULL, 5, datetime('now','localtime','start of day','-25 days','+13 hours','+6 minutes')),
  (23, 2, datetime('now','localtime','start of day','-16 days','+9 hours','+28 minutes'), 'https://example.org/jejak/P2/kelompok-03', 93, NULL, 5, datetime('now','localtime','start of day','-15 days','+23 hours','+28 minutes')),
  (23, 3, datetime('now','localtime','start of day','-12 days','+16 hours','+59 minutes'), 'https://example.org/jejak/P3/kelompok-03', 92, NULL, 5, datetime('now','localtime','start of day','-10 days','+8 hours','+59 minutes')),
  (23, 4, datetime('now','localtime','start of day','-8 days','+10 hours','+7 minutes'), 'https://example.org/jejak/P4/5025251023', 80, NULL, 5, datetime('now','localtime','start of day','-7 days','+4 hours','+7 minutes')),
  (23, 5, datetime('now','localtime','start of day','-2 days','+13 hours','+9 minutes'), 'https://example.org/jejak/P5/5025251023', 86, NULL, 5, datetime('now','localtime','start of day','-1 days','+9 hours','+9 minutes')),
  (24, 1, datetime('now','localtime','start of day','-20 days','+7 hours','+42 minutes'), 'https://example.org/jejak/P1/5025251024', 84, NULL, 5, datetime('now','localtime','start of day','-20 days','+14 hours','+42 minutes')),
  (24, 2, datetime('now','localtime','start of day','-16 days','+8 hours','+41 minutes'), 'https://example.org/jejak/P2/kelompok-03', 83, NULL, 5, datetime('now','localtime','start of day','-15 days','+20 hours','+41 minutes')),
  (24, 3, datetime('now','localtime','start of day','-12 days','+2 hours','+39 minutes'), 'https://example.org/jejak/P3/kelompok-03', 84, NULL, 5, datetime('now','localtime','start of day','-12 days','+14 hours','+39 minutes')),
  (24, 4, datetime('now','localtime','start of day','-10 days','+12 hours','+14 minutes'), 'https://example.org/jejak/P4/5025251024', 97, NULL, 5, datetime('now','localtime','start of day','-10 days','+23 hours','+14 minutes')),
  (24, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+34 minutes'), 'https://example.org/jejak/P5/5025251024', NULL, NULL, NULL, NULL),
  (25, 1, datetime('now','localtime','start of day','-20 days','+19 hours','+37 minutes'), 'https://example.org/jejak/P1/5025251025', 83, NULL, 5, datetime('now','localtime','start of day','-18 days','+8 hours','+37 minutes')),
  (25, 2, datetime('now','localtime','start of day','-17 days','+17 hours','+0 minutes'), 'https://example.org/jejak/P2/kelompok-03', 80, NULL, 5, datetime('now','localtime','start of day','-16 days','+23 hours','+0 minutes')),
  (25, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+42 minutes'), 'https://example.org/jejak/P3/kelompok-03', 90, NULL, 5, datetime('now','localtime','start of day','-10 days','+12 hours','+42 minutes')),
  (25, 4, datetime('now','localtime','start of day','-7 days','+21 hours','+14 minutes'), 'https://example.org/jejak/P4/5025251025', 80, NULL, 5, datetime('now','localtime','start of day','-5 days','+3 hours','+14 minutes')),
  (25, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+54 minutes'), 'https://example.org/jejak/P5/5025251025', 79, NULL, 5, datetime('now','localtime','start of day','+0 days','+5 hours','+54 minutes')),
  (26, 1, datetime('now','localtime','start of day','-24 days','+18 hours','+52 minutes'), 'https://example.org/jejak/P1/5025251026', 91, NULL, 5, datetime('now','localtime','start of day','-23 days','+17 hours','+52 minutes')),
  (26, 2, datetime('now','localtime','start of day','-16 days','+13 hours','+53 minutes'), 'https://example.org/jejak/P2/kelompok-03', 85, NULL, 5, datetime('now','localtime','start of day','-15 days','+23 hours','+53 minutes')),
  (26, 3, datetime('now','localtime','start of day','-10 days','+3 hours','+42 minutes'), 'https://example.org/jejak/P3/kelompok-03', 84, NULL, 5, datetime('now','localtime','start of day','-9 days','+0 hours','+42 minutes')),
  (26, 4, datetime('now','localtime','start of day','-11 days','+4 hours','+21 minutes'), 'https://example.org/jejak/P4/5025251026', 82, NULL, 5, datetime('now','localtime','start of day','-10 days','+13 hours','+21 minutes')),
  (26, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+54 minutes'), 'https://example.org/jejak/P5/5025251026', 75, NULL, 5, datetime('now','localtime','start of day','-1 days','+12 hours','+54 minutes')),
  (26, 6, datetime('now','localtime','start of day','-1 days','+2 hours','+33 minutes'), 'https://example.org/jejak/P6/kelompok-03', NULL, NULL, NULL, NULL),
  (27, 1, datetime('now','localtime','start of day','-25 days','+12 hours','+32 minutes'), 'https://example.org/jejak/P1/5025251027', 89, NULL, 5, datetime('now','localtime','start of day','-24 days','+21 hours','+32 minutes')),
  (27, 2, datetime('now','localtime','start of day','-18 days','+23 hours','+37 minutes'), 'https://example.org/jejak/P2/kelompok-03', 83, NULL, 5, datetime('now','localtime','start of day','-17 days','+17 hours','+37 minutes')),
  (27, 3, datetime('now','localtime','start of day','-16 days','+20 hours','+12 minutes'), 'https://example.org/jejak/P3/kelompok-03', 89, NULL, 5, datetime('now','localtime','start of day','-15 days','+10 hours','+12 minutes')),
  (27, 4, datetime('now','localtime','start of day','-8 days','+17 hours','+38 minutes'), 'https://example.org/jejak/P4/5025251027', 86, NULL, 5, datetime('now','localtime','start of day','-7 days','+23 hours','+38 minutes')),
  (27, 6, datetime('now','localtime','start of day','-1 days','+21 hours','+34 minutes'), 'https://example.org/jejak/P6/kelompok-03', NULL, NULL, NULL, NULL),
  (28, 1, datetime('now','localtime','start of day','-26 days','+2 hours','+20 minutes'), 'https://example.org/jejak/P1/5025251028', 81, NULL, 5, datetime('now','localtime','start of day','-25 days','+10 hours','+20 minutes')),
  (28, 2, datetime('now','localtime','start of day','-16 days','+20 hours','+4 minutes'), 'https://example.org/jejak/P2/kelompok-03', 85, NULL, 5, datetime('now','localtime','start of day','-15 days','+4 hours','+4 minutes')),
  (28, 3, datetime('now','localtime','start of day','-14 days','+5 hours','+11 minutes'), 'https://example.org/jejak/P3/kelompok-03', 82, NULL, 5, datetime('now','localtime','start of day','-13 days','+14 hours','+11 minutes')),
  (28, 4, datetime('now','localtime','start of day','-7 days','+22 hours','+32 minutes'), 'https://example.org/jejak/P4/5025251028', 95, NULL, 5, datetime('now','localtime','start of day','-6 days','+19 hours','+32 minutes')),
  (28, 5, datetime('now','localtime','start of day','-3 days','+17 hours','+26 minutes'), 'https://example.org/jejak/P5/5025251028', 98, NULL, 5, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (28, 6, datetime('now','localtime','start of day','-2 days','+14 hours','+3 minutes'), 'https://example.org/jejak/P6/kelompok-03', NULL, NULL, NULL, NULL),
  (29, 1, datetime('now','localtime','start of day','-20 days','+21 hours','+14 minutes'), 'https://example.org/jejak/P1/5025251029', 84, NULL, 5, datetime('now','localtime','start of day','-19 days','+20 hours','+14 minutes')),
  (29, 2, datetime('now','localtime','start of day','-18 days','+10 hours','+16 minutes'), 'https://example.org/jejak/P2/kelompok-03', 72, NULL, 5, datetime('now','localtime','start of day','-17 days','+13 hours','+16 minutes')),
  (29, 3, datetime('now','localtime','start of day','-12 days','+15 hours','+5 minutes'), 'https://example.org/jejak/P3/kelompok-03', 73, NULL, 5, datetime('now','localtime','start of day','-10 days','+4 hours','+5 minutes')),
  (29, 4, datetime('now','localtime','start of day','-13 days','+5 hours','+36 minutes'), 'https://example.org/jejak/P4/5025251029', 79, NULL, 5, datetime('now','localtime','start of day','-13 days','+23 hours','+36 minutes')),
  (29, 5, datetime('now','localtime','start of day','-2 days','+21 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251029', 78, NULL, 5, datetime('now','localtime','start of day','-1 days','+15 hours','+16 minutes')),
  (29, 6, datetime('now','localtime','start of day','-2 days','+6 hours','+35 minutes'), 'https://example.org/jejak/P6/kelompok-03', NULL, NULL, NULL, NULL),
  (30, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+44 minutes'), 'https://example.org/jejak/P1/5025251030', 87, NULL, 5, datetime('now','localtime','start of day','-19 days','+19 hours','+44 minutes')),
  (30, 2, datetime('now','localtime','start of day','-20 days','+8 hours','+27 minutes'), 'https://example.org/jejak/P2/kelompok-03', 82, NULL, 5, datetime('now','localtime','start of day','-19 days','+14 hours','+27 minutes')),
  (30, 3, datetime('now','localtime','start of day','-12 days','+16 hours','+17 minutes'), 'https://example.org/jejak/P3/kelompok-03', 79, NULL, 5, datetime('now','localtime','start of day','-11 days','+10 hours','+17 minutes')),
  (30, 4, datetime('now','localtime','start of day','-13 days','+19 hours','+10 minutes'), 'https://example.org/jejak/P4/5025251030', 87, NULL, 5, datetime('now','localtime','start of day','-12 days','+20 hours','+10 minutes')),
  (30, 5, datetime('now','localtime','start of day','-2 days','+10 hours','+43 minutes'), 'https://example.org/jejak/P5/5025251030', 91, NULL, 5, datetime('now','localtime','start of day','-1 days','+17 hours','+43 minutes')),
  (31, 1, datetime('now','localtime','start of day','-21 days','+14 hours','+26 minutes'), 'https://example.org/jejak/P1/5025251031', 88, NULL, 6, datetime('now','localtime','start of day','-20 days','+1 hours','+26 minutes')),
  (31, 2, datetime('now','localtime','start of day','-20 days','+10 hours','+26 minutes'), 'https://example.org/jejak/P2/kelompok-04', 86, NULL, 6, datetime('now','localtime','start of day','-18 days','+0 hours','+26 minutes')),
  (31, 3, datetime('now','localtime','start of day','-12 days','+4 hours','+32 minutes'), 'https://example.org/jejak/P3/kelompok-04', 83, NULL, 6, datetime('now','localtime','start of day','-12 days','+23 hours','+32 minutes')),
  (31, 5, datetime('now','localtime','start of day','-3 days','+19 hours','+22 minutes'), 'https://example.org/jejak/P5/5025251031', 84, 'Analisis runtut dan tajam.', 6, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (32, 1, datetime('now','localtime','start of day','-19 days','+13 hours','+51 minutes'), 'https://example.org/jejak/P1/5025251032', 89, NULL, 6, datetime('now','localtime','start of day','-18 days','+6 hours','+51 minutes')),
  (32, 2, datetime('now','localtime','start of day','-20 days','+7 hours','+18 minutes'), 'https://example.org/jejak/P2/kelompok-04', 87, NULL, 6, datetime('now','localtime','start of day','-19 days','+9 hours','+18 minutes')),
  (32, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+28 minutes'), 'https://example.org/jejak/P4/5025251032', 80, NULL, 6, datetime('now','localtime','start of day','-6 days','+5 hours','+28 minutes')),
  (32, 5, datetime('now','localtime','start of day','-2 days','+10 hours','+5 minutes'), 'https://example.org/jejak/P5/5025251032', 78, NULL, 6, datetime('now','localtime','start of day','-1 days','+11 hours','+5 minutes')),
  (33, 2, datetime('now','localtime','start of day','-20 days','+23 hours','+38 minutes'), 'https://example.org/jejak/P2/kelompok-04', 76, NULL, 6, datetime('now','localtime','start of day','-19 days','+12 hours','+38 minutes')),
  (33, 3, datetime('now','localtime','start of day','-11 days','+10 hours','+4 minutes'), 'https://example.org/jejak/P3/kelompok-04', 72, NULL, 6, datetime('now','localtime','start of day','-10 days','+5 hours','+4 minutes')),
  (33, 4, datetime('now','localtime','start of day','-10 days','+21 hours','+37 minutes'), 'https://example.org/jejak/P4/5025251033', 72, NULL, 6, datetime('now','localtime','start of day','-9 days','+9 hours','+37 minutes')),
  (33, 5, datetime('now','localtime','start of day','-2 days','+14 hours','+40 minutes'), 'https://example.org/jejak/P5/5025251033', 88, 'Referensi lengkap.', 6, datetime('now','localtime','start of day','+0 days','+4 hours','+40 minutes')),
  (34, 1, datetime('now','localtime','start of day','-21 days','+19 hours','+3 minutes'), 'https://example.org/jejak/P1/5025251034', 78, NULL, 6, datetime('now','localtime','start of day','-20 days','+2 hours','+3 minutes')),
  (34, 2, datetime('now','localtime','start of day','-15 days','+8 hours','+12 minutes'), 'https://example.org/jejak/P2/kelompok-04', 70, NULL, 6, datetime('now','localtime','start of day','-14 days','+22 hours','+12 minutes')),
  (34, 4, datetime('now','localtime','start of day','-7 days','+21 hours','+40 minutes'), 'https://example.org/jejak/P4/5025251034', 72, NULL, 6, datetime('now','localtime','start of day','-6 days','+19 hours','+40 minutes')),
  (35, 1, datetime('now','localtime','start of day','-21 days','+19 hours','+40 minutes'), 'https://example.org/jejak/P1/5025251035', 83, NULL, 6, datetime('now','localtime','start of day','-20 days','+13 hours','+40 minutes')),
  (35, 2, datetime('now','localtime','start of day','-18 days','+4 hours','+42 minutes'), 'https://example.org/jejak/P2/kelompok-04', 92, NULL, 6, datetime('now','localtime','start of day','-18 days','+15 hours','+42 minutes')),
  (35, 3, datetime('now','localtime','start of day','-13 days','+19 hours','+15 minutes'), 'https://example.org/jejak/P3/kelompok-04', 90, NULL, 6, datetime('now','localtime','start of day','-12 days','+15 hours','+15 minutes')),
  (35, 5, datetime('now','localtime','start of day','-2 days','+20 hours','+11 minutes'), 'https://example.org/jejak/P5/5025251035', NULL, NULL, NULL, NULL),
  (36, 2, datetime('now','localtime','start of day','-16 days','+9 hours','+18 minutes'), 'https://example.org/jejak/P2/kelompok-04', 86, NULL, 6, datetime('now','localtime','start of day','-15 days','+13 hours','+18 minutes')),
  (36, 3, datetime('now','localtime','start of day','-11 days','+16 hours','+38 minutes'), 'https://example.org/jejak/P3/kelompok-04', 81, NULL, 6, datetime('now','localtime','start of day','-10 days','+9 hours','+38 minutes')),
  (36, 4, datetime('now','localtime','start of day','-7 days','+14 hours','+45 minutes'), 'https://example.org/jejak/P4/5025251036', 70, NULL, 6, datetime('now','localtime','start of day','-6 days','+6 hours','+45 minutes')),
  (36, 5, datetime('now','localtime','start of day','-2 days','+21 hours','+3 minutes'), 'https://example.org/jejak/P5/5025251036', 81, NULL, 6, datetime('now','localtime','start of day','+0 days','+1 hours','+3 minutes')),
  (37, 4, datetime('now','localtime','start of day','-7 days','+22 hours','+5 minutes'), 'https://example.org/jejak/P4/5025251037', 70, NULL, 6, datetime('now','localtime','start of day','+0 days','+9 hours','+36 minutes')),
  (37, 5, datetime('now','localtime','start of day','-1 days','+9 hours','+47 minutes'), 'https://example.org/jejak/P5/5025251037', 70, 'Terlambat ±10 jam.', 6, datetime('now','localtime','start of day','+0 days','+9 hours','+58 minutes')),
  (37, 6, datetime('now','localtime','start of day','-3 days','+15 hours','+41 minutes'), 'https://example.org/jejak/P6/kelompok-04', NULL, NULL, NULL, NULL),
  (38, 1, datetime('now','localtime','start of day','-19 days','+18 hours','+13 minutes'), 'https://example.org/jejak/P1/5025251038', 94, NULL, 6, datetime('now','localtime','start of day','-17 days','+4 hours','+13 minutes')),
  (38, 2, datetime('now','localtime','start of day','-19 days','+21 hours','+21 minutes'), 'https://example.org/jejak/P2/kelompok-04', 97, NULL, 6, datetime('now','localtime','start of day','-17 days','+10 hours','+21 minutes')),
  (38, 4, datetime('now','localtime','start of day','-8 days','+2 hours','+3 minutes'), 'https://example.org/jejak/P4/5025251038', 80, NULL, 6, datetime('now','localtime','start of day','-7 days','+10 hours','+3 minutes')),
  (38, 5, datetime('now','localtime','start of day','-2 days','+22 hours','+30 minutes'), NULL, NULL, NULL, NULL, NULL),
  (39, 1, datetime('now','localtime','start of day','-24 days','+1 hours','+54 minutes'), 'https://example.org/jejak/P1/5025251039', 74, NULL, 6, datetime('now','localtime','start of day','-24 days','+12 hours','+54 minutes')),
  (39, 2, datetime('now','localtime','start of day','-19 days','+21 hours','+29 minutes'), 'https://example.org/jejak/P2/kelompok-04', 76, NULL, 6, datetime('now','localtime','start of day','-18 days','+6 hours','+29 minutes')),
  (39, 4, datetime('now','localtime','start of day','-8 days','+23 hours','+36 minutes'), 'https://example.org/jejak/P4/5025251039', 70, NULL, 6, datetime('now','localtime','start of day','-7 days','+16 hours','+36 minutes')),
  (39, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+12 minutes'), NULL, NULL, NULL, NULL, NULL),
  (40, 1, datetime('now','localtime','start of day','-27 days','+9 hours','+48 minutes'), 'https://example.org/jejak/P1/5025251040', 88, NULL, 6, datetime('now','localtime','start of day','-26 days','+2 hours','+48 minutes')),
  (40, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+34 minutes'), 'https://example.org/jejak/P2/kelompok-04', 90, NULL, 6, datetime('now','localtime','start of day','-14 days','+5 hours','+34 minutes')),
  (40, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+44 minutes'), 'https://example.org/jejak/P3/kelompok-04', 86, NULL, 6, datetime('now','localtime','start of day','-9 days','+7 hours','+44 minutes')),
  (40, 4, datetime('now','localtime','start of day','-10 days','+6 hours','+18 minutes'), 'https://example.org/jejak/P4/5025251040', 85, NULL, 6, datetime('now','localtime','start of day','-9 days','+15 hours','+18 minutes')),
  (41, 1, datetime('now','localtime','start of day','-20 days','+14 hours','+27 minutes'), 'https://example.org/jejak/P1/5025251041', 85, NULL, 1, datetime('now','localtime','start of day','-19 days','+23 hours','+27 minutes')),
  (41, 2, datetime('now','localtime','start of day','-17 days','+4 hours','+25 minutes'), 'https://example.org/jejak/P2/kelompok-05', 83, NULL, 1, datetime('now','localtime','start of day','-16 days','+16 hours','+25 minutes')),
  (41, 4, datetime('now','localtime','start of day','-7 days','+22 hours','+31 minutes'), 'https://example.org/jejak/P4/5025251041', 75, NULL, 1, datetime('now','localtime','start of day','-5 days','+4 hours','+31 minutes')),
  (41, 5, datetime('now','localtime','start of day','-8 days','+22 hours','+5 minutes'), 'https://example.org/jejak/P5/5025251041', 86, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (41, 6, datetime('now','localtime','start of day','-2 days','+21 hours','+58 minutes'), 'https://example.org/jejak/P6/kelompok-05', NULL, NULL, NULL, NULL),
  (42, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+51 minutes'), 'https://example.org/jejak/P1/5025251042', 85, NULL, 1, datetime('now','localtime','start of day','-18 days','+8 hours','+51 minutes')),
  (42, 2, datetime('now','localtime','start of day','-19 days','+10 hours','+3 minutes'), 'https://example.org/jejak/P2/kelompok-05', 90, NULL, 1, datetime('now','localtime','start of day','-17 days','+2 hours','+3 minutes')),
  (42, 3, datetime('now','localtime','start of day','-11 days','+12 hours','+50 minutes'), 'https://example.org/jejak/P3/kelompok-05', 95, NULL, 1, datetime('now','localtime','start of day','-10 days','+0 hours','+50 minutes')),
  (42, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+33 minutes'), 'https://example.org/jejak/P4/5025251042', 88, NULL, 1, datetime('now','localtime','start of day','-5 days','+13 hours','+33 minutes')),
  (42, 5, datetime('now','localtime','start of day','-3 days','+11 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251042', 91, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (42, 6, datetime('now','localtime','start of day','-2 days','+6 hours','+54 minutes'), 'https://example.org/jejak/P6/kelompok-05', NULL, NULL, NULL, NULL),
  (43, 1, datetime('now','localtime','start of day','-22 days','+1 hours','+7 minutes'), 'https://example.org/jejak/P1/5025251043', 93, NULL, 1, datetime('now','localtime','start of day','-22 days','+20 hours','+7 minutes')),
  (43, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+53 minutes'), 'https://example.org/jejak/P2/kelompok-05', 84, NULL, 1, datetime('now','localtime','start of day','-14 days','+5 hours','+53 minutes')),
  (43, 3, datetime('now','localtime','start of day','-11 days','+2 hours','+27 minutes'), 'https://example.org/jejak/P3/kelompok-05', 74, NULL, 1, datetime('now','localtime','start of day','-10 days','+8 hours','+27 minutes')),
  (43, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+54 minutes'), 'https://example.org/jejak/P4/5025251043', 74, NULL, 1, datetime('now','localtime','start of day','-6 days','+13 hours','+54 minutes')),
  (43, 5, datetime('now','localtime','start of day','-4 days','+4 hours','+18 minutes'), 'https://example.org/jejak/P5/5025251043', 84, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (43, 6, datetime('now','localtime','start of day','+0 days','+6 hours','+0 minutes'), 'https://example.org/jejak/P6/kelompok-05', NULL, NULL, NULL, NULL),
  (44, 1, datetime('now','localtime','start of day','-27 days','+6 hours','+52 minutes'), 'https://example.org/jejak/P1/5025251044', 89, NULL, 1, datetime('now','localtime','start of day','-27 days','+12 hours','+52 minutes')),
  (44, 2, datetime('now','localtime','start of day','-16 days','+1 hours','+23 minutes'), 'https://example.org/jejak/P2/kelompok-05', 88, NULL, 1, datetime('now','localtime','start of day','-16 days','+23 hours','+23 minutes')),
  (44, 3, datetime('now','localtime','start of day','-17 days','+6 hours','+57 minutes'), 'https://example.org/jejak/P3/kelompok-05', 86, NULL, 1, datetime('now','localtime','start of day','-17 days','+19 hours','+57 minutes')),
  (44, 4, datetime('now','localtime','start of day','-8 days','+8 hours','+39 minutes'), 'https://example.org/jejak/P4/5025251044', 86, NULL, 1, datetime('now','localtime','start of day','-7 days','+9 hours','+39 minutes')),
  (45, 1, datetime('now','localtime','start of day','-24 days','+2 hours','+49 minutes'), 'https://example.org/jejak/P1/5025251045', 94, NULL, 1, datetime('now','localtime','start of day','-24 days','+9 hours','+49 minutes')),
  (45, 2, datetime('now','localtime','start of day','-19 days','+19 hours','+36 minutes'), 'https://example.org/jejak/P2/kelompok-05', 97, NULL, 1, datetime('now','localtime','start of day','-18 days','+11 hours','+36 minutes')),
  (45, 3, datetime('now','localtime','start of day','-14 days','+19 hours','+49 minutes'), 'https://example.org/jejak/P3/kelompok-05', 84, NULL, 1, datetime('now','localtime','start of day','-13 days','+23 hours','+49 minutes')),
  (45, 4, datetime('now','localtime','start of day','-13 days','+20 hours','+29 minutes'), 'https://example.org/jejak/P4/5025251045', 84, NULL, 1, datetime('now','localtime','start of day','-12 days','+4 hours','+29 minutes')),
  (45, 5, datetime('now','localtime','start of day','-5 days','+13 hours','+21 minutes'), 'https://example.org/jejak/P5/5025251045', 95, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (46, 1, datetime('now','localtime','start of day','-22 days','+10 hours','+47 minutes'), 'https://example.org/jejak/P1/5025251046', 75, NULL, 1, datetime('now','localtime','start of day','-21 days','+23 hours','+47 minutes')),
  (46, 2, datetime('now','localtime','start of day','-18 days','+8 hours','+54 minutes'), 'https://example.org/jejak/P2/kelompok-05', 83, NULL, 1, datetime('now','localtime','start of day','-17 days','+9 hours','+54 minutes')),
  (46, 3, datetime('now','localtime','start of day','-10 days','+1 hours','+27 minutes'), 'https://example.org/jejak/P3/kelompok-05', 86, NULL, 1, datetime('now','localtime','start of day','-10 days','+16 hours','+27 minutes')),
  (46, 4, datetime('now','localtime','start of day','-9 days','+19 hours','+22 minutes'), 'https://example.org/jejak/P4/5025251046', 98, NULL, 1, datetime('now','localtime','start of day','-8 days','+11 hours','+22 minutes')),
  (47, 1, datetime('now','localtime','start of day','-21 days','+5 hours','+43 minutes'), 'https://example.org/jejak/P1/5025251047', 88, NULL, 1, datetime('now','localtime','start of day','-20 days','+14 hours','+43 minutes')),
  (47, 2, datetime('now','localtime','start of day','-16 days','+22 hours','+59 minutes'), 'https://example.org/jejak/P2/kelompok-05', 87, NULL, 1, datetime('now','localtime','start of day','-14 days','+10 hours','+59 minutes')),
  (47, 3, datetime('now','localtime','start of day','-11 days','+0 hours','+38 minutes'), 'https://example.org/jejak/P3/kelompok-05', 87, NULL, 1, datetime('now','localtime','start of day','-11 days','+21 hours','+38 minutes')),
  (47, 4, datetime('now','localtime','start of day','-8 days','+19 hours','+47 minutes'), 'https://example.org/jejak/P4/5025251047', 80, NULL, 1, datetime('now','localtime','start of day','-6 days','+4 hours','+47 minutes')),
  (47, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251047', 85, NULL, 1, datetime('now','localtime','start of day','-1 days','+18 hours','+58 minutes')),
  (48, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+27 minutes'), 'https://example.org/jejak/P1/5025251048', 78, NULL, 1, datetime('now','localtime','start of day','-19 days','+12 hours','+27 minutes')),
  (48, 2, datetime('now','localtime','start of day','-16 days','+17 hours','+2 minutes'), 'https://example.org/jejak/P2/kelompok-05', 80, NULL, 1, datetime('now','localtime','start of day','-15 days','+1 hours','+2 minutes')),
  (48, 3, datetime('now','localtime','start of day','-11 days','+15 hours','+57 minutes'), 'https://example.org/jejak/P3/kelompok-05', 69, NULL, 1, datetime('now','localtime','start of day','-9 days','+3 hours','+57 minutes')),
  (48, 4, datetime('now','localtime','start of day','-7 days','+0 hours','+18 minutes'), 'https://example.org/jejak/P4/5025251048', 78, NULL, 1, datetime('now','localtime','start of day','-6 days','+15 hours','+18 minutes')),
  (48, 5, datetime('now','localtime','start of day','-4 days','+6 hours','+42 minutes'), 'https://example.org/jejak/P5/5025251048', 84, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (49, 1, datetime('now','localtime','start of day','-24 days','+15 hours','+1 minutes'), 'https://example.org/jejak/P1/5025251049', 85, NULL, 1, datetime('now','localtime','start of day','-23 days','+12 hours','+1 minutes')),
  (49, 2, datetime('now','localtime','start of day','-19 days','+8 hours','+11 minutes'), 'https://example.org/jejak/P2/kelompok-05', 83, NULL, 1, datetime('now','localtime','start of day','-18 days','+11 hours','+11 minutes')),
  (49, 3, datetime('now','localtime','start of day','-11 days','+3 hours','+23 minutes'), 'https://example.org/jejak/P3/kelompok-05', 81, NULL, 1, datetime('now','localtime','start of day','-10 days','+1 hours','+23 minutes')),
  (49, 4, datetime('now','localtime','start of day','-9 days','+8 hours','+3 minutes'), 'https://example.org/jejak/P4/5025251049', 78, NULL, 1, datetime('now','localtime','start of day','-8 days','+21 hours','+3 minutes')),
  (49, 5, datetime('now','localtime','start of day','-4 days','+21 hours','+53 minutes'), 'https://example.org/jejak/P5/5025251049', 90, NULL, 1, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (50, 1, datetime('now','localtime','start of day','-20 days','+15 hours','+28 minutes'), 'https://example.org/jejak/P1/5025251050', 87, NULL, 1, datetime('now','localtime','start of day','-19 days','+7 hours','+28 minutes')),
  (50, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+53 minutes'), 'https://example.org/jejak/P2/kelompok-05', 86, NULL, 1, datetime('now','localtime','start of day','-15 days','+13 hours','+53 minutes')),
  (50, 3, datetime('now','localtime','start of day','-12 days','+21 hours','+40 minutes'), 'https://example.org/jejak/P3/kelompok-05', 86, NULL, 1, datetime('now','localtime','start of day','-11 days','+14 hours','+40 minutes')),
  (50, 4, datetime('now','localtime','start of day','-8 days','+19 hours','+42 minutes'), 'https://example.org/jejak/P4/5025251050', 84, NULL, 1, datetime('now','localtime','start of day','-7 days','+12 hours','+42 minutes')),
  (50, 5, datetime('now','localtime','start of day','-1 days','+10 hours','+57 minutes'), 'https://example.org/jejak/P5/5025251050', 94, NULL, 1, datetime('now','localtime','start of day','+0 days','+9 hours','+58 minutes')),
  (51, 1, datetime('now','localtime','start of day','-25 days','+14 hours','+23 minutes'), 'https://example.org/jejak/P1/5025251051', 67, NULL, 8, datetime('now','localtime','start of day','-25 days','+21 hours','+23 minutes')),
  (51, 2, datetime('now','localtime','start of day','-18 days','+18 hours','+44 minutes'), 'https://example.org/jejak/P2/kelompok-06', 86, NULL, 8, datetime('now','localtime','start of day','-16 days','+8 hours','+44 minutes')),
  (51, 3, datetime('now','localtime','start of day','-12 days','+9 hours','+39 minutes'), 'https://example.org/jejak/P3/kelompok-06', 69, NULL, 8, datetime('now','localtime','start of day','-11 days','+10 hours','+39 minutes')),
  (51, 4, datetime('now','localtime','start of day','-11 days','+8 hours','+10 minutes'), 'https://example.org/jejak/P4/5025251051', 73, NULL, 8, datetime('now','localtime','start of day','-10 days','+6 hours','+10 minutes')),
  (51, 5, datetime('now','localtime','start of day','-6 days','+17 hours','+39 minutes'), 'https://example.org/jejak/P5/5025251051', 74, NULL, 8, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (52, 1, datetime('now','localtime','start of day','-20 days','+0 hours','+8 minutes'), 'https://example.org/jejak/P1/5025251052', 77, NULL, 8, datetime('now','localtime','start of day','-20 days','+22 hours','+8 minutes')),
  (52, 2, datetime('now','localtime','start of day','-16 days','+10 hours','+58 minutes'), 'https://example.org/jejak/P2/kelompok-06', 81, NULL, 8, datetime('now','localtime','start of day','-15 days','+0 hours','+58 minutes')),
  (53, 1, datetime('now','localtime','start of day','-24 days','+20 hours','+5 minutes'), 'https://example.org/jejak/P1/5025251053', 79, NULL, 8, datetime('now','localtime','start of day','-23 days','+12 hours','+5 minutes')),
  (53, 2, datetime('now','localtime','start of day','-17 days','+22 hours','+56 minutes'), 'https://example.org/jejak/P2/kelompok-06', 75, NULL, 8, datetime('now','localtime','start of day','-15 days','+1 hours','+56 minutes')),
  (53, 3, datetime('now','localtime','start of day','-17 days','+0 hours','+55 minutes'), 'https://example.org/jejak/P3/kelompok-06', 81, NULL, 8, datetime('now','localtime','start of day','-17 days','+22 hours','+55 minutes')),
  (53, 4, datetime('now','localtime','start of day','-8 days','+3 hours','+43 minutes'), 'https://example.org/jejak/P4/5025251053', 83, NULL, 8, datetime('now','localtime','start of day','-7 days','+5 hours','+43 minutes')),
  (53, 5, datetime('now','localtime','start of day','-2 days','+22 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251053', 71, NULL, 8, datetime('now','localtime','start of day','+0 days','+8 hours','+16 minutes')),
  (54, 1, datetime('now','localtime','start of day','-26 days','+22 hours','+31 minutes'), 'https://example.org/jejak/P1/5025251054', 85, NULL, 8, datetime('now','localtime','start of day','-25 days','+6 hours','+31 minutes')),
  (54, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P2/kelompok-06', 84, NULL, 8, datetime('now','localtime','start of day','-15 days','+17 hours','+58 minutes')),
  (54, 3, datetime('now','localtime','start of day','-14 days','+3 hours','+40 minutes'), 'https://example.org/jejak/P3/kelompok-06', 78, NULL, 8, datetime('now','localtime','start of day','-14 days','+22 hours','+40 minutes')),
  (54, 4, datetime('now','localtime','start of day','-9 days','+8 hours','+15 minutes'), 'https://example.org/jejak/P4/5025251054', 76, NULL, 8, datetime('now','localtime','start of day','-9 days','+19 hours','+15 minutes')),
  (54, 5, datetime('now','localtime','start of day','-2 days','+18 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251054', 85, NULL, 8, datetime('now','localtime','start of day','-1 days','+9 hours','+58 minutes')),
  (54, 6, datetime('now','localtime','start of day','-3 days','+16 hours','+40 minutes'), 'https://example.org/jejak/P6/kelompok-06', NULL, NULL, NULL, NULL),
  (55, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+6 minutes'), 'https://example.org/jejak/P1/5025251055', 69, NULL, 8, datetime('now','localtime','start of day','-18 days','+15 hours','+6 minutes')),
  (55, 2, datetime('now','localtime','start of day','-20 days','+21 hours','+6 minutes'), 'https://example.org/jejak/P2/kelompok-06', 75, NULL, 8, datetime('now','localtime','start of day','-18 days','+0 hours','+6 minutes')),
  (55, 3, datetime('now','localtime','start of day','-12 days','+2 hours','+45 minutes'), 'https://example.org/jejak/P3/kelompok-06', 79, NULL, 8, datetime('now','localtime','start of day','-11 days','+16 hours','+45 minutes')),
  (55, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P4/5025251055', 67, NULL, 8, datetime('now','localtime','start of day','-5 days','+8 hours','+57 minutes')),
  (55, 5, datetime('now','localtime','start of day','-7 days','+0 hours','+22 minutes'), 'https://example.org/jejak/P5/5025251055', 75, NULL, 8, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (56, 1, datetime('now','localtime','start of day','-20 days','+22 hours','+48 minutes'), 'https://example.org/jejak/P1/5025251056', 84, NULL, 8, datetime('now','localtime','start of day','-19 days','+20 hours','+48 minutes')),
  (56, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+0 minutes'), 'https://example.org/jejak/P2/kelompok-06', 81, NULL, 8, datetime('now','localtime','start of day','-15 days','+7 hours','+0 minutes')),
  (56, 3, datetime('now','localtime','start of day','-11 days','+16 hours','+9 minutes'), 'https://example.org/jejak/P3/kelompok-06', 92, NULL, 8, datetime('now','localtime','start of day','-10 days','+13 hours','+9 minutes')),
  (56, 4, datetime('now','localtime','start of day','-6 days','+5 hours','+19 minutes'), 'https://example.org/jejak/P4/5025251056', 90, NULL, 8, datetime('now','localtime','start of day','-6 days','+22 hours','+19 minutes')),
  (56, 5, datetime('now','localtime','start of day','-6 days','+5 hours','+17 minutes'), 'https://example.org/jejak/P5/5025251056', 80, NULL, 8, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (57, 1, datetime('now','localtime','start of day','-20 days','+3 hours','+5 minutes'), 'https://example.org/jejak/P1/5025251057', 87, NULL, 8, datetime('now','localtime','start of day','-19 days','+13 hours','+5 minutes')),
  (57, 2, datetime('now','localtime','start of day','-17 days','+12 hours','+4 minutes'), 'https://example.org/jejak/P2/kelompok-06', 81, NULL, 8, datetime('now','localtime','start of day','-16 days','+6 hours','+4 minutes')),
  (57, 3, datetime('now','localtime','start of day','-11 days','+10 hours','+21 minutes'), 'https://example.org/jejak/P3/kelompok-06', 95, NULL, 8, datetime('now','localtime','start of day','-11 days','+18 hours','+21 minutes')),
  (57, 4, datetime('now','localtime','start of day','-10 days','+8 hours','+56 minutes'), 'https://example.org/jejak/P4/5025251057', 78, NULL, 8, datetime('now','localtime','start of day','-10 days','+16 hours','+56 minutes')),
  (57, 5, datetime('now','localtime','start of day','-2 days','+19 hours','+29 minutes'), 'https://example.org/jejak/P5/5025251057', 86, NULL, 8, datetime('now','localtime','start of day','+0 days','+8 hours','+29 minutes')),
  (58, 1, datetime('now','localtime','start of day','-22 days','+7 hours','+21 minutes'), 'https://example.org/jejak/P1/5025251058', 70, NULL, 8, datetime('now','localtime','start of day','-21 days','+18 hours','+21 minutes')),
  (58, 2, datetime('now','localtime','start of day','-17 days','+18 hours','+37 minutes'), 'https://example.org/jejak/P2/kelompok-06', 78, NULL, 8, datetime('now','localtime','start of day','-15 days','+4 hours','+37 minutes')),
  (58, 4, datetime('now','localtime','start of day','-7 days','+12 hours','+56 minutes'), 'https://example.org/jejak/P4/5025251058', 74, NULL, 8, datetime('now','localtime','start of day','-5 days','+2 hours','+56 minutes')),
  (59, 1, datetime('now','localtime','start of day','-20 days','+17 hours','+55 minutes'), 'https://example.org/jejak/P1/5025251059', 87, NULL, 8, datetime('now','localtime','start of day','-18 days','+5 hours','+55 minutes')),
  (59, 2, datetime('now','localtime','start of day','-16 days','+11 hours','+28 minutes'), 'https://example.org/jejak/P2/kelompok-06', 75, NULL, 8, datetime('now','localtime','start of day','-16 days','+22 hours','+28 minutes')),
  (59, 3, datetime('now','localtime','start of day','-17 days','+16 hours','+59 minutes'), 'https://example.org/jejak/P3/kelompok-06', 74, NULL, 8, datetime('now','localtime','start of day','-16 days','+2 hours','+59 minutes')),
  (59, 4, datetime('now','localtime','start of day','-12 days','+4 hours','+57 minutes'), 'https://example.org/jejak/P4/5025251059', 90, NULL, 8, datetime('now','localtime','start of day','-11 days','+18 hours','+57 minutes')),
  (59, 5, datetime('now','localtime','start of day','-2 days','+6 hours','+22 minutes'), 'https://example.org/jejak/P5/5025251059', 73, NULL, 8, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (60, 1, datetime('now','localtime','start of day','-21 days','+21 hours','+33 minutes'), 'https://example.org/jejak/P1/5025251060', 82, NULL, 8, datetime('now','localtime','start of day','-20 days','+15 hours','+33 minutes')),
  (60, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+6 minutes'), 'https://example.org/jejak/P2/kelompok-06', 91, NULL, 8, datetime('now','localtime','start of day','-15 days','+7 hours','+6 minutes')),
  (60, 3, datetime('now','localtime','start of day','-15 days','+10 hours','+20 minutes'), 'https://example.org/jejak/P3/kelompok-06', 80, NULL, 8, datetime('now','localtime','start of day','-14 days','+5 hours','+20 minutes')),
  (60, 4, datetime('now','localtime','start of day','-9 days','+23 hours','+19 minutes'), 'https://example.org/jejak/P4/5025251060', 92, NULL, 8, datetime('now','localtime','start of day','-7 days','+8 hours','+19 minutes')),
  (60, 5, datetime('now','localtime','start of day','-6 days','+14 hours','+49 minutes'), 'https://example.org/jejak/P5/5025251060', 84, NULL, 8, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (60, 6, datetime('now','localtime','start of day','+0 days','+3 hours','+9 minutes'), 'https://example.org/jejak/P6/kelompok-06', NULL, NULL, NULL, NULL),
  (61, 1, datetime('now','localtime','start of day','-20 days','+3 hours','+40 minutes'), 'https://example.org/jejak/P1/5025251061', 83, NULL, 9, datetime('now','localtime','start of day','-20 days','+14 hours','+40 minutes')),
  (61, 2, datetime('now','localtime','start of day','-16 days','+15 hours','+52 minutes'), 'https://example.org/jejak/P2/kelompok-07', 93, NULL, 9, datetime('now','localtime','start of day','-15 days','+12 hours','+52 minutes')),
  (61, 3, datetime('now','localtime','start of day','-15 days','+19 hours','+4 minutes'), 'https://example.org/jejak/P3/kelompok-07', 95, NULL, 9, datetime('now','localtime','start of day','-13 days','+4 hours','+4 minutes')),
  (61, 4, datetime('now','localtime','start of day','-7 days','+12 hours','+5 minutes'), 'https://example.org/jejak/P4/5025251061', 95, NULL, 9, datetime('now','localtime','start of day','-5 days','+4 hours','+5 minutes')),
  (61, 5, datetime('now','localtime','start of day','-5 days','+5 hours','+42 minutes'), 'https://example.org/jejak/P5/5025251061', 95, NULL, 9, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (62, 1, datetime('now','localtime','start of day','-23 days','+5 hours','+57 minutes'), 'https://example.org/jejak/P1/5025251062', 98, NULL, 9, datetime('now','localtime','start of day','-22 days','+4 hours','+57 minutes')),
  (62, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+10 minutes'), 'https://example.org/jejak/P2/kelompok-07', 82, NULL, 9, datetime('now','localtime','start of day','-15 days','+20 hours','+10 minutes')),
  (62, 3, datetime('now','localtime','start of day','-12 days','+8 hours','+46 minutes'), 'https://example.org/jejak/P3/kelompok-07', 84, NULL, 9, datetime('now','localtime','start of day','-12 days','+18 hours','+46 minutes')),
  (62, 4, datetime('now','localtime','start of day','-8 days','+23 hours','+17 minutes'), 'https://example.org/jejak/P4/5025251062', 85, NULL, 9, datetime('now','localtime','start of day','-7 days','+12 hours','+17 minutes')),
  (62, 5, datetime('now','localtime','start of day','-9 days','+0 hours','+46 minutes'), 'https://example.org/jejak/P5/5025251062', 97, NULL, 9, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (62, 6, datetime('now','localtime','start of day','-2 days','+4 hours','+13 minutes'), 'https://example.org/jejak/P6/kelompok-07', NULL, NULL, NULL, NULL),
  (63, 1, datetime('now','localtime','start of day','-24 days','+22 hours','+26 minutes'), 'https://example.org/jejak/P1/5025251063', 81, NULL, 9, datetime('now','localtime','start of day','-23 days','+16 hours','+26 minutes')),
  (63, 2, datetime('now','localtime','start of day','-17 days','+14 hours','+13 minutes'), 'https://example.org/jejak/P2/kelompok-07', 85, NULL, 9, datetime('now','localtime','start of day','-15 days','+0 hours','+13 minutes')),
  (63, 3, datetime('now','localtime','start of day','-10 days','+10 hours','+16 minutes'), 'https://example.org/jejak/P3/kelompok-07', 78, NULL, 9, datetime('now','localtime','start of day','-10 days','+17 hours','+16 minutes')),
  (63, 4, datetime('now','localtime','start of day','-11 days','+18 hours','+55 minutes'), 'https://example.org/jejak/P4/5025251063', 86, NULL, 9, datetime('now','localtime','start of day','-10 days','+5 hours','+55 minutes')),
  (63, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+42 minutes'), 'https://example.org/jejak/P5/5025251063', 81, NULL, 9, datetime('now','localtime','start of day','-1 days','+10 hours','+42 minutes')),
  (64, 1, datetime('now','localtime','start of day','-22 days','+8 hours','+3 minutes'), 'https://example.org/jejak/P1/5025251064', 91, NULL, 9, datetime('now','localtime','start of day','-22 days','+17 hours','+3 minutes')),
  (64, 2, datetime('now','localtime','start of day','-16 days','+6 hours','+52 minutes'), 'https://example.org/jejak/P2/kelompok-07', 89, NULL, 9, datetime('now','localtime','start of day','-15 days','+12 hours','+52 minutes')),
  (64, 3, datetime('now','localtime','start of day','-13 days','+10 hours','+0 minutes'), 'https://example.org/jejak/P3/kelompok-07', 92, NULL, 9, datetime('now','localtime','start of day','-13 days','+23 hours','+0 minutes')),
  (64, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+2 minutes'), 'https://example.org/jejak/P4/5025251064', 92, NULL, 9, datetime('now','localtime','start of day','-6 days','+5 hours','+2 minutes')),
  (64, 5, datetime('now','localtime','start of day','-2 days','+18 hours','+54 minutes'), 'https://example.org/jejak/P5/5025251064', 98, NULL, 9, datetime('now','localtime','start of day','-1 days','+20 hours','+54 minutes')),
  (64, 6, datetime('now','localtime','start of day','-1 days','+17 hours','+21 minutes'), 'https://example.org/jejak/P6/kelompok-07', NULL, NULL, NULL, NULL),
  (65, 1, datetime('now','localtime','start of day','-20 days','+20 hours','+36 minutes'), 'https://example.org/jejak/P1/5025251065', 88, NULL, 9, datetime('now','localtime','start of day','-19 days','+16 hours','+36 minutes')),
  (65, 2, datetime('now','localtime','start of day','-16 days','+20 hours','+45 minutes'), 'https://example.org/jejak/P2/kelompok-07', 82, NULL, 9, datetime('now','localtime','start of day','-15 days','+10 hours','+45 minutes')),
  (65, 3, datetime('now','localtime','start of day','-11 days','+6 hours','+18 minutes'), 'https://example.org/jejak/P3/kelompok-07', 98, NULL, 9, datetime('now','localtime','start of day','-11 days','+13 hours','+18 minutes')),
  (65, 4, datetime('now','localtime','start of day','-6 days','+3 hours','+38 minutes'), 'https://example.org/jejak/P4/5025251065', 84, NULL, 9, datetime('now','localtime','start of day','-6 days','+12 hours','+38 minutes')),
  (65, 5, datetime('now','localtime','start of day','-2 days','+17 hours','+11 minutes'), 'https://example.org/jejak/P5/5025251065', 85, NULL, 9, datetime('now','localtime','start of day','-1 days','+15 hours','+11 minutes')),
  (66, 1, datetime('now','localtime','start of day','-22 days','+21 hours','+46 minutes'), 'https://example.org/jejak/P1/5025251066', 87, NULL, 9, datetime('now','localtime','start of day','-20 days','+2 hours','+46 minutes')),
  (66, 2, datetime('now','localtime','start of day','-18 days','+1 hours','+44 minutes'), 'https://example.org/jejak/P2/kelompok-07', 84, NULL, 9, datetime('now','localtime','start of day','-17 days','+9 hours','+44 minutes')),
  (66, 3, datetime('now','localtime','start of day','-11 days','+21 hours','+24 minutes'), 'https://example.org/jejak/P3/kelompok-07', 81, NULL, 9, datetime('now','localtime','start of day','-10 days','+11 hours','+24 minutes')),
  (66, 4, datetime('now','localtime','start of day','-9 days','+19 hours','+45 minutes'), 'https://example.org/jejak/P4/5025251066', 87, NULL, 9, datetime('now','localtime','start of day','-8 days','+23 hours','+45 minutes')),
  (66, 5, datetime('now','localtime','start of day','-2 days','+18 hours','+57 minutes'), 'https://example.org/jejak/P5/5025251066', NULL, NULL, NULL, NULL),
  (66, 6, datetime('now','localtime','start of day','-3 days','+23 hours','+36 minutes'), 'https://example.org/jejak/P6/kelompok-07', NULL, NULL, NULL, NULL),
  (67, 1, datetime('now','localtime','start of day','-22 days','+10 hours','+39 minutes'), 'https://example.org/jejak/P1/5025251067', 73, NULL, 9, datetime('now','localtime','start of day','-22 days','+21 hours','+39 minutes')),
  (67, 2, datetime('now','localtime','start of day','-17 days','+17 hours','+45 minutes'), 'https://example.org/jejak/P2/kelompok-07', 73, NULL, 9, datetime('now','localtime','start of day','-16 days','+5 hours','+45 minutes')),
  (67, 3, datetime('now','localtime','start of day','-11 days','+7 hours','+48 minutes'), 'https://example.org/jejak/P3/kelompok-07', 85, NULL, 9, datetime('now','localtime','start of day','-10 days','+6 hours','+48 minutes')),
  (67, 4, datetime('now','localtime','start of day','-12 days','+3 hours','+32 minutes'), 'https://example.org/jejak/P4/5025251067', 72, NULL, 9, datetime('now','localtime','start of day','-12 days','+20 hours','+32 minutes')),
  (67, 5, datetime('now','localtime','start of day','-2 days','+20 hours','+56 minutes'), 'https://example.org/jejak/P5/5025251067', 64, NULL, 9, datetime('now','localtime','start of day','-1 days','+15 hours','+56 minutes')),
  (68, 1, datetime('now','localtime','start of day','-20 days','+17 hours','+55 minutes'), 'https://example.org/jejak/P1/5025251068', 98, NULL, 9, datetime('now','localtime','start of day','-19 days','+20 hours','+55 minutes')),
  (68, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+37 minutes'), 'https://example.org/jejak/P2/kelompok-07', 98, NULL, 9, datetime('now','localtime','start of day','-14 days','+13 hours','+37 minutes')),
  (68, 3, datetime('now','localtime','start of day','-11 days','+12 hours','+13 minutes'), 'https://example.org/jejak/P3/kelompok-07', 98, NULL, 9, datetime('now','localtime','start of day','-10 days','+9 hours','+13 minutes')),
  (68, 4, datetime('now','localtime','start of day','-12 days','+2 hours','+9 minutes'), 'https://example.org/jejak/P4/5025251068', 89, NULL, 9, datetime('now','localtime','start of day','-12 days','+16 hours','+9 minutes')),
  (68, 5, datetime('now','localtime','start of day','-3 days','+17 hours','+13 minutes'), 'https://example.org/jejak/P5/5025251068', 97, NULL, 9, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (68, 6, datetime('now','localtime','start of day','-3 days','+21 hours','+49 minutes'), 'https://example.org/jejak/P6/kelompok-07', NULL, NULL, NULL, NULL),
  (69, 1, datetime('now','localtime','start of day','-19 days','+11 hours','+14 minutes'), 'https://example.org/jejak/P1/5025251069', 85, NULL, 9, datetime('now','localtime','start of day','-18 days','+6 hours','+14 minutes')),
  (69, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+8 minutes'), 'https://example.org/jejak/P2/kelompok-07', 91, NULL, 9, datetime('now','localtime','start of day','-15 days','+8 hours','+8 minutes')),
  (69, 3, datetime('now','localtime','start of day','-15 days','+6 hours','+2 minutes'), 'https://example.org/jejak/P3/kelompok-07', 80, NULL, 9, datetime('now','localtime','start of day','-14 days','+9 hours','+2 minutes')),
  (69, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+49 minutes'), 'https://example.org/jejak/P4/5025251069', 76, NULL, 9, datetime('now','localtime','start of day','-6 days','+7 hours','+49 minutes')),
  (69, 5, datetime('now','localtime','start of day','-4 days','+14 hours','+59 minutes'), 'https://example.org/jejak/P5/5025251069', 74, NULL, 9, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (70, 1, datetime('now','localtime','start of day','-19 days','+12 hours','+9 minutes'), 'https://example.org/jejak/P1/5025251070', 91, NULL, 9, datetime('now','localtime','start of day','-18 days','+12 hours','+9 minutes')),
  (70, 2, datetime('now','localtime','start of day','-17 days','+15 hours','+9 minutes'), 'https://example.org/jejak/P2/kelompok-07', 85, NULL, 9, datetime('now','localtime','start of day','-16 days','+8 hours','+9 minutes')),
  (70, 3, datetime('now','localtime','start of day','-15 days','+17 hours','+50 minutes'), 'https://example.org/jejak/P3/kelompok-07', 78, NULL, 9, datetime('now','localtime','start of day','-14 days','+18 hours','+50 minutes')),
  (70, 4, datetime('now','localtime','start of day','-12 days','+14 hours','+58 minutes'), 'https://example.org/jejak/P4/5025251070', 86, NULL, 9, datetime('now','localtime','start of day','-11 days','+3 hours','+58 minutes')),
  (70, 5, datetime('now','localtime','start of day','-3 days','+17 hours','+28 minutes'), 'https://example.org/jejak/P5/5025251070', 90, NULL, 9, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (70, 6, datetime('now','localtime','start of day','-2 days','+12 hours','+55 minutes'), 'https://example.org/jejak/P6/kelompok-07', NULL, NULL, NULL, NULL),
  (71, 1, datetime('now','localtime','start of day','-25 days','+3 hours','+30 minutes'), 'https://example.org/jejak/P1/5025251071', 72, NULL, 10, datetime('now','localtime','start of day','-24 days','+5 hours','+30 minutes')),
  (71, 2, datetime('now','localtime','start of day','-19 days','+9 hours','+24 minutes'), 'https://example.org/jejak/P2/kelompok-08', 76, NULL, 10, datetime('now','localtime','start of day','-18 days','+21 hours','+24 minutes')),
  (71, 3, datetime('now','localtime','start of day','-11 days','+14 hours','+59 minutes'), 'https://example.org/jejak/P3/kelompok-08', 76, NULL, 10, datetime('now','localtime','start of day','-10 days','+2 hours','+59 minutes')),
  (71, 4, datetime('now','localtime','start of day','-13 days','+5 hours','+3 minutes'), 'https://example.org/jejak/P4/5025251071', 80, NULL, 10, datetime('now','localtime','start of day','-12 days','+2 hours','+3 minutes')),
  (71, 5, datetime('now','localtime','start of day','-5 days','+12 hours','+28 minutes'), 'https://example.org/jejak/P5/5025251071', 82, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (72, 1, datetime('now','localtime','start of day','-21 days','+23 hours','+16 minutes'), 'https://example.org/jejak/P1/5025251072', 84, NULL, 10, datetime('now','localtime','start of day','-19 days','+6 hours','+16 minutes')),
  (72, 2, datetime('now','localtime','start of day','-18 days','+17 hours','+2 minutes'), 'https://example.org/jejak/P2/kelompok-08', 74, NULL, 10, datetime('now','localtime','start of day','-17 days','+19 hours','+2 minutes')),
  (72, 3, datetime('now','localtime','start of day','-11 days','+11 hours','+24 minutes'), 'https://example.org/jejak/P3/kelompok-08', 76, NULL, 10, datetime('now','localtime','start of day','-11 days','+20 hours','+24 minutes')),
  (72, 4, datetime('now','localtime','start of day','-8 days','+21 hours','+24 minutes'), 'https://example.org/jejak/P4/5025251072', 84, NULL, 10, datetime('now','localtime','start of day','-6 days','+0 hours','+24 minutes')),
  (72, 5, datetime('now','localtime','start of day','-2 days','+1 hours','+40 minutes'), 'https://example.org/jejak/P5/5025251072', 79, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (73, 1, datetime('now','localtime','start of day','-24 days','+12 hours','+38 minutes'), 'https://example.org/jejak/P1/5025251073', 83, NULL, 10, datetime('now','localtime','start of day','-23 days','+0 hours','+38 minutes')),
  (73, 2, datetime('now','localtime','start of day','-18 days','+14 hours','+46 minutes'), 'https://example.org/jejak/P2/kelompok-08', 77, NULL, 10, datetime('now','localtime','start of day','-17 days','+3 hours','+46 minutes')),
  (73, 3, datetime('now','localtime','start of day','-15 days','+15 hours','+5 minutes'), 'https://example.org/jejak/P3/kelompok-08', 82, NULL, 10, datetime('now','localtime','start of day','-13 days','+5 hours','+5 minutes')),
  (73, 4, datetime('now','localtime','start of day','-7 days','+19 hours','+24 minutes'), 'https://example.org/jejak/P4/5025251073', 74, NULL, 10, datetime('now','localtime','start of day','-6 days','+9 hours','+24 minutes')),
  (73, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+15 minutes'), 'https://example.org/jejak/P5/5025251073', 83, NULL, 10, datetime('now','localtime','start of day','+0 days','+1 hours','+15 minutes')),
  (74, 1, datetime('now','localtime','start of day','-20 days','+16 hours','+44 minutes'), 'https://example.org/jejak/P1/5025251074', 75, NULL, 10, datetime('now','localtime','start of day','-19 days','+0 hours','+44 minutes')),
  (74, 2, datetime('now','localtime','start of day','-16 days','+14 hours','+56 minutes'), 'https://example.org/jejak/P2/kelompok-08', 80, NULL, 10, datetime('now','localtime','start of day','-15 days','+16 hours','+56 minutes')),
  (75, 1, datetime('now','localtime','start of day','-19 days','+9 hours','+50 minutes'), 'https://example.org/jejak/P1/5025251075', 93, NULL, 10, datetime('now','localtime','start of day','-18 days','+20 hours','+50 minutes')),
  (75, 2, datetime('now','localtime','start of day','-18 days','+18 hours','+15 minutes'), 'https://example.org/jejak/P2/kelompok-08', 80, NULL, 10, datetime('now','localtime','start of day','-17 days','+6 hours','+15 minutes')),
  (75, 3, datetime('now','localtime','start of day','-11 days','+14 hours','+6 minutes'), 'https://example.org/jejak/P3/kelompok-08', 92, NULL, 10, datetime('now','localtime','start of day','-10 days','+3 hours','+6 minutes')),
  (75, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+32 minutes'), 'https://example.org/jejak/P4/5025251075', 83, NULL, 10, datetime('now','localtime','start of day','-6 days','+16 hours','+32 minutes')),
  (76, 1, datetime('now','localtime','start of day','-23 days','+8 hours','+39 minutes'), 'https://example.org/jejak/P1/5025251076', 84, NULL, 10, datetime('now','localtime','start of day','-23 days','+20 hours','+39 minutes')),
  (76, 2, datetime('now','localtime','start of day','-18 days','+16 hours','+15 minutes'), 'https://example.org/jejak/P2/kelompok-08', 76, NULL, 10, datetime('now','localtime','start of day','-17 days','+23 hours','+15 minutes')),
  (76, 4, datetime('now','localtime','start of day','-11 days','+14 hours','+20 minutes'), 'https://example.org/jejak/P4/5025251076', 79, NULL, 10, datetime('now','localtime','start of day','-10 days','+12 hours','+20 minutes')),
  (76, 5, datetime('now','localtime','start of day','-9 days','+20 hours','+48 minutes'), 'https://example.org/jejak/P5/5025251076', 78, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (76, 6, datetime('now','localtime','start of day','-1 days','+8 hours','+46 minutes'), 'https://example.org/jejak/P6/kelompok-08', NULL, NULL, NULL, NULL),
  (77, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P1/5025251077', 72, NULL, 10, datetime('now','localtime','start of day','-19 days','+21 hours','+58 minutes')),
  (77, 2, datetime('now','localtime','start of day','-16 days','+16 hours','+30 minutes'), 'https://example.org/jejak/P2/kelompok-08', 75, NULL, 10, datetime('now','localtime','start of day','-15 days','+2 hours','+30 minutes')),
  (77, 3, datetime('now','localtime','start of day','-11 days','+20 hours','+34 minutes'), 'https://example.org/jejak/P3/kelompok-08', 70, NULL, 10, datetime('now','localtime','start of day','-10 days','+23 hours','+34 minutes')),
  (77, 4, datetime('now','localtime','start of day','-12 days','+0 hours','+44 minutes'), 'https://example.org/jejak/P4/5025251077', 68, NULL, 10, datetime('now','localtime','start of day','-12 days','+9 hours','+44 minutes')),
  (78, 1, datetime('now','localtime','start of day','-27 days','+6 hours','+43 minutes'), 'https://example.org/jejak/P1/5025251078', 84, NULL, 10, datetime('now','localtime','start of day','-26 days','+0 hours','+43 minutes')),
  (78, 2, datetime('now','localtime','start of day','-17 days','+15 hours','+50 minutes'), 'https://example.org/jejak/P2/kelompok-08', 64, NULL, 10, datetime('now','localtime','start of day','-17 days','+22 hours','+50 minutes')),
  (78, 3, datetime('now','localtime','start of day','-11 days','+14 hours','+50 minutes'), 'https://example.org/jejak/P3/kelompok-08', 89, NULL, 10, datetime('now','localtime','start of day','-9 days','+6 hours','+50 minutes')),
  (78, 4, datetime('now','localtime','start of day','-7 days','+5 hours','+6 minutes'), 'https://example.org/jejak/P4/5025251078', 84, NULL, 10, datetime('now','localtime','start of day','-6 days','+6 hours','+6 minutes')),
  (78, 5, datetime('now','localtime','start of day','-5 days','+11 hours','+3 minutes'), 'https://example.org/jejak/P5/5025251078', 83, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (79, 2, datetime('now','localtime','start of day','-17 days','+5 hours','+7 minutes'), 'https://example.org/jejak/P2/kelompok-08', 75, NULL, 10, datetime('now','localtime','start of day','-17 days','+15 hours','+7 minutes')),
  (79, 3, datetime('now','localtime','start of day','-14 days','+1 hours','+37 minutes'), 'https://example.org/jejak/P3/kelompok-08', 75, NULL, 10, datetime('now','localtime','start of day','-14 days','+13 hours','+37 minutes')),
  (79, 4, datetime('now','localtime','start of day','-7 days','+22 hours','+43 minutes'), 'https://example.org/jejak/P4/5025251079', 76, NULL, 10, datetime('now','localtime','start of day','-6 days','+4 hours','+43 minutes')),
  (79, 5, datetime('now','localtime','start of day','-2 days','+20 hours','+46 minutes'), 'https://example.org/jejak/P5/5025251079', 72, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (80, 2, datetime('now','localtime','start of day','-15 days','+18 hours','+24 minutes'), 'https://example.org/jejak/P2/kelompok-08', 81, NULL, 10, datetime('now','localtime','start of day','-14 days','+16 hours','+24 minutes')),
  (80, 3, datetime('now','localtime','start of day','-12 days','+14 hours','+57 minutes'), 'https://example.org/jejak/P3/kelompok-08', 74, NULL, 10, datetime('now','localtime','start of day','-10 days','+6 hours','+57 minutes')),
  (80, 4, datetime('now','localtime','start of day','-8 days','+23 hours','+0 minutes'), 'https://example.org/jejak/P4/5025251080', 68, NULL, 10, datetime('now','localtime','start of day','-7 days','+13 hours','+0 minutes')),
  (80, 5, datetime('now','localtime','start of day','-7 days','+8 hours','+28 minutes'), 'https://example.org/jejak/P5/5025251080', 81, NULL, 10, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (81, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P1/5025251081', 85, NULL, 11, datetime('now','localtime','start of day','-19 days','+23 hours','+58 minutes')),
  (81, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+49 minutes'), 'https://example.org/jejak/P2/kelompok-09', 79, NULL, 11, datetime('now','localtime','start of day','-15 days','+9 hours','+49 minutes')),
  (81, 3, datetime('now','localtime','start of day','-13 days','+11 hours','+13 minutes'), 'https://example.org/jejak/P3/kelompok-09', 78, NULL, 11, datetime('now','localtime','start of day','-13 days','+18 hours','+13 minutes')),
  (81, 4, datetime('now','localtime','start of day','-7 days','+19 hours','+16 minutes'), 'https://example.org/jejak/P4/5025251081', 77, NULL, 11, datetime('now','localtime','start of day','-6 days','+15 hours','+16 minutes')),
  (81, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+6 minutes'), 'https://example.org/jejak/P5/5025251081', 75, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (82, 1, datetime('now','localtime','start of day','-22 days','+11 hours','+29 minutes'), 'https://example.org/jejak/P1/5025251082', 83, NULL, 11, datetime('now','localtime','start of day','-22 days','+23 hours','+29 minutes')),
  (82, 2, datetime('now','localtime','start of day','-16 days','+6 hours','+18 minutes'), 'https://example.org/jejak/P2/kelompok-09', 80, NULL, 11, datetime('now','localtime','start of day','-15 days','+12 hours','+18 minutes')),
  (82, 3, datetime('now','localtime','start of day','-10 days','+3 hours','+40 minutes'), 'https://example.org/jejak/P3/kelompok-09', 83, NULL, 11, datetime('now','localtime','start of day','-10 days','+18 hours','+40 minutes')),
  (82, 4, datetime('now','localtime','start of day','-8 days','+19 hours','+5 minutes'), 'https://example.org/jejak/P4/5025251082', 71, NULL, 11, datetime('now','localtime','start of day','-7 days','+3 hours','+5 minutes')),
  (82, 5, datetime('now','localtime','start of day','-1 days','+12 hours','+3 minutes'), 'https://example.org/jejak/P5/5025251082', 83, NULL, 11, datetime('now','localtime','start of day','+0 days','+9 hours','+58 minutes')),
  (83, 1, datetime('now','localtime','start of day','-20 days','+20 hours','+6 minutes'), 'https://example.org/jejak/P1/5025251083', 67, NULL, 11, datetime('now','localtime','start of day','-18 days','+11 hours','+6 minutes')),
  (83, 2, datetime('now','localtime','start of day','-19 days','+1 hours','+35 minutes'), 'https://example.org/jejak/P2/kelompok-09', 69, NULL, 11, datetime('now','localtime','start of day','-19 days','+16 hours','+35 minutes')),
  (83, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+37 minutes'), 'https://example.org/jejak/P3/kelompok-09', 78, NULL, 11, datetime('now','localtime','start of day','-10 days','+17 hours','+37 minutes')),
  (83, 4, datetime('now','localtime','start of day','-8 days','+9 hours','+25 minutes'), 'https://example.org/jejak/P4/5025251083', 78, NULL, 11, datetime('now','localtime','start of day','-8 days','+17 hours','+25 minutes')),
  (83, 5, datetime('now','localtime','start of day','-4 days','+18 hours','+46 minutes'), 'https://example.org/jejak/P5/5025251083', 81, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (84, 1, datetime('now','localtime','start of day','-20 days','+22 hours','+36 minutes'), 'https://example.org/jejak/P1/5025251084', 84, NULL, 11, datetime('now','localtime','start of day','-19 days','+10 hours','+36 minutes')),
  (84, 2, datetime('now','localtime','start of day','-15 days','+14 hours','+13 minutes'), 'https://example.org/jejak/P2/kelompok-09', 93, NULL, 11, datetime('now','localtime','start of day','-14 days','+4 hours','+13 minutes')),
  (84, 3, datetime('now','localtime','start of day','-11 days','+8 hours','+15 minutes'), 'https://example.org/jejak/P3/kelompok-09', 85, NULL, 11, datetime('now','localtime','start of day','-10 days','+20 hours','+15 minutes')),
  (84, 4, datetime('now','localtime','start of day','-6 days','+13 hours','+58 minutes'), 'https://example.org/jejak/P4/5025251084', 98, NULL, 11, datetime('now','localtime','start of day','-6 days','+20 hours','+58 minutes')),
  (84, 5, datetime('now','localtime','start of day','-2 days','+16 hours','+25 minutes'), 'https://example.org/jejak/P5/5025251084', 83, NULL, 11, datetime('now','localtime','start of day','-1 days','+12 hours','+25 minutes')),
  (84, 6, datetime('now','localtime','start of day','-1 days','+22 hours','+59 minutes'), 'https://example.org/jejak/P6/kelompok-09', NULL, NULL, NULL, NULL),
  (85, 1, datetime('now','localtime','start of day','-20 days','+21 hours','+10 minutes'), 'https://example.org/jejak/P1/5025251085', 89, NULL, 11, datetime('now','localtime','start of day','-18 days','+0 hours','+10 minutes')),
  (85, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+3 minutes'), 'https://example.org/jejak/P2/kelompok-09', 81, NULL, 11, datetime('now','localtime','start of day','-15 days','+9 hours','+3 minutes')),
  (85, 3, datetime('now','localtime','start of day','-11 days','+21 hours','+49 minutes'), 'https://example.org/jejak/P3/kelompok-09', 94, NULL, 11, datetime('now','localtime','start of day','-10 days','+6 hours','+49 minutes')),
  (85, 4, datetime('now','localtime','start of day','-13 days','+3 hours','+32 minutes'), 'https://example.org/jejak/P4/5025251085', 93, NULL, 11, datetime('now','localtime','start of day','-13 days','+16 hours','+32 minutes')),
  (85, 5, datetime('now','localtime','start of day','-2 days','+1 hours','+39 minutes'), 'https://example.org/jejak/P5/5025251085', 87, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (86, 1, datetime('now','localtime','start of day','-20 days','+20 hours','+57 minutes'), 'https://example.org/jejak/P1/5025251086', 97, NULL, 11, datetime('now','localtime','start of day','-18 days','+6 hours','+57 minutes')),
  (86, 2, datetime('now','localtime','start of day','-19 days','+10 hours','+10 minutes'), 'https://example.org/jejak/P2/kelompok-09', 87, NULL, 11, datetime('now','localtime','start of day','-18 days','+6 hours','+10 minutes')),
  (86, 3, datetime('now','localtime','start of day','-11 days','+20 hours','+24 minutes'), 'https://example.org/jejak/P3/kelompok-09', 82, NULL, 11, datetime('now','localtime','start of day','-9 days','+9 hours','+24 minutes')),
  (86, 4, datetime('now','localtime','start of day','-10 days','+18 hours','+19 minutes'), 'https://example.org/jejak/P4/5025251086', 92, NULL, 11, datetime('now','localtime','start of day','-9 days','+0 hours','+19 minutes')),
  (86, 5, datetime('now','localtime','start of day','-4 days','+2 hours','+11 minutes'), 'https://example.org/jejak/P5/5025251086', 98, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (87, 1, datetime('now','localtime','start of day','-27 days','+22 hours','+31 minutes'), 'https://example.org/jejak/P1/5025251087', 80, NULL, 11, datetime('now','localtime','start of day','-26 days','+17 hours','+31 minutes')),
  (87, 2, datetime('now','localtime','start of day','-19 days','+23 hours','+4 minutes'), 'https://example.org/jejak/P2/kelompok-09', 79, NULL, 11, datetime('now','localtime','start of day','-18 days','+11 hours','+4 minutes')),
  (87, 3, datetime('now','localtime','start of day','-11 days','+5 hours','+42 minutes'), 'https://example.org/jejak/P3/kelompok-09', 72, NULL, 11, datetime('now','localtime','start of day','-11 days','+17 hours','+42 minutes')),
  (87, 4, datetime('now','localtime','start of day','-8 days','+4 hours','+14 minutes'), 'https://example.org/jejak/P4/5025251087', 89, NULL, 11, datetime('now','localtime','start of day','-8 days','+18 hours','+14 minutes')),
  (87, 5, datetime('now','localtime','start of day','-5 days','+16 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251087', 76, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (88, 1, datetime('now','localtime','start of day','-22 days','+23 hours','+18 minutes'), 'https://example.org/jejak/P1/5025251088', 83, NULL, 11, datetime('now','localtime','start of day','-21 days','+15 hours','+18 minutes')),
  (88, 2, datetime('now','localtime','start of day','-16 days','+22 hours','+18 minutes'), 'https://example.org/jejak/P2/kelompok-09', 77, NULL, 11, datetime('now','localtime','start of day','-15 days','+23 hours','+18 minutes')),
  (88, 3, datetime('now','localtime','start of day','-11 days','+3 hours','+31 minutes'), 'https://example.org/jejak/P3/kelompok-09', 75, NULL, 11, datetime('now','localtime','start of day','-10 days','+3 hours','+31 minutes')),
  (88, 4, datetime('now','localtime','start of day','-7 days','+19 hours','+23 minutes'), 'https://example.org/jejak/P4/5025251088', 89, NULL, 11, datetime('now','localtime','start of day','-6 days','+4 hours','+23 minutes')),
  (88, 5, datetime('now','localtime','start of day','-3 days','+1 hours','+16 minutes'), 'https://example.org/jejak/P5/5025251088', 83, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (89, 1, datetime('now','localtime','start of day','-23 days','+9 hours','+45 minutes'), 'https://example.org/jejak/P1/5025251089', 76, NULL, 11, datetime('now','localtime','start of day','-22 days','+6 hours','+45 minutes')),
  (89, 2, datetime('now','localtime','start of day','-17 days','+10 hours','+39 minutes'), 'https://example.org/jejak/P2/kelompok-09', 75, NULL, 11, datetime('now','localtime','start of day','-17 days','+21 hours','+39 minutes')),
  (90, 1, datetime('now','localtime','start of day','-21 days','+22 hours','+11 minutes'), 'https://example.org/jejak/P1/5025251090', 75, NULL, 11, datetime('now','localtime','start of day','-20 days','+9 hours','+11 minutes')),
  (90, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P2/kelompok-09', 83, NULL, 11, datetime('now','localtime','start of day','-15 days','+6 hours','+57 minutes')),
  (90, 3, datetime('now','localtime','start of day','-14 days','+21 hours','+48 minutes'), 'https://example.org/jejak/P3/kelompok-09', 83, NULL, 11, datetime('now','localtime','start of day','-13 days','+12 hours','+48 minutes')),
  (90, 4, datetime('now','localtime','start of day','-12 days','+10 hours','+19 minutes'), 'https://example.org/jejak/P4/5025251090', 79, NULL, 11, datetime('now','localtime','start of day','-11 days','+9 hours','+19 minutes')),
  (90, 5, datetime('now','localtime','start of day','-6 days','+9 hours','+56 minutes'), 'https://example.org/jejak/P5/5025251090', 82, NULL, 11, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (91, 1, datetime('now','localtime','start of day','-20 days','+3 hours','+27 minutes'), 'https://example.org/jejak/P1/5025251091', 82, NULL, 12, datetime('now','localtime','start of day','-20 days','+16 hours','+27 minutes')),
  (91, 2, datetime('now','localtime','start of day','-15 days','+2 hours','+17 minutes'), 'https://example.org/jejak/P2/kelompok-10', 80, NULL, 12, datetime('now','localtime','start of day','-14 days','+11 hours','+17 minutes')),
  (91, 4, datetime('now','localtime','start of day','-13 days','+12 hours','+15 minutes'), 'https://example.org/jejak/P4/5025251091', 96, NULL, 12, datetime('now','localtime','start of day','-12 days','+15 hours','+15 minutes')),
  (91, 5, datetime('now','localtime','start of day','-8 days','+8 hours','+45 minutes'), 'https://example.org/jejak/P5/5025251091', 91, NULL, 12, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (92, 1, datetime('now','localtime','start of day','-26 days','+12 hours','+57 minutes'), 'https://example.org/jejak/P1/5025251092', 82, NULL, 12, datetime('now','localtime','start of day','-26 days','+20 hours','+57 minutes')),
  (92, 2, datetime('now','localtime','start of day','-16 days','+6 hours','+23 minutes'), 'https://example.org/jejak/P2/kelompok-10', 86, NULL, 12, datetime('now','localtime','start of day','-15 days','+13 hours','+23 minutes')),
  (92, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+52 minutes'), 'https://example.org/jejak/P3/kelompok-10', 88, NULL, 12, datetime('now','localtime','start of day','-10 days','+18 hours','+52 minutes')),
  (92, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P4/5025251092', 76, NULL, 12, datetime('now','localtime','start of day','-6 days','+22 hours','+57 minutes')),
  (92, 5, datetime('now','localtime','start of day','-2 days','+14 hours','+35 minutes'), 'https://example.org/jejak/P5/5025251092', 84, NULL, 12, datetime('now','localtime','start of day','-1 days','+20 hours','+35 minutes')),
  (92, 6, datetime('now','localtime','start of day','-1 days','+21 hours','+52 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (93, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+55 minutes'), 'https://example.org/jejak/P1/5025251093', 85, NULL, 12, datetime('now','localtime','start of day','-18 days','+7 hours','+55 minutes')),
  (93, 2, datetime('now','localtime','start of day','-19 days','+13 hours','+44 minutes'), 'https://example.org/jejak/P2/kelompok-10', 86, NULL, 12, datetime('now','localtime','start of day','-17 days','+2 hours','+44 minutes')),
  (93, 3, datetime('now','localtime','start of day','-10 days','+4 hours','+30 minutes'), 'https://example.org/jejak/P3/kelompok-10', 92, NULL, 12, datetime('now','localtime','start of day','-9 days','+9 hours','+30 minutes')),
  (93, 4, datetime('now','localtime','start of day','-9 days','+6 hours','+29 minutes'), 'https://example.org/jejak/P4/5025251093', 89, NULL, 12, datetime('now','localtime','start of day','-9 days','+18 hours','+29 minutes')),
  (93, 5, datetime('now','localtime','start of day','-7 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P5/5025251093', 90, NULL, 12, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (93, 6, datetime('now','localtime','start of day','-2 days','+2 hours','+52 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (94, 1, datetime('now','localtime','start of day','-20 days','+21 hours','+42 minutes'), 'https://example.org/jejak/P1/5025251094', 85, NULL, 12, datetime('now','localtime','start of day','-19 days','+18 hours','+42 minutes')),
  (94, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+40 minutes'), 'https://example.org/jejak/P2/kelompok-10', 91, NULL, 12, datetime('now','localtime','start of day','-15 days','+12 hours','+40 minutes')),
  (94, 3, datetime('now','localtime','start of day','-15 days','+17 hours','+16 minutes'), 'https://example.org/jejak/P3/kelompok-10', 82, NULL, 12, datetime('now','localtime','start of day','-13 days','+6 hours','+16 minutes')),
  (94, 4, datetime('now','localtime','start of day','-7 days','+20 hours','+21 minutes'), 'https://example.org/jejak/P4/5025251094', 71, NULL, 12, datetime('now','localtime','start of day','-6 days','+10 hours','+21 minutes')),
  (94, 5, datetime('now','localtime','start of day','-7 days','+23 hours','+15 minutes'), 'https://example.org/jejak/P5/5025251094', NULL, NULL, NULL, NULL),
  (94, 6, datetime('now','localtime','start of day','+0 days','+6 hours','+42 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (95, 1, datetime('now','localtime','start of day','-26 days','+15 hours','+53 minutes'), 'https://example.org/jejak/P1/5025251095', 85, NULL, 12, datetime('now','localtime','start of day','-25 days','+20 hours','+53 minutes')),
  (95, 2, datetime('now','localtime','start of day','-16 days','+16 hours','+9 minutes'), 'https://example.org/jejak/P2/kelompok-10', 76, NULL, 12, datetime('now','localtime','start of day','-15 days','+0 hours','+9 minutes')),
  (95, 3, datetime('now','localtime','start of day','-10 days','+10 hours','+53 minutes'), 'https://example.org/jejak/P3/kelompok-10', 87, NULL, 12, datetime('now','localtime','start of day','-10 days','+19 hours','+53 minutes')),
  (95, 4, datetime('now','localtime','start of day','-10 days','+22 hours','+13 minutes'), 'https://example.org/jejak/P4/5025251095', 80, NULL, 12, datetime('now','localtime','start of day','-9 days','+19 hours','+13 minutes')),
  (95, 5, datetime('now','localtime','start of day','-2 days','+20 hours','+55 minutes'), 'https://example.org/jejak/P5/5025251095', NULL, NULL, NULL, NULL),
  (96, 1, datetime('now','localtime','start of day','-22 days','+1 hours','+16 minutes'), 'https://example.org/jejak/P1/5025251096', 94, NULL, 12, datetime('now','localtime','start of day','-21 days','+1 hours','+16 minutes')),
  (96, 2, datetime('now','localtime','start of day','-19 days','+4 hours','+10 minutes'), 'https://example.org/jejak/P2/kelompok-10', 97, NULL, 12, datetime('now','localtime','start of day','-18 days','+3 hours','+10 minutes')),
  (96, 3, datetime('now','localtime','start of day','-14 days','+11 hours','+25 minutes'), 'https://example.org/jejak/P3/kelompok-10', 82, NULL, 12, datetime('now','localtime','start of day','-13 days','+7 hours','+25 minutes')),
  (96, 4, datetime('now','localtime','start of day','-13 days','+1 hours','+36 minutes'), 'https://example.org/jejak/P4/5025251096', 90, NULL, 12, datetime('now','localtime','start of day','-13 days','+16 hours','+36 minutes')),
  (96, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+3 minutes'), 'https://example.org/jejak/P5/5025251096', 84, NULL, 12, datetime('now','localtime','start of day','+0 days','+9 hours','+3 minutes')),
  (96, 6, datetime('now','localtime','start of day','-2 days','+16 hours','+56 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (97, 1, datetime('now','localtime','start of day','-19 days','+13 hours','+56 minutes'), 'https://example.org/jejak/P1/5025251097', 76, NULL, 12, datetime('now','localtime','start of day','-17 days','+2 hours','+56 minutes')),
  (97, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+49 minutes'), 'https://example.org/jejak/P2/kelompok-10', 86, NULL, 12, datetime('now','localtime','start of day','-14 days','+9 hours','+49 minutes')),
  (97, 4, datetime('now','localtime','start of day','-10 days','+20 hours','+18 minutes'), 'https://example.org/jejak/P4/5025251097', 82, NULL, 12, datetime('now','localtime','start of day','-9 days','+19 hours','+18 minutes')),
  (97, 5, datetime('now','localtime','start of day','-7 days','+10 hours','+49 minutes'), 'https://example.org/jejak/P5/5025251097', 88, NULL, 12, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (97, 6, datetime('now','localtime','start of day','-2 days','+12 hours','+56 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (98, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P1/5025251098', 76, NULL, 12, datetime('now','localtime','start of day','-18 days','+0 hours','+58 minutes')),
  (98, 2, datetime('now','localtime','start of day','-17 days','+8 hours','+56 minutes'), 'https://example.org/jejak/P2/kelompok-10', 79, NULL, 12, datetime('now','localtime','start of day','-16 days','+22 hours','+56 minutes')),
  (98, 3, datetime('now','localtime','start of day','-11 days','+11 hours','+38 minutes'), 'https://example.org/jejak/P3/kelompok-10', 85, NULL, 12, datetime('now','localtime','start of day','-10 days','+14 hours','+38 minutes')),
  (98, 4, datetime('now','localtime','start of day','-12 days','+17 hours','+56 minutes'), 'https://example.org/jejak/P4/5025251098', 79, NULL, 12, datetime('now','localtime','start of day','-10 days','+0 hours','+56 minutes')),
  (98, 5, datetime('now','localtime','start of day','-2 days','+15 hours','+21 minutes'), 'https://example.org/jejak/P5/5025251098', 80, NULL, 12, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (98, 6, datetime('now','localtime','start of day','-3 days','+12 hours','+26 minutes'), 'https://example.org/jejak/P6/kelompok-10', NULL, NULL, NULL, NULL),
  (99, 1, datetime('now','localtime','start of day','-26 days','+8 hours','+52 minutes'), 'https://example.org/jejak/P1/5025251099', 83, NULL, 12, datetime('now','localtime','start of day','-25 days','+7 hours','+52 minutes')),
  (99, 2, datetime('now','localtime','start of day','-16 days','+8 hours','+58 minutes'), 'https://example.org/jejak/P2/kelompok-10', 81, NULL, 12, datetime('now','localtime','start of day','-15 days','+20 hours','+58 minutes')),
  (99, 3, datetime('now','localtime','start of day','-14 days','+12 hours','+45 minutes'), 'https://example.org/jejak/P3/kelompok-10', 82, NULL, 12, datetime('now','localtime','start of day','-13 days','+0 hours','+45 minutes')),
  (99, 4, datetime('now','localtime','start of day','-8 days','+11 hours','+29 minutes'), 'https://example.org/jejak/P4/5025251099', 84, NULL, 12, datetime('now','localtime','start of day','-7 days','+2 hours','+29 minutes')),
  (99, 5, datetime('now','localtime','start of day','-2 days','+14 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251099', NULL, NULL, NULL, NULL),
  (100, 1, datetime('now','localtime','start of day','-21 days','+21 hours','+0 minutes'), 'https://example.org/jejak/P1/5025251100', 81, NULL, 12, datetime('now','localtime','start of day','-20 days','+4 hours','+0 minutes')),
  (100, 2, datetime('now','localtime','start of day','-18 days','+12 hours','+39 minutes'), 'https://example.org/jejak/P2/kelompok-10', 85, NULL, 12, datetime('now','localtime','start of day','-17 days','+15 hours','+39 minutes')),
  (100, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P3/kelompok-10', 77, NULL, 12, datetime('now','localtime','start of day','-10 days','+8 hours','+58 minutes')),
  (100, 5, datetime('now','localtime','start of day','-3 days','+2 hours','+53 minutes'), 'https://example.org/jejak/P5/5025251100', 78, NULL, 12, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (101, 1, datetime('now','localtime','start of day','-20 days','+21 hours','+54 minutes'), 'https://example.org/jejak/P1/5025251101', 80, NULL, 13, datetime('now','localtime','start of day','-19 days','+21 hours','+54 minutes')),
  (101, 3, datetime('now','localtime','start of day','-10 days','+3 hours','+28 minutes'), 'https://example.org/jejak/P3/kelompok-11', 64, NULL, 13, datetime('now','localtime','start of day','-9 days','+3 hours','+28 minutes')),
  (101, 4, datetime('now','localtime','start of day','-11 days','+16 hours','+13 minutes'), 'https://example.org/jejak/P4/5025251101', 76, NULL, 13, datetime('now','localtime','start of day','-10 days','+7 hours','+13 minutes')),
  (101, 5, datetime('now','localtime','start of day','-2 days','+13 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251101', 72, NULL, 13, datetime('now','localtime','start of day','-1 days','+21 hours','+58 minutes')),
  (102, 1, datetime('now','localtime','start of day','-25 days','+22 hours','+52 minutes'), 'https://example.org/jejak/P1/5025251102', 90, NULL, 13, datetime('now','localtime','start of day','-23 days','+1 hours','+52 minutes')),
  (102, 2, datetime('now','localtime','start of day','-16 days','+17 hours','+26 minutes'), 'https://example.org/jejak/P2/kelompok-11', 90, NULL, 13, datetime('now','localtime','start of day','-14 days','+4 hours','+26 minutes')),
  (102, 3, datetime('now','localtime','start of day','-12 days','+22 hours','+24 minutes'), 'https://example.org/jejak/P3/kelompok-11', 86, NULL, 13, datetime('now','localtime','start of day','-11 days','+11 hours','+24 minutes')),
  (102, 4, datetime('now','localtime','start of day','-7 days','+14 hours','+27 minutes'), 'https://example.org/jejak/P4/5025251102', 85, NULL, 13, datetime('now','localtime','start of day','-7 days','+23 hours','+27 minutes')),
  (102, 6, datetime('now','localtime','start of day','-3 days','+23 hours','+42 minutes'), 'https://example.org/jejak/P6/kelompok-11', NULL, NULL, NULL, NULL),
  (103, 1, datetime('now','localtime','start of day','-21 days','+16 hours','+31 minutes'), 'https://example.org/jejak/P1/5025251103', 78, NULL, 13, datetime('now','localtime','start of day','-19 days','+1 hours','+31 minutes')),
  (103, 2, datetime('now','localtime','start of day','-16 days','+10 hours','+19 minutes'), 'https://example.org/jejak/P2/kelompok-11', 79, NULL, 13, datetime('now','localtime','start of day','-16 days','+23 hours','+19 minutes')),
  (103, 3, datetime('now','localtime','start of day','-16 days','+23 hours','+47 minutes'), 'https://example.org/jejak/P3/kelompok-11', 80, NULL, 13, datetime('now','localtime','start of day','-14 days','+10 hours','+47 minutes')),
  (103, 4, datetime('now','localtime','start of day','-11 days','+19 hours','+59 minutes'), 'https://example.org/jejak/P4/5025251103', 75, NULL, 13, datetime('now','localtime','start of day','-10 days','+22 hours','+59 minutes')),
  (103, 5, datetime('now','localtime','start of day','-3 days','+23 hours','+13 minutes'), 'https://example.org/jejak/P5/5025251103', 84, NULL, 13, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (104, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+51 minutes'), 'https://example.org/jejak/P1/5025251104', 86, NULL, 13, datetime('now','localtime','start of day','-18 days','+7 hours','+51 minutes')),
  (104, 2, datetime('now','localtime','start of day','-16 days','+5 hours','+2 minutes'), 'https://example.org/jejak/P2/kelompok-11', 89, NULL, 13, datetime('now','localtime','start of day','-16 days','+16 hours','+2 minutes')),
  (104, 3, datetime('now','localtime','start of day','-12 days','+23 hours','+29 minutes'), 'https://example.org/jejak/P3/kelompok-11', 72, NULL, 13, datetime('now','localtime','start of day','-11 days','+8 hours','+29 minutes')),
  (104, 5, datetime('now','localtime','start of day','-7 days','+14 hours','+46 minutes'), 'https://example.org/jejak/P5/5025251104', 77, NULL, 13, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (105, 1, datetime('now','localtime','start of day','-20 days','+17 hours','+7 minutes'), 'https://example.org/jejak/P1/5025251105', 80, NULL, 13, datetime('now','localtime','start of day','-18 days','+2 hours','+7 minutes')),
  (105, 2, datetime('now','localtime','start of day','-16 days','+20 hours','+55 minutes'), 'https://example.org/jejak/P2/kelompok-11', 82, NULL, 13, datetime('now','localtime','start of day','-14 days','+3 hours','+55 minutes')),
  (105, 3, datetime('now','localtime','start of day','-14 days','+1 hours','+32 minutes'), 'https://example.org/jejak/P3/kelompok-11', 76, NULL, 13, datetime('now','localtime','start of day','-14 days','+19 hours','+32 minutes')),
  (106, 1, datetime('now','localtime','start of day','-20 days','+2 hours','+15 minutes'), 'https://example.org/jejak/P1/5025251106', 87, NULL, 13, datetime('now','localtime','start of day','-19 days','+9 hours','+15 minutes')),
  (106, 2, datetime('now','localtime','start of day','-15 days','+4 hours','+49 minutes'), 'https://example.org/jejak/P2/kelompok-11', 73, NULL, 13, datetime('now','localtime','start of day','-15 days','+18 hours','+49 minutes')),
  (106, 3, datetime('now','localtime','start of day','-13 days','+14 hours','+10 minutes'), 'https://example.org/jejak/P3/kelompok-11', 72, NULL, 13, datetime('now','localtime','start of day','-11 days','+2 hours','+10 minutes')),
  (106, 4, datetime('now','localtime','start of day','-6 days','+10 hours','+31 minutes'), 'https://example.org/jejak/P4/5025251106', 83, NULL, 13, datetime('now','localtime','start of day','-4 days','+1 hours','+31 minutes')),
  (106, 5, datetime('now','localtime','start of day','-4 days','+16 hours','+26 minutes'), 'https://example.org/jejak/P5/5025251106', 85, NULL, 13, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (106, 6, datetime('now','localtime','start of day','-3 days','+5 hours','+24 minutes'), 'https://example.org/jejak/P6/kelompok-11', NULL, NULL, NULL, NULL),
  (107, 1, datetime('now','localtime','start of day','-20 days','+7 hours','+38 minutes'), 'https://example.org/jejak/P1/5025251107', 80, NULL, 13, datetime('now','localtime','start of day','-20 days','+21 hours','+38 minutes')),
  (107, 2, datetime('now','localtime','start of day','-19 days','+23 hours','+11 minutes'), 'https://example.org/jejak/P2/kelompok-11', 75, NULL, 13, datetime('now','localtime','start of day','-17 days','+2 hours','+11 minutes')),
  (107, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+58 minutes'), 'https://example.org/jejak/P3/kelompok-11', 66, NULL, 13, datetime('now','localtime','start of day','-9 days','+6 hours','+58 minutes')),
  (107, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+22 minutes'), 'https://example.org/jejak/P4/5025251107', 67, NULL, 13, datetime('now','localtime','start of day','-6 days','+18 hours','+22 minutes')),
  (107, 5, datetime('now','localtime','start of day','-2 days','+23 hours','+52 minutes'), 'https://example.org/jejak/P5/5025251107', 88, NULL, 13, datetime('now','localtime','start of day','+0 days','+7 hours','+52 minutes')),
  (108, 2, datetime('now','localtime','start of day','-20 days','+23 hours','+49 minutes'), 'https://example.org/jejak/P2/kelompok-11', 85, NULL, 13, datetime('now','localtime','start of day','-19 days','+15 hours','+49 minutes')),
  (108, 3, datetime('now','localtime','start of day','-12 days','+21 hours','+43 minutes'), 'https://example.org/jejak/P3/kelompok-11', 80, NULL, 13, datetime('now','localtime','start of day','-10 days','+10 hours','+43 minutes')),
  (108, 5, datetime('now','localtime','start of day','-2 days','+3 hours','+35 minutes'), 'https://example.org/jejak/P5/5025251108', 76, NULL, 13, datetime('now','localtime','start of day','-1 days','+9 hours','+35 minutes')),
  (109, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+54 minutes'), 'https://example.org/jejak/P1/5025251109', 71, NULL, 13, datetime('now','localtime','start of day','-19 days','+9 hours','+54 minutes')),
  (109, 2, datetime('now','localtime','start of day','-18 days','+15 hours','+6 minutes'), 'https://example.org/jejak/P2/kelompok-11', 68, NULL, 13, datetime('now','localtime','start of day','-17 days','+3 hours','+6 minutes')),
  (109, 3, datetime('now','localtime','start of day','-17 days','+10 hours','+12 minutes'), 'https://example.org/jejak/P3/kelompok-11', 86, NULL, 13, datetime('now','localtime','start of day','-15 days','+2 hours','+12 minutes')),
  (109, 4, datetime('now','localtime','start of day','-11 days','+16 hours','+30 minutes'), 'https://example.org/jejak/P4/5025251109', 84, NULL, 13, datetime('now','localtime','start of day','-9 days','+5 hours','+30 minutes')),
  (109, 5, datetime('now','localtime','start of day','-2 days','+14 hours','+15 minutes'), 'https://example.org/jejak/P5/5025251109', 78, NULL, 13, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (110, 1, datetime('now','localtime','start of day','-21 days','+4 hours','+3 minutes'), 'https://example.org/jejak/P1/5025251110', 67, NULL, 13, datetime('now','localtime','start of day','-21 days','+20 hours','+3 minutes')),
  (110, 2, datetime('now','localtime','start of day','-17 days','+7 hours','+40 minutes'), 'https://example.org/jejak/P2/kelompok-11', 79, NULL, 13, datetime('now','localtime','start of day','-17 days','+20 hours','+40 minutes')),
  (110, 3, datetime('now','localtime','start of day','-12 days','+22 hours','+56 minutes'), 'https://example.org/jejak/P3/kelompok-11', 65, NULL, 13, datetime('now','localtime','start of day','-10 days','+12 hours','+56 minutes')),
  (110, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+57 minutes'), 'https://example.org/jejak/P4/5025251110', 82, NULL, 13, datetime('now','localtime','start of day','-5 days','+12 hours','+57 minutes')),
  (110, 5, datetime('now','localtime','start of day','-7 days','+15 hours','+58 minutes'), 'https://example.org/jejak/P5/5025251110', 77, NULL, 13, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (111, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+31 minutes'), 'https://example.org/jejak/P1/5025251111', 66, NULL, 14, datetime('now','localtime','start of day','-19 days','+15 hours','+31 minutes')),
  (111, 2, datetime('now','localtime','start of day','-16 days','+11 hours','+42 minutes'), 'https://example.org/jejak/P2/kelompok-12', 70, NULL, 14, datetime('now','localtime','start of day','-15 days','+1 hours','+42 minutes')),
  (111, 3, datetime('now','localtime','start of day','-13 days','+10 hours','+44 minutes'), 'https://example.org/jejak/P3/kelompok-12', 81, NULL, 14, datetime('now','localtime','start of day','-12 days','+13 hours','+44 minutes')),
  (111, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+30 minutes'), 'https://example.org/jejak/P4/5025251111', 77, NULL, 14, datetime('now','localtime','start of day','-5 days','+2 hours','+30 minutes')),
  (111, 5, datetime('now','localtime','start of day','-6 days','+19 hours','+14 minutes'), 'https://example.org/jejak/P5/5025251111', 75, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (111, 6, datetime('now','localtime','start of day','-2 days','+6 hours','+25 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (112, 1, datetime('now','localtime','start of day','-25 days','+12 hours','+32 minutes'), 'https://example.org/jejak/P1/5025251112', 81, NULL, 14, datetime('now','localtime','start of day','-24 days','+5 hours','+32 minutes')),
  (112, 2, datetime('now','localtime','start of day','-17 days','+21 hours','+24 minutes'), 'https://example.org/jejak/P2/kelompok-12', 92, NULL, 14, datetime('now','localtime','start of day','-15 days','+3 hours','+24 minutes')),
  (112, 3, datetime('now','localtime','start of day','-13 days','+16 hours','+37 minutes'), 'https://example.org/jejak/P3/kelompok-12', 78, NULL, 14, datetime('now','localtime','start of day','-12 days','+11 hours','+37 minutes')),
  (112, 4, datetime('now','localtime','start of day','-7 days','+23 hours','+24 minutes'), 'https://example.org/jejak/P4/5025251112', 86, NULL, 14, datetime('now','localtime','start of day','-6 days','+8 hours','+24 minutes')),
  (112, 5, datetime('now','localtime','start of day','-2 days','+17 hours','+25 minutes'), 'https://example.org/jejak/P5/5025251112', 91, NULL, 14, datetime('now','localtime','start of day','-1 days','+20 hours','+25 minutes')),
  (113, 1, datetime('now','localtime','start of day','-20 days','+8 hours','+27 minutes'), 'https://example.org/jejak/P1/5025251113', 83, NULL, 14, datetime('now','localtime','start of day','-20 days','+18 hours','+27 minutes')),
  (113, 2, datetime('now','localtime','start of day','-16 days','+6 hours','+24 minutes'), 'https://example.org/jejak/P2/kelompok-12', 72, NULL, 14, datetime('now','localtime','start of day','-16 days','+12 hours','+24 minutes')),
  (114, 1, datetime('now','localtime','start of day','-23 days','+3 hours','+7 minutes'), 'https://example.org/jejak/P1/5025251114', 98, NULL, 14, datetime('now','localtime','start of day','-22 days','+14 hours','+7 minutes')),
  (114, 2, datetime('now','localtime','start of day','-17 days','+21 hours','+42 minutes'), 'https://example.org/jejak/P2/kelompok-12', 94, NULL, 14, datetime('now','localtime','start of day','-15 days','+1 hours','+42 minutes')),
  (114, 3, datetime('now','localtime','start of day','-11 days','+5 hours','+12 minutes'), 'https://example.org/jejak/P3/kelompok-12', 88, NULL, 14, datetime('now','localtime','start of day','-11 days','+16 hours','+12 minutes')),
  (114, 5, datetime('now','localtime','start of day','-7 days','+23 hours','+56 minutes'), 'https://example.org/jejak/P5/5025251114', 93, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (114, 6, datetime('now','localtime','start of day','-2 days','+16 hours','+22 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (115, 1, datetime('now','localtime','start of day','-24 days','+11 hours','+10 minutes'), 'https://example.org/jejak/P1/5025251115', 90, NULL, 14, datetime('now','localtime','start of day','-23 days','+9 hours','+10 minutes')),
  (115, 2, datetime('now','localtime','start of day','-17 days','+19 hours','+25 minutes'), 'https://example.org/jejak/P2/kelompok-12', 95, NULL, 14, datetime('now','localtime','start of day','-16 days','+7 hours','+25 minutes')),
  (115, 3, datetime('now','localtime','start of day','-13 days','+7 hours','+58 minutes'), 'https://example.org/jejak/P3/kelompok-12', 84, NULL, 14, datetime('now','localtime','start of day','-12 days','+6 hours','+58 minutes')),
  (115, 4, datetime('now','localtime','start of day','-7 days','+18 hours','+11 minutes'), 'https://example.org/jejak/P4/5025251115', 80, NULL, 14, datetime('now','localtime','start of day','-6 days','+20 hours','+11 minutes')),
  (115, 5, datetime('now','localtime','start of day','-5 days','+8 hours','+10 minutes'), 'https://example.org/jejak/P5/5025251115', 86, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (115, 6, datetime('now','localtime','start of day','-2 days','+10 hours','+23 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (116, 1, datetime('now','localtime','start of day','-20 days','+20 hours','+41 minutes'), 'https://example.org/jejak/P1/5025251116', 78, NULL, 14, datetime('now','localtime','start of day','-18 days','+4 hours','+41 minutes')),
  (116, 2, datetime('now','localtime','start of day','-17 days','+1 hours','+42 minutes'), 'https://example.org/jejak/P2/kelompok-12', 80, NULL, 14, datetime('now','localtime','start of day','-17 days','+9 hours','+42 minutes')),
  (116, 3, datetime('now','localtime','start of day','-13 days','+7 hours','+55 minutes'), 'https://example.org/jejak/P3/kelompok-12', 74, NULL, 14, datetime('now','localtime','start of day','-12 days','+9 hours','+55 minutes')),
  (116, 4, datetime('now','localtime','start of day','-8 days','+4 hours','+32 minutes'), 'https://example.org/jejak/P4/5025251116', 76, NULL, 14, datetime('now','localtime','start of day','-7 days','+6 hours','+32 minutes')),
  (116, 6, datetime('now','localtime','start of day','-2 days','+4 hours','+54 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (117, 1, datetime('now','localtime','start of day','-22 days','+6 hours','+47 minutes'), 'https://example.org/jejak/P1/5025251117', 90, NULL, 14, datetime('now','localtime','start of day','-21 days','+5 hours','+47 minutes')),
  (117, 2, datetime('now','localtime','start of day','-20 days','+19 hours','+26 minutes'), 'https://example.org/jejak/P2/kelompok-12', 86, NULL, 14, datetime('now','localtime','start of day','-19 days','+10 hours','+26 minutes')),
  (117, 3, datetime('now','localtime','start of day','-12 days','+18 hours','+49 minutes'), 'https://example.org/jejak/P3/kelompok-12', 88, NULL, 14, datetime('now','localtime','start of day','-11 days','+14 hours','+49 minutes')),
  (117, 4, datetime('now','localtime','start of day','-9 days','+1 hours','+33 minutes'), 'https://example.org/jejak/P4/5025251117', 88, NULL, 14, datetime('now','localtime','start of day','-8 days','+7 hours','+33 minutes')),
  (117, 5, datetime('now','localtime','start of day','-4 days','+16 hours','+52 minutes'), 'https://example.org/jejak/P5/5025251117', 79, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (118, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+20 minutes'), 'https://example.org/jejak/P1/5025251118', 78, NULL, 14, datetime('now','localtime','start of day','-19 days','+16 hours','+20 minutes')),
  (118, 2, datetime('now','localtime','start of day','-16 days','+23 hours','+39 minutes'), 'https://example.org/jejak/P2/kelompok-12', 83, NULL, 14, datetime('now','localtime','start of day','-14 days','+14 hours','+39 minutes')),
  (118, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+47 minutes'), 'https://example.org/jejak/P3/kelompok-12', 71, NULL, 14, datetime('now','localtime','start of day','-9 days','+5 hours','+47 minutes')),
  (118, 4, datetime('now','localtime','start of day','-8 days','+22 hours','+16 minutes'), 'https://example.org/jejak/P4/5025251118', 75, NULL, 14, datetime('now','localtime','start of day','-7 days','+23 hours','+16 minutes')),
  (118, 5, datetime('now','localtime','start of day','-9 days','+10 hours','+23 minutes'), 'https://example.org/jejak/P5/5025251118', 74, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (118, 6, datetime('now','localtime','start of day','-2 days','+18 hours','+12 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (119, 1, datetime('now','localtime','start of day','-20 days','+12 hours','+41 minutes'), 'https://example.org/jejak/P1/5025251119', 93, NULL, 14, datetime('now','localtime','start of day','-20 days','+22 hours','+41 minutes')),
  (119, 2, datetime('now','localtime','start of day','-18 days','+6 hours','+25 minutes'), 'https://example.org/jejak/P2/kelompok-12', 76, NULL, 14, datetime('now','localtime','start of day','-17 days','+11 hours','+25 minutes')),
  (119, 3, datetime('now','localtime','start of day','-11 days','+7 hours','+18 minutes'), 'https://example.org/jejak/P3/kelompok-12', 85, NULL, 14, datetime('now','localtime','start of day','-10 days','+8 hours','+18 minutes')),
  (119, 4, datetime('now','localtime','start of day','-7 days','+22 hours','+35 minutes'), 'https://example.org/jejak/P4/5025251119', 70, NULL, 14, datetime('now','localtime','start of day','-6 days','+19 hours','+35 minutes')),
  (119, 5, datetime('now','localtime','start of day','-2 days','+0 hours','+5 minutes'), 'https://example.org/jejak/P5/5025251119', 86, NULL, 14, datetime('now','localtime','start of day','-1 days','+16 hours','+5 minutes')),
  (119, 6, datetime('now','localtime','start of day','-3 days','+13 hours','+49 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (120, 1, datetime('now','localtime','start of day','-20 days','+19 hours','+16 minutes'), 'https://example.org/jejak/P1/5025251120', 86, NULL, 14, datetime('now','localtime','start of day','-18 days','+2 hours','+16 minutes')),
  (120, 2, datetime('now','localtime','start of day','-17 days','+16 hours','+2 minutes'), 'https://example.org/jejak/P2/kelompok-12', 94, NULL, 14, datetime('now','localtime','start of day','-15 days','+8 hours','+2 minutes')),
  (120, 3, datetime('now','localtime','start of day','-11 days','+23 hours','+56 minutes'), 'https://example.org/jejak/P3/kelompok-12', 95, NULL, 14, datetime('now','localtime','start of day','-10 days','+8 hours','+56 minutes')),
  (120, 4, datetime('now','localtime','start of day','-7 days','+5 hours','+26 minutes'), 'https://example.org/jejak/P4/5025251120', 75, NULL, 14, datetime('now','localtime','start of day','-6 days','+6 hours','+26 minutes')),
  (120, 6, datetime('now','localtime','start of day','-2 days','+4 hours','+2 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL),
  (121, 1, datetime('now','localtime','start of day','-20 days','+23 hours','+32 minutes'), 'https://example.org/jejak/P1/5025251121', 69, NULL, 4, datetime('now','localtime','start of day','-19 days','+20 hours','+32 minutes')),
  (121, 2, datetime('now','localtime','start of day','-16 days','+18 hours','+6 minutes'), 'https://example.org/jejak/P2/kelompok-02', 77, NULL, 4, datetime('now','localtime','start of day','-15 days','+22 hours','+6 minutes')),
  (121, 3, datetime('now','localtime','start of day','-17 days','+13 hours','+43 minutes'), 'https://example.org/jejak/P3/kelompok-02', 76, NULL, 4, datetime('now','localtime','start of day','-16 days','+22 hours','+43 minutes')),
  (121, 4, datetime('now','localtime','start of day','-8 days','+4 hours','+30 minutes'), 'https://example.org/jejak/P4/5025251121', 73, NULL, 4, datetime('now','localtime','start of day','-7 days','+18 hours','+30 minutes')),
  (121, 5, datetime('now','localtime','start of day','-2 days','+5 hours','+30 minutes'), 'https://example.org/jejak/P5/5025251121', 70, NULL, 4, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (122, 1, datetime('now','localtime','start of day','-20 days','+11 hours','+13 minutes'), 'https://example.org/jejak/P1/5025251122', 72, NULL, 8, datetime('now','localtime','start of day','-18 days','+2 hours','+13 minutes')),
  (122, 2, datetime('now','localtime','start of day','-18 days','+23 hours','+9 minutes'), 'https://example.org/jejak/P2/kelompok-06', 83, NULL, 8, datetime('now','localtime','start of day','-16 days','+13 hours','+9 minutes')),
  (122, 3, datetime('now','localtime','start of day','-14 days','+18 hours','+9 minutes'), 'https://example.org/jejak/P3/kelompok-06', 83, NULL, 8, datetime('now','localtime','start of day','-13 days','+5 hours','+9 minutes')),
  (122, 5, datetime('now','localtime','start of day','-1 days','+19 hours','+7 minutes'), 'https://example.org/jejak/P5/5025251122', 79, NULL, 8, datetime('now','localtime','start of day','+0 days','+2 hours','+7 minutes')),
  (123, 1, datetime('now','localtime','start of day','-20 days','+19 hours','+10 minutes'), 'https://example.org/jejak/P1/5025251123', 72, NULL, 11, datetime('now','localtime','start of day','-19 days','+18 hours','+10 minutes')),
  (123, 2, datetime('now','localtime','start of day','-19 days','+5 hours','+35 minutes'), 'https://example.org/jejak/P2/kelompok-09', 72, NULL, 11, datetime('now','localtime','start of day','-18 days','+17 hours','+35 minutes')),
  (123, 3, datetime('now','localtime','start of day','-16 days','+11 hours','+23 minutes'), 'https://example.org/jejak/P3/kelompok-09', 81, NULL, 11, datetime('now','localtime','start of day','-16 days','+23 hours','+23 minutes')),
  (123, 4, datetime('now','localtime','start of day','-9 days','+10 hours','+28 minutes'), 'https://example.org/jejak/P4/5025251123', 83, NULL, 11, datetime('now','localtime','start of day','-9 days','+16 hours','+28 minutes')),
  (123, 5, datetime('now','localtime','start of day','-1 days','+19 hours','+49 minutes'), 'https://example.org/jejak/P5/5025251123', 80, NULL, 11, datetime('now','localtime','start of day','+0 days','+9 hours','+58 minutes')),
  (123, 6, datetime('now','localtime','start of day','-3 days','+22 hours','+34 minutes'), 'https://example.org/jejak/P6/kelompok-09', NULL, NULL, NULL, NULL),
  (124, 2, datetime('now','localtime','start of day','-16 days','+2 hours','+37 minutes'), 'https://example.org/jejak/P2/kelompok-12', 88, NULL, 14, datetime('now','localtime','start of day','-16 days','+9 hours','+37 minutes')),
  (124, 3, datetime('now','localtime','start of day','-10 days','+18 hours','+36 minutes'), 'https://example.org/jejak/P3/kelompok-12', 96, NULL, 14, datetime('now','localtime','start of day','-8 days','+5 hours','+36 minutes')),
  (124, 4, datetime('now','localtime','start of day','-13 days','+8 hours','+29 minutes'), 'https://example.org/jejak/P4/5025251124', 89, NULL, 14, datetime('now','localtime','start of day','-13 days','+21 hours','+29 minutes')),
  (124, 5, datetime('now','localtime','start of day','-5 days','+10 hours','+3 minutes'), 'https://example.org/jejak/P5/5025251124', 81, NULL, 14, datetime('now','localtime','start of day','-1 days','+8 hours','+0 minutes')),
  (124, 6, datetime('now','localtime','start of day','-3 days','+1 hours','+51 minutes'), 'https://example.org/jejak/P6/kelompok-12', NULL, NULL, NULL, NULL);

-- Kader nonaktif (mengundurkan diri) tetap disimpan agar riwayat penilaiannya utuh (BR-06).
UPDATE kader SET aktif = 0, diubah_pada = datetime('now','localtime','start of day','-1 days','+19 hours','+30 minutes') WHERE nrp IN ('5025251015', '5025251052', '5025251089', '5025251113');

INSERT INTO pengaturan (kunci, nilai, keterangan, diubah_pada) VALUES
  ('risiko_r1_min_belum', '2', 'R1: jumlah penugasan jatuh tempo belum dikumpulkan (>=)', datetime('now','localtime','start of day','-1 days','+13 hours','+22 minutes')),
  ('risiko_r2_min_ketuntasan', '60', 'R2: ketuntasan minimum dalam persen (<)', datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  ('risiko_r3_min_nilai', '60', 'R3: nilai akhir minimum (<)', datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  ('risiko_r3_min_dinilai', '2', 'R3 berlaku bila penugasan yang sudah dinilai >= nilai ini', datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes')),
  ('program_nama', 'Kaderisasi 2026', 'Nama program yang tampil di sidebar', datetime('now','localtime','start of day','-34 days','+9 hours','+0 minutes'));

INSERT INTO log_aktivitas (pengguna_id, aksi, entitas, entitas_id, keterangan, waktu) VALUES
  (1, 'Tambah', 'Pengguna', 2, 'Membuat akun admin sinta.dewi', datetime('now','localtime','start of day','-39 days','+8 hours','+0 minutes')),
  (1, 'Tambah', 'Pengguna', 3, 'Membuat akun pemandu alya.rahmadani', datetime('now','localtime','start of day','-37 days','+10 hours','+0 minutes')),
  (1, 'Tambah', 'Pengguna', 4, 'Membuat akun pemandu bima.saputra', datetime('now','localtime','start of day','-37 days','+10 hours','+1 minutes')),
  (1, 'Tambah', 'Pengguna', 5, 'Membuat akun pemandu citra.lestari', datetime('now','localtime','start of day','-37 days','+10 hours','+2 minutes')),
  (1, 'Tambah', 'Pengguna', 6, 'Membuat akun pemandu daffa.prakoso', datetime('now','localtime','start of day','-37 days','+10 hours','+3 minutes')),
  (1, 'Tambah', 'Pengguna', 7, 'Membuat akun pemandu elsa.maharani', datetime('now','localtime','start of day','-37 days','+10 hours','+4 minutes')),
  (1, 'Tambah', 'Pengguna', 8, 'Membuat akun pemandu fikri.hidayat', datetime('now','localtime','start of day','-37 days','+10 hours','+5 minutes')),
  (1, 'Tambah', 'Pengguna', 9, 'Membuat akun pemandu gita.puspita', datetime('now','localtime','start of day','-37 days','+10 hours','+6 minutes')),
  (1, 'Tambah', 'Pengguna', 10, 'Membuat akun pemandu hafiz.ramadhan', datetime('now','localtime','start of day','-37 days','+10 hours','+7 minutes')),
  (1, 'Tambah', 'Pengguna', 11, 'Membuat akun pemandu intan.permata', datetime('now','localtime','start of day','-37 days','+10 hours','+8 minutes')),
  (1, 'Tambah', 'Pengguna', 12, 'Membuat akun pemandu jovan.kurniawan', datetime('now','localtime','start of day','-37 days','+10 hours','+9 minutes')),
  (1, 'Tambah', 'Pengguna', 13, 'Membuat akun pemandu kirana.ayu', datetime('now','localtime','start of day','-37 days','+10 hours','+10 minutes')),
  (1, 'Tambah', 'Kelompok', 1, 'Membuat Kelompok 01 · Arunika', datetime('now','localtime','start of day','-34 days','+9 hours','+5 minutes')),
  (1, 'Tambah', 'Kelompok', 2, 'Membuat Kelompok 02 · Bhaskara', datetime('now','localtime','start of day','-34 days','+9 hours','+6 minutes')),
  (1, 'Tambah', 'Kelompok', 3, 'Membuat Kelompok 03 · Candramawa', datetime('now','localtime','start of day','-34 days','+9 hours','+7 minutes')),
  (1, 'Tambah', 'Kelompok', 4, 'Membuat Kelompok 04 · Dirgantara', datetime('now','localtime','start of day','-34 days','+9 hours','+8 minutes')),
  (1, 'Tambah', 'Kelompok', 5, 'Membuat Kelompok 05 · Elakshi', datetime('now','localtime','start of day','-34 days','+9 hours','+9 minutes')),
  (1, 'Tambah', 'Kelompok', 6, 'Membuat Kelompok 06 · Gemintang', datetime('now','localtime','start of day','-34 days','+9 hours','+10 minutes')),
  (1, 'Tambah', 'Kelompok', 7, 'Membuat Kelompok 07 · Harsa', datetime('now','localtime','start of day','-34 days','+9 hours','+11 minutes')),
  (1, 'Tambah', 'Kelompok', 8, 'Membuat Kelompok 08 · Indurasmi', datetime('now','localtime','start of day','-34 days','+9 hours','+12 minutes')),
  (1, 'Tambah', 'Kelompok', 9, 'Membuat Kelompok 09 · Jumantara', datetime('now','localtime','start of day','-34 days','+9 hours','+13 minutes')),
  (1, 'Tambah', 'Kelompok', 10, 'Membuat Kelompok 10 · Kalandra', datetime('now','localtime','start of day','-34 days','+9 hours','+14 minutes')),
  (1, 'Tambah', 'Kelompok', 11, 'Membuat Kelompok 11 · Lembayung', datetime('now','localtime','start of day','-34 days','+9 hours','+15 minutes')),
  (1, 'Tambah', 'Kelompok', 12, 'Membuat Kelompok 12 · Mahika', datetime('now','localtime','start of day','-34 days','+9 hours','+16 minutes')),
  (1, 'Tambah', 'Penugasan', 1, 'Menambahkan P1 · Esai Refleksi Diri (bobot 10)', datetime('now','localtime','start of day','-31 days','+10 hours','+0 minutes')),
  (1, 'Tambah', 'Penugasan', 2, 'Menambahkan P2 · Profil Angkatan (bobot 10)', datetime('now','localtime','start of day','-31 days','+10 hours','+3 minutes')),
  (1, 'Tambah', 'Penugasan', 3, 'Menambahkan P3 · Video Perkenalan Kelompok (bobot 15)', datetime('now','localtime','start of day','-31 days','+10 hours','+6 minutes')),
  (1, 'Tambah', 'Penugasan', 4, 'Menambahkan P4 · Laporan Kunjungan Lab (bobot 15)', datetime('now','localtime','start of day','-31 days','+10 hours','+9 minutes')),
  (1, 'Tambah', 'Penugasan', 5, 'Menambahkan P5 · Studi Kasus Organisasi (bobot 20)', datetime('now','localtime','start of day','-31 days','+10 hours','+12 minutes')),
  (1, 'Tambah', 'Penugasan', 6, 'Menambahkan P6 · Proyek Sosial Kelompok (bobot 10)', datetime('now','localtime','start of day','-31 days','+10 hours','+15 minutes')),
  (1, 'Tambah', 'Penugasan', 7, 'Menambahkan P7 · Esai Visi Angkatan (bobot 10)', datetime('now','localtime','start of day','-31 days','+10 hours','+18 minutes')),
  (1, 'Tambah', 'Kader', NULL, 'Menambahkan 120 kader angkatan 2025', datetime('now','localtime','start of day','-29 days','+14 hours','+0 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-28 days','+7 hours','+19 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-28 days','+9 hours','+37 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-28 days','+16 hours','+5 minutes')),
  (14, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-27 days','+17 hours','+19 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-26 days','+7 hours','+18 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-26 days','+8 hours','+17 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-26 days','+10 hours','+30 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-25 days','+10 hours','+8 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-24 days','+19 hours','+30 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-24 days','+20 hours','+35 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-23 days','+18 hours','+17 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-23 days','+22 hours','+37 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-22 days','+11 hours','+29 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-22 days','+18 hours','+31 minutes')),
  (10, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-21 days','+15 hours','+57 minutes')),
  (3, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 01', datetime('now','localtime','start of day','-19 days','+9 hours','+59 minutes')),
  (4, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 11 kader Kelompok 02', datetime('now','localtime','start of day','-19 days','+10 hours','+59 minutes')),
  (5, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 03', datetime('now','localtime','start of day','-19 days','+11 hours','+59 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-19 days','+12 hours','+31 minutes')),
  (6, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 7 kader Kelompok 04', datetime('now','localtime','start of day','-19 days','+12 hours','+59 minutes')),
  (1, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 05', datetime('now','localtime','start of day','-19 days','+13 hours','+59 minutes')),
  (8, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 11 kader Kelompok 06', datetime('now','localtime','start of day','-19 days','+14 hours','+59 minutes')),
  (9, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 07', datetime('now','localtime','start of day','-19 days','+15 hours','+59 minutes')),
  (10, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 8 kader Kelompok 08', datetime('now','localtime','start of day','-19 days','+16 hours','+59 minutes')),
  (11, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 11 kader Kelompok 09', datetime('now','localtime','start of day','-19 days','+17 hours','+59 minutes')),
  (12, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 10', datetime('now','localtime','start of day','-19 days','+18 hours','+59 minutes')),
  (13, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 9 kader Kelompok 11', datetime('now','localtime','start of day','-19 days','+19 hours','+59 minutes')),
  (14, 'Nilai', 'Pengumpulan', NULL, 'Menilai P1 untuk 10 kader Kelompok 12', datetime('now','localtime','start of day','-19 days','+20 hours','+59 minutes')),
  (1, 'Tambah', 'Kader', NULL, 'Menambahkan 4 kader pindahan (5025251121–124)', datetime('now','localtime','start of day','-18 days','+10 hours','+0 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-18 days','+19 hours','+16 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-18 days','+22 hours','+2 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-17 days','+9 hours','+31 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-17 days','+9 hours','+58 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-17 days','+11 hours','+14 minutes')),
  (14, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-16 days','+18 hours','+0 minutes')),
  (3, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 01', datetime('now','localtime','start of day','-15 days','+9 hours','+59 minutes')),
  (4, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 11 kader Kelompok 02', datetime('now','localtime','start of day','-15 days','+10 hours','+59 minutes')),
  (5, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 03', datetime('now','localtime','start of day','-15 days','+11 hours','+59 minutes')),
  (6, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 9 kader Kelompok 04', datetime('now','localtime','start of day','-15 days','+12 hours','+59 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-15 days','+13 hours','+1 minutes')),
  (1, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 05', datetime('now','localtime','start of day','-15 days','+13 hours','+59 minutes')),
  (8, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 11 kader Kelompok 06', datetime('now','localtime','start of day','-15 days','+14 hours','+59 minutes')),
  (9, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 07', datetime('now','localtime','start of day','-15 days','+15 hours','+59 minutes')),
  (10, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 08', datetime('now','localtime','start of day','-15 days','+16 hours','+59 minutes')),
  (11, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 11 kader Kelompok 09', datetime('now','localtime','start of day','-15 days','+17 hours','+59 minutes')),
  (12, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 10 kader Kelompok 10', datetime('now','localtime','start of day','-15 days','+18 hours','+59 minutes')),
  (13, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 9 kader Kelompok 11', datetime('now','localtime','start of day','-15 days','+19 hours','+59 minutes')),
  (14, 'Nilai', 'Pengumpulan', NULL, 'Menilai P2 untuk 11 kader Kelompok 12', datetime('now','localtime','start of day','-15 days','+20 hours','+59 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-14 days','+8 hours','+5 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-14 days','+9 hours','+50 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-14 days','+22 hours','+37 minutes')),
  (9, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-14 days','+22 hours','+44 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-13 days','+9 hours','+29 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-13 days','+12 hours','+39 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-13 days','+15 hours','+53 minutes')),
  (9, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-13 days','+17 hours','+51 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-13 days','+22 hours','+39 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-12 days','+11 hours','+47 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-12 days','+12 hours','+12 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-12 days','+14 hours','+6 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-11 days','+10 hours','+44 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-11 days','+15 hours','+13 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-11 days','+16 hours','+17 minutes')),
  (3, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 01', datetime('now','localtime','start of day','-10 days','+9 hours','+59 minutes')),
  (4, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 02', datetime('now','localtime','start of day','-10 days','+10 hours','+59 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-10 days','+11 hours','+8 minutes')),
  (5, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 03', datetime('now','localtime','start of day','-10 days','+11 hours','+59 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-10 days','+12 hours','+32 minutes')),
  (6, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 5 kader Kelompok 04', datetime('now','localtime','start of day','-10 days','+12 hours','+59 minutes')),
  (1, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 9 kader Kelompok 05', datetime('now','localtime','start of day','-10 days','+13 hours','+59 minutes')),
  (8, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 9 kader Kelompok 06', datetime('now','localtime','start of day','-10 days','+14 hours','+59 minutes')),
  (9, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 07', datetime('now','localtime','start of day','-10 days','+15 hours','+59 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-10 days','+16 hours','+37 minutes')),
  (10, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 8 kader Kelompok 08', datetime('now','localtime','start of day','-10 days','+16 hours','+59 minutes')),
  (11, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 09', datetime('now','localtime','start of day','-10 days','+17 hours','+59 minutes')),
  (12, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 8 kader Kelompok 10', datetime('now','localtime','start of day','-10 days','+18 hours','+59 minutes')),
  (13, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 11', datetime('now','localtime','start of day','-10 days','+19 hours','+59 minutes')),
  (14, 'Nilai', 'Pengumpulan', NULL, 'Menilai P3 untuk 10 kader Kelompok 12', datetime('now','localtime','start of day','-10 days','+20 hours','+59 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-9 days','+8 hours','+48 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-9 days','+16 hours','+22 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-9 days','+20 hours','+48 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-8 days','+13 hours','+59 minutes')),
  (14, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-8 days','+21 hours','+27 minutes')),
  (10, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-7 days','+15 hours','+57 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-7 days','+16 hours','+24 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-7 days','+20 hours','+23 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-7 days','+20 hours','+56 minutes')),
  (3, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 10 kader Kelompok 01', datetime('now','localtime','start of day','-6 days','+9 hours','+59 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+10 hours','+16 minutes')),
  (4, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 9 kader Kelompok 02', datetime('now','localtime','start of day','-6 days','+10 hours','+59 minutes')),
  (5, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 10 kader Kelompok 03', datetime('now','localtime','start of day','-6 days','+11 hours','+59 minutes')),
  (6, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 8 kader Kelompok 04', datetime('now','localtime','start of day','-6 days','+12 hours','+59 minutes')),
  (1, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 10 kader Kelompok 05', datetime('now','localtime','start of day','-6 days','+13 hours','+59 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+14 hours','+13 minutes')),
  (8, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 9 kader Kelompok 06', datetime('now','localtime','start of day','-6 days','+14 hours','+59 minutes')),
  (9, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 10 kader Kelompok 07', datetime('now','localtime','start of day','-6 days','+15 hours','+59 minutes')),
  (10, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 9 kader Kelompok 08', datetime('now','localtime','start of day','-6 days','+16 hours','+59 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+17 hours','+3 minutes')),
  (11, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 10 kader Kelompok 09', datetime('now','localtime','start of day','-6 days','+17 hours','+59 minutes')),
  (12, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 9 kader Kelompok 10', datetime('now','localtime','start of day','-6 days','+18 hours','+59 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+19 hours','+48 minutes')),
  (13, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 7 kader Kelompok 11', datetime('now','localtime','start of day','-6 days','+19 hours','+59 minutes')),
  (14, 'Nilai', 'Pengumpulan', NULL, 'Menilai P4 untuk 9 kader Kelompok 12', datetime('now','localtime','start of day','-6 days','+20 hours','+59 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+21 hours','+57 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-6 days','+22 hours','+12 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-5 days','+19 hours','+39 minutes')),
  (9, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-4 days','+7 hours','+0 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-4 days','+10 hours','+13 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-3 days','+14 hours','+9 minutes')),
  (10, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-3 days','+19 hours','+41 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-3 days','+22 hours','+33 minutes')),
  (14, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-2 days','+16 hours','+10 minutes')),
  (7, 'Login Gagal', 'Sesi', NULL, 'Login gagal (password salah)', datetime('now','localtime','start of day','-2 days','+22 hours','+10 minutes')),
  (4, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 9 kader Kelompok 02', datetime('now','localtime','start of day','-1 days','+8 hours','+37 minutes')),
  (5, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 8 kader Kelompok 03', datetime('now','localtime','start of day','-1 days','+9 hours','+14 minutes')),
  (9, 'Ubah', 'Pengumpulan', NULL, 'Mencatat 3 pengumpulan terlambat pada P5', datetime('now','localtime','start of day','-1 days','+9 hours','+30 minutes')),
  (5, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+9 hours','+30 minutes')),
  (6, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 5 kader Kelompok 04', datetime('now','localtime','start of day','-1 days','+9 hours','+51 minutes')),
  (1, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 8 kader Kelompok 05', datetime('now','localtime','start of day','-1 days','+10 hours','+28 minutes')),
  (1, 'Tambah', 'Pengguna', 14, 'Membuat akun pemandu lutfi.hakim', datetime('now','localtime','start of day','-1 days','+10 hours','+40 minutes')),
  (14, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+10 hours','+45 minutes')),
  (8, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 9 kader Kelompok 06', datetime('now','localtime','start of day','-1 days','+11 hours','+5 minutes')),
  (9, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 9 kader Kelompok 07', datetime('now','localtime','start of day','-1 days','+11 hours','+42 minutes')),
  (10, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 7 kader Kelompok 08', datetime('now','localtime','start of day','-1 days','+12 hours','+19 minutes')),
  (11, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 10 kader Kelompok 09', datetime('now','localtime','start of day','-1 days','+12 hours','+56 minutes')),
  (2, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+13 hours','+20 minutes')),
  (2, 'Ubah', 'Pengaturan', NULL, 'Ambang R1 kader berisiko: 3 → 2 penugasan', datetime('now','localtime','start of day','-1 days','+13 hours','+22 minutes')),
  (12, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 7 kader Kelompok 10', datetime('now','localtime','start of day','-1 days','+13 hours','+33 minutes')),
  (13, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 8 kader Kelompok 11', datetime('now','localtime','start of day','-1 days','+14 hours','+10 minutes')),
  (14, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 8 kader Kelompok 12', datetime('now','localtime','start of day','-1 days','+14 hours','+47 minutes')),
  (10, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+16 hours','+0 minutes')),
  (10, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+18 hours','+17 minutes')),
  (1, 'Hapus', 'Kader', 52, 'Menonaktifkan Gilang Mahendra (mengundurkan diri)', datetime('now','localtime','start of day','-1 days','+19 hours','+30 minutes')),
  (11, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+20 hours','+5 minutes')),
  (1, 'Ubah', 'Kader', 77, 'Memindahkan Yoga Pratama: Kelompok 07 → 08', datetime('now','localtime','start of day','-1 days','+20 hours','+14 minutes')),
  (13, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+21 hours','+15 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','-1 days','+21 hours','+59 minutes')),
  (8, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+5 hours','+0 minutes')),
  (12, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+6 hours','+40 minutes')),
  (4, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+7 hours','+2 minutes')),
  (6, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+7 hours','+48 minutes')),
  (9, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+8 hours','+10 minutes')),
  (1, 'Tambah', 'Penugasan', 8, 'Menambahkan P8 · Presentasi Akhir (bobot 10)', datetime('now','localtime','start of day','+0 days','+9 hours','+2 minutes')),
  (6, 'Ubah', 'Pengumpulan', NULL, 'Nilai P4 Rizky Maulana: 64 → 70', datetime('now','localtime','start of day','+0 days','+9 hours','+36 minutes')),
  (3, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+9 hours','+48 minutes')),
  (1, 'Login', 'Sesi', NULL, 'Login berhasil', datetime('now','localtime','start of day','+0 days','+9 hours','+55 minutes')),
  (3, 'Nilai', 'Pengumpulan', NULL, 'Menilai P5 untuk 9 kader Kelompok 01', datetime('now','localtime','start of day','+0 days','+9 hours','+58 minutes'));

COMMIT;

-- Contoh query verifikasi:
--   SELECT * FROM v_dashboard;
--   SELECT * FROM v_kader_berisiko;
--   SELECT * FROM v_rekap_kelompok;
--   SELECT * FROM v_rekap_kader WHERE nrp = '5025251034';