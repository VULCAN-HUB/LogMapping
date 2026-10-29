using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace LogMapping;

internal sealed record InstallPlan(string Target, string Candidate, string Backup, string OldHash, string NewHash);

internal static class PortableUpdate
{
    public static string Hash(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }
    public static void Validate(InstallPlan plan)
    {
        if (!Path.IsPathFullyQualified(plan.Target) || !Path.IsPathFullyQualified(plan.Candidate) || !Path.IsPathFullyQualified(plan.Backup)) throw new InvalidDataException("Absolute paths required.");
        var root = Path.GetDirectoryName(plan.Target)!;
        if (Path.GetDirectoryName(plan.Backup) != root || Path.GetDirectoryName(Path.GetDirectoryName(plan.Candidate)) != root || Path.GetFileName(plan.Candidate) != "candidate.exe") throw new InvalidDataException("Invalid update layout.");
        if (File.Exists(plan.Backup) || plan.Target == plan.Candidate || plan.Target == plan.Backup) throw new InvalidDataException("Backup must be new.");
        foreach (var path in new[] { root, Path.GetDirectoryName(plan.Candidate)!, plan.Target, plan.Candidate })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked update paths are unsupported.");
        if (!Hash(plan.Target).Equals(plan.OldHash, StringComparison.OrdinalIgnoreCase) || !Hash(plan.Candidate).Equals(plan.NewHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update hash changed.");
    }
    // Both staged files and backup are on the destination volume. No catalog paths are modified.
    public static async Task<bool> ReplaceAndRestartAsync(InstallPlan plan, Func<string, Task<bool>> start)
    {
        Validate(plan);
        File.Replace(plan.Candidate, plan.Target, plan.Backup);
        bool ready = false;
        try { ready = await start(plan.Target); } catch { }
        if (ready) return true;
        if (!Hash(plan.Target).Equals(plan.NewHash, StringComparison.OrdinalIgnoreCase) || !Hash(plan.Backup).Equals(plan.OldHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Rollback files changed; preserve both files for recovery.");
        File.Replace(plan.Backup, plan.Target, plan.Candidate);
        return false;
    }
    public static async Task RunHelperAsync(string pipe, string planFile)
    {
        if (!pipe.StartsWith("LogMapping-update-", StringComparison.Ordinal)) throw new InvalidDataException("Invalid update session.");
        var helperDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (Path.GetFullPath(planFile) != Path.Combine(helperDir, "plan.json")) throw new InvalidDataException("Invalid plan location.");
        var plan = JsonSerializer.Deserialize<InstallPlan>(await File.ReadAllTextAsync(planFile))!;
        if (Path.GetDirectoryName(plan.Candidate) != helperDir) throw new InvalidDataException("Invalid staged package.");
        Validate(plan);
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await client.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(client); using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync("ready");
        var command = await reader.ReadLineAsync(deadline.Token);
        if (command == null || !command.StartsWith("apply:") || !int.TryParse(command[6..], out int pid)) return;
        try { using var parent = Process.GetProcessById(pid); await parent.WaitForExitAsync(deadline.Token); } catch (ArgumentException) { }
        bool installed = await ReplaceAndRestartAsync(plan, async target => {
            string ackName = "LogMapping-ready-" + Guid.NewGuid().ToString("N");
            using var ack = new NamedPipeServerStream(ackName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var launch = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! };
            launch.ArgumentList.Add("--update-ready"); launch.ArgumentList.Add(ackName);
            using var process = Process.Start(launch);
            if (process == null) return false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await ack.WaitForConnectionAsync(timeout.Token);
                using var result = new StreamReader(ack);
                if (await result.ReadLineAsync(timeout.Token) != "ready") throw new IOException("Replacement did not acknowledge startup.");
                return true;
            }
            catch
            {
                // Stop only the just-started replacement, before restoring its executable.
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                return false;
            }
        });
        if (!installed) Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(plan.Target)! });
        await File.WriteAllTextAsync(Path.Combine(helperDir, "result.json"), JsonSerializer.Serialize(new { installed, rolledBack = !installed }));
    }
    public static async Task SignalReadyAsync(string pipe)
    {
        if (!pipe.StartsWith("LogMapping-ready-", StringComparison.Ordinal)) return;
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync("ready");
    }
}
