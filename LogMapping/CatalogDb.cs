// CatalogDb.cs — SQLite 기반 드라이브 카탈로그 저장소
// 대용량(수백만 파일) 대응: 파일 트리를 메모리에 올리지 않고 DB에서 지연 조회
//
// 동시성: SqliteConnection(_conn)은 동시 명령에 안전하지 않다. 백그라운드 스캔/검색/내보내기와
// UI 스레드의 조회가 같은 연결을 동시에 쓰면 예외·손상이 날 수 있으므로, _conn을 만지는 모든
// public 메서드를 _gate 락으로 직렬화한다. 긴 스캔(InsertStreaming)은 청크 단위로 커밋하며
// 청크 사이에 락을 놓아, 스캔 중에도 조회가 끼어들 수 있게 해 UI 멈춤을 막는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LogMapping
{
    internal sealed class CatalogDb : IDisposable
    {
        private SqliteConnection _conn = null!;
        private readonly object _gate = new object(); // _conn 접근 직렬화
        private bool _ftsEnabled;   // FTS5 trigram 사용 가능 여부 (미지원 시 LIKE 폴백)
        private bool _dirtySinceBackup; // 마지막 백업 이후 데이터 변경 여부
        public string Path { get; }
        public bool WasRecovered { get; private set; } // 손상으로 DB를 재생성했는지

        public CatalogDb(string dbPath)
        {
            Path = dbPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
            OpenWithRecovery();
            // 성능 PRAGMA + busy_timeout(연결 간 잠금 경합 시 즉시 실패 대신 대기)
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;");
            EnsureSchema();
        }

        // DB를 열고 무결성을 확인. 손상(malformed) 시 손상 파일을 정리하고 새 DB를 생성한다.
        private void OpenWithRecovery()
        {
            _conn = new SqliteConnection($"Data Source={Path}");
            _conn.Open();
            bool ok;
            try
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "PRAGMA quick_check";
                ok = (c.ExecuteScalar() as string) == "ok";
            }
            catch { ok = false; } // malformed 등 → 예외

            if (ok) return;

            // 손상: 연결을 닫고 db/-wal/-shm을 제거한 뒤 새로 생성
            try { _conn.Dispose(); } catch { }
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(Path + ext)) File.Delete(Path + ext); } catch { }
            }
            _conn = new SqliteConnection($"Data Source={Path}");
            _conn.Open();
            WasRecovered = true;
        }

        private void Exec(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private long Scalar(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        // FTS5 trigram 가상 테이블 + 동기화 트리거 구성. 미지원 환경은 _ftsEnabled=false로 폴백.
        private void EnsureFts()
        {
            try
            {
                Exec("CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(name, content='files', content_rowid='id', tokenize='trigram');");
                Exec("CREATE TRIGGER IF NOT EXISTS files_fts_ai AFTER INSERT ON files BEGIN INSERT INTO files_fts(rowid,name) VALUES(new.id,new.name); END;");
                Exec("CREATE TRIGGER IF NOT EXISTS files_fts_ad AFTER DELETE ON files BEGIN INSERT INTO files_fts(files_fts,rowid,name) VALUES('delete',old.id,old.name); END;");
                // 기존 DB(트리거 도입 이전 데이터)가 색인 안 돼 있으면 1회 재구축
                if (Scalar("SELECT count(*) FROM files_fts") == 0 && Scalar("SELECT count(*) FROM files") > 0)
                    Exec("INSERT INTO files_fts(files_fts) VALUES('rebuild');");
                _ftsEnabled = true;
            }
            catch { _ftsEnabled = false; }
        }

        private void EnsureSchema()
        {
            Exec(@"
CREATE TABLE IF NOT EXISTS drives(
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  num           INTEGER,
  name          TEXT,
  cap           INTEGER,
  used          INTEGER,
  color         TEXT,
  note          TEXT,
  tags          TEXT,
  purchase_date TEXT,
  warranty_date TEXT,
  health_status TEXT,
  health_note   TEXT,
  scanned_path  TEXT,
  scanned_at    TEXT,
  last_seen     TEXT
);
CREATE TABLE IF NOT EXISTS files(
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  drive_id    INTEGER NOT NULL,
  name        TEXT NOT NULL,
  is_dir      INTEGER NOT NULL,
  size        INTEGER NOT NULL DEFAULT 0,
  parent_path TEXT NOT NULL,
  full_path   TEXT NOT NULL,
  color       TEXT,
  mtime       TEXT
);
CREATE INDEX IF NOT EXISTS idx_files_drive_parent ON files(drive_id, parent_path);
CREATE INDEX IF NOT EXISTS idx_files_name         ON files(drive_id, name);
CREATE INDEX IF NOT EXISTS idx_files_color        ON files(drive_id, color);
CREATE INDEX IF NOT EXISTS idx_files_full         ON files(drive_id, full_path);
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
");
            // 기존 DB 호환: mtime 컬럼이 없으면 추가
            try { Exec("ALTER TABLE files ADD COLUMN mtime TEXT"); } catch { }
            EnsureFts();
        }

        // ── 드라이브 메타 저장 (있으면 update, 없으면 insert) ──────────────────────
        public long UpsertDrive(JsonElement d)
        {
            long? id = d.TryGetProperty("dbId", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64() : (long?)null;

            string S(string k) => d.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            long N(string k) => d.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            string Tags() {
                if (d.TryGetProperty("tags", out var e) && e.ValueKind == JsonValueKind.Array)
                    return e.GetRawText();
                return "[]";
            }

            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                if (id.HasValue)
                {
                    cmd.CommandText = @"UPDATE drives SET num=$num,name=$name,cap=$cap,used=$used,color=$color,
                        note=$note,tags=$tags,purchase_date=$pd,warranty_date=$wd,health_status=$hs,health_note=$hn,
                        scanned_path=$sp,scanned_at=$sa,last_seen=$ls WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", id.Value);
                }
                else
                {
                    cmd.CommandText = @"INSERT INTO drives(num,name,cap,used,color,note,tags,purchase_date,warranty_date,
                        health_status,health_note,scanned_path,scanned_at,last_seen)
                        VALUES($num,$name,$cap,$used,$color,$note,$tags,$pd,$wd,$hs,$hn,$sp,$sa,$ls);
                        SELECT last_insert_rowid();";
                }
                cmd.Parameters.AddWithValue("$num", N("num"));
                cmd.Parameters.AddWithValue("$name", S("name"));
                cmd.Parameters.AddWithValue("$cap", N("cap"));
                cmd.Parameters.AddWithValue("$used", N("used"));
                cmd.Parameters.AddWithValue("$color", S("color"));
                cmd.Parameters.AddWithValue("$note", S("note"));
                cmd.Parameters.AddWithValue("$tags", Tags());
                cmd.Parameters.AddWithValue("$pd", S("purchaseDate"));
                cmd.Parameters.AddWithValue("$wd", S("warrantyDate"));
                cmd.Parameters.AddWithValue("$hs", S("healthStatus"));
                cmd.Parameters.AddWithValue("$hn", S("healthNote"));
                cmd.Parameters.AddWithValue("$sp", S("scannedPath"));
                cmd.Parameters.AddWithValue("$sa", S("scannedAt"));
                cmd.Parameters.AddWithValue("$ls", S("lastSeen"));

                _dirtySinceBackup = true;
                if (id.HasValue) { cmd.ExecuteNonQuery(); return id.Value; }
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
        }

        // 스캔 직후 임시 드라이브 생성 (메타는 이후 UpdateMeta로 갱신)
        public long CreateDrive(string scannedPath)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO drives(num,name,cap,used,color,note,tags,scanned_path,scanned_at,last_seen)
                    VALUES(0,'',0,0,'lime','','[]',$sp,$now,$now); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$sp", scannedPath);
                cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
        }

        // 드라이브의 used(MB) 갱신
        public void SetUsed(long driveId, long usedMB)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE drives SET used=$u WHERE id=$d";
                cmd.Parameters.AddWithValue("$u", usedMB);
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteDrive(long driveId)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE drive_id=$d; DELETE FROM drives WHERE id=$d;";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.ExecuteNonQuery();
                _dirtySinceBackup = true;
            }
        }

        // 유효 id 목록에 없는 드라이브(고아) 정리. ids는 신뢰된 내부 정수만.
        public void PruneDrives(IEnumerable<long> validIds)
        {
            var list = new List<long>(validIds);
            var csv = list.Count > 0 ? string.Join(",", list) : "0";
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE drive_id NOT IN (" + csv + "); DELETE FROM drives WHERE id NOT IN (" + csv + ");";
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearFiles(long driveId)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE drive_id=$d;";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.ExecuteNonQuery();
            }
        }

        // ── 스트리밍 삽입 ─────────────────────────────────────────────────────────
        // 스캐너가 노드를 만들자마자 emit 호출 → 전체 트리를 메모리에 쌓지 않음 (OOM 방지).
        // 청크 단위(CHUNK)로 트랜잭션을 끊어 커밋하며, 청크 사이마다 _gate 락을 놓아
        // 긴 스캔 중에도 다른 조회가 끼어들 수 있게 한다(UI 멈춤 방지).
        public int InsertStreaming(long driveId, Action<Action<string, string, bool, long, string>> producer)
        {
            ClearFiles(driveId);
            const int CHUNK = 4000;
            var batch = new List<(string name, int dir, long sz, string pp, string fp, string mt)>(CHUNK);
            int count = 0;

            void Flush()
            {
                if (batch.Count == 0) return;
                lock (_gate)
                {
                    using var tx = _conn.BeginTransaction();
                    using var cmd = _conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO files(drive_id,name,is_dir,size,parent_path,full_path,mtime)
                                        VALUES($d,$n,$dir,$sz,$pp,$fp,$mt)";
                    var pD = cmd.Parameters.Add("$d", SqliteType.Integer);
                    var pN = cmd.Parameters.Add("$n", SqliteType.Text);
                    var pDir = cmd.Parameters.Add("$dir", SqliteType.Integer);
                    var pSz = cmd.Parameters.Add("$sz", SqliteType.Integer);
                    var pPP = cmd.Parameters.Add("$pp", SqliteType.Text);
                    var pFP = cmd.Parameters.Add("$fp", SqliteType.Text);
                    var pMT = cmd.Parameters.Add("$mt", SqliteType.Text);
                    pD.Value = driveId;
                    foreach (var b in batch)
                    {
                        pN.Value = b.name; pDir.Value = b.dir; pSz.Value = b.sz;
                        pPP.Value = b.pp; pFP.Value = b.fp; pMT.Value = b.mt;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    _dirtySinceBackup = true;
                }
                count += batch.Count;
                batch.Clear();
            }

            Action<string, string, bool, long, string> emit = (parentPath, name, isDir, sizeKB, mtime) =>
            {
                // 한글 NFC 정규화 (macOS APFS는 NFD 저장)
                name = (name ?? "").Normalize(System.Text.NormalizationForm.FormC);
                parentPath = (parentPath ?? "").Normalize(System.Text.NormalizationForm.FormC);
                string full = parentPath.Length == 0 ? name : parentPath + "/" + name;
                batch.Add((name, isDir ? 1 : 0, sizeKB, parentPath, full, isDir ? "" : (mtime ?? "")));
                if (batch.Count >= CHUNK) Flush();
            };
            producer(emit);
            Flush();
            return count;
        }

        // ── 스캔 트리(중첩 객체)를 평탄화해 일괄 삽입 ──────────────────────────────
        public int InsertTree(long driveId, List<object> tree)
        {
            ClearFiles(driveId);
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO files(drive_id,name,is_dir,size,parent_path,full_path,mtime)
                                    VALUES($d,$n,$dir,$sz,$pp,$fp,$mt)";
                var pD = cmd.Parameters.Add("$d", SqliteType.Integer);
                var pN = cmd.Parameters.Add("$n", SqliteType.Text);
                var pDir = cmd.Parameters.Add("$dir", SqliteType.Integer);
                var pSz = cmd.Parameters.Add("$sz", SqliteType.Integer);
                var pPP = cmd.Parameters.Add("$pp", SqliteType.Text);
                var pFP = cmd.Parameters.Add("$fp", SqliteType.Text);
                var pMT = cmd.Parameters.Add("$mt", SqliteType.Text);
                pD.Value = driveId;

                int count = 0;
                // 익명 객체(new { type, name, size/children })를 reflection으로 읽음
                object? Prop(object n, string name) => n.GetType().GetProperty(name)?.GetValue(n);

                void Walk(List<object> nodes, string parentPath)
                {
                    foreach (var n in nodes)
                    {
                        if (n == null) continue;
                        string type = Prop(n, "type")?.ToString() ?? "";
                        // 한글 정규화(NFC) — macOS APFS는 NFD(자모 분리)로 저장하므로 검색 일관성 위해 변환
                        string name = (Prop(n, "name")?.ToString() ?? "").Normalize(System.Text.NormalizationForm.FormC);
                        bool isDir = type == "dir";
                        long size = 0;
                        if (!isDir)
                        {
                            var sz = Prop(n, "size");
                            if (sz != null) long.TryParse(sz.ToString(), out size);
                        }
                        string full = parentPath.Length == 0 ? name : parentPath + "/" + name;

                        string mtime = isDir ? "" : (Prop(n, "mtime")?.ToString() ?? "");
                        pN.Value = name; pDir.Value = isDir ? 1 : 0; pSz.Value = size;
                        pPP.Value = parentPath; pFP.Value = full; pMT.Value = mtime;
                        cmd.ExecuteNonQuery();
                        count++;

                        if (isDir && Prop(n, "children") is List<object> kids)
                            Walk(kids, full);
                    }
                }
                Walk(tree, "");
                tx.Commit();
                _dirtySinceBackup = true;
                return count;
            }
        }

        // 특정 폴더의 직속 자식 (지연 로딩) — 폴더 먼저, 이름순
        public string GetChildrenJson(long driveId, string parentPath)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT name,is_dir,size,full_path,color,mtime,
                    (SELECT COUNT(*) FROM files c WHERE c.drive_id=f.drive_id AND c.parent_path=f.full_path) AS childCount
                    FROM files f WHERE drive_id=$d AND parent_path=$p
                    ORDER BY is_dir DESC, name COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.Parameters.AddWithValue("$p", parentPath);
                return ReadRows(cmd);
            }
        }

        // 전체 드라이브 통합 검색
        // 3글자 이상 + FTS 사용 가능 → trigram 인덱스로 후보를 좁히고 SQL에서 관련도 정렬.
        // 그 외(2글자 이하 / FTS 미지원) → 기존 LIKE 폴백.
        public string SearchJson(string query, int limit)
        {
            query = (query ?? "").Trim();
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                if (_ftsEnabled && query.Length >= 3)
                {
                    var ql = query.ToLowerInvariant();
                    cmd.CommandText = @"SELECT f.name,f.is_dir,f.size,f.full_path,f.color,f.drive_id,d.num as driveNum,d.name as driveName
                        FROM files_fts ft JOIN files f ON f.id=ft.rowid JOIN drives d ON d.id=f.drive_id
                        WHERE files_fts MATCH @m AND instr(lower(f.name), @ql) > 0
                        ORDER BY (CASE WHEN lower(f.name)=@ql THEN 0
                                       WHEN instr(lower(f.name), @ql)=1 THEN 1
                                       ELSE 2 END), length(f.name), f.name COLLATE NOCASE
                        LIMIT @lim";
                    cmd.Parameters.AddWithValue("@m", "\"" + query.Replace("\"", "\"\"") + "\"");
                    cmd.Parameters.AddWithValue("@ql", ql);
                    cmd.Parameters.AddWithValue("@lim", limit);
                }
                else
                {
                    cmd.CommandText = @"SELECT f.name,f.is_dir,f.size,f.full_path,f.color,f.drive_id,d.num as driveNum,d.name as driveName
                        FROM files f JOIN drives d ON d.id=f.drive_id
                        WHERE f.name LIKE $q ESCAPE '\' ORDER BY f.name COLLATE NOCASE LIMIT $lim";
                    cmd.Parameters.AddWithValue("$q", "%" + Escape(query) + "%");
                    cmd.Parameters.AddWithValue("$lim", limit);
                }
                return ReadRows(cmd);
            }
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        public void SetItemColor(long driveId, string fullPath, string? color)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE files SET color=$c WHERE drive_id=$d AND full_path=$p";
                cmd.Parameters.AddWithValue("$c", (object?)color ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.Parameters.AddWithValue("$p", fullPath);
                cmd.ExecuteNonQuery();
                _dirtySinceBackup = true;
            }
        }

        // 색상 태그로 필터 (전체 경로 반환)
        public string FilesByColorJson(long driveId, string color, int limit)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT name,is_dir,size,full_path,color,mtime FROM files
                    WHERE drive_id=$d AND color=$c ORDER BY full_path LIMIT $lim";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.Parameters.AddWithValue("$c", color);
                cmd.Parameters.AddWithValue("$lim", limit);
                return ReadRows(cmd);
            }
        }

        // 폴더 목록 — is_dir=1인 항목 전체, full_path 정렬
        public string FoldersByDriveJson(long driveId, int limit)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT name,is_dir,size,full_path,color,mtime,
                    (SELECT COUNT(*) FROM files c WHERE c.drive_id=f.drive_id AND c.parent_path=f.full_path) AS childCount
                    FROM files f WHERE drive_id=$d AND is_dir=1 ORDER BY full_path LIMIT $lim";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.Parameters.AddWithValue("$lim", limit > 0 ? limit : 50000);
                return ReadRows(cmd);
            }
        }

        // 카테고리(확장자 목록) 필터 — extCsv는 'jpg,png,...'. 앞에 '!'면 제외(기타용)
        public string FilesByExtsJson(long driveId, string extCsv, int limit)
        {
            bool exclude = false;
            extCsv ??= "";
            if (extCsv.StartsWith("!")) { exclude = true; extCsv = extCsv.Substring(1); }
            var exts = new List<string>();
            foreach (var e in extCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = e.Trim().ToLowerInvariant();
                if (System.Text.RegularExpressions.Regex.IsMatch(t, "^[a-z0-9]+$")) exts.Add("'" + t + "'");
            }
            var inClause = exts.Count > 0 ? string.Join(",", exts) : "''";
            var op = exclude ? "NOT IN" : "IN";
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT name,is_dir,size,full_path,color,mtime FROM files
                    WHERE drive_id=$d AND is_dir=0 AND
                    LOWER(CASE WHEN instr(name,'.')>0 THEN replace(name, rtrim(name, replace(name,'.','')), '') ELSE '' END) " + op + " (" + inClause + @")
                    ORDER BY name COLLATE NOCASE LIMIT $lim";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.Parameters.AddWithValue("$lim", limit);
                return ReadRows(cmd);
            }
        }

        // 중복 파일(이름+크기 동일) — 전체 드라이브에 걸쳐
        public string DuplicatesJson(int limit)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT f.name,f.size,f.full_path,d.num AS driveNum,d.name AS driveName,d.color
                    FROM files f JOIN drives d ON d.id=f.drive_id
                    WHERE f.is_dir=0 AND (LOWER(f.name)||'|'||f.size) IN (
                      SELECT LOWER(name)||'|'||size FROM files WHERE is_dir=0
                      GROUP BY LOWER(name),size HAVING COUNT(*)>=2
                    )
                    ORDER BY f.size DESC, LOWER(f.name) LIMIT $lim";
                cmd.Parameters.AddWithValue("$lim", limit);
                return ReadRows(cmd);
            }
        }

        // 중복 전수 통계 (표시 목록과 무관하게 전체 기준 그룹 수·낭비 용량 집계)
        public string DuplicateStatsJson()
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT(*) AS groups, COALESCE(SUM(wasted),0) AS wastedKB FROM (
                    SELECT (COUNT(*)-1)*size AS wasted FROM files WHERE is_dir=0
                    GROUP BY LOWER(name), size HAVING COUNT(*) >= 2)";
                return ReadRows(cmd);
            }
        }

        // 드라이브 목록 + 파일 수
        public string ListDrivesJson()
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT d.*,
                    (SELECT COUNT(*) FROM files f WHERE f.drive_id=d.id AND f.is_dir=0) AS fileCount
                    FROM drives d ORDER BY d.num";
                return ReadRows(cmd);
            }
        }

        // 드라이브 통계 (확장자 분류는 JS에서, 여기선 총계만)
        public string DriveStatsJson(long driveId)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT
                    (SELECT COUNT(*) FROM files WHERE drive_id=$d AND is_dir=0) AS files,
                    (SELECT COUNT(*) FROM files WHERE drive_id=$d AND is_dir=1) AS dirs,
                    (SELECT COALESCE(SUM(size),0) FROM files WHERE drive_id=$d AND is_dir=0) AS totalKB";
                cmd.Parameters.AddWithValue("$d", driveId);
                return ReadRows(cmd);
            }
        }

        // 카테고리별 집계 (확장자 → 카테고리 매핑은 호출측 JS가 미리 줄 수 없으니 확장자 카운트만)
        public string ExtCountsJson(long driveId)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT
                    LOWER(CASE WHEN instr(name,'.')>0 THEN replace(name, rtrim(name, replace(name,'.','')), '') ELSE '' END) AS ext,
                    COUNT(*) AS cnt, COALESCE(SUM(size),0) AS kb
                    FROM files WHERE drive_id=$d AND is_dir=0 GROUP BY ext";
                cmd.Parameters.AddWithValue("$d", driveId);
                return ReadRows(cmd);
            }
        }

        // 카테고리 분류 (JS EXT_MAP과 동일)
        private static readonly (string Label, string[] Exts)[] _catMap = new[]
        {
            ("영상", new[]{"mkv","mp4","avi","mov","wmv","m4v","flv","webm","ts","m2ts","rmvb","mpg","mpeg","braw","r3d","mxf","mts","vob","3gp","f4v","asf"}),
            ("사진", new[]{"jpg","jpeg","png","gif","webp","raw","cr2","cr3","nef","arw","tiff","tif","bmp","heic","dng","orf","rw2","raf","srw","x3f","heif","avif"}),
            ("음악", new[]{"mp3","flac","wav","aac","m4a","ogg","opus","wma","aiff","alac","ape","dsf","dff"}),
            ("문서", new[]{"pdf","docx","doc","xlsx","xls","pptx","ppt","txt","md","hwp","pages","numbers","key","csv","rtf","odt","ods","odp"}),
            ("디자인", new[]{"ai","psd","indd","xd","fig","sketch","eps","svg","afdesign","afphoto","blend","c4d","ae","prproj","drp","resolve"}),
        };
        private static string CategoryLabel(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) return "기타";
            var ext = name.Substring(dot + 1).ToLowerInvariant();
            foreach (var (label, exts) in _catMap) if (Array.IndexOf(exts, ext) >= 0) return label;
            return "기타";
        }
        private static string Csv(string? s)
        {
            s ??= "";
            return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // 전체 드라이브 파일을 CSV로 직접 작성 (대용량 안전, 메모리 안 씀)
        public void ExportCsv(string path)
        {
            lock (_gate)
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(true)); // BOM
                sw.WriteLine("드라이브번호,드라이브이름,파일경로,파일이름,크기(KB),분류,수정일");
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"SELECT d.num, d.name AS dname, f.full_path, f.name AS fname, f.size, f.mtime
                    FROM files f JOIN drives d ON d.id=f.drive_id WHERE f.is_dir=0
                    ORDER BY d.num, f.full_path";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var num = r.GetValue(0)?.ToString() ?? "";
                    var dname = r.IsDBNull(1) ? "" : r.GetString(1);
                    var fpath = r.IsDBNull(2) ? "" : r.GetString(2);
                    var fname = r.IsDBNull(3) ? "" : r.GetString(3);
                    var size = r.GetValue(4)?.ToString() ?? "0";
                    var mtime = r.IsDBNull(5) ? "" : r.GetString(5);
                    sw.WriteLine(string.Join(",", num, Csv(dname), Csv(fpath), Csv(fname), size, CategoryLabel(fname), mtime));
                }
            }
        }

        // 모바일에서 안전하게 열 수 있는 단일 HTML 1개당 최대 파일 수.
        // 초과 시 드라이브별로 쪼개 각 파일이 모바일 메모리 한계를 넘지 않게 한다.
        private const long VIEWER_SPLIT_THRESHOLD = 150000;
        private static string SanitizeFileName(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "drive" : s.Trim();
        }

        // 모바일 뷰어 HTML 생성 — 항목 수가 임계를 넘으면 드라이브별로 분할 생성.
        // 생성된 파일 경로 목록을 반환(분할 안내용).
        public List<string> ExportViewer(string path)
        {
            lock (_gate)
            {
                var made = new List<string>();
                long total = Scalar("SELECT count(*) FROM files");
                if (total <= VIEWER_SPLIT_THRESHOLD)
                {
                    ExportViewerInternal(path, null);
                    made.Add(path);
                    return made;
                }
                // 분할: 드라이브별 개별 HTML
                var baseNoExt = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(0, path.Length - 5) : path;
                var drivesList = new List<(long id, string num, string name)>();
                using (var dcmd = _conn.CreateCommand())
                {
                    dcmd.CommandText = "SELECT id,num,name FROM drives ORDER BY num";
                    using var dr = dcmd.ExecuteReader();
                    while (dr.Read())
                        drivesList.Add((dr.GetInt64(0), dr.GetValue(1)?.ToString() ?? "0", dr.IsDBNull(2) ? "" : dr.GetString(2)));
                }
                foreach (var d in drivesList)
                {
                    var dest = baseNoExt + "_" + d.num + "번_" + SanitizeFileName(d.name) + ".html";
                    ExportViewerInternal(dest, d.id);
                    made.Add(dest);
                }
                return made;
            }
        }

        // 실제 뷰어 HTML 생성. onlyDriveId 지정 시 해당 드라이브만 포함. (호출측이 _gate 락 보유)
        private void ExportViewerInternal(string path, long? onlyDriveId)
        {
            const string head = "<!DOCTYPE html>\n<html lang=\"ko\"><head><meta charset=\"UTF-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>LogMapping Viewer</title>"
                + "<style>*{box-sizing:border-box;margin:0;padding:0}body{background:#111;color:#f0ece8;font-family:-apple-system,sans-serif;padding:12px;-webkit-text-size-adjust:100%}"
                + "h1{font-size:17px;font-weight:700;letter-spacing:2px;color:#D35400;margin-bottom:10px}h1 span{color:#f0ece8}"
                + "#q{width:100%;padding:12px 14px;background:#1a1a1a;border:1px solid #2a2a2a;border-radius:8px;color:#f0ece8;font-size:16px;margin-bottom:8px;outline:none}#q:focus{border-color:#D35400}"
                + "#drivebar{display:flex;gap:6px;flex-wrap:wrap;margin-bottom:8px}.db{background:#1a1a1a;border:1px solid #2a2a2a;color:#aaa;border-radius:16px;padding:6px 14px;font-size:13px;cursor:pointer}.db.on{background:#D35400;color:#fff;border-color:#D35400;font-weight:700}"
                + "#info{font-size:11px;color:#777;margin-bottom:8px;min-height:14px}"
                + ".row{padding:10px 8px;border-bottom:1px solid #1c1c1c;font-size:14px;display:flex;align-items:center;gap:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}"
                + ".row.dir{cursor:pointer;color:#f0ece8;font-weight:600}.row.dir:active{background:#1c1c1c}.row.file{color:#bbb}"
                + ".cnt{margin-left:auto;font-size:11px;color:#666;flex-shrink:0}"
                + ".rp{font-size:10px;color:#666;word-break:break-all;margin-top:2px}.sr{display:block;padding:9px 8px;border-bottom:1px solid #1c1c1c}.srn{font-size:14px;color:#f0ece8}.badge{color:#D35400;font-size:11px;font-weight:700}"
                + ".no-res{text-align:center;color:#444;padding:50px 0;font-size:13px}mark{background:#D3540055;color:#FF7A2F;border-radius:2px}</style></head>"
                + "<body><h1>LOG<span>MAPPING</span></h1>"
                + "<input type=\"search\" id=\"q\" placeholder=\"전체 파일 검색...\" oninput=\"onSearch(this.value)\" autocomplete=\"off\">"
                + "<div id=\"drivebar\"></div><div id=\"info\"></div><div id=\"tree\"></div>\n<script>\nvar DATA=[";
            const string tail = "];\n"
                + "function byId(x){return document.getElementById(x);}\n"
                + "var drives={},driveNums=[];\n"
                + "DATA.forEach(function(f){if(!drives[f.dn]){drives[f.dn]={c:{},files:[]};driveNums.push(f.dn);}var parts=f.p.split('/');var cur=drives[f.dn];for(var i=0;i<parts.length-1;i++){var s=parts[i];if(!cur.c[s])cur.c[s]={c:{},files:[]};cur=cur.c[s];}var last=parts[parts.length-1];if(f.d){if(!cur.c[last])cur.c[last]={c:{},files:[]};}else{cur.files.push(last);}});\n"
                + "driveNums.sort(function(a,b){return a-b;});var curDrive=driveNums[0],openState={};\n"
                + "function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}\n"
                + "function hl(t,q){var i=t.toLowerCase().indexOf(q.toLowerCase());if(i<0)return esc(t);return esc(t.slice(0,i))+'<mark>'+esc(t.slice(i,i+q.length))+'</mark>'+esc(t.slice(i+q.length));}\n"
                + "function renderBar(){byId('drivebar').innerHTML=driveNums.map(function(d){return '<button class=\"db'+(d==curDrive?' on':'')+'\" onclick=\"selDrive('+d+')\">'+d+'번 하드</button>';}).join('');}\n"
                + "function selDrive(d){curDrive=d;openState={};byId('q').value='';renderBar();renderTree();}\n"
                + "function countAll(node){var c=node.files.length;for(var k in node.c)c+=countAll(node.c[k]);return c;}\n"
                + "function renderNode(node,path,depth){var html='';var dirs=Object.keys(node.c).sort();dirs.forEach(function(name){var p=path?path+'/'+name:name;var open=openState[p];var pe=p.replace(/\\\\/g,'\\\\\\\\').replace(/'/g,\"\\\\'\");html+='<div class=\"row dir\" style=\"padding-left:'+(depth*14+8)+'px\" onclick=\"tog(\\''+pe+'\\')\">'+(open?'\\u25be':'\\u25b8')+' \\ud83d\\udcc1 '+esc(name)+'<span class=\"cnt\">'+countAll(node.c[name])+'</span></div>';if(open)html+=renderNode(node.c[name],p,depth+1);});node.files.slice().sort().forEach(function(fn){html+='<div class=\"row file\" style=\"padding-left:'+(depth*14+24)+'px\">\\ud83d\\udcc4 '+esc(fn)+'</div>';});return html;}\n"
                + "function renderTree(){var root=drives[curDrive];if(!root){byId('tree').innerHTML='<div class=no-res>데이터 없음</div>';return;}byId('info').textContent=curDrive+'번 하드 \\u00b7 '+countAll(root).toLocaleString()+'개 파일';byId('tree').innerHTML=renderNode(root,'',0)||'<div class=no-res>비어있음</div>';}\n"
                + "function tog(p){openState[p]=!openState[p];renderTree();}\n"
                + "function goTo(dn,p){byId('q').value='';curDrive=dn;openState={};var parts=p.split('/');var cum='';for(var i=0;i<parts.length;i++){cum=cum?cum+'/'+parts[i]:parts[i];openState[cum]=true;}renderBar();renderTree();window.scrollTo(0,0);}\n"
                + "function scr(n,q){n=n.toLowerCase();if(n===q)return 3;if(n.indexOf(q)===0)return 2;return 1;}\n"
                + "function onSearch(q){q=(q||'').trim();if(!q){renderBar();renderTree();return;}var ql=q.toLowerCase();var res=[];for(var i=0;i<DATA.length;i++){var f=DATA[i];if(f.n.toLowerCase().indexOf(ql)>=0||f.p.toLowerCase().indexOf(ql)>=0)res.push(f);}res.sort(function(a,b){return scr(b.n,ql)-scr(a.n,ql)||a.n.length-b.n.length||a.n.localeCompare(b.n);});var tot=res.length;res=res.slice(0,500);byId('info').textContent=tot+'개 결과'+(tot>500?' (상위 500)':'');byId('tree').innerHTML=res.length?res.map(function(f){var ic=f.d?'\\ud83d\\udcc1':'\\ud83d\\udcc4';var pe=f.p.replace(/\\\\/g,'\\\\\\\\').replace(/'/g,\"\\\\'\");var click=f.d?(' onclick=\"goTo('+f.dn+',\\''+pe+'\\')\" style=\"cursor:pointer\"'):'';return '<div class=\"sr\"'+click+'><div class=\"srn\">'+ic+' '+hl(f.n,q)+' <span class=\"badge\">['+f.dn+'번]</span></div><div class=\"rp\">'+esc(f.p)+'</div></div>';}).join(''):'<div class=no-res>결과 없음</div>';}\n"
                + "renderBar();renderTree();\n"
                + "</script></body></html>";

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(true));
            sw.Write(head);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT f.name, f.full_path, d.num, f.is_dir FROM files f JOIN drives d ON d.id=f.drive_id"
                + (onlyDriveId.HasValue ? " WHERE f.drive_id=@only" : "");
            if (onlyDriveId.HasValue) cmd.Parameters.AddWithValue("@only", onlyDriveId.Value);
            using var r = cmd.ExecuteReader();
            bool first = true;
            while (r.Read())
            {
                if (!first) sw.Write(',');
                first = false;
                var nameRaw = (r.IsDBNull(0) ? "" : r.GetString(0)).Normalize(System.Text.NormalizationForm.FormC);
                var pathRaw = (r.IsDBNull(1) ? "" : r.GetString(1)).Normalize(System.Text.NormalizationForm.FormC);
                var n = JsonSerializer.Serialize(nameRaw);
                var p = JsonSerializer.Serialize(pathRaw);
                var dn = r.GetValue(2)?.ToString() ?? "";
                var isDir = (!r.IsDBNull(3) && Convert.ToInt64(r.GetValue(3)) == 1) ? 1 : 0;
                sw.Write("{\"n\":" + n + ",\"p\":" + p + ",\"dn\":" + dn + ",\"d\":" + isDir + "}");
            }
            sw.Write(tail);
        }

        private static string ReadRows(SqliteCommand cmd)
        {
            using var r = cmd.ExecuteReader();
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            while (r.Read())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                for (int i = 0; i < r.FieldCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(r.GetName(i)));
                    sb.Append(':');
                    if (r.IsDBNull(i)) { sb.Append("null"); continue; }
                    var val = r.GetValue(i);
                    sb.Append(val is long or int or double or float
                        ? JsonSerializer.Serialize(val)
                        : JsonSerializer.Serialize(val.ToString()));
                }
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // catalog.db → catalog.db.bak 안전 백업 (열린 상태에서도 가능)
        public void BackupSelf()
        {
            lock (_gate)
            {
                var dest = Path + ".bak";
                using var d = new SqliteConnection($"Data Source={dest}");
                d.Open();
                _conn.BackupDatabase(d);
                _dirtySinceBackup = false;
            }
        }

        // 마지막 백업 이후 변경이 있을 때만 백업 (종료 시 1회 호출용)
        public void BackupIfDirty()
        {
            if (_dirtySinceBackup) BackupSelf();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                // 종료 시 WAL을 메인 DB로 합쳐(checkpoint) 비정상 종료로 인한 손상을 예방
                try
                {
                    if (_conn != null && _conn.State == System.Data.ConnectionState.Open)
                    {
                        using var c = _conn.CreateCommand();
                        c.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        c.ExecuteNonQuery();
                    }
                }
                catch { }
                _conn?.Dispose();
            }
        }
    }
}
