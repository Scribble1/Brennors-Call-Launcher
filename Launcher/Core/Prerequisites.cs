using System.Diagnostics;
using Microsoft.Win32;

namespace BrennorsCall.Launcher;

public static class Prerequisites
{
    public static async Task EnsureWindowsRuntime(string content, ReleaseManifest manifest, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string path = "Engine/Extras/Redist/en-us/vc_redist.x64.exe";
        var redist = manifest.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (redist is null) return; // Other signed test clients may not use the Unreal runtime.
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        if (key?.GetValue("Installed") is int installed && installed == 1 && key.GetValue("Version") is string version &&
            Version.TryParse(version.TrimStart('v'), out var current) && current >= new Version(14, 44)) return;
        var executable = SafePath.Under(content, path);
        if (!await Manifests.Matches(executable, redist, ct)) throw new InvalidDataException("Windows prerequisite failed verification. Repair the installation.");
        using var fileLock = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        info.Arguments = "/install /quiet /norestart";
        using var process = Process.Start(info) ?? throw new IOException("Windows runtime installation could not start.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode is not (0 or 1638 or 3010)) throw new IOException("Windows C++ runtime installation failed or was cancelled. Retry Play and allow the prerequisite installation.");
    }
}
