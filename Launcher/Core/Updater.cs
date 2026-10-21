using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Http.Json;

namespace BrennorsCall.Launcher;

public sealed record UpdateProgress(string Stage, long Completed, long Total, double BytesPerSecond, TimeSpan? Remaining);
public sealed record InstalledRelease(string Directory, string Channel, long Sequence, string ManifestHash);
public sealed record VerifiedRelease(ReleaseManifest Manifest, byte[] Envelope, string Hash);

public sealed class InstallationLock : IDisposable
{
    private readonly FileStream file;
    public InstallationLock(string root)
    {
        SafePath.RejectLinks(root); Directory.CreateDirectory(root);
        try { file = new FileStream(Path.Combine(root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another launcher is updating or starting a client in this installation. Retry shortly.", e); }
    }
    public void Dispose() => file.Dispose();
}

public sealed class GameUpdater(HttpClient http, DeploymentConfig config, LauncherSettings settings, Func<AccountSession?>? session = null)
{
    public string Root => Path.GetFullPath(settings.GameDirectory);
    public InstalledRelease? Installed => AtomicFile.Load<InstalledRelease>(Path.Combine(Root, "active.json"));
    public string VersionPath(InstalledRelease installed) => SafePath.Under(Root, "versions/" + installed.Directory);
    public static string ManifestHash(byte[] envelope) => Convert.ToHexStringLower(SHA256.HashData(envelope));

    public async Task<VerifiedRelease> Latest(string product = "game", CancellationToken ct = default)
    {
        var endpoint = config.Endpoint(settings.Channel);
        var url = new Uri(endpoint.BaseUrl.TrimEnd('/') + $"/{settings.Channel}/{product}.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (endpoint.PrivateDownloads && settings.Channel != "live") Authorize(request, endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
            bytes.Write(buffer, 0, read);
        }
        var envelope = bytes.ToArray(); var manifest = Manifests.Verify(envelope, config.TrustedKeys, product, settings.Channel);
        var hash = ManifestHash(envelope);
        CheckSequence(manifest, hash);
        return new(manifest, envelope, hash);
    }
    private string HistoryPath(string product, string channel) => SafePath.Under(Root, $"history/{product}-{channel}.json");
    public void CheckSequence(ReleaseManifest manifest, string hash)
    {
        var highest = AtomicFile.Load<InstalledRelease>(HistoryPath(manifest.Product, manifest.Channel));
        if (highest is not null && (manifest.Sequence < highest.Sequence || manifest.Sequence == highest.Sequence && hash != highest.ManifestHash))
            throw new InvalidDataException("Release rollback or reused sequence was rejected. Publish a new sequence for every release.");
    }
    public VerifiedRelease ReadInstalled(InstalledRelease installed)
    {
        var envelope = File.ReadAllBytes(SafePath.Under(VersionPath(installed), "release.json"));
        if (ManifestHash(envelope) != installed.ManifestHash) throw new CryptographicException("Installed release metadata was changed.");
        var manifest = Manifests.Verify(envelope, config.TrustedKeys, "game", installed.Channel);
        if (manifest.Sequence != installed.Sequence) throw new CryptographicException("Installed release identity mismatch.");
        return new(manifest, envelope, installed.ManifestHash);
    }
    public async Task<List<ReleaseFile>> Required(ReleaseManifest manifest, string? source, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var required = new List<ReleaseFile>(); long done = 0; var total = manifest.Files.Sum(f => f.Size);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (source is null || !await Manifests.Matches(SafePath.Under(source, file.Path), file, ct)) required.Add(file);
            done += file.Size; progress?.Report(new("Verifying files", done, total, 0, null));
        }
        return required;
    }
    public async Task<InstalledRelease> Install(VerifiedRelease release, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        settings.Validate();
        using var installationLock = new InstallationLock(Root);
        // Reverify even when called from a local tool or cached UI selection.
        var manifest = Manifests.Verify(release.Envelope, config.TrustedKeys, "game", settings.Channel);
        if (release.Hash != ManifestHash(release.Envelope)) throw new CryptographicException("Release identity mismatch.");
        CheckSequence(manifest, release.Hash);
        // Keep package paths short even beneath a deep CI checkout or user-selected directory.
        // Channel, sequence and the full manifest identity remain in signed/active metadata.
        var name = release.Hash[..16] + "-" + Guid.NewGuid().ToString("N")[..16];
        var destination = SafePath.Under(Root, "versions/" + name);
        ValidateExecutablePath(SafePath.Under(destination, "content/" + manifest.Executable));
        var current = Installed;
        var source = current is null ? null : SafePath.Under(VersionPath(current), "content");
        var required = await Required(manifest, source, progress, ct);
        if (current is not null && current.ManifestHash == release.Hash && required.Count == 0 &&
            (!OperatingSystem.IsWindows() || SafePath.Under(VersionPath(current), "content/" + manifest.Executable).Length < 260)) return current;
        var stage = SafePath.Under(Root, "staging/" + release.Hash);
        var content = SafePath.Under(stage, "content"); Directory.CreateDirectory(content);
        long needed = 0;
        foreach (var file in manifest.Files)
            if (!await Manifests.Matches(SafePath.Under(content, file.Path), file, ct)) needed = checked(needed + file.Size);
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        if (drive.AvailableFreeSpace < needed + 128L * 1024 * 1024) throw new IOException("Not enough disk space for a safe staged update. Free space or select another installation directory.");
        var missing = required.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var downloads = new List<ReleaseFile>();
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = SafePath.Under(content, file.Path);
            if (await Manifests.Matches(target, file, ct)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!missing.Contains(file.Path) && source is not null)
            {
                // Copies preserve immutable old versions; hard links would couple repairs to running clients.
                await using var input = new FileStream(SafePath.Under(source, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await input.CopyToAsync(output, ct);
            }
            else downloads.Add(file);
        }
        var total = downloads.Sum(f => f.Size); long transferred = 0; var watch = Stopwatch.StartNew();
        var limiter = new BandwidthLimiter(settings.BandwidthKiB * 1024L);
        await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = settings.DownloadConcurrency, CancellationToken = ct }, async (file, token) =>
        {
            await Download(file, SafePath.Under(content, file.Path), config.Endpoint(settings.Channel).BaseUrl, limiter, count =>
            {
                var done = Interlocked.Add(ref transferred, count); var speed = done / Math.Max(0.01, watch.Elapsed.TotalSeconds);
                progress?.Report(new("Downloading", Math.Min(done, total), total, speed, speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, total - done) / speed) : null));
            }, token, release);
        });
        // Verify the entire candidate, including copied files, before activation.
        if ((await Required(manifest, content, progress, ct)).Count != 0) throw new CryptographicException("Staged game failed verification. The previous version is still active.");
        AtomicFile.Write(SafePath.Under(stage, "release.json"), release.Envelope);
        using (File.Create(SafePath.Under(stage, ".in-use"))) { }
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(stage, destination);
        var active = new InstalledRelease(name, manifest.Channel, manifest.Sequence, release.Hash);
        // High-water record first: a crash cannot permit an older release to replace the candidate.
        AtomicFile.Save(HistoryPath("game", manifest.Channel), active);
        AtomicFile.Save(Path.Combine(Root, "active.json"), active);
        progress?.Report(new("Ready to play", 1, 1, 0, null));
        return active;
    }
    public static void ValidateExecutablePath(string executable)
    {
        if (OperatingSystem.IsWindows() && executable.Length >= 260)
            throw new InvalidDataException("The game installation directory is too long for Windows game startup. Choose a shorter directory in Settings, then install again.");
    }
    public void Cleanup()
    {
        using var installationLock = new InstallationLock(Root);
        var active = Installed; var versions = Path.Combine(Root, "versions");
        if (!Directory.Exists(versions)) return;
        // Retain the most recent previous version for recovery as well as all in-use versions.
        var candidates = new DirectoryInfo(versions).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).ToArray();
        var previous = candidates.FirstOrDefault(d => d.Name != active?.Directory)?.Name;
        foreach (var directory in candidates)
        {
            if (directory.Name == active?.Directory || directory.Name == previous) continue;
            SafePath.RejectLinks(directory.FullName);
            if (Directory.EnumerateFileSystemEntries(directory.FullName, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) continue;
            try
            {
                using (new FileStream(Path.Combine(directory.FullName, ".in-use"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(directory.FullName, true);
            }
            catch (IOException) { /* A client or scanner still holds the version. Retry next time. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Authorize(HttpRequestMessage request, ChannelEndpoint endpoint)
    {
        var account = session?.Invoke();
        if (account is null || account.Expires <= DateTimeOffset.UtcNow || account.ApiUrl != endpoint.ApiUrl.TrimEnd('/'))
            throw new InvalidOperationException("Sign in to download the private game build.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
    }
    private async Task<Uri> DownloadUri(ReleaseFile file, Uri endpoint, VerifiedRelease? release, CancellationToken ct)
    {
        var channel = config.Endpoint(settings.Channel);
        if (!channel.PrivateDownloads) return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/blobs/" + file.Sha256);
        if (endpoint != config.ValidateUrl(channel.BaseUrl) || release is null) throw new InvalidDataException("Private download requires its verified release identity.");
        using var request = new HttpRequestMessage(HttpMethod.Post, channel.BaseUrl.TrimEnd('/') + $"/{settings.Channel}/download");
        if (release.Manifest.Product != "launcher" || settings.Channel != "live") Authorize(request, channel);
        request.Content = JsonContent.Create(new { product = release.Manifest.Product, sequence = release.Manifest.Sequence, manifestHash = release.Hash, sha256 = file.Sha256 });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Your session expired. Sign in again to continue downloading.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("Invalid download link response.");
        // Limit the broker response even when Content-Length is omitted.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[16385]; var length = 0;
        while (length < bytes.Length) { var count = await stream.ReadAsync(bytes.AsMemory(length), ct); if (count == 0) break; length += count; }
        if (length > 16384) throw new InvalidDataException("Invalid download link response.");
        using var json = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, length));
        if (!Uri.TryCreate(json.RootElement.GetProperty("url").GetString(), UriKind.Absolute, out var uri) || !PrivateDownloadUrls.IsCdn(uri))
            throw new InvalidDataException("Download link is not a trusted GitHub CDN address.");
        return uri;
    }
    public async Task Download(ReleaseFile file, string target, string baseUrl, BandwidthLimiter limiter, Action<int>? received = null, CancellationToken ct = default, VerifiedRelease? release = null)
    {
        var endpoint = config.ValidateUrl(baseUrl);
        var partial = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); SafePath.RejectLinks(partial);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > file.Size) { File.Delete(partial); offset = 0; }
                if (offset < file.Size || !File.Exists(partial))
                {
                    var uri = await DownloadUri(file, endpoint, release, ct);
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); throw new IOException("Download range changed; restarting this file."); }
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != offset || range.Length != file.Size || range.To != file.Size - 1) throw new IOException("Invalid resumable download response.");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else throw new IOException("Unexpected download response.");
                    await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[128 * 1024]; long size = offset;
                    while (true)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var count = await input.ReadAsync(buffer, timeout.Token);
                        if (count == 0) break;
                        size += count;
                        if (size > file.Size) throw new IOException("Download exceeds signed file size.");
                        await limiter.Wait(count, ct); await output.WriteAsync(buffer.AsMemory(0, count), ct); received?.Invoke(count);
                    }
                    await output.FlushAsync(ct); output.Flush(true);
                }
                if (!await Manifests.Matches(partial, file, ct))
                {
                    // A truncated response can resume, but a complete corrupt response must restart.
                    if (new FileInfo(partial).Length >= file.Size) File.Delete(partial);
                    throw new IOException("Downloaded file failed SHA-256 verification. Retry is safe.");
                }
                File.Move(partial, target, true); return;
            }
            catch (Exception e) when ((e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
}

public sealed class BandwidthLimiter(long bytesPerSecond)
{
    private readonly object gate = new();
    private DateTimeOffset next = DateTimeOffset.UtcNow;
    public async Task Wait(int bytes, CancellationToken ct)
    {
        if (bytesPerSecond <= 0) return;
        TimeSpan delay;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (next < now) next = now;
            next += TimeSpan.FromSeconds((double)bytes / bytesPerSecond); delay = next - now;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}
