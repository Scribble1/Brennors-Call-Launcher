using System.Diagnostics;
using System.Text.RegularExpressions;
using BrennorsCall.Launcher;

// Runs from per-user staging after the WPF process exits. Inno installs side by side and
// updates its registered shortcuts only after file installation succeeds.
try
{
    if (args.Length != 8) throw new ArgumentException("Invalid update handoff.");
    var config = DeploymentConfig.Load(args[4]); config.Endpoint(args[5]);
    var manifest = Manifests.Verify(File.ReadAllBytes(args[2]), config.TrustedKeys, "launcher", args[5]);
    if (manifest.Files.Length != 1 || !Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+(\.\d+)?$")) throw new InvalidDataException("Invalid installer release.");
    // Keep a read handle denying replacement until CreateProcess has opened the verified installer.
    using var installerLock = new FileStream(args[3], FileMode.Open, FileAccess.Read, FileShare.Read);
    if (!await Manifests.Matches(args[3], manifest.Files[0])) throw new InvalidDataException("Installer integrity check failed.");
    try
    {
        using var parent = Process.GetProcessById(int.Parse(args[0]));
        if (parent.StartTime.ToUniversalTime().Ticks == long.Parse(args[1]))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); await parent.WaitForExitAsync(timeout.Token);
        }
    }
    catch (ArgumentException) { /* Parent has already exited. */ }
    var oldDirectory = Path.GetFullPath(args[6]).TrimEnd(Path.DirectorySeparatorChar);
    var parentDirectory = Directory.GetParent(oldDirectory) ?? throw new InvalidDataException("Invalid installation directory.");
    // Installer layout is ROOT/app/VERSION. Development launches cannot self-upgrade an arbitrary checkout.
    if (parentDirectory.Name != "app") throw new InvalidOperationException("Self-update requires an installed launcher. Build and run the installer first.");
    var installRoot = parentDirectory.Parent!.FullName;
    var info = new ProcessStartInfo(args[3]) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/DIR=" + installRoot }) info.ArgumentList.Add(arg);
    using var setup = Process.Start(info) ?? throw new IOException("Installer could not start.");
    await setup.WaitForExitAsync();
    if (setup.ExitCode != 0) throw new IOException("Installer failed; the previous launcher remains available.");
    var executable = Path.Combine(installRoot, "app", manifest.Version, "BrennorsCall.Launcher.exe");
    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
}
catch (Exception error)
{
    // Fixed diagnostics only: no payloads, tokens, or command-line arguments.
    if (args.Length == 8 && Directory.Exists(args[7])) File.AppendAllText(Path.Combine(args[7], "launcher-update.log"), $"{DateTimeOffset.UtcNow:O} Update failed: {error.GetType().Name}. Previous launcher retained.\n");
    if (args.Length == 8 && File.Exists(Path.Combine(args[6], "BrennorsCall.Launcher.exe")))
        Process.Start(new ProcessStartInfo(Path.Combine(args[6], "BrennorsCall.Launcher.exe")) { UseShellExecute = true });
    Environment.ExitCode = 1;
}
