using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogMapping;

internal sealed record SemanticVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string Pre) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text)
    {
        var m = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if (!m.Success) throw new FormatException("배포 버전 형식이 올바르지 않습니다.");
        string pre = m.Groups[4].Value;
        if (pre.Split('.').Any(x => x.Length > 1 && x.All(char.IsDigit) && x[0] == '0'))
            throw new FormatException("잘못된 테스트 버전 번호입니다.");
        return new(BigInteger.Parse(m.Groups[1].Value), BigInteger.Parse(m.Groups[2].Value), BigInteger.Parse(m.Groups[3].Value), pre);
    }
    public int CompareTo(SemanticVersion? other)
    {
        if (other == null) return 1;
        foreach (int result in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) })
            if (result != 0) return result;
        if (Pre == other.Pre) return 0;
        if (Pre.Length == 0) return 1;
        if (other.Pre.Length == 0) return -1;
        var a = Pre.Split('.'); var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = BigInteger.TryParse(a[i], out var av), bn = BigInteger.TryParse(b[i], out var bv);
            int c = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : StringComparer.Ordinal.Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}

internal sealed record UpdateAsset(string Version, string Url, string Sha256, long Size, bool Preview);
internal sealed record UpdateStatus(string State, string Message, string CurrentVersion, string Channel,
    string? NewVersion = null, long Received = 0, long Total = 0, bool CanOpen = false);

internal sealed class AppUpdates : IDisposable
{
    public const string Repository = "VULCAN-HUB/LogMapping";
    public static string RunningVersion => (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public static string? BrandUrl(string key) => key switch {
        "youtube" => "https://www.youtube.com/@unknown8563",
        "discord" => "https://discord.gg/fmwEzmCzPz", _ => null };
    private readonly HttpClient _http;
    private readonly string _cache;
    private readonly string _version;
    private readonly bool _beta;
    private readonly bool _supported;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancel;
    private UpdateAsset? _candidate;
    private string? _verifiedFile;
    public UpdateStatus Status { get; private set; }
    public event Action<UpdateStatus>? Changed;
    public AppUpdates(string version, string cache, HttpClient? http = null, bool? supported = null)
    {
        _version = version; _beta = SemanticVersion.Parse(version).Pre.Length != 0;
        _cache = cache;
        _supported = supported ?? (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LogMapping-Update/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        Status = new("idle", "확인 버튼을 눌러 새 버전을 조회하세요.", version, _beta ? "beta" : "stable");
        if (!_supported) Set("unavailable", "이 플랫폼의 업데이트 패키지는 지원하지 않습니다.");
    }
    private void Set(string state, string message, long received = 0, long total = 0)
    {
        Status = new(state, message, _version, _beta ? "beta" : "stable", _candidate?.Version, received, total, _verifiedFile != null);
        Changed?.Invoke(Status);
    }
    public static bool AllowedDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo == "" &&
        (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)
         || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
    public static UpdateAsset? Select(string json, string current, bool beta)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("배포 목록 형식 오류");
        var now = SemanticVersion.Parse(current);
        var releases = new List<(JsonElement Item, SemanticVersion Version)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            string tag = item.GetProperty("tag_name").GetString() ?? "";
            // The initial public tags predate three-part versioning and cannot be newer than this app.
            if (tag is "v0.1-beta" or "v0.2-beta") continue;
            var v = SemanticVersion.Parse(tag);
            bool preview = item.GetProperty("prerelease").GetBoolean() || v.Pre.Length != 0;
            if (!beta && preview) continue;
            releases.Add((item, v));
        }
        if (releases.Count == 0) throw new NotSupportedException("이 채널에 확인할 수 있는 배포가 없습니다.");
        var latest = releases.OrderByDescending(r => r.Version).First();
        if (latest.Version.CompareTo(now) <= 0) return null;
        var assets = latest.Item.GetProperty("assets").EnumerateArray().Where(a =>
            a.GetProperty("name").GetString() is "LogMapping-win-x64-portable.exe" or "LogMapping.exe").ToArray();
        // LogMapping.exe is the established Windows x64 portable package in this repository.
        if (assets.Length != 1) throw new NotSupportedException("Windows x64 포터블 자산을 하나로 식별할 수 없습니다.");
        var asset = assets[0];
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new NotSupportedException("배포 파일의 SHA-256 정보가 없어 다운로드할 수 없습니다.");
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var uri = new Uri(url);
        string tagName = latest.Item.GetProperty("tag_name").GetString()!;
        string name = asset.GetProperty("name").GetString()!;
        if (url != $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tagName)}/{name}" || !AllowedDownload(uri))
            throw new FormatException("배포 파일 주소가 저장소·태그와 일치하지 않습니다.");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > 1024L * 1024 * 1024) throw new FormatException("배포 파일 크기가 올바르지 않습니다.");
        return new(tagName.TrimStart('v'), url, digest[7..], size, latest.Version.Pre.Length != 0 || latest.Item.GetProperty("prerelease").GetBoolean());
    }
    public async Task CheckAsync()
    {
        if (!_supported || !await _gate.WaitAsync(0)) return;
        try
        {
            _candidate = null; _verifiedFile = null;
            Set("checking", "새 버전을 확인하고 있습니다…");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024).WaitAsync(timeout.Token);
            _candidate = Select(await response.Content.ReadAsStringAsync(timeout.Token), _version, _beta);
            Set(_candidate == null ? "current" : "available", _candidate == null ? "최신 버전을 사용하고 있습니다." :
                $"새 버전 {_candidate.Version}{(_candidate.Preview ? " (베타 / 테스트판)" : "") }이 있습니다.");
        }
        catch (NotSupportedException e) { Set("unavailable", e.Message); }
        catch (OperationCanceledException) { Set("error", "확인 시간이 초과됐습니다. 인터넷 연결을 확인하고 다시 시도하세요."); }
        catch (HttpRequestException) { Set("error", "배포 서버에 연결하지 못했습니다. 인터넷 연결 또는 GitHub 요청 제한을 확인하세요."); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or UriFormatException) { Set("error", "배포 메타데이터가 올바르지 않습니다. 나중에 다시 확인하세요."); }
        finally { _gate.Release(); }
    }
    private async Task<HttpResponseMessage> DownloadResponse(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        for (int count = 0; count < 6; count++)
        {
            if (!AllowedDownload(uri)) throw new InvalidDataException("허용되지 않은 다운로드 주소입니다.");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location; response.Dispose();
                if (next == null) throw new InvalidDataException("다운로드 이동 주소가 없습니다.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("다운로드 주소 이동이 너무 많습니다.");
    }
    public async Task DownloadAsync()
    {
        if (!_supported || !await _gate.WaitAsync(0)) return;
        string? partial = null;
        try
        {
            if (_candidate == null || Status.State != "available") return;
            _cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = _cancel.Token;
            Set("downloading", "업데이트를 다운로드하고 있습니다…", 0, _candidate.Size);
            string folder = Path.Combine(_cache, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            partial = Path.Combine(folder, "LogMapping.exe.partial");
            using var response = await DownloadResponse(_candidate.Url, token);
            if (response.Content.Headers.ContentLength is long length && length != _candidate.Size) throw new InvalidDataException("배포 파일 크기가 일치하지 않습니다.");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536]; long received = 0; var lastReport = Environment.TickCount64;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    received += read;
                    if (received > _candidate.Size) throw new InvalidDataException("파일이 안내된 크기를 초과했습니다.");
                    hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), token);
                    if (Environment.TickCount64 - lastReport >= 150) { Set("downloading", "업데이트를 다운로드하고 있습니다…", received, _candidate.Size); lastReport = Environment.TickCount64; }
                }
                if (received != _candidate.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(_candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("파일 크기 또는 SHA-256 검증에 실패했습니다. 파일은 사용할 수 없습니다.");
            }
            token.ThrowIfCancellationRequested();
            string final = Path.Combine(folder, "LogMapping.exe");
            File.Move(partial, final); partial = null; _verifiedFile = final;
            Set("ready", "다운로드와 SHA-256 검증 완료. 재시작하여 업데이트할 수 있습니다.", _candidate.Size, _candidate.Size);
        }
        catch (OperationCanceledException) { Set("available", "다운로드를 취소했거나 시간이 초과됐습니다. 다시 다운로드할 수 있습니다."); }
        catch (HttpRequestException) { Set("error", "다운로드 연결이 끊겼습니다. 업데이트 확인부터 다시 시도하세요."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Set("error", e is InvalidDataException ? e.Message : "업데이트 파일을 저장할 수 없습니다. 여유 공간과 폴더 권한을 확인하세요."); }
        finally
        {
            if (partial != null) { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            _cancel?.Dispose(); _cancel = null; _gate.Release();
        }
    }
    public void Installing() => Set("installing", "변경 내용을 저장하고 업데이트를 준비합니다…");
    public void ApplyFailed() => Set("ready", "적용하지 못했습니다. 기존 앱을 계속 사용할 수 있습니다. 다시 시도하세요.");
    public UpdateAsset Candidate => _candidate ?? throw new InvalidOperationException("No update selected.");
    public void Cancel() => _cancel?.Cancel();
    public async Task<string> VerifiedFolderAsync()
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException("업데이트 작업 중입니다.");
        try
        {
            if (_verifiedFile == null || _candidate == null) throw new InvalidOperationException("검증된 파일이 없습니다.");
            await using var stream = File.OpenRead(_verifiedFile);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (stream.Length != _candidate.Size || !hash.Equals(_candidate.Sha256, StringComparison.OrdinalIgnoreCase))
            { _verifiedFile = null; Set("error", "검증 후 파일이 변경됐습니다. 다시 다운로드하세요."); throw new InvalidDataException("업데이트 파일이 변경됐습니다."); }
            return Path.GetDirectoryName(_verifiedFile)!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _verifiedFile = null; Set("error", "다운로드 파일을 읽을 수 없습니다. 다시 확인하고 다운로드하세요."); throw; }
        finally { _gate.Release(); }
    }
    public void Dispose() { Cancel(); _http.Dispose(); }
}
