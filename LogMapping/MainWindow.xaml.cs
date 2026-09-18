using DiscUtils.HfsPlus;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
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
            try
            { Directory.CreateDirectory(DataDir); }
            catch(Exception ex) { MessageBox.Show("data 폴더를 만들 수 없습니다. 쓰기 가능한 위치로 프로그램을 옮겨 주세요.\n"+ex.Message); Application.Current.Shutdown();return; }

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
            webView.CoreWebView2.NavigationStarting += (_,e)=> { if(!IsAppOrigin(e.Uri))e.Cancel=true; };
            webView.CoreWebView2.NewWindowRequested += (_,e)=>e.Handled=true;

            // DOM 로드 후 경로 주입
            webView.CoreWebView2.DOMContentLoaded += async (s, e) =>
            {
                var script = "window._APP_DIR=" + JsonSerializer.Serialize(_appDir) + ";window._DATA_DIR=" + JsonSerializer.Serialize(DataDir) + ";if(typeof APP_DIR!=='undefined'){APP_DIR=window._APP_DIR;DATA_DIR=window._DATA_DIR;}";
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
            var resources = new[] { "index.html" };

            foreach (var res in resources)
            {
                var resourceName = "LogMapping.resources." + res;
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                var destPath = Path.Combine(appDir2, res);
                // 내용 해시가 같으면 재추출을 생략한다.
                if(File.Exists(destPath))
                {
                    using var cached=File.OpenRead(destPath);
                    var same=SHA256.HashData(cached).SequenceEqual(SHA256.HashData(stream));stream.Position=0;
                    if(same)continue;
                }
                using var fs = File.Create(destPath);
                stream.CopyTo(fs);
            }
        }

        private static bool IsAppOrigin(string uri) => Uri.TryCreate(uri,UriKind.Absolute,out var u) && u.Scheme=="https" && u.Host=="logmapping.app";

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if(!IsAppOrigin(e.Source))return;
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
                    case "openFileDialog": HandleOpenFileDialog(msg); break;
                    case "saveFileDialog": HandleSaveFileDialog(msg); break;
                    case "listDrives": HandleListDrives(msg); break;
                    case "openFolderDialog": HandleOpenFolderDialog(msg); break;
                    case "copyToClipboard": HandleCopyToClipboard(msg); break;
                    case "openUrl": HandleOpenUrl(msg); break;
                    // ── SQLite ──
                    case "closeReady": _closeReady?.TrySetResult(msg.Content=="ok"); break;
                    case "dbDescribe": HandleDbDescribe(msg); break;
                    case "dbCopyColors": HandleDbCopyColors(msg); break;
                    case "dbScan": HandleDbScan(msg); break;
                    case "cancelScan": HandleCancelScan(msg); break;
                    case "dbUpdateDrive": HandleDbUpdateDrive(msg); break;
                    case "dbDeleteDrive": HandleDbDeleteDrive(msg); break;
                    case "dbChildren": HandleDbChildren(msg); break;
                    case "dbSearch": HandleDbSearch(msg); break;
                    case "dbSetColor": HandleDbSetColor(msg); break;
                    case "dbExtCounts": HandleDbExtCounts(msg); break;
                    case "dbFilesByColor": HandleDbFilesByColor(msg); break;
                    case "dbPrune": HandleDbPrune(msg); break;
                    case "dbByExts": HandleDbByExts(msg); break;
                    case "dbFolders": HandleDbFolders(msg); break;
                    case "dbExportCsv": HandleDbExportCsv(msg); break;
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
                AtomicStorage.Write(path, msg.Content ?? "");
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

        // ── 스트리밍 스캐너 (트리 미빌드, OOM 방지) ───────────────────────────────
        // 윈도우 드라이브/폴더: 노드를 만들자마자 emit(parentPath,name,isDir,sizeKB,mtime)
        private int WalkDirStream(string dirPath, Action<string,string,bool,long,string> emit, CancellationToken token)
            => FileScanner.Scan(dirPath,emit,token);

        // Mac 드라이브(APFS/HFS+): 트리 미빌드 스트리밍.
        // 반환값 = (사용 MB, 총 MB). 총 MB는 사용 MB와 같은 헤더·같은 정밀도로 계산해
        // "사용 > 총" 역전(여유 음수·100%+ 초과)을 방지한다. APFS는 총 MB 미보유 → 0 반환(JS가 meta.cap 폴백).
        private (long usedMB, long totalMB) WalkMacDriveStream(string macPath, Action<string, string, bool, long, string> emit, CancellationToken token, string? expectedId)
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
                using var apfs = new ApfsReader(physStream, partOffset, token);
                var volumes = apfs.FindVolumes();
                if (volIndex >= volumes.Count) throw new IOException("저장된 APFS 볼륨을 찾지 못했습니다.");
                if (volumes.Count == 0) throw new Exception("APFS 볼륨을 찾을 수 없습니다.");
                var vol = !string.IsNullOrEmpty(expectedId) ? volumes.FirstOrDefault(v=>v.VolumeId==expectedId)
                    ?? throw new IOException("APFS 볼륨 식별자가 다릅니다. 기존 목록은 보존했습니다.") : volumes[volIndex];
                if (vol.OmapPhys == 0) throw new Exception("암호화된 볼륨은 파일 목록을 읽을 수 없습니다.");
                apfs.WalkVolumeStream(vol.OmapPhys, vol.RootTreeOid, emit);
                return ((long)vol.UsedMB, apfs.TotalMB);
            }

            if(!string.IsNullOrEmpty(expectedId) && ReadHfsIdentity(physStream,partOffset)!=expectedId)
                throw new IOException("HFS+ 볼륨 식별자가 다릅니다. 기존 목록은 보존했습니다.");
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
                token.ThrowIfCancellationRequested();
                var (curPath, rel) = stack.Pop();
                if (!visited.Add(curPath)) continue; // 하드링크 순환 방지
                string[] entries;
                entries = hfs.GetFileSystemEntries(curPath);
                // 폴더 목록을 1회 조회로 받아 entry별 DirectoryExists(B-트리 재탐색) 호출 제거
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in hfs.GetDirectories(curPath)) dirSet.Add(d);
                foreach (var entry in entries)
                {
                    var name = entry.Replace('/', '\\').TrimEnd('\\').Split('\\').LastOrDefault() ?? "";
                    if (string.IsNullOrEmpty(name) || name=="." || name=="..") continue;
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
                            size = hfs.GetFileLength(entry) / 1024;
                            mtime = hfs.GetLastWriteTime(entry).ToString("yyyy-MM-dd");
                            emit(rel, name, false, Math.Max(1, size), mtime);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { throw new IOException("HFS+ 항목을 읽지 못했습니다: "+entry,ex); }
                }
            }
            token.ThrowIfCancellationRequested();
            return (hfsUsedMB, hfsTotalMB);
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
                var fileName = msg.FileName ?? "my_drives.hcat";
                var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = extension == ".csv" ? "CSV 파일 (*.csv)|*.csv" :
                             extension == ".html" ? "HTML 뷰어 (*.html)|*.html" : "HCat 파일 (*.hcat)|*.hcat",
                    DefaultExt = extension,
                    AddExtension = true,
                    Title = extension == ".hcat" ? "카탈로그 저장" : "내보내기 저장",
                    FileName = fileName
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
                            totalGB = d.TotalSize / 1073741824.0,
                            freeGB = d.TotalFreeSpace / 1073741824.0,
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

            if(msg.IncludeMac)
            {
                Task.Run(()=> { drives.AddRange(FindMacPartitions());SendToJS("listDrivesResult",new{id=msg.Id,drives=drives.ToArray()}); });return;
            }
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
        private static string? ReadHfsIdentity(Stream stream,long offset)
        {
            stream.Position=offset+1024;var header=new byte[512];stream.ReadExactly(header);
            if(header[0]!=0x48 || (header[1]!=0x2B && header[1]!=0x58))return null;
            // Apple TN1150: finderInfo[6..7], bytes 104..111 of volume header.
            var bytes=header.AsSpan(104,8).ToArray();return bytes.Any(b=>b!=0)?"HFS-"+Convert.ToHexString(bytes):null;
        }
        private IEnumerable<object> FindMacPartitions()
        {
            var result=new List<object>();
            try
            {
                var scope=new ManagementScope(@"\\.\Root\Microsoft\Windows\Storage");scope.Connect();
                using var searcher=new ManagementObjectSearcher(scope,new ObjectQuery("SELECT DiskNumber,Offset,Size,GptType FROM MSFT_Partition"));
                foreach(ManagementObject p in searcher.Get().Cast<ManagementObject>())
                {
                    var type=(p["GptType"]?.ToString()??"").Trim('{','}');
                    bool isApfs=type.Equals("7C3457EF-0000-11AA-AA11-00306543ECAC",StringComparison.OrdinalIgnoreCase);
                    if(!isApfs && !type.Equals("48465300-0000-11AA-AA11-00306543ECAC",StringComparison.OrdinalIgnoreCase))continue;
                    int disk=Convert.ToInt32(p["DiskNumber"]);long offset=Convert.ToInt64(p["Offset"]??0L);
                    try
                    {
                        using var stream=OpenPhysicalDrive(disk)??throw new IOException("디스크 접근 실패");
                        if(isApfs)
                        {
                            using var reader=new ApfsReader(stream,offset);var volumes=reader.FindVolumes();
                            for(int i=0;i<volumes.Count;i++)
                            {
                                var v=volumes[i];
                                result.Add(new{path=$"macvol://d{disk}o{offset}/{i}",label=$"[APFS] {v.Name} (Disk {disk})",
                                    totalGB=reader.TotalMB/1024.0,freeGB=0.0,driveType="Removable",isMac=true,fsType="APFS",volumeId=v.VolumeId});
                            }
                        }
                        else
                        {
                            var (bs,total,free)=ReadHfsPlusHeader(stream,offset);
                            result.Add(new{path=$"macvol://d{disk}o{offset}/0",label=$"[HFS+] Mac 드라이브 (Disk {disk})",
                                totalGB=total*bs/1073741824.0,freeGB=free*bs/1073741824.0,driveType="Removable",isMac=true,fsType="HFS+",volumeId=ReadHfsIdentity(stream,offset)});
                        }
                    }
                    catch(Exception ex)
                    {
                        result.Add(new{path=$"macvol://d{disk}o{offset}/0",label=$"Mac 드라이브 (Disk {disk}) — 식별 실패: {ex.Message}",
                            totalGB=0.0,freeGB=0.0,driveType="Removable",isMac=true,fsType=isApfs?"APFS":"HFS+",volumeId=(string?)null});
                    }
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
        private Task? _scanTask;
        private int _activeExports;
        // DB 핸들러들이 여러 백그라운드 스레드에서 동시에 호출하므로, 생성·반환을 모두 락 안에서 처리한다.
        private CatalogDb Db()
        {
            lock (_dbLock)
            {
                _db ??= new CatalogDb(Path.Combine(DataDir, "catalog.db"));
                return _db;
            }
        }

        private void HandleDbScan(WebMessage msg)
        {
            if(_scanTask is { IsCompleted:false }) { SendToJS("dbScanResult",new{id=msg.Id,error="다른 스캔이 진행 중입니다."});return; }
            _scanCts?.Dispose();_scanCts=new CancellationTokenSource();var cts=_scanCts;

            var folderPath = msg.Path ?? "";
            _scanTask = Task.Run(() =>
            {
                long driveId = -1;
                try
                {
                    var db = Db();
                    void CheckWindowsIdentity() {
                        if(msg.VolumeId?.StartsWith("VSN-")==true && GetVolumeSerial(System.IO.Path.GetPathRoot(folderPath)??"")!=msg.VolumeId)
                            throw new IOException("연결된 볼륨 식별자가 다릅니다. 기존 목록은 보존했습니다.");
                    }
                    CheckWindowsIdentity();
                    // 경로 기준 삭제 금지: 같은 드라이브 문자(예: E:\)를 다른 하드가 재사용하면
                    // 먼저 스캔한 드라이브의 파일이 통째로 지워지는 버그가 있었음.
                    // JS가 volumeId로 동일 매체를 확인하고 새 세대에 연결한다.
                    // 기존 세대는 다른 .hcat의 참조를 위해 보존한다.
                    driveId = db.CreateDrive(folderPath);

                    // 스트리밍 스캔: 트리를 메모리에 쌓지 않고 노드를 만들자마자 DB에 INSERT
                    int pc = 0;int excluded=0;
                    long macUsedMB = 0;
                    long macTotalMB = 0;
                    db.InsertStreaming(driveId, dbEmit =>
                    {
                        Action<string, string, bool, long, string> emit = (pp, n, isDir, sz, mt) =>
                        {
                            cts.Token.ThrowIfCancellationRequested(); // 취소 요청 시 즉시 중단
                            dbEmit(pp, n, isDir, sz, mt);
                            if (!isDir && (++pc % 500 == 0)) SendToJS("scanProgress", new { id = msg.Id, count = pc });
                        };
                        if (folderPath.StartsWith("macvol://")) (macUsedMB, macTotalMB) = WalkMacDriveStream(folderPath, emit, cts.Token,msg.VolumeId);
                        else excluded=WalkDirStream(folderPath, emit,cts.Token);
                    });

                    cts.Token.ThrowIfCancellationRequested();CheckWindowsIdentity();
                    if(!folderPath.StartsWith("macvol://") && System.IO.Path.GetFullPath(folderPath).TrimEnd('\\')==System.IO.Path.GetPathRoot(folderPath)?.TrimEnd('\\'))
                    { var info=new DriveInfo(folderPath);macTotalMB=info.TotalSize/1048576;macUsedMB=(info.TotalSize-info.TotalFreeSpace)/1048576; }
                    var statsJson = db.DriveStatsJson(driveId);
                    var rootJson = db.GetChildrenJson(driveId, "");
                    SendToJS("dbScanResult", new { id = msg.Id, driveId, statsJson, rootJson, usedMB = macUsedMB, totalMB = macTotalMB, autoBackupSkipped=db.FileSizeBytes>CatalogDb.AutoBackupMaxBytes, warning=excluded>0?$"시스템 폴더·연결 항목 {excluded}개는 하위 내용을 제외했습니다.":null });

                    // 뷰어 캐시는 내보내기에서 필요할 때 생성한다.
                    // 스캔 성공 이후 압축 작업이 다음 스캔이나 종료를 막지 않게 한다.
                }
                catch (OperationCanceledException)
                {
                    // 취소: 부분적으로 삽입된 데이터 정리
                    if (driveId >= 0) try { Db().DeleteDrive(driveId); } catch { }
                    SendToJS("dbScanResult", new { id = msg.Id, error = "cancelled" });
                }
                catch (Exception ex) {
                    if(driveId>=0)try{Db().DeleteDrive(driveId);}catch{}
                    SendToJS("dbScanResult",new{id=msg.Id,error="스캔을 완료하지 못했습니다. 기존 목록은 보존했습니다.\n"+ex.Message});
                }
            });
        }

        private void HandleCancelScan(WebMessage msg)
        {
            _scanCts?.Cancel();
            SendToJS("cancelScanResult", new { id = msg.Id, success = true });
        }

        private void HandleDbUpdateDrive(WebMessage msg)
        {
            Task.Run(() =>
            {
                try
                {
                    if (!msg.Meta.HasValue) { SendToJS("dbUpdateDriveResult", new { id = msg.Id, success = false, error = "meta 없음" }); return; }
                    long driveId = Db().UpsertDrive(msg.Meta.Value);
                    SendToJS("dbUpdateDriveResult", new { id = msg.Id, driveId, success = true });
                }
                catch (Exception ex) { SendToJS("dbUpdateDriveResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        private void HandleDbDeleteDrive(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { Db().DeleteDrive(msg.DriveId); SendToJS("dbDeleteDriveResult", new { id = msg.Id, success = true }); }
                catch (Exception ex) { SendToJS("dbDeleteDriveResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        // ⚠️ DB 핸들러는 반드시 백그라운드에서 실행한다. UI 스레드에서 돌리면 대용량 카탈로그
        // (2.5GB·350만 행)에서 조회 한 번이 앱 전체를 멈추게 하고, 그 동안의 클릭이 "응답 없음"으로
        // 이어졌다. CatalogDb는 _gate 락으로 직렬화되므로 백그라운드 실행이 안전하다.
        private void HandleDbChildren(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().GetChildrenJson(msg.DriveId, msg.ParentPath ?? ""); SendToJS("dbChildrenResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbChildrenResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbSearch(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().SearchJson(msg.Query ?? "", msg.Limit > 0 ? msg.Limit : 500,msg.DriveIds??Array.Empty<long>(),msg.Color,msg.Extensions,msg.FoldersOnly,msg.Sort); SendToJS("dbSearchResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbSearchResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbSetColor(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { Db().SetItemColor(msg.DriveId, msg.Path ?? "", string.IsNullOrEmpty(msg.Color) ? null : msg.Color); SendToJS("dbSetColorResult", new { id = msg.Id, success = true }); }
                catch (Exception ex) { SendToJS("dbSetColorResult", new { id = msg.Id, success = false, error = ex.Message }); }
            });
        }

        private void HandleDbExtCounts(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().ExtCountsJson(msg.DriveId); SendToJS("dbExtCountsResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbExtCountsResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbFilesByColor(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().FilesByColorJson(msg.DriveId, msg.Color ?? "", msg.Limit > 0 ? msg.Limit : 1000,msg.Sort); SendToJS("dbFilesByColorResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbFilesByColorResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbDescribe(WebMessage msg) => Task.Run(()=> {
            try {SendToJS("dbDescribeResult",new{id=msg.Id,description=Db().DescribeJson()});}
            catch(Exception ex){SendToJS("dbDescribeResult",new{id=msg.Id,error=ex.Message});}
        });
        private void HandleDbCopyColors(WebMessage msg) => Task.Run(()=> {
            try {Db().CopyColors(msg.OldDriveId,msg.DriveId);SendToJS("dbCopyColorsResult",new{id=msg.Id,success=true});}
            catch(Exception ex){SendToJS("dbCopyColorsResult",new{id=msg.Id,error=ex.Message});}
        });
        private void HandleDbBackup(WebMessage msg)
        {
            Interlocked.Increment(ref _activeExports);
            Task.Run(()=> {
                string? pending=null;
                try
                {
                    if(string.IsNullOrEmpty(msg.Content))throw new IOException("백업할 카탈로그 내용이 없습니다.");
                    using var catalog=JsonDocument.Parse(msg.Content);
                    var dir=System.IO.Path.Combine(DataDir,"backups");Directory.CreateDirectory(dir);
                    var name=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
                    pending=System.IO.Path.Combine(dir,".pending-"+name);Directory.CreateDirectory(pending);
                    Db().BackupTo(System.IO.Path.Combine(pending,"catalog.db"));
                    AtomicStorage.Write(System.IO.Path.Combine(pending,"catalog.hcat"),msg.Content);
                    var final=System.IO.Path.Combine(dir,name);Directory.Move(pending,final);pending=null;
                    SendToJS("dbBackupResult",new{id=msg.Id,success=true,path=final});
                }
                catch(Exception ex){SendToJS("dbBackupResult",new{id=msg.Id,error=ex.Message});}
                finally{Interlocked.Decrement(ref _activeExports);}
            });
        }
        private void HandleDbExportViewer(WebMessage msg) => RunExport(msg,true);
        private void HandleDbExportCsv(WebMessage msg) => RunExport(msg,false);
        private void RunExport(WebMessage msg,bool viewer)
        {
            Interlocked.Increment(ref _activeExports);
            Task.Run(()=> {
                string? temp=null;
                var type=viewer?"dbExportViewerResult":"dbExportCsvResult";
                try
                {
                    var path=System.IO.Path.GetFullPath(msg.Path??"");temp=path+".tmp-"+Guid.NewGuid().ToString("N");
                    if(viewer)Db().ExportViewer(temp,msg.DriveIds??Array.Empty<long>(),(done,total)=>SendToJS("exportProgress",new{id=msg.Id,done,total}));
                    else Db().ExportCsv(temp,msg.DriveIds??Array.Empty<long>());
                    File.Move(temp,path,true);temp=null;
                    SendToJS(type,new{id=msg.Id,success=true,count=1});
                }
                catch(Exception ex){SendToJS(type,new{id=msg.Id,error=ex.Message});}
                finally{if(temp!=null && File.Exists(temp))try{File.Delete(temp);}catch{} Interlocked.Decrement(ref _activeExports);}
            });
        }

        private void HandleDbByExts(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().FilesByExtsJson(msg.DriveId, msg.Query ?? "", msg.Limit > 0 ? msg.Limit : 5000,msg.Sort); SendToJS("dbByExtsResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbByExtsResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbFolders(WebMessage msg)
        {
            Task.Run(() =>
            {
                try { var rowsJson = Db().FoldersByDriveJson(msg.DriveId, msg.Limit > 0 ? msg.Limit : 50000,msg.Sort); SendToJS("dbFoldersResult", new { id = msg.Id, rowsJson }); }
                catch (Exception ex) { SendToJS("dbFoldersResult", new { id = msg.Id, error = ex.Message }); }
            });
        }

        private void HandleDbPrune(WebMessage msg)
        {
            Task.Run(() =>
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
            });
        }

        private void SendToJS(string type, object data)
        {
            var json = JsonSerializer.Serialize(new { type, data });
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                webView.CoreWebView2?.PostWebMessageAsJson(json);
            });
        }

        private bool _allowClose,_closing;
        private TaskCompletionSource<bool>? _closeReady;
        private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if(_allowClose)return;
            if(webView.CoreWebView2==null && _db==null)return;
            e.Cancel=true;if(_closing)return;
            if(Volatile.Read(ref _activeExports)>0){MessageBox.Show("내보내기 또는 백업이 끝난 뒤 종료해 주세요.");return;}
            _closing=true;
            try
            {
                if(_scanTask is {IsCompleted:false})
                {
                    if(MessageBox.Show("진행 중인 스캔을 취소하고 저장 후 종료할까요?","LogMapping",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    _scanCts?.Cancel();await _scanTask;
                }
                _closeReady=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SendToJS("windowClosing",new{});
                var completed=await Task.WhenAny(_closeReady.Task,Task.Delay(30000));
                if(completed!=_closeReady.Task || !await _closeReady.Task)
                {MessageBox.Show("저장을 완료하지 못해 종료하지 않았습니다. 오류를 확인하고 다시 저장해 주세요.");return;}
                await Task.Run(()=> { _db?.BackupIfDirty(); _db?.Dispose(); });
                _db=null;_allowClose=true;Close();
            }
            catch(Exception ex){MessageBox.Show("종료 준비 실패: "+ex.Message);}
            finally{_closing=false;if(!_allowClose)SendToJS("windowCloseAborted",new{});}
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
        public long[]? DriveIds { get; set; }
        public long OldDriveId { get; set; }
        public string? Extensions { get; set; }
        public string? Sort { get; set; }
        public string? VolumeId { get; set; }
        public bool FoldersOnly { get; set; }
        public bool IncludeMac { get; set; }
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
