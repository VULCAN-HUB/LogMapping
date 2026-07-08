using DiscUtils.HfsPlus;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LogMapping
{
    public partial class MainWindow : Window
    {
        private readonly string _appDir = AppDomain.CurrentDomain.BaseDirectory;
        // 데이터(.hcat)는 exe 옆에, WebView2 캐시·추출 리소스는 로컬에
        private string DataDir => Path.Combine(_appDir, "data");
        private static readonly string _localBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogMapping");

        public MainWindow()
        {
            InitializeComponent();
            InitWebView();
        }

        private async void InitWebView()
        {
            Directory.CreateDirectory(DataDir);

            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(_localBase, ".webview2"));
                await webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                     || ex.Message.Contains("WebView2")
                                     || ex.Message.Contains("Edge")
                                     || ex.HResult == unchecked((int)0x80004005))
            {
                MessageBox.Show(
                    "Microsoft WebView2 런타임이 설치되어 있지 않습니다.\n\n" +
                    "LogMapping을 실행하려면 아래 링크에서\n" +
                    "WebView2 런타임을 먼저 설치해 주세요.\n\n" +
                    "https://aka.ms/webview2\n\n" +
                    "(Microsoft Edge가 설치된 PC에서는 이미 포함되어 있습니다)",
                    "WebView2 런타임 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Application.Current.Shutdown();
                return;
            }

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "logmapping.app", _localBase, CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.AddHostObjectToScript("nativeBridge", new NativeBridge(_appDir, DataDir));
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // DOM 로드 후 경로 주입
            webView.CoreWebView2.DOMContentLoaded += async (s, e) =>
            {
                var appDirEscaped = _appDir.Replace("\\", "\\\\");
                var dataDirEscaped = DataDir.Replace("\\", "\\\\");
                var script = "window._APP_DIR='" + appDirEscaped + "'; window._DATA_DIR='" + dataDirEscaped + "'; if(typeof APP_DIR!=='undefined'){APP_DIR=window._APP_DIR;DATA_DIR=window._DATA_DIR;}";
                await webView.CoreWebView2.ExecuteScriptAsync(script);
            };

            Closing += OnWindowClosing;

            ExtractResources();
            webView.CoreWebView2.Navigate("https://logmapping.app/app/index.html");
        }

        private void ExtractResources()
        {
            // 로컬 캐시에 추출 — USB에서 실행해도 빠르게 로드됨
            var appDir2 = Path.Combine(_localBase, "app");
            Directory.CreateDirectory(appDir2);

            var asm = Assembly.GetExecutingAssembly();
            var resources = new[] { "index.html", "scanner.js" };

            foreach (var res in resources)
            {
                var resourceName = "LogMapping.resources." + res;
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                var destPath = Path.Combine(appDir2, res);
                // 이미 같은 크기면 재추출 스킵
                if (File.Exists(destPath) && new FileInfo(destPath).Length == stream.Length) continue;
                using var fs = File.Create(destPath);
                stream.CopyTo(fs);
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (msg == null) return;

                switch (msg.Type)
                {
                    case "readFile": HandleReadFile(msg); break;
                    case "writeFile": HandleWriteFile(msg); break;
                    case "listFiles": HandleListFiles(msg); break;
                    case "scanFolder": HandleScanFolder(msg); break;
                    case "openFileDialog": HandleOpenFileDialog(msg); break;
                    case "saveFileDialog": HandleSaveFileDialog(msg); break;
                    case "listDrives": HandleListDrives(msg); break;
                    case "openFolderDialog": HandleOpenFolderDialog(msg); break;
                    case "copyToClipboard": HandleCopyToClipboard(msg); break;
                    case "openUrl": HandleOpenUrl(msg); break;
                    case "closeWindow": Application.Current.Dispatcher.Invoke(() => Close()); break;
                    // ── SQLite ──
                    case "dbScan": HandleDbScan(msg); break;
                    case "cancelScan": HandleCancelScan(msg); break;
                    case "dbUpdateDrive": HandleDbUpdateDrive(msg); break;
                    case "dbDeleteDrive": HandleDbDeleteDrive(msg); break;
                    case "dbChildren": HandleDbChildren(msg); break;
                    case "dbSearch": HandleDbSearch(msg); break;
                    case "dbListDrives": HandleDbListDrives(msg); break;
                    case "dbSetColor": HandleDbSetColor(msg); break;
                    case "dbStats": HandleDbStats(msg); break;
                    case "dbExtCounts": HandleDbExtCounts(msg); break;
                    case "dbFilesByColor": HandleDbFilesByColor(msg); break;
                    case "dbPrune": HandleDbPrune(msg); break;
                    case "dbByExts": HandleDbByExts(msg); break;
                    case "dbFolders": HandleDbFolders(msg); break;
                    case "dbExportCsv": HandleDbExportCsv(msg); break;
                    case "dbDuplicates": HandleDbDuplicates(msg); break;
                    case "dbDuplicateStats": HandleDbDuplicateStats(msg); break;
                    case "dbExportViewer": HandleDbExportViewer(msg); break;
                    case "dbBackup": HandleDbBackup(msg); break;
                }
            }
            catch (Exception ex)
            {
                SendToJS("error", new { message = ex.Message });
            }
        }

        private void HandleReadFile(WebMessage msg)
        {
            try
            {
                var path = msg.Path ?? "";
                if (!File.Exists(path)) { SendToJS("readFileResult", new { id = msg.Id, error = "파일 없음", path }); return; }
                var content = File.ReadAllText(path);
                SendToJS("readFileResult", new { id = msg.Id, content, path });
            }
            catch (Exception ex) { SendToJS("readFileResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleWriteFile(WebMessage msg)
        {
            try
            {
                var path = msg.Path ?? "";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, msg.Content ?? "");
                SendToJS("writeFileResult", new { id = msg.Id, success = true, path });
            }
            catch (Exception ex) { SendToJS("writeFileResult", new { id = msg.Id, success = false, error = ex.Message }); }
        }

        private void HandleListFiles(WebMessage msg)
        {
            try
            {
                var dir = msg.Path ?? DataDir;
                if (!Directory.Exists(dir)) { SendToJS("listFilesResult", new { id = msg.Id, files = Array.Empty<object>() }); return; }
                var files = Directory.GetFiles(dir, "*.hcat")
                    .Select(f => new { name = Path.GetFileName(f), path = f, modified = File.GetLastWriteTime(f).ToString("yyyy-MM-dd") })
                    .ToArray();
                SendToJS("listFilesResult", new { id = msg.Id, files });
            }
            catch (Exception ex) { SendToJS("listFilesResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleScanFolder(WebMessage msg)
        {
            var folderPath = msg.Path ?? "";
            Task.Run(() =>
            {
                try
                {
                    var fileCount = 0;
                    List<object> tree;

                    if (folderPath.StartsWith("macvol://"))
                    {
                        tree = WalkMacDrive(folderPath, ref fileCount, count =>
                        {
                            if (count % 1000 == 0)
                                SendToJS("scanProgress", new { id = msg.Id, count });
                        });
                    }
                    else
                    {
                        tree = WalkDir(folderPath, ref fileCount, count =>
                        {
                            if (count % 1000 == 0)
                                SendToJS("scanProgress", new { id = msg.Id, count });
                        });
                    }

                    SendToJS("scanResult", new { id = msg.Id, tree, totalFiles = fileCount });
                }
                catch (Exception ex)
                {
                    SendToJS("scanResult", new { id = msg.Id, error = ex.Message });
                }
            });
        }

        // \\.\PhysicalDriveN 을 FileStream으로 열기 (관리자 권한 필요)
        private static FileStream? OpenPhysicalDrive(int diskNum)
        {
            try
            {
                return new FileStream($"\\\\.\\PhysicalDrive{diskNum}",
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch { return null; }
        }

        // ── HFS+ 볼륨 헤더 읽기 ─────────────────────────────────────────────────────
        // 빅엔디안 32비트 정수
        private static uint BE32(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        // HFSPlusVolumeHeader (파티션 offset + 1024, 전부 빅엔디안):
        //  +0:  signature (2)  'H+'(0x482B) / 'HX'(0x4858)
        //  +40: blockSize  (4)
        //  +44: totalBlocks(4)
        //  +48: freeBlocks (4)
        // 반환: (blockSize, totalBlocks, freeBlocks). 실패/비HFS+ 시 (0,0,0)
        private static (uint blockSize, ulong totalBlocks, ulong freeBlocks)
            ReadHfsPlusHeader(Stream physStream, long partOffset)
        {
            try
            {
                physStream.Position = partOffset + 1024; // 섹터 정렬(2*512)
                var h = new byte[512];                   // 물리 디스크 섹터 단위 읽기
                int done = 0;
                while (done < h.Length)
                {
                    int n = physStream.Read(h, done, h.Length - done);
                    if (n == 0) break;
                    done += n;
                }
                if (done < 52) return (0, 0, 0);
                if (!(h[0] == 0x48 && (h[1] == 0x2B || h[1] == 0x58))) return (0, 0, 0);
                uint  bsz   = BE32(h, 40);
                ulong total = BE32(h, 44);
                ulong free  = BE32(h, 48);
                if (bsz < 512 || bsz > 1024 * 1024) return (0, 0, 0);
                return (bsz, total, free);
            }
            catch { return (0, 0, 0); }
        }

        // macvol://d{diskNum}o{offset}/{volIdx}  — APFS / HFS+
        private List<object> WalkMacDrive(string macPath, ref int fileCount, Action<int> onProgress)
        {
            // 경로 파싱: macvol://d{diskNum}o{offset}/{volIdx}
            var inner = macPath.Replace("macvol://", "");
            var slashIdx = inner.IndexOf('/');
            int volIndex   = slashIdx >= 0 ? int.Parse(inner[(slashIdx + 1)..]) : 0;
            var diskPart   = slashIdx >= 0 ? inner[..slashIdx] : inner;
            var oIdx       = diskPart.IndexOf('o');
            var diskNum    = int.Parse(diskPart[1..oIdx]);
            var partOffset = long.Parse(diskPart[(oIdx + 1)..]);

            using var physStream = OpenPhysicalDrive(diskNum)
                ?? throw new Exception("Mac 드라이브를 열 수 없습니다. 앱을 관리자 권한으로 실행해주세요.");

            // HFS+ 판별: 파티션 내 offset 1024 (= partOffset + 1024, 섹터 정렬)에서 매직 확인
            physStream.Position = partOffset + 1024; // 1024 = 2 * 512 → 섹터 정렬 OK
            var hfsMagicBuf = new byte[4];
            physStream.Read(hfsMagicBuf, 0, 4);
            bool isHfsPlusDrive = (hfsMagicBuf[0] == 0x48 && hfsMagicBuf[1] == 0x2B) ||
                                  (hfsMagicBuf[0] == 0x48 && hfsMagicBuf[1] == 0x58);

            if (!isHfsPlusDrive)
            {
                // APFS: raw 스트림 + 파티션 오프셋 직접 전달
                if (!ApfsReader.Detect(physStream, partOffset))
                    throw new Exception("APFS 또는 HFS+ 파티션을 인식할 수 없습니다.");

                using var apfs = new ApfsReader(physStream, partOffset);
                var volumes = apfs.FindVolumes();

                if (volIndex >= volumes.Count) volIndex = 0;
                if (volumes.Count == 0)
                    throw new Exception("APFS 볼륨을 찾을 수 없습니다.");

                var vol = volumes[volIndex];
                if (vol.OmapPhys == 0)
                    throw new Exception("암호화된 볼륨은 파일 목록을 읽을 수 없습니다.");

                var tree = apfs.WalkVolume(vol.OmapPhys, vol.RootTreeOid);
                CountTree(tree, ref fileCount, onProgress);
                return tree;
            }

            // HFS+: OffsetStream 래퍼로 DiscUtils에 전달
            var hfsStream = new OffsetStream(physStream, partOffset, long.MaxValue / 2);
            hfsStream.Position = 1024;
            var magic = new byte[2];
            hfsStream.Read(magic, 0, 2);
            bool isHfsPlus = (magic[0] == 0x48 && magic[1] == 0x2B) ||
                             (magic[0] == 0x48 && magic[1] == 0x58);
            if (!isHfsPlus)
                throw new Exception("지원하지 않는 Mac 파일시스템입니다.");

            hfsStream.Position = 0;
            using var hfs = new HfsPlusFileSystem(hfsStream);

            // DiscUtils 경로 규약: 백슬래시(\), 루트 "\".
            // 루트 조회 실패는 묵살하지 않고 예외로 띄워 실제 원인(마운트/저널 등)을 표면화한다.
            _ = hfs.GetFileSystemEntries(@"\");

            var rootResult = new List<object>();
            var walkStack = new Stack<(string Path, List<object> Parent)>();
            walkStack.Push((@"\", rootResult));

            while (walkStack.Count > 0)
            {
                var (curPath, parentList) = walkStack.Pop();
                string[] entries;
                try { entries = hfs.GetFileSystemEntries(curPath); }
                catch { continue; }

                // 폴더 목록을 1회 조회로 받아 entry별 DirectoryExists(B-트리 재탐색) 호출 제거
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var d in hfs.GetDirectories(curPath)) dirSet.Add(d); } catch { }

                foreach (var entry in entries)
                {
                    var name = entry.Replace('/', '\\').TrimEnd('\\').Split('\\').LastOrDefault() ?? "";
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;

                    try
                    {
                        if (dirSet.Contains(entry))
                        {
                            var children = new List<object>();
                            parentList.Add(new { type = "dir", name, children });
                            walkStack.Push((entry, children));
                        }
                        else
                        {
                            fileCount++;
                            onProgress(fileCount);
                            var size = 0L;
                            try { size = hfs.GetFileLength(entry) / 1024; } catch { }
                            parentList.Add(new { type = "file", name, size = Math.Max(1, size) });
                        }
                    }
                    catch { }
                }
            }

            return rootResult;
        }

        // ── 스트리밍 스캐너 (트리 미빌드, OOM 방지) ───────────────────────────────
        // 윈도우 드라이브/폴더: 노드를 만들자마자 emit(parentPath,name,isDir,sizeKB,mtime)
        private void WalkDirStream(string dirPath, Action<string, string, bool, long, string> emit)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Path, string Rel)>();
            stack.Push((dirPath, ""));
            while (stack.Count > 0)
            {
                var (curPath, rel) = stack.Pop();
                if (!visited.Add(curPath)) continue; // 이미 방문한 경로 스킵 (정션/심링크 순환 방지)
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(curPath); } catch { continue; }
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
                    if (Directory.Exists(entry))
                    {
                        emit(rel, name, true, 0, "");
                        stack.Push((entry, rel.Length == 0 ? name : rel + "/" + name));
                    }
                    else
                    {
                        long size = 0; string mtime = "";
                        try { var fi = new FileInfo(entry); size = fi.Length / 1024; mtime = fi.LastWriteTime.ToString("yyyy-MM-dd"); } catch { }
                        emit(rel, name, false, Math.Max(1, size), mtime);
                    }
                }
            }
        }

        // Mac 드라이브(APFS/HFS+): 트리 미빌드 스트리밍.
        // 반환값 = (사용 MB, 총 MB). 총 MB는 사용 MB와 같은 헤더·같은 정밀도로 계산해
        // "사용 > 총" 역전(여유 음수·100%+ 초과)을 방지한다. APFS는 총 MB 미보유 → 0 반환(JS가 meta.cap 폴백).
        private (long usedMB, long totalMB) WalkMacDriveStream(string macPath, Action<string, string, bool, long, string> emit)
        {
            var inner = macPath.Replace("macvol://", "");
            var slashIdx = inner.IndexOf('/');
            int volIndex = slashIdx >= 0 ? int.Parse(inner[(slashIdx + 1)..]) : 0;
            var diskPart = slashIdx >= 0 ? inner[..slashIdx] : inner;
            var oIdx = diskPart.IndexOf('o');
            var diskNum = int.Parse(diskPart[1..oIdx]);
            var partOffset = long.Parse(diskPart[(oIdx + 1)..]);

            using var physStream = OpenPhysicalDrive(diskNum)
                ?? throw new Exception("Mac 드라이브를 열 수 없습니다. 앱을 관리자 권한으로 실행해주세요.");

            physStream.Position = partOffset + 1024;
            var hfsMagicBuf = new byte[4];
            physStream.Read(hfsMagicBuf, 0, 4);
            bool isHfsPlusDrive = (hfsMagicBuf[0] == 0x48 && hfsMagicBuf[1] == 0x2B) ||
                                  (hfsMagicBuf[0] == 0x48 && hfsMagicBuf[1] == 0x58);

            if (!isHfsPlusDrive)
            {
                if (!ApfsReader.Detect(physStream, partOffset))
                    throw new Exception("APFS 또는 HFS+ 파티션을 인식할 수 없습니다.");
                using var apfs = new ApfsReader(physStream, partOffset);
                var volumes = apfs.FindVolumes();
                if (volIndex >= volumes.Count) volIndex = 0;
                if (volumes.Count == 0) throw new Exception("APFS 볼륨을 찾을 수 없습니다.");
                var vol = volumes[volIndex];
                if (vol.OmapPhys == 0) throw new Exception("암호화된 볼륨은 파일 목록을 읽을 수 없습니다.");
                apfs.WalkVolumeStream(vol.OmapPhys, vol.RootTreeOid, emit);
                return ((long)vol.UsedMB, 0L);
            }

            var hfsStream = new OffsetStream(physStream, partOffset, long.MaxValue / 2);
            hfsStream.Position = 1024;
            var magic = new byte[2];
            hfsStream.Read(magic, 0, 2);
            bool isHfsPlus = (magic[0] == 0x48 && magic[1] == 0x2B) ||
                             (magic[0] == 0x48 && magic[1] == 0x58);
            if (!isHfsPlus) throw new Exception("지원하지 않는 Mac 파일시스템입니다.");

            // HFS+ 총·사용 용량 계산 (볼륨 헤더, 빅엔디안). raw physStream에서 읽음.
            // 총·사용 둘 다 같은 헤더값(htotal/hfree/hbsz)에서 MB 단위로 산출 → free = 총-사용 = hfree*bsz ≥ 0 보장.
            long hfsUsedMB = 0;
            long hfsTotalMB = 0;
            var (hbsz, htotal, hfree) = ReadHfsPlusHeader(physStream, partOffset);
            if (hbsz > 0 && htotal >= hfree)
            {
                hfsUsedMB  = (long)((htotal - hfree) * (ulong)hbsz / 1024 / 1024);
                hfsTotalMB = (long)(htotal * (ulong)hbsz / 1024 / 1024);
            }

            hfsStream.Position = 0;
            using var hfs = new HfsPlusFileSystem(hfsStream);

            // DiscUtils 경로 규약: 백슬래시(\), 루트 "\". 루트 실패는 묵살하지 않고 표면화.
            _ = hfs.GetFileSystemEntries(@"\");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Path, string Rel)>();
            stack.Push((@"\", ""));
            while (stack.Count > 0)
            {
                var (curPath, rel) = stack.Pop();
                if (!visited.Add(curPath)) continue; // 하드링크 순환 방지
                string[] entries;
                try { entries = hfs.GetFileSystemEntries(curPath); } catch { continue; }
                // 폴더 목록을 1회 조회로 받아 entry별 DirectoryExists(B-트리 재탐색) 호출 제거
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var d in hfs.GetDirectories(curPath)) dirSet.Add(d); } catch { }
                foreach (var entry in entries)
                {
                    var name = entry.Replace('/', '\\').TrimEnd('\\').Split('\\').LastOrDefault() ?? "";
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
                    try
                    {
                        if (dirSet.Contains(entry))
                        {
                            emit(rel, name, true, 0, "");
                            stack.Push((entry, rel.Length == 0 ? name : rel + "/" + name));
                        }
                        else
                        {
                            long size = 0; string mtime = "";
                            try { size = hfs.GetFileLength(entry) / 1024; } catch { }
                            try { mtime = hfs.GetLastWriteTime(entry).ToString("yyyy-MM-dd"); } catch { }
                            emit(rel, name, false, Math.Max(1, size), mtime);
                        }
                    }
                    catch { }
                }
            }
            return (hfsUsedMB, hfsTotalMB);
        }

        private static void CountTree(List<object> tree, ref int fileCount, Action<int> onProgress)
        {
            foreach (var node in tree)
            {
                var type = node.GetType().GetProperty("type")?.GetValue(node) as string;
                if (type == "file")
                {
                    fileCount++;
                    onProgress(fileCount);
                }
                else if (type == "dir")
                {
                    var children = node.GetType().GetProperty("children")?.GetValue(node) as List<object>;
                    if (children != null) CountTree(children, ref fileCount, onProgress);
                }
            }
        }

        private List<object> WalkDir(string dirPath, ref int fileCount, Action<int> onProgress)
        {
            // 재귀 대신 명시적 스택 사용 — 깊은 디렉토리에서 StackOverflowException 방지
            var rootResult = new List<object>();
            var stack = new Stack<(string Path, List<object> Parent)>();
            stack.Push((dirPath, rootResult));

            while (stack.Count > 0)
            {
                var (curPath, parentList) = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(curPath); }
                catch { continue; }

                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;

                    if (Directory.Exists(entry))
                    {
                        var children = new List<object>();
                        parentList.Add(new { type = "dir", name, children });
                        stack.Push((entry, children));
                    }
                    else
                    {
                        fileCount++;
                        onProgress(fileCount);
                        var size = 0L; var mtime = "";
                        try { var fi = new FileInfo(entry); size = fi.Length / 1024; mtime = fi.LastWriteTime.ToString("yyyy-MM-dd"); } catch { }
                        parentList.Add(new { type = "file", name, size = Math.Max(1, size), mtime });
                    }
                }
            }
            return rootResult;
        }

        private void HandleOpenFileDialog(WebMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "HCat 파일 (*.hcat)|*.hcat|모든 파일 (*.*)|*.*",
                    Title = "카탈로그 파일 열기"
                };
                if (dlg.ShowDialog() == true)
                    SendToJS("openFileDialogResult", new { id = msg.Id, path = dlg.FileName, cancelled = false });
                else
                    SendToJS("openFileDialogResult", new { id = msg.Id, cancelled = true });
            });
        }

        private void HandleSaveFileDialog(WebMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "HCat 파일 (*.hcat)|*.hcat",
                    Title = "카탈로그 저장",
                    FileName = msg.FileName ?? "my_drives.hcat"
                };
                if (dlg.ShowDialog() == true)
                    SendToJS("saveFileDialogResult", new { id = msg.Id, path = dlg.FileName, cancelled = false });
                else
                    SendToJS("saveFileDialogResult", new { id = msg.Id, cancelled = true });
            });
        }

        // 볼륨 일련번호(Volume Serial Number) — 드라이브 문자가 아닌 안정적 식별자.
        // 외장하드를 같은 문자(예: E:\)에 바꿔 꽂아도 볼륨마다 값이 달라, 서로 다른 드라이브를
        // 같은 항목으로 오인해 덮어쓰는 버그를 막는다.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(
            string rootPathName, System.Text.StringBuilder? volumeNameBuffer, int volumeNameSize,
            out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
            System.Text.StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

        private static string? GetVolumeSerial(string rootPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rootPath)) return null;
                if (!rootPath.EndsWith("\\")) rootPath += "\\";
                if (GetVolumeInformation(rootPath, null, 0, out uint vsn, out _, out _, null, 0) && vsn != 0)
                    return "VSN-" + vsn.ToString("X8");
                return null;
            }
            catch { return null; }
        }

        private void HandleListDrives(WebMessage msg)
        {
            // 1) Windows 드라이브 즉시 응답
            var drives = new List<object>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady || d.DriveType == DriveType.CDRom) continue;
                        if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                        drives.Add(new
                        {
                            path = d.RootDirectory.FullName,
                            label = d.Name.TrimEnd('\\') + " - " + (string.IsNullOrEmpty(d.VolumeLabel) ? "드라이브" : d.VolumeLabel),
                            totalGB = (int)(d.TotalSize / 1024 / 1024 / 1024),
                            freeGB = (int)(d.AvailableFreeSpace / 1024 / 1024 / 1024),
                            driveType = d.DriveType.ToString(),
                            isMac = false,
                            fsType = (string?)null,
                            volumeId = GetVolumeSerial(d.RootDirectory.FullName)
                        });
                    }
                    catch { }
                }
            }
            catch { }

            SendToJS("listDrivesResult", new { id = msg.Id, drives = drives.ToArray() });

            // 2) Mac 파티션은 백그라운드 스캔 후 별도 푸시
            Task.Run(() =>
            {
                try
                {
                    var macDrives = FindMacPartitions().ToList();
                    if (macDrives.Count > 0)
                        SendToJS("macDrivesFound", new { drives = macDrives.ToArray() });
                }
                catch { }
            });
        }

        // GPT 파티션 타입 GUID로 Mac 드라이브 탐지 (관리자 권한 불필요)
        // Apple APFS : 7C3457EF-0000-11AA-AA11-00306543ECAC
        // Apple HFS+ : 48465300-0000-11AA-AA11-00306543ECAC
        // macvol 경로 형식: macvol://d{diskNum}o{offsetBytes}/{volIndex}
        private IEnumerable<object> FindMacPartitions()
        {
            var result = new List<object>();
            try
            {
                // 물리 디스크 크기를 DiskNumber → 실제 크기(GB)로 미리 수집
                var diskSizeGB = new Dictionary<int, int>();
                try
                {
                    var diskScope = new ManagementScope(@"\\.\Root\Microsoft\Windows\Storage");
                    diskScope.Connect();
                    using var diskSearcher = new ManagementObjectSearcher(
                        diskScope,
                        new ObjectQuery("SELECT Number, Size FROM MSFT_Disk"));
                    foreach (ManagementObject d in diskSearcher.Get().Cast<ManagementObject>())
                    {
                        var num  = Convert.ToInt32(d["Number"]);
                        var size = Convert.ToInt64(d["Size"] ?? 0L);
                        diskSizeGB[num] = (int)(size / 1024 / 1024 / 1024);
                    }
                }
                catch { }

                var scope = new ManagementScope(@"\\.\Root\Microsoft\Windows\Storage");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT DiskNumber, PartitionNumber, Offset, Size, GptType FROM MSFT_Partition"));

                var seen = new HashSet<int>(); // 같은 디스크에서 APFS 파티션이 여러 개일 때 중복 방지
                foreach (ManagementObject p in searcher.Get().Cast<ManagementObject>())
                {
                    var gptType = (p["GptType"]?.ToString() ?? "").Trim('{', '}');
                    string fsType;
                    if (gptType.Equals("7C3457EF-0000-11AA-AA11-00306543ECAC", StringComparison.OrdinalIgnoreCase))
                        fsType = "APFS";
                    else if (gptType.Equals("48465300-0000-11AA-AA11-00306543ECAC", StringComparison.OrdinalIgnoreCase))
                        fsType = "HFS+";
                    else
                        continue;

                    var diskNum    = Convert.ToInt32(p["DiskNumber"]);
                    var partOffset = Convert.ToInt64(p["Offset"] ?? 0L);

                    // APFS 슈퍼블록 / HFS+ 볼륨 헤더에서 실제 볼륨 크기 읽기 (WMI 물리 디스크 크기는 신뢰 불가)
                    int totalGB = 0;
                    int freeGB  = 0;
                    try
                    {
                        using var physStream = new FileStream(
                            $@"\\.\PhysicalDrive{diskNum}",
                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                            bufferSize: 512, useAsync: false);
                        if (ApfsReader.Detect(physStream, partOffset))
                        {
                            physStream.Position = partOffset + 36;
                            var hdr = new byte[12];
                            physStream.Read(hdr, 0, 12);
                            uint  bsz    = BitConverter.ToUInt32(hdr, 0);       // nx_block_size
                            ulong nblk   = BitConverter.ToUInt64(hdr, 4);       // nx_block_count
                            if (bsz >= 512 && bsz <= 65536 && nblk > 0)
                                totalGB = (int)(nblk * bsz / 1024 / 1024 / 1024);
                        }
                        else
                        {
                            // HFS+ 볼륨 헤더에서 총/여유 용량 (빅엔디안)
                            var (bsz, total, free) = ReadHfsPlusHeader(physStream, partOffset);
                            if (bsz > 0 && total > 0)
                            {
                                totalGB = (int)(total * (ulong)bsz / 1024 / 1024 / 1024);
                                if (total >= free)
                                    freeGB = (int)(free * (ulong)bsz / 1024 / 1024 / 1024);
                            }
                        }
                    }
                    catch { }
                    if (totalGB == 0)
                        totalGB = diskSizeGB.TryGetValue(diskNum, out int dGB) ? dGB : 0;

                    string MakeMacVolPath(int vi) => $"macvol://d{diskNum}o{partOffset}/{vi}";

                    result.Add(new
                    {
                        path      = MakeMacVolPath(0),
                        label     = $"[{fsType}] Mac 드라이브 (Disk {diskNum})",
                        totalGB,
                        freeGB,
                        driveType = "Removable",
                        isMac     = true,
                        fsType,
                        volumeId  = (string?)MakeMacVolPath(0)  // Mac은 경로를 식별자로 폴백(현행 유지). 추후 볼륨 UUID로 개선 여지
                    });
                }
            }
            catch { }

            return result;
        }

        private void HandleOpenFolderDialog(WebMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "스캔할 폴더 선택 (NAS · 네트워크 드라이브 포함)"
                };
                if (dlg.ShowDialog() == true)
                    SendToJS("openFolderDialogResult", new { id = msg.Id, path = dlg.FolderName, cancelled = false });
                else
                    SendToJS("openFolderDialogResult", new { id = msg.Id, cancelled = true });
            });
        }

        private void HandleCopyToClipboard(WebMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Clipboard.SetText(msg.Content ?? "");
                SendToJS("copyToClipboardResult", new { id = msg.Id, success = true });
            });
        }

        private void HandleOpenUrl(WebMessage msg)
        {
            var url = msg.Path ?? "";
            if (!url.StartsWith("https://") && !url.StartsWith("http://")) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }

        // ── SQLite 카탈로그 ───────────────────────────────────────────────────────
        private CatalogDb? _db;
        private readonly object _dbLock = new();
        private CancellationTokenSource? _scanCts;
        private CatalogDb Db()
        {
            if (_db == null)
                lock (_dbLock) { _db ??= new CatalogDb(Path.Combine(DataDir, "catalog.db")); }
            return _db;
        }

        private void HandleDbScan(WebMessage msg)
        {
            // 이전 스캔이 아직 진행 중이면 먼저 취소
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var cts = _scanCts;

            var folderPath = msg.Path ?? "";
            Task.Run(() =>
            {
                long driveId = -1;
                try
                {
                    var db = Db();
                    // 경로 기준 삭제 금지: 같은 드라이브 문자(예: E:\)를 다른 하드가 재사용하면
                    // 먼저 스캔한 드라이브의 파일이 통째로 지워지는 버그가 있었음.
                    // 드라이브 식별·기존분 정리는 JS가 volumeId로 처리(재스캔 시 dbDeleteDrive 호출),
                    // 정리 누락분(고아)은 로드 시 dbPrune로 청소한다.
                    driveId = db.CreateDrive(folderPath);

                    // 스트리밍 스캔: 트리를 메모리에 쌓지 않고 노드를 만들자마자 DB에 INSERT
                    int pc = 0;
                    long macUsedMB = 0;
                    long macTotalMB = 0;
                    db.InsertStreaming(driveId, dbEmit =>
                    {
                        Action<string, string, bool, long, string> emit = (pp, n, isDir, sz, mt) =>
                        {
                            cts.Token.ThrowIfCancellationRequested(); // 취소 요청 시 즉시 중단
                            dbEmit(pp, n, isDir, sz, mt);
                            if (!isDir && (++pc % 10 == 0)) SendToJS("scanProgress", new { id = msg.Id, count = pc });
                        };
                        if (folderPath.StartsWith("macvol://")) (macUsedMB, macTotalMB) = WalkMacDriveStream(folderPath, emit);
                        else WalkDirStream(folderPath, emit);
                    });

                    var statsJson = db.DriveStatsJson(driveId);
                    var rootJson = db.GetChildrenJson(driveId, "");
                    SendToJS("dbScanResult", new { id = msg.Id, driveId, statsJson, rootJson, usedMB = macUsedMB, totalMB = macTotalMB });
                }
                catch (OperationCanceledException)
                {
                    // 취소: 부분적으로 삽입된 데이터 정리
                    if (driveId >= 0) try { Db().DeleteDrive(driveId); } catch { }
                    SendToJS("dbScanResult", new { id = msg.Id, error = "cancelled" });
                }
                catch (Exception ex) { SendToJS("dbScanResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleCancelScan(WebMessage msg)
        {
            _scanCts?.Cancel();
            SendToJS("cancelScanResult", new { id = msg.Id, success = true });
        }

        private void HandleDbUpdateDrive(WebMessage msg)
        {
            try
            {
                if (!msg.Meta.HasValue) { SendToJS("dbUpdateDriveResult", new { id = msg.Id, success = false, error = "meta 없음" }); return; }
                long driveId = Db().UpsertDrive(msg.Meta.Value);
                SendToJS("dbUpdateDriveResult", new { id = msg.Id, driveId, success = true });
            }
            catch (Exception ex) { SendToJS("dbUpdateDriveResult", new { id = msg.Id, success = false, error = ex.Message }); }
        }

        private void HandleDbDeleteDrive(WebMessage msg)
        {
            try { Db().DeleteDrive(msg.DriveId); SendToJS("dbDeleteDriveResult", new { id = msg.Id, success = true }); }
            catch (Exception ex) { SendToJS("dbDeleteDriveResult", new { id = msg.Id, success = false, error = ex.Message }); }
        }

        private void HandleDbChildren(WebMessage msg)
        {
            try { var rowsJson = Db().GetChildrenJson(msg.DriveId, msg.ParentPath ?? ""); SendToJS("dbChildrenResult", new { id = msg.Id, rowsJson }); }
            catch (Exception ex) { SendToJS("dbChildrenResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleDbSearch(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().SearchJson(msg.Query ?? "", msg.Limit > 0 ? msg.Limit : 500); SendToJS("dbSearchResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbSearchResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbListDrives(WebMessage msg)
        {
            try { var rowsJson = Db().ListDrivesJson(); SendToJS("dbListDrivesResult", new { id = msg.Id, rowsJson }); }
            catch (Exception ex) { SendToJS("dbListDrivesResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleDbSetColor(WebMessage msg)
        {
            try { Db().SetItemColor(msg.DriveId, msg.Path ?? "", string.IsNullOrEmpty(msg.Color) ? null : msg.Color); SendToJS("dbSetColorResult", new { id = msg.Id, success = true }); }
            catch (Exception ex) { SendToJS("dbSetColorResult", new { id = msg.Id, success = false, error = ex.Message }); }
        }

        private void HandleDbStats(WebMessage msg)
        {
            try { var rowsJson = Db().DriveStatsJson(msg.DriveId); SendToJS("dbStatsResult", new { id = msg.Id, rowsJson }); }
            catch (Exception ex) { SendToJS("dbStatsResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleDbExtCounts(WebMessage msg)
        {
            try { var rowsJson = Db().ExtCountsJson(msg.DriveId); SendToJS("dbExtCountsResult", new { id = msg.Id, rowsJson }); }
            catch (Exception ex) { SendToJS("dbExtCountsResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleDbFilesByColor(WebMessage msg)
        {
            try { var rowsJson = Db().FilesByColorJson(msg.DriveId, msg.Color ?? "", msg.Limit > 0 ? msg.Limit : 1000); SendToJS("dbFilesByColorResult", new { id = msg.Id, rowsJson }); }
            catch (Exception ex) { SendToJS("dbFilesByColorResult", new { id = msg.Id, error = ex.Message }); }
        }

        private void HandleDbBackup(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { Db().BackupSelf(); SendToJS("dbBackupResult", new { id = msg.Id, success = true }); }
                catch (Exception ex) { SendToJS("dbBackupResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        private void HandleDbExportViewer(WebMessage msg)
        {
            Task.Run(() =>
            {
                try
                {
                    var files = Db().ExportViewer(msg.Path ?? "");
                    SendToJS("dbExportViewerResult", new { id = msg.Id, success = true, count = files.Count, files = files.ToArray() });
                }
                catch (Exception ex) { SendToJS("dbExportViewerResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        private void HandleDbDuplicates(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().DuplicatesJson(msg.Limit > 0 ? msg.Limit : 5000); SendToJS("dbDuplicatesResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbDuplicatesResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbDuplicateStats(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().DuplicateStatsJson(); SendToJS("dbDuplicateStatsResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbDuplicateStatsResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbExportCsv(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { Db().ExportCsv(msg.Path ?? ""); SendToJS("dbExportCsvResult", new { id = msg.Id, success = true }); }
                catch (Exception ex) { SendToJS("dbExportCsvResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        private void HandleDbByExts(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().FilesByExtsJson(msg.DriveId, msg.Query ?? "", msg.Limit > 0 ? msg.Limit : 5000); SendToJS("dbByExtsResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbByExtsResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbFolders(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().FoldersByDriveJson(msg.DriveId, msg.Limit > 0 ? msg.Limit : 50000); SendToJS("dbFoldersResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbFoldersResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbPrune(WebMessage msg)
        {
            try
            {
                var ids = new List<long>();
                foreach (var part in (msg.Query ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (long.TryParse(part.Trim(), out var v)) ids.Add(v);
                Db().PruneDrives(ids);
                SendToJS("dbPruneResult", new { id = msg.Id, success = true });
            }
            catch (Exception ex) { SendToJS("dbPruneResult", new { id = msg.Id, success = false, error = ex.Message }); }
        }

        private void SendToJS(string type, object data)
        {
            var json = JsonSerializer.Serialize(new { type, data });
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                webView.CoreWebView2?.PostWebMessageAsJson(json);
            });
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SendToJS("windowClosing", new { });
            // DEF-006: 백업은 매 저장이 아니라 종료 시 1회, 변경이 있었을 때만 수행
            try { _db?.BackupIfDirty(); } catch { }
            // WAL 체크포인트 + 연결 정상 종료 (손상 예방)
            try { _db?.Dispose(); _db = null; } catch { }
        }
    }

    public class WebMessage
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public string? Path { get; set; }
        public string? Content { get; set; }
        public string? FileName { get; set; }
        // SQLite 관련
        public long DriveId { get; set; }
        public string? ParentPath { get; set; }
        public string? Query { get; set; }
        public string? Color { get; set; }
        public int Limit { get; set; }
        public JsonElement? Meta { get; set; }
    }

    [System.Runtime.InteropServices.ComVisible(true)]
    public class NativeBridge
    {
        private readonly string _appDir;
        private readonly string _dataDir;
        public NativeBridge(string appDir, string dataDir) { _appDir = appDir; _dataDir = dataDir; }
        public string GetAppDir() => _appDir;
        public string GetDataDir() => _dataDir;
    }

    // 물리 디스크 스트림에서 파티션 오프셋 구간만 노출하는 래퍼
    // 64KB 블록 캐시: 매 Read마다 섹터 재읽기·버퍼 재할당을 막아 DiscUtils B-트리 탐색 성능을 크게 개선
    internal sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long   _offset;
        private readonly long   _length;

        private const int SECTOR     = 512;
        private const int BLOCK_SIZE = 65536; // 64KB 프리패치 블록
        private const int MAX_CACHED_BLOCKS = 2048; // 최대 128MB — B-트리 노드 재읽기를 RAM에서 처리

        // 블록 캐시: B-트리 탐색이 여러 노드를 번갈아 읽으므로 단일 블록으론 스래싱 발생
        private readonly Dictionary<long, (byte[] Data, int Len)> _cache = new();
        private readonly Queue<long> _cacheOrder = new(); // FIFO 퇴출

        public OffsetStream(Stream inner, long offset, long length)
        {
            _inner  = inner;
            _offset = offset;
            _length = length;
            _inner.Position = offset;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = _offset + value;
        }

        // 물리 디스크는 512바이트 섹터 경계 정렬 읽기만 허용.
        // 64KB 블록 단위 멀티블록 캐시로 DiscUtils 수십만 번 호출 시 디스크 I/O를 최소화한다.
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - Position;
            if (remaining <= 0) return 0;
            count = (int)Math.Min(count, remaining);

            long absPos = _inner.Position;
            int totalCopied = 0;

            // 요청 구간이 블록 경계를 걸칠 수 있으므로 블록 단위로 채움
            while (totalCopied < count)
            {
                long pos        = absPos + totalCopied;
                long blockStart = pos / BLOCK_SIZE * BLOCK_SIZE;
                int  inBlock    = (int)(pos - blockStart);

                var (data, len) = GetBlock(blockStart);
                int avail = len - inBlock;
                if (avail <= 0) break; // 디스크 끝 또는 읽기 실패

                int toCopy = Math.Min(count - totalCopied, avail);
                Array.Copy(data, inBlock, buffer, offset + totalCopied, toCopy);
                totalCopied += toCopy;
            }

            _inner.Position = absPos + totalCopied;
            return totalCopied;
        }

        // 블록 시작 위치(BLOCK_SIZE 정렬, SECTOR 정렬 충족)의 64KB 블록을 캐시에서 가져오거나 디스크에서 읽음
        private (byte[] Data, int Len) GetBlock(long blockStart)
        {
            if (_cache.TryGetValue(blockStart, out var hit)) return hit;

            var data = new byte[BLOCK_SIZE];
            _inner.Position = blockStart;
            int read = 0;
            while (read < BLOCK_SIZE)
            {
                int n;
                try { n = _inner.Read(data, read, BLOCK_SIZE - read); }
                catch { break; } // 디스크 끝 근처 읽기 오류 허용
                if (n == 0) break;
                read += n;
            }

            if (_cache.Count >= MAX_CACHED_BLOCKS)
            {
                var evict = _cacheOrder.Dequeue();
                _cache.Remove(evict);
            }
            _cache[blockStart] = (data, read);
            _cacheOrder.Enqueue(blockStart);
            return (data, read);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin   => _offset + offset,
                SeekOrigin.Current => _inner.Position + offset,
                SeekOrigin.End     => _offset + _length + offset,
                _                  => throw new ArgumentException()
            };
            _inner.Position = target;
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
