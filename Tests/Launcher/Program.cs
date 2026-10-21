using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrennorsCall.Launcher;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var temp = Path.Combine(root, ".local", "launcher-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action, string label)
{
    try { await action(); } catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException or FormatException or JsonException or HttpRequestException) { Check(true, label); return; }
    throw new Exception("Not rejected: " + label);
}
Task RejectSync(Action action, string label) => Reject(() => { action(); return Task.CompletedTask; }, label);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var config = new DeploymentConfig { AllowLoopbackHttp = true, TrustedKeys = new() { ["test"] = key.ExportSubjectPublicKeyInfoPem() }, Channels = new() { ["live"] = new("http://127.0.0.1:5095", "http://127.0.0.1:5080", "127.0.0.1:7777") } };
var handler = new DistributionHandler(); using var http = new HttpClient(handler);
var settings = new LauncherSettings { GameDirectory = Path.Combine(temp, "game"), Channel = "live" };
var updater = new GameUpdater(http, config, settings);
ReleaseFile Add(string name, byte[] data)
{
    var hash = Convert.ToHexStringLower(SHA256.HashData(data)); handler.Blobs[hash] = data; return new(name, data.Length, hash);
}
VerifiedRelease Release(long sequence, params ReleaseFile[] files)
{
    var manifest = new ReleaseManifest(1, "game", "live", $"0.1.{sequence}", sequence, DateTimeOffset.UtcNow, files[0].Path, files, "Signed news", "Signed patch notes");
    var bytes = Manifests.Sign(manifest, "test", key); return new(manifest, bytes, GameUpdater.ManifestHash(bytes));
}
try
{
    var executable = Add("BrennorsCall/Binaries/Win64/Game.exe", Encoding.UTF8.GetBytes("not-executed-test-data"));
    var asset = Add("Content/pak.dat", RandomNumberGenerator.GetBytes(400_000));
    var first = Release(1, executable, asset); handler.Envelope = first.Envelope;
    Check(Manifests.Verify(first.Envelope, config.TrustedKeys, "game", "live").Version == "0.1.1", "Signed manifest round trip");
    await RejectSync(() => Manifests.Verify(first.Envelope, new Dictionary<string, string>(), "game", "live"), "Unknown key");
    await RejectSync(() => Manifests.Verify(first.Envelope, config.TrustedKeys, "game", "testing"), "Channel mixup");
    var envelope = JsonSerializer.Deserialize<SignedRelease>(first.Envelope, Manifests.Json)!;
    await RejectSync(() => Manifests.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) }), config.TrustedKeys, "game", "live"), "Tampered signature");
    foreach (var path in new[] { "../outside.exe", "/absolute.exe", "C:/x.exe", "x\\evil.exe", "a/./b", "a//b", "a/../b", "a/file:ads", "CON.txt", "a/CON .txt", "a/NUL", "a/end.", "a/end ", "a/%2e%2e/file", "a/*", "a/COM1.dat", "a\u0000.exe" })
        await RejectSync(() => SafePath.Validate(path), "Unsafe path " + path);
    await RejectSync(() => Manifests.Validate(first.Manifest with { Files = [executable, executable with { Path = executable.Path.ToUpperInvariant() }] }, "game", "live"), "Case duplicate");
    await RejectSync(() => Manifests.Validate(first.Manifest with { Files = [executable, asset with { Path = "Content" }, asset] }, "game", "live"), "File-directory collision");
    await RejectSync(() => Manifests.Validate(first.Manifest with { Executable = "missing.exe" }, "game", "live"), "Missing executable");
    await RejectSync(() => config.ValidateUrl("http://example.com"), "Nonlocal HTTP");
    await RejectSync(() => config.ValidateUrl("https://user:password@example.com"), "URL credentials");
    Check((await updater.Required(first.Manifest, null)).Count == 2, "Initial plan includes all files");
    var limitWatch = Stopwatch.StartNew(); await new BandwidthLimiter(1_000_000).Wait(100_000, CancellationToken.None);
    Check(limitWatch.Elapsed.TotalSeconds is >= 0.08 and < 5, "Positive aggregate bandwidth limit waits for a bounded interval");
    var installed = await updater.Install(first);
    var activePath = Path.Combine(updater.VersionPath(installed), "content");
    Check((await updater.Required(first.Manifest, activePath)).Count == 0, "Initial install verifies");
    Check(await Manifests.Matches(SafePath.Under(activePath, asset.Path), asset), "SHA256 file verification");
    Check((await updater.Latest()).Hash == first.Hash, "Fetch and authenticate manifest");
    using (new InstallationLock(settings.GameDirectory)) await Reject(() => updater.Install(first), "Concurrent updater prevention");
    var nextAsset = Add("Content/pak.dat", RandomNumberGenerator.GetBytes(500_000));
    var second = Release(2, executable, nextAsset);
    Check((await updater.Required(second.Manifest, activePath)).Select(f => f.Path).SequenceEqual(new[] { asset.Path }), "Incremental plan skips matching binary");
    var downloadsBefore = handler.Requests.Count;
    handler.InterruptOnce = nextAsset.Sha256;
    var secondInstall = await updater.Install(second);
    Check(handler.Requests.Skip(downloadsBefore).All(r => r.Hash == nextAsset.Sha256), "Only changed file downloaded");
    Check(handler.Requests.Any(r => r.Hash == nextAsset.Sha256 && r.Offset > 0), "Interrupted download resumes with Range");
    Check(await Manifests.Matches(SafePath.Under(activePath, asset.Path), asset), "Old version remains intact");
    Check(secondInstall.Directory != installed.Directory, "Version activation creates an independent directory");
    await Reject(() => updater.Install(first), "Signed rollback rejected");
    var sameSequence = Release(2, executable, asset); await Reject(() => updater.Install(sameSequence), "Reused sequence rejected");
    var corrupt = Add("Content/bad.dat", RandomNumberGenerator.GetBytes(1000)); var third = Release(3, executable, corrupt);
    handler.Corrupt = corrupt.Sha256;
    await Reject(() => updater.Install(third), "Bad download hash rejected");
    Check(updater.Installed!.Directory == secondInstall.Directory, "Failed update leaves active version usable");
    handler.Corrupt = null;
    var targetPath = SafePath.Under(Path.Combine(updater.VersionPath(secondInstall), "content"), nextAsset.Path);
    await File.WriteAllTextAsync(targetPath, "corrupt");
    Check((await updater.Required(second.Manifest, Path.Combine(updater.VersionPath(secondInstall), "content"))).Count == 1, "Corruption detected");
    var repaired = await updater.Install(second);
    Check(repaired.Directory != secondInstall.Directory && await Manifests.Matches(SafePath.Under(Path.Combine(updater.VersionPath(repaired), "content"), nextAsset.Path), nextAsset), "Repair activates a verified replacement");
    // A range-ignoring server must safely restart rather than append a full response.
    handler.IgnoreRanges = true;
    var downloadTarget = Path.Combine(temp, "resume.dat"); await File.WriteAllBytesAsync(downloadTarget + ".part", handler.Blobs[asset.Sha256][..55]);
    await updater.Download(asset, downloadTarget, config.Channels["live"].BaseUrl, new(0));
    Check(await Manifests.Matches(downloadTarget, asset), "Range-ignoring host restarts safely"); handler.IgnoreRanges = false;
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await updater.Install(third, ct: cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(updater.Installed!.Directory == repaired.Directory, "Cancelled install retains active version"); }
    }
    var settingsStore = new SettingsStore(Path.Combine(temp, "settings")); settings.BandwidthKiB = 127; settingsStore.Save(settings);
    Check(settingsStore.Load().GameDirectory == settings.GameDirectory && settingsStore.Load().BandwidthKiB == 127, "Settings persist across restart");
    Check(!File.ReadAllText(Path.Combine(temp, "settings", "settings.json")).Contains("token", StringComparison.OrdinalIgnoreCase), "Ordinary settings contain no credentials");
    if (OperatingSystem.IsWindows())
    {
        var tooDeep = new GameUpdater(http, config, new LauncherSettings { GameDirectory = Path.Combine(temp, new string('a', 110), new string('b', 70)) });
        await Reject(() => tooDeep.Install(first), "Unlaunchable Windows installation path rejected before download");
        Check(tooDeep.Installed is null, "Rejected long path never activates an installation");
        var vault = new AccountVault(Path.Combine(temp, "vault"));
        var a = new AccountSession("main", config.Channels["live"].ApiUrl, "secret-token-one", DateTimeOffset.UtcNow.AddHours(1));
        var b = a with { Name = "buff", Token = "secret-token-two" };
        vault.Save(a); vault.Save(b);
        Check(vault.List().Length == 2 && vault.Restore(vault.List().Single(s => s.Name == "buff")).Token == b.Token, "DPAPI independently restores multiple sessions");
        Check(!File.ReadAllText(Path.Combine(temp, "vault", "accounts.dpapi.json")).Contains("secret-token"), "Stored tokens are encrypted");
        vault.Remove(a.Name, a.ApiUrl); Check(vault.List().Single().Name == "buff", "Removing one saved account retains the other");
        var worker = Path.Combine(root, "Tests/LauncherWorker/bin/Release/net10.0");
        var workerFiles = Directory.GetFiles(worker).Where(p => !p.EndsWith(".pdb")).Select(p => Add(Path.GetFileName(p), File.ReadAllBytes(p))).ToArray();
        var ordered = workerFiles.OrderBy(f => f.Path == "BrennorsCall.LauncherWorker.exe" ? 0 : 1).ToArray();
        var workerRelease = Release(4, ordered); await updater.Install(workerRelease);
        var manager = new ClientManager(Path.Combine(temp, "clients-data"));
        Environment.SetEnvironmentVariable("WORLD_API_KEY", "do-not-inherit-test-secret");
        RunningClient? one = null, two = null;
        try
        {
            var existingPipes = Directory.GetFiles(@"\\.\pipe\").ToHashSet();
            var launch = manager.Launch(updater, settings, a, Guid.NewGuid(), "127.0.0.1:7777", _ => Task.FromResult("one-use-ticket-one"));
            string? newPipe = null;
            for (var attempt = 0; attempt < 100 && newPipe is null; attempt++)
            {
                newPipe = Directory.GetFiles(@"\\.\pipe\").FirstOrDefault(p => p.Contains("BrennorsCall-") && !existingPipes.Contains(p));
                if (newPipe is null) await Task.Delay(10);
            }
            Check(newPipe is not null, "Per-process secure pipe was created");
            using (var impostor = new System.IO.Pipes.NamedPipeClientStream(".", Path.GetFileName(newPipe!), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                await impostor.ConnectAsync(3000);
                var rejected = false;
                try { using var timeout = new CancellationTokenSource(3000); rejected = await impostor.ReadAsync(new byte[1], timeout.Token) == 0; }
                catch (IOException) { rejected = true; }
                Check(rejected, "A same-user wrong-PID pipe client receives no ticket");
            }
            one = await launch;
            two = await manager.Launch(updater, settings, b, Guid.NewGuid(), "127.0.0.1:7777", _ => Task.FromResult("one-use-ticket-two"));
            Check(manager.Running().Length == 2 && one.Pid != two.Pid, "Two real processes launch concurrently via secure IPC");
            var identity1 = JsonDocument.Parse(File.ReadAllText(Path.Combine(one.RuntimeDirectory, "identity.json"))).RootElement;
            var identity2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(two.RuntimeDirectory, "identity.json"))).RootElement;
            Check(identity1.GetProperty("account").GetString() == "main" && identity2.GetProperty("account").GetString() == "buff", "Account identity is isolated per process");
            Check(one.RuntimeDirectory != two.RuntimeDirectory && identity1.GetProperty("temp").GetString() != identity2.GetProperty("temp").GetString(), "Settings, logs and temp directories are isolated");
            Check(!identity1.GetProperty("hasInheritedSecret").GetBoolean(), "Developer secrets are not inherited by the game");
            var newManager = new ClientManager(Path.Combine(temp, "clients-data")); Check(newManager.Running().Length == 2, "Client management survives launcher restart");
            await updater.Install(Release(5, executable)); await updater.Install(Release(6, executable, asset)); updater.Cleanup();
            Check(Directory.Exists(one.VersionDirectory), "Cleanup preserves a version held by running clients");
            using var clientProcess = Process.GetProcessById(one.Pid);
            manager.Close(one, true); await clientProcess.WaitForExitAsync();
            Check(ClientManager.IsRunning(two), "Closing one client preserves another client's session");
        }
        finally { if (one is not null) manager.Close(one, true); if (two is not null) manager.Close(two, true); Environment.SetEnvironmentVariable("WORLD_API_KEY", null); }
    }
    Console.WriteLine($"PASS {checks} launcher checks: signatures, paths, hashes, staging, resume, rollback protection, repair, locks, settings, DPAPI and real multiprocess IPC.");
}
catch (Exception error) { Console.Error.WriteLine("FAIL " + error); Environment.ExitCode = 1; }

sealed class DistributionHandler : HttpMessageHandler
{
    public byte[] Envelope = [];
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public List<(string Hash, long Offset)> Requests { get; } = [];
    public string? InterruptOnce, Corrupt;
    public bool IgnoreRanges;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(".json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Envelope) });
        var hash = request.RequestUri.Segments.Last(); var offset = request.Headers.Range?.Ranges.First().From ?? 0;
        lock (Requests) Requests.Add((hash, offset));
        var data = Blobs[hash]; if (Corrupt == hash) data = new byte[data.Length];
        if (IgnoreRanges) offset = 0;
        var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        if (InterruptOnce == hash)
        {
            InterruptOnce = null; response.Content = new ByteArrayContent(data[(int)offset..(int)(offset + (data.Length - offset) / 2)]);
        }
        else response.Content = new ByteArrayContent(data[(int)offset..]);
        if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
        return Task.FromResult(response);
    }
}
