using System.Text.Json;

namespace BrennorsCall.Launcher;

public sealed class LauncherSettings
{
    public string GameDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrennorsCall", "Game");
    public string Channel { get; set; } = "live";
    public bool AutomaticUpdates { get; set; } = true;
    public int DownloadConcurrency { get; set; } = 3;
    public int BandwidthKiB { get; set; }
    public bool Fullscreen { get; set; }
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 900;
    public bool NoSound { get; set; }
    public bool StartWithWindows { get; set; }
    public bool RememberAccounts { get; set; } = true;
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(GameDirectory) || Channel is not ("development" or "testing" or "live") ||
            DownloadConcurrency is < 1 or > 8 || BandwidthKiB is < 0 or > 1_000_000 || Width is < 640 or > 7680 || Height is < 480 or > 4320)
            throw new InvalidDataException("Check the absolute installation directory, channel, concurrency (1–8), bandwidth and resolution.");
        SafePath.RejectLinks(GameDirectory);
    }
}
public sealed record ChannelEndpoint(string BaseUrl, string ApiUrl, string WorldAddress, bool PrivateDownloads = false);
public sealed class DeploymentConfig
{
    public Dictionary<string, string> TrustedKeys { get; set; } = [];
    public Dictionary<string, ChannelEndpoint> Channels { get; set; } = [];
    public string DefaultChannel { get; set; } = "live";
    public bool AllowLoopbackHttp { get; set; }
    public ChannelEndpoint Endpoint(string channel)
    {
        if (!Channels.TryGetValue(channel, out var endpoint) || TrustedKeys.Count == 0)
            throw new InvalidDataException("This launcher has no trusted distribution configuration for that channel. Contact the publisher.");
        ValidateUrl(endpoint.BaseUrl); ValidateUrl(endpoint.ApiUrl);
        if (string.IsNullOrWhiteSpace(endpoint.WorldAddress) || endpoint.WorldAddress.Length > 253 || endpoint.WorldAddress.Any(c => !char.IsAsciiLetterOrDigit(c) && !".:-[]".Contains(c)))
            throw new InvalidDataException("Invalid world address.");
        return endpoint;
    }
    public Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Scheme == "https" || AllowLoopbackHttp && uri.Scheme == "http" && uri.IsLoopback) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Distribution and authentication require HTTPS (explicit loopback development is allowed).");
        return uri;
    }
    public static DeploymentConfig Load(string path) => JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(path), Manifests.Json) ?? throw new InvalidDataException("Missing publisher configuration.");
}
public static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Save<T>(string path, T value) => Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Manifests.Json));
    public static T? Load<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Manifests.Json) : default;
}
public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public LauncherSettings Load()
    {
        var settings = AtomicFile.Load<LauncherSettings>(Path.Combine(DirectoryPath, "settings.json")) ?? new();
        settings.Validate(); return settings;
    }
    public void Save(LauncherSettings settings) { settings.Validate(); AtomicFile.Save(Path.Combine(DirectoryPath, "settings.json"), settings); }
}
