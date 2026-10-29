using LogMapping;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--live")
{
    string cache = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "LogMapping-live-" + Guid.NewGuid().ToString("N"));
    using var live = new AppUpdates("0.2.3-beta", cache);
    await live.CheckAsync();
    Console.WriteLine(JsonSerializer.Serialize(live.Status));
    if (live.Status.State != "available") throw new Exception("Expected a newer published beta for the simulated older version.");
    await live.DownloadAsync();
    if (live.Status.State != "ready") throw new Exception(live.Status.Message);
    var folder = await live.VerifiedFolderAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { state = live.Status, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "LogMapping.exe")))), bytes = new FileInfo(Path.Combine(folder, "LogMapping.exe")).Length }));
    return;
}

var root = Path.Combine(Path.GetTempPath(), "LogMapping-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
async Task Test(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); passed++; }
Task Sync(Action f) { f(); return Task.CompletedTask; }
byte[] payload = Encoding.UTF8.GetBytes("synthetic-package-only");
string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
object Release(string tag, bool preview = false, string asset = "LogMapping.exe", string? digest = null, string? url = null) => new {
    tag_name = tag, prerelease = preview, draft = false, assets = new[] { new { name = asset, size = payload.Length,
    digest = digest ?? "sha256:" + hash, browser_download_url = url ?? $"https://github.com/VULCAN-HUB/LogMapping/releases/download/{tag}/{asset}" } }
};
string Json(params object[] records) => JsonSerializer.Serialize(records);
string feed = Json(Release("v0.2.6-beta.1", true));
AppUpdates Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new("0.2.5-beta.1", Path.Combine(root, Guid.NewGuid().ToString("N")), new HttpClient(new Handler(handler)), true);
HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
HttpResponseMessage Binary(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
await Test("semantic numeric versions and preview precedence", () => Sync(() => {
    Assert(SemanticVersion.Parse("0.10.0").CompareTo(SemanticVersion.Parse("0.9.9")) > 0);
    Assert(SemanticVersion.Parse("1.0.0-beta.10").CompareTo(SemanticVersion.Parse("1.0.0-beta.2")) > 0);
    Assert(SemanticVersion.Parse("1.0.0").CompareTo(SemanticVersion.Parse("1.0.0-rc.1")) > 0);
    Assert(SemanticVersion.Parse("1.0.0+build").CompareTo(SemanticVersion.Parse("1.0.0")) == 0);
    foreach (var bad in new[] { "1.01.0", "1.0.0-beta.01", "1.0", "latest", "1.0.0;cmd" }) { try { SemanticVersion.Parse(bad); throw new Exception("accepted invalid version"); } catch (FormatException) { } }
}));
await Test("stable excludes prereleases including inconsistent flags", () => Sync(() => {
    Assert(AppUpdates.Select(Json(Release("v1.0.0"), Release("v2.0.0-beta.1")), "1.0.0", false) == null);
    Assert(AppUpdates.Select(Json(Release("v1.0.0"), Release("v2.0.0", true)), "1.0.0", false) == null);
}));
await Test("beta feed chooses highest version rather than publication order", () => Sync(() => {
    Assert(AppUpdates.Select(Json(Release("v0.3.0-beta.2", true), Release("v0.2.6", false), Release("v0.3.0-beta.10", true)), "0.2.5-beta.1", true)?.Version == "0.3.0-beta.10");
}));
await Test("equal and older versions never downgrade", () => Sync(() => Assert(AppUpdates.Select(Json(Release("v0.2.4-beta", true)), "0.2.5-beta.1", true) == null)));
await Test("legacy tags are tolerated", () => Sync(() => Assert(AppUpdates.Select(Json(Release("v0.1-beta", true), Release("v0.2.4-beta", true)), "0.2.5-beta.1", true) == null)));
await Test("unsupported architecture asset not selected", async () => {
    using var service = Service((_, _) => Task.FromResult(Ok(Json(Release("v1.0.0", false, "LogMapping-arm64.exe")))));
    await service.CheckAsync(); Assert(service.Status.State == "unavailable");
});
await Test("missing hash and empty channel are unavailable", async () => {
    foreach (var body in new[] { "[]", Json(Release("v1.0.0", digest: "")) }) {
        using var service = Service((_, _) => Task.FromResult(Ok(body))); await service.CheckAsync(); Assert(service.Status.State == "unavailable");
    }
});
await Test("unsafe asset URL and malformed metadata are errors", async () => {
    foreach (var body in new[] { "{oops", "{}", "[{}]", Json(Release("v1.0.0", url: "https://example.invalid/LogMapping.exe")) }) {
        using var service = Service((_, _) => Task.FromResult(Ok(body))); await service.CheckAsync(); Assert(service.Status.State == "error");
    }
});
await Test("offline then retry recovers without downloading", async () => {
    int requests = 0;
    using var service = Service((_, _) => { if (++requests == 1) throw new HttpRequestException(); return Task.FromResult(Ok(feed)); });
    await service.CheckAsync(); Assert(service.Status.State == "error");
    await service.CheckAsync(); Assert(service.Status.State == "available" && requests == 2 && !service.Status.CanOpen);
});
await Test("check duplicate is ignored", async () => {
    int requests = 0; var release = new TaskCompletionSource();
    using var service = Service(async (_, _) => { requests++; await release.Task; return Ok(feed); });
    var first = service.CheckAsync(); await service.CheckAsync(); Assert(requests == 1 && service.Status.State == "checking");
    release.SetResult(); await first;
});
await Test("verified download ready and tamper blocks opening", async () => {
    using var service = Service((req, _) => Task.FromResult(req.RequestUri!.Host == "api.github.com" ? Ok(feed) : Binary(payload)));
    await service.CheckAsync(); await service.DownloadAsync(); Assert(service.Status.State == "ready" && service.Status.CanOpen);
    string folder = await service.VerifiedFolderAsync(); Assert(File.ReadAllBytes(Path.Combine(folder, "LogMapping.exe")).SequenceEqual(payload));
    File.WriteAllText(Path.Combine(folder, "LogMapping.exe"), "modified");
    try { await service.VerifiedFolderAsync(); throw new Exception("tamper accepted"); } catch (InvalidDataException) { }
    Assert(service.Status.State == "error" && !service.Status.CanOpen);
});
await Test("hash mismatch and truncated body never become ready", async () => {
    foreach (var bytes in new[] { new byte[payload.Length], new byte[3] }) {
        using var service = Service((req, _) => Task.FromResult(req.RequestUri!.Host == "api.github.com" ? Ok(feed) : Binary(bytes)));
        await service.CheckAsync(); await service.DownloadAsync(); Assert(service.Status.State == "error" && !service.Status.CanOpen);
    }
});
await Test("cancel, duplicate download, check during download and retry", async () => {
    int downloads = 0; var started = new TaskCompletionSource();
    using var service = Service(async (req, token) => {
        if (req.RequestUri!.Host == "api.github.com") return Ok(feed);
        if (++downloads == 1) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        return Binary(payload);
    });
    await service.CheckAsync(); var download = service.DownloadAsync(); await started.Task;
    await service.DownloadAsync(); await service.CheckAsync(); Assert(downloads == 1 && service.Status.State == "downloading");
    service.Cancel(); await download; Assert(service.Status.State == "available");
    await service.DownloadAsync(); Assert(service.Status.State == "ready");
});
await Test("redirect outside GitHub storage blocked", async () => {
    using var service = Service((req, _) => {
        if (req.RequestUri!.Host == "api.github.com") return Task.FromResult(Ok(feed));
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://example.invalid/file.exe"); return Task.FromResult(redirect);
    });
    await service.CheckAsync(); await service.DownloadAsync(); Assert(service.Status.State == "error" && !service.Status.CanOpen);
});
await Test("brand and download URL allowlists reject arbitrary commands", () => Sync(() => {
    Assert(AppUpdates.BrandUrl("discord") == "https://discord.gg/fmwEzmCzPz"); Assert(AppUpdates.BrandUrl("https://example.invalid") == null);
    foreach (var url in new[] { "http://github.com/VULCAN-HUB/LogMapping/releases/download/v1/a", "https://github.com/other/repo/releases/download/v1/a", "https://github.com.evil.invalid/a", "https://user@release-assets.githubusercontent.com/a", "https://release-assets.githubusercontent.com:444/a" }) Assert(!AppUpdates.AllowedDownload(new Uri(url)));
}));
await Test("unsupported platform makes no network request", async () => {
    using var service = new AppUpdates("1.0.0", root, new HttpClient(new Handler((_, _) => throw new Exception("network attempted"))), false);
    await service.CheckAsync(); await service.DownloadAsync(); Assert(service.Status.State == "unavailable");
});
InstallPlan Plan()
{
    var dir=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
    var stage=Path.Combine(dir,".update");Directory.CreateDirectory(stage);
    string target=Path.Combine(dir,"LogMapping.exe"),candidate=Path.Combine(stage,"candidate.exe");
    File.WriteAllText(target,"old executable");File.WriteAllText(candidate,"new executable");
    Directory.CreateDirectory(Path.Combine(dir,"data"));File.WriteAllText(Path.Combine(dir,"data","catalog.db"),"user database sentinel");File.WriteAllText(Path.Combine(dir,"catalog.hcat"),"catalog sentinel");
    return new(target,candidate,Path.Combine(dir,"previous.exe"),PortableUpdate.Hash(target),PortableUpdate.Hash(candidate));
}
await Test("portable replacement keeps backup and catalog bytes",async()=>{
    var p=Plan();bool result=await PortableUpdate.ReplaceAndRestartAsync(p,path=>Task.FromResult(File.ReadAllText(path)=="new executable"));
    Assert(result&&File.ReadAllText(p.Backup)=="old executable");
    Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(p.Target)!,"data","catalog.db"))=="user database sentinel");
    Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(p.Target)!,"catalog.hcat"))=="catalog sentinel");
});
await Test("startup rejection rolls back original executable",async()=>{
    var p=Plan();Assert(!await PortableUpdate.ReplaceAndRestartAsync(p,_=>Task.FromResult(false)));
    Assert(File.ReadAllText(p.Target)=="old executable"&&File.ReadAllText(p.Candidate)=="new executable");
});
await Test("launch exception rolls back original executable",async()=>{
    var p=Plan();Assert(!await PortableUpdate.ReplaceAndRestartAsync(p,_=>throw new IOException("launch failed")));
    Assert(File.ReadAllText(p.Target)=="old executable");
});
await Test("locked target is never overwritten",async()=>{
    var p=Plan();using var locked=new FileStream(p.Target,FileMode.Open,FileAccess.Read,FileShare.Read);
    try{await PortableUpdate.ReplaceAndRestartAsync(p,_=>Task.FromResult(true));throw new Exception("locked replace accepted");}catch(IOException){}
    Assert(!File.Exists(p.Backup)&&File.ReadAllText(p.Target)=="old executable");
});
await Test("candidate tamper and existing backup prevent replacement",async()=>{
    foreach(bool tamper in new[]{true,false}){
        var p=Plan();if(tamper)File.WriteAllText(p.Candidate,"changed");else File.WriteAllText(p.Backup,"unrelated backup");
        try{await PortableUpdate.ReplaceAndRestartAsync(p,_=>Task.FromResult(true));throw new Exception("unsafe replace accepted");}catch(InvalidDataException){}
        Assert(File.ReadAllText(p.Target)=="old executable");
    }
});
await Test("rollback refuses to overwrite externally modified replacement",async()=>{
    var p=Plan();try{await PortableUpdate.ReplaceAndRestartAsync(p,path=>{File.WriteAllText(path,"external change");return Task.FromResult(false);});throw new Exception("rollback overwrote external change");}catch(IOException){}
    Assert(File.ReadAllText(p.Target)=="external change"&&File.ReadAllText(p.Backup)=="old executable");
});
Assert(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any());
Console.WriteLine($"Passed {passed}; failed 0");

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
