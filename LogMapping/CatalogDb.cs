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
using System.IO.Compression;
using System.Text;
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

        // DB를 열고 열림 가능 여부를 확인. 손상(malformed) 시 손상 파일을 정리하고 새 DB를 생성한다.
        //
        // ⚠️ 여기서 `PRAGMA quick_check`(전수 검사)를 하지 않는다. quick_check는 DB 전체를 읽어
        // 2.5GB/파일 350만 건 카탈로그에서 375초(6분)가 걸렸고, 앱 시작 때마다 UI를 정지시켰다.
        // 대신 스키마 페이지만 읽는 가벼운 확인으로 "열 수 있는 DB인지"만 판정한다.
        // 실제 손상은 이후 쿼리에서 SqliteException으로 표면화된다(각 핸들러가 오류를 JS로 보고).
        private void OpenWithRecovery()
        {
            _conn = new SqliteConnection($"Data Source={Path}");
            _conn.Open();
            bool ok;
            try
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "PRAGMA schema_version; SELECT count(*) FROM sqlite_master;";
                c.ExecuteScalar();
                ok = true;
            }
            catch { ok = false; } // malformed / not a database 등 → 예외

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

        private string? GetMeta(string k)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM meta WHERE k=$k";
                cmd.Parameters.AddWithValue("$k", k);
                return cmd.ExecuteScalar() as string;
            }
            catch { return null; }
        }

        private void SetMeta(string k, string v)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // FTS5 trigram 가상 테이블 + 동기화 트리거 구성. 미지원 환경은 _ftsEnabled=false로 폴백.
        //
        // ⚠️ 색인 재구축 필요 여부를 `count(*)`로 판단하면 안 된다. 실측(2.5GB·350만 행):
        //    SELECT count(*) FROM files_fts → 59.5초,  SELECT count(*) FROM files → 107초.
        // 이 판단이 DB를 열 때마다(=앱 시작마다) 실행되어 카탈로그가 뜨기까지 2~3분이 걸렸다.
        // → 행 존재 여부는 EXISTS(0.00초)로 확인하고, 한 번 확인했으면 meta 플래그로 기록해
        //   다음 시작부터는 검사 자체를 건너뛴다.
        private void EnsureFts()
        {
            try
            {
                Exec("CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(name, content='files', content_rowid='id', tokenize='trigram');");
                Exec("CREATE TRIGGER IF NOT EXISTS files_fts_ai AFTER INSERT ON files BEGIN INSERT INTO files_fts(rowid,name) VALUES(new.id,new.name); END;");
                Exec("CREATE TRIGGER IF NOT EXISTS files_fts_ad AFTER DELETE ON files BEGIN INSERT INTO files_fts(files_fts,rowid,name) VALUES('delete',old.id,old.name); END;");

                if (GetMeta("fts_ready") != "1")
                {
                    // 기존 DB(트리거 도입 이전 데이터)가 색인 안 돼 있으면 1회만 재구축
                    // files_fts_data는 색인이 비어도 설정 행이 있어 판정에 쓸 수 없다.
                    // 문서당 1행인 files_fts_docsize로 "색인된 문서가 있는지"를 본다(0.1초).
                    bool anyFiles = Scalar("SELECT EXISTS(SELECT 1 FROM files)") == 1;
                    bool anyIndex;
                    try { anyIndex = Scalar("SELECT EXISTS(SELECT 1 FROM files_fts_docsize)") == 1; }
                    catch { anyIndex = Scalar("SELECT EXISTS(SELECT 1 FROM files_fts)") == 1; }
                    if (anyFiles && !anyIndex)
                        Exec("INSERT INTO files_fts(files_fts) VALUES('rebuild');");
                    SetMeta("fts_ready", "1");
                }
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
-- 뷰어 내보내기용 캐시: 드라이브별 파일목록을 gzip으로 미리 압축해 보관한다.
-- 스캔할 때 한 번 만들어 두면 이후 내보내기는 이 blob을 꺼내 쓰기만 하면 된다.
-- 해당 드라이브를 다시 스캔하면(ClearFiles) 자동으로 지워져 낡은 캐시가 남지 않는다.
CREATE TABLE IF NOT EXISTS viewer_cache(
  drive_id INTEGER PRIMARY KEY,
  built_at TEXT,
  payload  BLOB
);
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
                cmd.CommandText = "DELETE FROM files WHERE drive_id=$d; DELETE FROM drives WHERE id=$d; DELETE FROM viewer_cache WHERE drive_id=$d;";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.ExecuteNonQuery();
                _dirtySinceBackup = true;
            }
        }

        // 유효 id 목록에 없는 드라이브(고아) 정리. ids는 신뢰된 내부 정수만.
        //
        // ⚠️ `DELETE FROM files WHERE drive_id NOT IN (...)`를 바로 실행하면 고아가 0개여도
        // files 전체(350만 행, 2.5GB)를 SCAN한다. 카탈로그를 열 때마다 이 비용이 들어 UI가 멈췄다.
        // 그래서 수십 행짜리 drives 테이블로 고아 id를 먼저 찾고(즉시), 있을 때만 그 id로만 삭제한다
        // (drive_id가 idx_files_drive_parent의 선두 컬럼이라 인덱스를 탄다).
        public void PruneDrives(IEnumerable<long> validIds)
        {
            var list = new List<long>(validIds);
            var csv = list.Count > 0 ? string.Join(",", list) : "0";
            lock (_gate)
            {
                var orphans = new List<long>();
                using (var q = _conn.CreateCommand())
                {
                    q.CommandText = "SELECT id FROM drives WHERE id NOT IN (" + csv + ")";
                    using var r = q.ExecuteReader();
                    while (r.Read()) orphans.Add(r.GetInt64(0));
                }
                if (orphans.Count == 0) return;   // 정리할 것이 없으면 전체 스캔 없이 즉시 종료

                var ocsv = string.Join(",", orphans);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE drive_id IN (" + ocsv + "); DELETE FROM drives WHERE id IN (" + ocsv + "); DELETE FROM viewer_cache WHERE drive_id IN (" + ocsv + ");";
                cmd.ExecuteNonQuery();
                _dirtySinceBackup = true;
            }
        }

        public void ClearFiles(long driveId)
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                // 파일 목록이 바뀌면 뷰어 캐시도 낡으므로 함께 지운다
                cmd.CommandText = "DELETE FROM files WHERE drive_id=$d; DELETE FROM viewer_cache WHERE drive_id=$d;";
                cmd.Parameters.AddWithValue("$d", driveId);
                cmd.ExecuteNonQuery();
            }
        }

        // ── 뷰어 캐시 ─────────────────────────────────────────────────────────────
        // 드라이브의 파일 목록(줄마다 'D'|'F' + full_path)을 gzip으로 압축해 DB에 보관한다.
        // 스캔 직후 미리 만들어 두면, 뷰어 내보내기는 350만 행을 다시 읽고 압축할 필요 없이
        // 이 blob을 꺼내 base64로 적기만 하면 된다(15초 → 수 초).
        private byte[] BuildPayload(long driveId)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
            using (var gw = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT full_path, is_dir FROM files WHERE drive_id=$d";
                cmd.Parameters.AddWithValue("$d", driveId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    gw.Write(Convert.ToInt64(r.GetValue(1) ?? 0L) == 1 ? 'D' : 'F');
                    gw.Write(r.GetString(0));
                    gw.Write('\n');
                }
            }
            return ms.ToArray();
        }

        private byte[]? ReadCache(long driveId)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM viewer_cache WHERE drive_id=$d";
            cmd.Parameters.AddWithValue("$d", driveId);
            return cmd.ExecuteScalar() as byte[];
        }

        private void WriteCache(long driveId, byte[] payload)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO viewer_cache(drive_id,built_at,payload) VALUES($d,$t,$p)
                                ON CONFLICT(drive_id) DO UPDATE SET built_at=$t, payload=$p";
            cmd.Parameters.AddWithValue("$d", driveId);
            cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.ExecuteNonQuery();
        }

        // 스캔 직후 호출 — 해당 드라이브의 뷰어 캐시를 미리 만들어 둔다.
        public void BuildViewerCache(long driveId)
        {
            lock (_gate)
            {
                var payload = BuildPayload(driveId);
                WriteCache(driveId, payload);
                _dirtySinceBackup = true;
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

        // ── 모바일 뷰어 HTML (단일 파일) ───────────────────────────────────────────
        // 드라이브가 몇 개든 **항상 파일 1개**로 내보낸다(오너 요구). 예전에는 항목이 15만을 넘으면
        // 드라이브별로 쪼개서 20개 파일이 나왔다.
        //
        // 한 파일에 350만 항목을 무압축으로 담으면 450~670MB가 되어 폰에서 열 수 없다. 그래서
        // 드라이브별 목록을 **gzip으로 압축해 base64로 내장**하고(경로는 반복이 많아 약 16배 압축),
        // 브라우저에서 DecompressionStream으로 **선택한 드라이브만 그때 해제**한다.
        // 결과: 350만 항목 기준 약 38MB 단일 파일, 메모리 사용은 드라이브 1개분.
        // onProgress(완료 드라이브 수, 전체 드라이브 수) — 진행 상황을 UI에 보고한다.
        //
        // ⚠️ 드라이브 목록에 파일 수 COUNT(*) 서브쿼리를 넣지 않는다. 20개 드라이브 × 350만 행을
        // 세느라 **본 작업 시작 전에 3분 20초**가 걸렸고(진행바가 멈춘 것처럼 보였다), 정작 뷰어는
        // 그 값을 쓰지 않는다(항목 수는 브라우저가 트리에서 직접 센다).
        // 또 락을 드라이브 1개 단위로만 잡아, 내보내기 중에도 앱의 조회가 끼어들 수 있게 한다.
        public List<string> ExportViewer(string path, Action<int, int>? onProgress = null)
        {
            var drives = new List<(long id, string num, string name, bool dead)>();
            lock (_gate)
            {
                using var dcmd = _conn.CreateCommand();
                dcmd.CommandText = "SELECT id, num, name, health_status FROM drives ORDER BY num";
                using var dr = dcmd.ExecuteReader();
                while (dr.Read())
                    drives.Add((dr.GetInt64(0), dr.GetValue(1)?.ToString() ?? "0",
                                dr.IsDBNull(2) ? "" : dr.GetString(2),
                                (dr.IsDBNull(3) ? "" : dr.GetString(3)) == "dead"));
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var sw = new StreamWriter(fs, new UTF8Encoding(true));
            sw.Write(ViewerHead);

            // 드라이브 메타 (i=payload 인덱스, n=번호, name)
            sw.Write("<script>\nvar DRIVES=[");
            for (int i = 0; i < drives.Count; i++)
            {
                if (i > 0) sw.Write(',');
                sw.Write("{i:" + i + ",n:" + JsonSerializer.Serialize(drives[i].num)
                         + ",name:" + JsonSerializer.Serialize(drives[i].name)
                         + (drives[i].dead ? ",dead:1" : "") + "}");
            }
            sw.Write("];\n</scr" + "ipt>\n");

            // ⚠️ 동작 로직을 **payload보다 먼저** 넣는다. 예전에는 스크립트가 파일 맨 뒤에 있어
            // 32MB를 전부 파싱하기 전까지 화면에 아무것도 못 그렸다(폰에서 1분 넘게 빈 화면).
            // 앞에 두면 하드 버튼이 즉시 뜨고, 목록은 해당 payload가 도착하는 대로 표시된다.
            sw.Write(ViewerTail);

            onProgress?.Invoke(0, drives.Count);

            // 드라이브별 압축 payload — 각 줄 = ('D'|'F') + full_path
            for (int i = 0; i < drives.Count; i++)
            {
                byte[] gzBytes;
                lock (_gate)
                {
                    // 캐시가 있으면 그대로 사용, 없으면(예: 이 기능 이전에 스캔한 드라이브) 만들어서 저장
                    gzBytes = ReadCache(drives[i].id) ?? Array.Empty<byte>();
                    if (gzBytes.Length == 0)
                    {
                        gzBytes = BuildPayload(drives[i].id);
                        WriteCache(drives[i].id, gzBytes);
                    }
                }
                // ⚠️ base64를 JS 문자열 리터럴로 넣지 않는다. 350만 항목이면 스크립트 본문이 30MB를
                // 넘고, 스마트폰 인앱 브라우저가 그 큰 스크립트를 파싱하다 실패/지연했다(실제 발생).
                // type='text/plain' 블록에 넣으면 HTML 텍스트로 취급돼 JS 파서를 거치지 않는다.
                sw.Write("<script type='text/plain' id='p" + i + "'>" + Convert.ToBase64String(gzBytes) + "</scr" + "ipt>\n");
                onProgress?.Invoke(i + 1, drives.Count);
            }

            sw.Write("</body></html>");
            return new List<string> { path };
        }

        // 뷰어 HTML 앞부분 (스타일 + 골격). JS 문자열은 모두 홑따옴표를 써서 C# 이스케이프를 줄인다.
        private const string ViewerHead = @"<!DOCTYPE html>
<html lang='ko'><head><meta charset='UTF-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'><title>LogMapping Viewer</title>
<style>*{box-sizing:border-box;margin:0;padding:0}
body{background:#111;color:#f0ece8;font-family:-apple-system,sans-serif;padding:12px;-webkit-text-size-adjust:100%}
h1{font-size:17px;font-weight:700;letter-spacing:2px;color:#D35400;margin-bottom:10px}h1 span{color:#f0ece8}
#q{width:100%;padding:12px 14px;background:#1a1a1a;border:1px solid #2a2a2a;border-radius:8px;color:#f0ece8;font-size:16px;margin-bottom:8px;outline:none}
#q:focus{border-color:#D35400}
#drivebar{display:flex;gap:6px;flex-wrap:wrap;margin-bottom:8px}
.db{background:#1a1a1a;border:1px solid #2a2a2a;color:#aaa;border-radius:16px;padding:6px 14px;font-size:13px;cursor:pointer}
.db.on{background:#D35400;color:#fff;border-color:#D35400;font-weight:700}
#info{font-size:11px;color:#777;margin-bottom:8px;min-height:14px}
.row{padding:10px 8px;border-bottom:1px solid #1c1c1c;font-size:14px;display:flex;align-items:center;gap:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.row.dir{cursor:pointer;color:#f0ece8;font-weight:600}.row.dir:active{background:#1c1c1c}.row.file{color:#bbb}
.cnt{margin-left:auto;font-size:11px;color:#666;flex-shrink:0}
.rp{font-size:10px;color:#666;word-break:break-all;margin-top:2px}
.sr{display:block;padding:9px 8px;border-bottom:1px solid #1c1c1c}.sr.dir{cursor:pointer}
.srn{font-size:14px;color:#f0ece8}.badge{color:#D35400;font-size:11px;font-weight:700}
.via{font-size:9px;color:#777;border:1px solid #333;border-radius:8px;padding:1px 6px;margin-left:4px}
.no-res{text-align:center;color:#444;padding:50px 0;font-size:13px}
mark{background:#D3540055;color:#FF7A2F;border-radius:2px}
.err{background:#2a1010;border:1px solid #5a2020;color:#ff8a80;padding:12px;border-radius:8px;font-size:13px;line-height:1.6}</style></head>
<body><h1>LOG<span>MAPPING</span></h1>
<input type='search' id='q' placeholder='전체 파일 검색...' autocomplete='off'>
<div id='drivebar'></div><div id='info'></div>
<div id='tree'><div class='err' id='nojs'>
<b style='font-size:15px'>⚠️ 목록을 표시할 수 없습니다</b><br><br>
카카오톡·구글드라이브 등 <b>앱 안에서 바로 열면</b> 자바스크립트가 차단되어 목록이 나오지 않습니다.<br><br>
<b>· PC(윈도우·맥) 브라우저에서 열어주세요</b> — 가장 확실합니다<br>
<b>· 휴대폰에서 보려면</b> 자바스크립트를 지원하는 브라우저 앱을 설치해 그 앱으로 열어야 합니다<br><br>
<span style='font-size:11px;color:#c99'>이 문구가 보이면 파일이 잘못된 것이 아니라, 여는 앱이 스크립트를 막고 있는 것입니다.</span>
</div></div>
<noscript><div class='err'>이 브라우저에서 자바스크립트가 꺼져 있어 목록을 표시할 수 없습니다. PC 브라우저에서 열어주세요.</div></noscript>
";

        // 뷰어 HTML 뒷부분 (동작). 압축 해제는 선택한 드라이브에 대해서만 수행한다.
        private const string ViewerTail = @"
<script>
// 스크립트가 실행됐다는 뜻이므로, '표시할 수 없습니다' 경고를 즉시 '읽는 중'으로 바꾼다.
// (경고는 스크립트를 막는 앱에서만 그대로 남는다)
(function(){var n=document.getElementById('nojs');
 if(n){n.className='no-res';n.id='boot';n.innerHTML='목록을 읽는 중입니다...<br><span style=""font-size:11px"">파일이 커서 잠시 걸릴 수 있습니다.</span>';}})();
function byId(x){return document.getElementById(x);}
function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
function hl(t,q){var i=t.toLowerCase().indexOf(q.toLowerCase());if(i<0)return esc(t);
  return esc(t.slice(0,i))+'<mark>'+esc(t.slice(i,i+q.length))+'</mark>'+esc(t.slice(i+q.length));}
function setInfo(t){byId('info').textContent=t;}

var cur=0, curText=null, curTree=null, openState={}, _rows=[], busy=false;
var noZip = (typeof DecompressionStream==='undefined');

// base64(gzip) → 텍스트. payload는 <script type='text/plain' id='pN'> 블록에서 읽는다.
async function inflate(i){
  var el=document.getElementById('p'+i);
  // 아직 문서가 다 읽히지 않아 payload가 없을 수 있다 → 다 읽힐 때까지 기다린 뒤 다시 찾는다
  if(!el && document.readyState==='loading'){
    await new Promise(function(r){ document.addEventListener('DOMContentLoaded', r, {once:true}); });
    el=document.getElementById('p'+i);
  }
  if(!el) throw new Error('아직 목록을 읽는 중입니다. 잠시 후 다시 눌러주세요.');
  var b=atob(el.textContent.trim()), n=b.length, u=new Uint8Array(n);
  for(var k=0;k<n;k++)u[k]=b.charCodeAt(k);
  var st=new Blob([u]).stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(st).text();
}

// 텍스트(한 줄 = 'D'|'F' + 경로) → 폴더 트리
function buildTree(text){
  var root={c:{},f:[]}, i=0, n=text.length;
  while(i<n){
    var j=text.indexOf('\n',i); if(j<0)j=n;
    if(j>i){
      var isDir=text.charCodeAt(i)===68; // 'D'
      var p=text.substring(i+1,j), parts=p.split('/'), curN=root;
      for(var k=0;k<parts.length-1;k++){
        var s=parts[k], nx=curN.c[s];
        if(!nx){nx={c:{},f:[]};curN.c[s]=nx;}
        curN=nx;
      }
      var last=parts[parts.length-1];
      if(isDir){ if(!curN.c[last])curN.c[last]={c:{},f:[]}; }
      else curN.f.push(last);
    }
    i=j+1;
  }
  return root;
}

function countAll(node){var c=node.f.length;for(var k in node.c)c+=countAll(node.c[k]);return c;}

function renderBar(){
  byId('drivebar').innerHTML=DRIVES.map(function(d){
    return ""<button class='db""+(d.i===cur?' on':'')+""' data-di='""+d.i+""'>""+d.n+'번 하드'+(d.dead?' 🚫':'')+'</button>';
  }).join('');
}

function renderNode(node,path,depth){
  var html='', dirs=Object.keys(node.c).sort();
  dirs.forEach(function(name){
    var p=path?path+'/'+name:name, open=openState[p], ri=_rows.length;
    _rows.push(p);
    html+=""<div class='row dir' style='padding-left:""+(depth*14+8)+""px' data-ri='""+ri+""'>""
      +(open?'▾':'▸')+' 📁 '+esc(name)+""<span class='cnt'>""+countAll(node.c[name])+'</span></div>';
    if(open) html+=renderNode(node.c[name],p,depth+1);
  });
  node.f.slice().sort().forEach(function(fn){
    html+=""<div class='row file' style='padding-left:""+(depth*14+24)+""px'>📄 ""+esc(fn)+'</div>';
  });
  return html;
}

function renderTree(){
  if(!curTree){byId('tree').innerHTML=""<div class='no-res'>데이터 없음</div>"";return;}
  var d=DRIVES[cur];
  setInfo(d.n+'번 하드 · '+countAll(curTree).toLocaleString()+'개 항목');
  _rows=[];
  byId('tree').innerHTML=renderNode(curTree,'',0)||""<div class='no-res'>비어있음</div>"";
}

async function selDrive(i){
  if(busy)return; busy=true;
  try{
    cur=i; openState={}; byId('q').value=''; renderBar();
    // 고장(스캔 불가)으로 등록된 하드 — 목록이 없는 이유를 분명히 알린다(스캔 누락이 아님)
    if(DRIVES[i].dead){
      curText=null; curTree=null;
      setInfo(DRIVES[i].n+'번 하드 · 파일 목록 없음');
      byId('tree').innerHTML=""<div class='err'>🚫 <b>""+esc(DRIVES[i].name)+""</b> — 고장(스캔 불가)으로 등록된 하드입니다.<br><span style='font-size:11px;color:#999'>읽을 수 없어 번호와 이름만 기록되어 있습니다. 스캔이 누락된 것이 아닙니다.</span></div>"";
      return;
    }
    setInfo(DRIVES[i].n+'번 하드 불러오는 중...');
    byId('tree').innerHTML='';
    curText=await inflate(i);      // 이 드라이브만 해제 (메모리 절약)
    curTree=buildTree(curText);
    renderTree();
  }catch(e){ byId('tree').innerHTML=""<div class='err'>불러오기 실패: ""+esc(e.message)+'</div>'; }
  finally{ busy=false; }
}

function tog(p){openState[p]=!openState[p];renderTree();}

function goTo(di,p){
  byId('q').value='';
  var parts=p.split('/'), cum='';
  var apply=function(){
    openState={};
    for(var i=0;i<parts.length;i++){cum=cum?cum+'/'+parts[i]:parts[i];openState[cum]=true;}
    renderTree(); window.scrollTo(0,0);
  };
  if(di===cur && curTree){ apply(); }
  else { selDrive(di).then(apply); }
}

// 전체 드라이브 검색 — 드라이브를 하나씩 해제해 훑고 즉시 버린다(메모리 = 1개분).
// 중간에 끊지 않고 **모든 하드를 끝까지 순회**한다(하드당 상한만 둔다). 그래야 뒤쪽 하드의
// 결과가 빠지지 않는다. 정렬은 폴더 먼저, 그다음 관련도(정확>시작>포함)·짧은 이름 순.
// **이름 일치**와 **경로(상위 폴더)만 일치**를 따로 모은다.
// 섞어서 모으면 ① 상위 폴더가 검색어인 하위 항목(이름엔 검색어가 없음)이 이름 부분일치와 같은
// 등급으로 끼어들어 순서가 뒤죽박죽이 되고, ② 그런 항목이 하드당 상한을 잡아먹어 진짜 이름
// 일치 결과가 밀려난다.
async function searchAll(q){
  var ql=q.toLowerCase(), nameOut=[], pathOut=[];
  var PER_NAME=400, PER_PATH=60, TOTAL=4000;
  for(var di=0; di<DRIVES.length; di++){
    if(DRIVES[di].dead) continue;   // 고장 하드는 목록이 없으므로 건너뜀
    setInfo('검색 중... ('+(di+1)+'/'+DRIVES.length+' 하드, '+nameOut.length+'개 발견)');
    var text = (di===cur && curText) ? curText : await inflate(di);
    var i=0, n=text.length, nHit=0, pHit=0;
    while(i<n){
      var j=text.indexOf('\n',i); if(j<0)j=n;
      if(j>i){
        var line=text.substring(i,j);
        if(line.toLowerCase().indexOf(ql,1)>0){
          var p=line.substring(1), nm=p.substring(p.lastIndexOf('/')+1);
          var item={di:di, dir:line.charCodeAt(0)===68, p:p, n:nm};
          if(nm.toLowerCase().indexOf(ql)>=0){ if(nHit<PER_NAME){nameOut.push(item);nHit++;} }
          else { if(pHit<PER_PATH){pathOut.push(item);pHit++;} }
        }
      }
      i=j+1;
      if((nHit>=PER_NAME&&pHit>=PER_PATH) || nameOut.length>=TOTAL)break;  // 이 하드만 중단
    }
    if(nameOut.length>=TOTAL)break;
    await new Promise(function(r){setTimeout(r,0);});  // 진행 표시가 멈추지 않게 양보
  }
  // 이름 일치: 폴더 먼저 → 정확 > 앞부분 > 부분 → 짧은 이름
  function scr(s){s=s.toLowerCase(); if(s===ql)return 0; if(s.indexOf(ql)===0)return 1; return 2;}
  nameOut.sort(function(a,b){
    return (b.dir-a.dir) || (scr(a.n)-scr(b.n)) || (a.n.length-b.n.length) || a.n.localeCompare(b.n,'ko');
  });
  // 경로만 일치(상위 폴더가 검색어인 하위 항목): 항상 이름 일치 뒤에
  pathOut.sort(function(a,b){ return (b.dir-a.dir) || a.p.localeCompare(b.p,'ko'); });
  for(var k=0;k<pathOut.length;k++) pathOut[k].viaPath=true;
  return nameOut.concat(pathOut);
}

var _seq=0;
async function onSearch(q){
  q=(q||'').trim();
  if(!q){ renderBar(); renderTree(); return; }
  if(q.length<2){ setInfo('2글자 이상 입력하세요'); return; }
  var my=++_seq;
  setInfo('검색 준비...');
  var all=await searchAll(q);
  if(my!==_seq)return;   // 입력이 바뀌면 결과 버림
  var nd=0,np=0;
  for(var k=0;k<all.length;k++){ if(all[k].dir)nd++; if(all[k].viaPath)np++; }
  var res=all.slice(0,500);
  _rows=[];
  setInfo('폴더 '+nd+'개 · 파일 '+(all.length-nd)+'개'
    +(np?' (이름 일치 '+(all.length-np)+' + 상위폴더 일치 '+np+')':'')
    +(all.length>500?' · 상위 500 표시':''));
  byId('tree').innerHTML = res.length ? res.map(function(f){
    var ic=f.dir?'📁':'📄', extra='';
    if(f.dir){ var ri=_rows.length; _rows.push({di:f.di,p:f.p}); extra="" data-nav='""+ri+""'""; }
    var via=f.viaPath?"" <span class='via'>상위폴더 일치</span>"":'';
    return ""<div class='sr""+(f.dir?' dir':'')+""'""+extra+""><div class='srn'>""+ic+' '+hl(f.n,q)
      +"" <span class='badge'>[""+DRIVES[f.di].n+""번]</span>""+via+""</div><div class='rp'>""+esc(f.p)+'</div></div>';
  }).join('') : ""<div class='no-res'>결과 없음</div>"";
}

// 이벤트 위임 — 경로를 HTML 속성에 직접 넣지 않아 따옴표 이스케이프 문제가 없다
byId('drivebar').addEventListener('click',function(e){
  var b=e.target.closest('.db'); if(b) selDrive(Number(b.dataset.di));
});
byId('tree').addEventListener('click',function(e){
  var nav=e.target.closest('[data-nav]');
  if(nav){ var t=_rows[Number(nav.dataset.nav)]; goTo(t.di,t.p); return; }
  var r=e.target.closest('[data-ri]');
  if(r) tog(_rows[Number(r.dataset.ri)]);
});
var _t;
byId('q').addEventListener('input',function(e){
  clearTimeout(_t); var v=e.target.value;
  _t=setTimeout(function(){ onSearch(v); },350);
});

var boot=byId('boot'); if(boot) boot.remove();   // 정적 '준비 중' 안내 제거 (여기까지 왔으면 JS 동작)
// 이 스크립트는 payload보다 **앞**에 있으므로 하드 버튼은 즉시 그린다.
// 목록(payload)은 문서가 더 읽혀야 도착하므로, 다 읽힌 뒤(DOMContentLoaded)에 첫 하드를 연다.
renderBar();
if(noZip){
  byId('tree').innerHTML=""<div class='err'>이 브라우저는 압축 해제를 지원하지 않습니다.<br>크롬·엣지·삼성인터넷 최신 버전 또는 사파리 16.4 이상에서 열어주세요.</div>"";
} else if(!DRIVES.length){ setInfo('드라이브가 없습니다'); }
else {
  setInfo('목록을 읽는 중... (' + DRIVES.length + '개 하드)');
  var openFirst=function(){ var b=byId('boot'); if(b)b.remove(); selDrive(0); };
  if(document.readyState==='loading') document.addEventListener('DOMContentLoaded', openFirst);
  else openFirst();
}
</script>";

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

        public long FileSizeBytes
        {
            get { try { return new FileInfo(Path).Length; } catch { return 0; } }
        }

        // 이 크기를 넘으면 종료 시 자동 백업을 하지 않는다.
        // 전체 복사는 DB 크기만큼 쓰기가 일어나 2.5GB에서는 수 분이 걸리고, 그 동안 창이 닫히지 않는다.
        // 대용량은 사용자가 툴바의 [백업] 버튼으로 원하는 시점에 수행한다.
        public const long AutoBackupMaxBytes = 1L * 1024 * 1024 * 1024;

        // 마지막 백업 이후 변경이 있을 때만 백업 (종료 시 1회 호출용).
        // 반환값 false = 건너뜀(대용량 또는 다른 작업 진행 중).
        public bool BackupIfDirty()
        {
            if (!_dirtySinceBackup) return true;
            if (FileSizeBytes > AutoBackupMaxBytes) return false;
            // 내보내기 등 긴 작업이 락을 잡고 있으면 종료를 붙잡지 않고 건너뛴다
            if (!System.Threading.Monitor.TryEnter(_gate, 1000)) return false;
            try
            {
                var dest = Path + ".bak";
                using var d = new SqliteConnection($"Data Source={dest}");
                d.Open();
                _conn.BackupDatabase(d);
                _dirtySinceBackup = false;
                return true;
            }
            catch { return false; }
            finally { System.Threading.Monitor.Exit(_gate); }
        }

        public void Dispose()
        {
            // ⚠️ lock(_gate)로 무한 대기하면, 뷰어 내보내기·CSV 같은 긴 작업 중에 창을 닫을 때
            // 앱이 닫히지 않는다. 짧게만 기다리고 못 잡으면 정리를 건너뛴다
            // (WAL은 다음 실행 때 SQLite가 복구하므로 데이터가 깨지지 않는다).
            bool got = System.Threading.Monitor.TryEnter(_gate, 3000);
            if (!got) return;
            try
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
            finally { System.Threading.Monitor.Exit(_gate); }
        }
    }
}
