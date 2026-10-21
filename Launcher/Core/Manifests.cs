using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrennorsCall.Launcher;

public sealed record ReleaseFile(string Path, long Size, string Sha256);
public sealed record ReleaseManifest(int Schema, string Product, string Channel, string Version, long Sequence,
    DateTimeOffset Published, string Executable, ReleaseFile[] Files, string News = "", string PatchNotes = "");
public sealed record SignedRelease(string KeyId, string Payload, string Signature);

public static partial class Manifests
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")] private static partial Regex Identifier();
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex HashPattern();
    public static bool IsHash(string value) => value is not null && HashPattern().IsMatch(value);
    public static void Validate(ReleaseManifest manifest, string product, string channel)
    {
        if (manifest.Schema != 1 || manifest.Product != product || manifest.Channel != channel ||
            manifest.Version is null || !Identifier().IsMatch(manifest.Version) || manifest.Version.Contains("..") ||
            manifest.Sequence < 1 || manifest.Published > DateTimeOffset.UtcNow.AddMinutes(15) ||
            manifest.Files is null || manifest.Files.Length is < 1 or > 200_000 ||
            manifest.News is null || manifest.PatchNotes is null || manifest.News.Length > 20000 || manifest.PatchNotes.Length > 100000)
            throw new InvalidDataException("Release metadata is invalid or belongs to another channel.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Release contains a null file.");
            SafePath.Validate(file.Path);
            if (!paths.Add(file.Path) || file.Size < 0 || !IsHash(file.Sha256))
                throw new InvalidDataException("Release contains duplicate files, invalid sizes or hashes.");
            total = checked(total + file.Size);
        }
        foreach (var path in paths)
        {
            var parent = path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (paths.Contains(parent)) throw new InvalidDataException("Release file conflicts with a directory.");
            }
        }
        SafePath.Validate(manifest.Executable);
        if (!paths.Contains(manifest.Executable) || !manifest.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release must include its Windows executable.");
    }

    public static ReleaseManifest Verify(byte[] envelope, IReadOnlyDictionary<string, string> trustedKeys, string product, string channel)
    {
        if (envelope.Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        var signed = JsonSerializer.Deserialize<SignedRelease>(envelope, Json) ?? throw new InvalidDataException("Empty manifest.");
        if (signed.KeyId is null || !trustedKeys.TryGetValue(signed.KeyId, out var pem)) throw new CryptographicException("Manifest signing key is not trusted.");
        var payload = Convert.FromBase64String(signed.Payload);
        using var key = ECDsa.Create(); key.ImportFromPem(pem);
        if (key.KeySize != 256 || !key.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("Manifest signature is invalid. No files were activated.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(payload, Json) ?? throw new InvalidDataException("Empty release.");
        Validate(manifest, product, channel);
        return manifest;
    }

    public static byte[] Sign(ReleaseManifest manifest, string keyId, ECDsa privateKey)
    {
        Validate(manifest, manifest.Product, manifest.Channel);
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedRelease(keyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))), Json);
    }

    public static async Task<bool> Matches(string path, ReleaseFile file, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == file.Sha256;
    }
}

public static class SafePath
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 220 || path.Contains('\\') || path.Contains('%') ||
            path.Any(c => c < 32 || c > 126 || ":*?\"<>|".Contains(c)) || path.StartsWith('/'))
            throw new InvalidDataException("Unsafe release path.");
        foreach (var part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || Reserved.Contains(part.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException("Unsafe release path component.");
    }
    public static string Under(string root, string relative)
    {
        Validate(relative);
        root = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release path escapes installation.");
        RejectLinks(result);
        return result;
    }
    public static void RejectLinks(string path)
    {
        for (var entry = Path.GetFullPath(path); entry is not null; entry = Path.GetDirectoryName(entry))
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation paths must not contain links or junctions.");
    }
}
