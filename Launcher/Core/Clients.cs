using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BrennorsCall.Launcher;

public sealed record RunningClient(int Pid, long StartTicks, string Account, Guid Character, string Executable, string RuntimeDirectory, string VersionDirectory)
{
    public string Display => $"{Account}  ·  PID {Pid}";
}
public sealed record LaunchMessage(string Ticket, string ServerAddress, string AccountName, Guid CharacterId);
public sealed class ClientManager(string dataDirectory)
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    private string Records => Path.Combine(dataDirectory, "clients");
    public RunningClient[] Running()
    {
        Directory.CreateDirectory(Records); var clients = new List<RunningClient>();
        foreach (var path in Directory.EnumerateFiles(Records, "*.json"))
        {
            try
            {
                var client = AtomicFile.Load<RunningClient>(path);
                if (client is not null && IsRunning(client)) clients.Add(client);
                else File.Delete(path);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return clients.ToArray();
    }
    public static bool IsRunning(RunningClient client)
    {
        try
        {
            using var process = Process.GetProcessById(client.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == client.StartTicks &&
                string.Equals(process.MainModule?.FileName, client.Executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public void Close(RunningClient client, bool force = false)
    {
        if (!IsRunning(client)) return;
        using var process = Process.GetProcessById(client.Pid);
        if (force) process.Kill(); else if (!process.CloseMainWindow()) throw new InvalidOperationException("Client did not accept a close request. Use End client to stop this process.");
    }
    public async Task<RunningClient> Launch(GameUpdater updater, LauncherSettings settings, AccountSession account, Guid character,
        string serverAddress, Func<CancellationToken, Task<string>> getTicket, CancellationToken ct = default, bool renderOffscreen = false)
    {
        settings.Validate();
        using var installationLock = new InstallationLock(updater.Root);
        var installed = updater.Installed ?? throw new FileNotFoundException("Install the game first.");
        if (installed.Channel != settings.Channel) throw new InvalidOperationException("Install the selected channel before playing.");
        var release = updater.ReadInstalled(installed);
        var versionDirectory = updater.VersionPath(installed); var content = SafePath.Under(versionDirectory, "content");
        if ((await updater.Required(release.Manifest, content, null, ct)).Count != 0) throw new InvalidDataException("Game files are missing or modified. Repair the installation before playing.");
        await Prerequisites.EnsureWindowsRuntime(content, release.Manifest, ct);
        var executable = SafePath.Under(content, release.Manifest.Executable);
        GameUpdater.ValidateExecutablePath(executable);
        var id = Guid.NewGuid().ToString("N"); var runtime = Path.Combine(dataDirectory, "runtime", id); Directory.CreateDirectory(runtime);
        var pipeName = "BrennorsCall-" + id;
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
        // Construct a minimal environment rather than inheriting developer/database/cloud credentials.
        var environment = info.Environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        info.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "COMSPEC", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PUBLIC", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE" })
            if (environment.TryGetValue(key, out var value) && value is not null) info.Environment[key] = value;
        var temp = Path.Combine(runtime, "Temp"); Directory.CreateDirectory(temp);
        info.Environment["TEMP"] = temp; info.Environment["TMP"] = temp;
        info.Environment["BC_BACKEND_URL"] = account.ApiUrl;
        info.Environment["BC_LAUNCH_PIPE"] = pipeName;
        info.Environment["BC_LAUNCHER_PID"] = Environment.ProcessId.ToString();
        info.Environment["BC_VERSION_LOCK"] = Path.Combine(versionDirectory, ".in-use");
        foreach (var arg in new[] { "-game", settings.Fullscreen ? "-fullscreen" : "-windowed", $"-ResX={settings.Width}", $"-ResY={settings.Height}", "-UserDir=" + runtime + Path.DirectorySeparatorChar, "-abslog=" + Path.Combine(runtime, "client.log") }) info.ArgumentList.Add(arg);
        if (settings.NoSound) info.ArgumentList.Add("-nosound");
        if (renderOffscreen)
        {
            info.ArgumentList.Add("-RenderOffScreen");
            info.ArgumentList.Add("-ExecCmds=t.IdleWhenNotForeground 0,t.MaxFPS 45");
        }
        // Keep a shared version handle through initialization; the game takes over before acknowledging IPC.
        using var versionLock = new FileStream(Path.Combine(versionDirectory, ".in-use"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var process = Process.Start(info) ?? throw new IOException("Windows could not start the game.");
        var client = new RunningClient(process.Id, process.StartTime.ToUniversalTime().Ticks, account.Name, character, executable, runtime, versionDirectory);
        AtomicFile.Save(Path.Combine(Records, id + ".json"), client);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
            // An incorrect process may connect first, but never receives the credential.
            while (true)
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                if (!OperatingSystem.IsWindows() || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != process.Id)
                { pipe.Disconnect(); continue; }
                break;
            }
            var ticket = await getTicket(timeout.Token); // Mint only after this client is ready to receive it.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new LaunchMessage(ticket, serverAddress, account.Name, character), Manifests.Json);
            if (bytes.Length > 16384) throw new InvalidDataException("Launch message exceeds the IPC limit.");
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token);
            await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
            var ack = new byte[1]; await pipe.ReadExactlyAsync(ack, timeout.Token);
            if (ack[0] != 1) throw new IOException("Game rejected secure launch handoff.");
            return client;
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            throw new IOException("Game did not accept its launch ticket. Retry Play for a fresh ticket; inspect diagnostics if this persists.");
        }
    }
}
