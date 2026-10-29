using LogMapping;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--apply-update") { await PortableUpdate.RunHelperAsync(args[1], args[2]); return; }
if (args.Length == 2 && args[0] == "--update-ready")
{
    if (File.Exists(Path.Combine(AppContext.BaseDirectory,"reject-start.flag")))
    {
        using var rejected = new NamedPipeClientStream(".", args[1], PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rejected.ConnectAsync(deadline.Token);
        using var response = new StreamWriter(rejected) { AutoFlush = true };
        await response.WriteLineAsync("startup-failed");
        return;
    }
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"restarted.txt"),"new process started");
    await PortableUpdate.SignalReadyAsync(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--hold")
{
    while(!File.Exists(args[1])) await Task.Delay(50);
    return;
}
if(args.Length == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory,"reject-start.flag")))
{
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"restored.txt"),"previous process restarted");
    return;
}
if(args.Length!=2 || (args[0]!="--test" && args[0]!="--test-failure")) throw new ArgumentException("Use --test or --test-failure with a new test directory.");
bool rejectStartup = args[0] == "--test-failure";
string root=Path.GetFullPath(args[1]);
if(Directory.Exists(root))throw new IOException("Test directory must not exist.");
Directory.CreateDirectory(root);
if(rejectStartup) await File.WriteAllTextAsync(Path.Combine(root,"reject-start.flag"),"reject replacement startup");
var stage=Path.Combine(root,".update");Directory.CreateDirectory(stage);
string executable=Environment.ProcessPath!,target=Path.Combine(root,"LogMapping.exe"),candidate=Path.Combine(stage,"candidate.exe"),helper=Path.Combine(stage,"helper.exe");
File.Copy(executable,target);File.Copy(executable,candidate);File.Copy(executable,helper);
// An inert overlay differentiates the two harmless test executables without changing app logic.
using(var overlay=new FileStream(candidate,FileMode.Append))overlay.Write(new byte[]{1,2,3,4});
Directory.CreateDirectory(Path.Combine(root,"data"));await File.WriteAllTextAsync(Path.Combine(root,"data","catalog.db"),"database sentinel");await File.WriteAllTextAsync(Path.Combine(root,"catalog.hcat"),"catalog sentinel");
var plan=new InstallPlan(target,candidate,Path.Combine(root,"previous.exe"),PortableUpdate.Hash(target),PortableUpdate.Hash(candidate));
string planFile=Path.Combine(stage,"plan.json");await File.WriteAllTextAsync(planFile,JsonSerializer.Serialize(plan));
var parentStart=new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true};parentStart.ArgumentList.Add("--hold");parentStart.ArgumentList.Add(Path.Combine(root,"exit.flag"));
using var parent=Process.Start(parentStart)!;
string pipeName="LogMapping-update-"+Guid.NewGuid().ToString("N");
using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
var helperStart=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};helperStart.ArgumentList.Add("--apply-update");helperStart.ArgumentList.Add(pipeName);helperStart.ArgumentList.Add(planFile);
using var worker=Process.Start(helperStart)!;
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
await pipe.WaitForConnectionAsync(timeout.Token);
using var reader=new StreamReader(pipe);using var writer=new StreamWriter(pipe){AutoFlush=true};
if(await reader.ReadLineAsync(timeout.Token)!="ready")throw new Exception("Helper not ready");
await writer.WriteLineAsync("apply:"+parent.Id);
await Task.Delay(300);
if(PortableUpdate.Hash(target)!=plan.OldHash)throw new Exception("Replaced while parent alive");
await File.WriteAllTextAsync(Path.Combine(root,"exit.flag"),"exit");
await worker.WaitForExitAsync(timeout.Token);
if(rejectStartup)
{
    string restored = Path.Combine(root,"restored.txt");
    while(!File.Exists(restored)) await Task.Delay(50,timeout.Token);
    if(worker.ExitCode!=0 || PortableUpdate.Hash(target)!=plan.OldHash || PortableUpdate.Hash(candidate)!=plan.NewHash || File.Exists(plan.Backup)) throw new Exception("Rollback process integration failed");
    using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stage,"result.json")));
    if(result.RootElement.GetProperty("installed").GetBoolean() || !result.RootElement.GetProperty("rolledBack").GetBoolean()) throw new Exception("Rollback result was incorrect");
}
else if(worker.ExitCode!=0 || PortableUpdate.Hash(target)!=plan.NewHash || PortableUpdate.Hash(plan.Backup)!=plan.OldHash || !File.Exists(Path.Combine(root,"restarted.txt")))throw new Exception("Update process integration failed");
if(await File.ReadAllTextAsync(Path.Combine(root,"data","catalog.db"))!="database sentinel" || await File.ReadAllTextAsync(Path.Combine(root,"catalog.hcat"))!="catalog sentinel")throw new Exception("User data changed");
Console.WriteLine(rejectStartup
    ? "PASS actual helper failure: rejected startup, original EXE restored and restarted, candidate retained and unchanged data"
    : "PASS actual helper process: wait for parent exit, replace, restart acknowledgment, backup and unchanged data");
