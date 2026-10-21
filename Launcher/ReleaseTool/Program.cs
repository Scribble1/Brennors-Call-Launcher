using System.Security.Cryptography;
using System.Text.Json;
using BrennorsCall.Launcher;

// Arguments contain file paths and public metadata only. Private keys are read from a protected file.
if (args.Length == 0) throw new ArgumentException("Commands: keygen PRIVATE_PATH PUBLIC_PATH | publish SOURCE OUTPUT PRODUCT CHANNEL VERSION SEQUENCE EXECUTABLE KEY_ID PRIVATE_PATH [NEWS_PATH PATCH_NOTES_PATH] | config OUTPUT PUBLIC_PATH KEY_ID BASE_URL API_URL WORLD CHANNEL");
switch (args[0])
{
    case "verify":
    {
        if (args.Length != 6) throw new ArgumentException("verify ENVELOPE KEY_PATH KEY_ID PRODUCT CHANNEL");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[2]));
        var manifest = Manifests.Verify(File.ReadAllBytes(args[1]), new Dictionary<string,string> { [args[3]] = key.ExportSubjectPublicKeyInfoPem() }, args[4], args[5]);
        Console.WriteLine(JsonSerializer.Serialize(manifest, Manifests.Json)); break;
    }
    case "keygen":
    {
        if (args.Length != 3 || File.Exists(args[1]) || File.Exists(args[2])) throw new ArgumentException("Choose new key paths outside source control.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem()); File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("Generated ECDSA P-256 keys. Protect the private key; ship only the public key."); break;
    }
    case "config":
    case "config-v1":
    {
        if (args.Length != 8) throw new ArgumentException("config-v1 OUTPUT PUBLIC_PATH KEY_ID BASE_URL API_URL WORLD CHANNEL");
        var config = new DeploymentConfig { DefaultChannel = args[7], AllowLoopbackHttp = new Uri(args[4]).IsLoopback && new Uri(args[5]).IsLoopback,
            TrustedKeys = new() { [args[3]] = File.ReadAllText(args[2]) }, Channels = new() { [args[7]] = new(args[4], args[5], args[6]) } };
        config.Endpoint(args[7]); AtomicFile.Save(args[1], config); break;
    }
    case "publish":
    {
        if (args.Length is < 10 or > 12) throw new ArgumentException("publish SOURCE OUTPUT PRODUCT CHANNEL VERSION SEQUENCE EXECUTABLE KEY_ID PRIVATE_PATH [NEWS_PATH PATCH_NOTES_PATH]");
        var source = Path.GetFullPath(args[1]); var output = Path.GetFullPath(args[2]);
        if (output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || output == source) throw new ArgumentException("Distribution output must be outside source files.");
        if (args[3] is not ("game" or "launcher") || args[4] is not ("development" or "testing" or "live")) throw new ArgumentException("Unknown product or channel.");
        SafePath.RejectLinks(source); SafePath.RejectLinks(output); Directory.CreateDirectory(output);
        var blobs = Path.Combine(output, "blobs"); Directory.CreateDirectory(blobs);
        var files = new List<ReleaseFile>();
        // Enumerate without traversing junctions, symlinks or developer output.
        void Visit(string directory)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Publish source contains a link or junction.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (new[] { "Saved", ".git", ".local", "Intermediate", "DerivedDataCache" }.Contains(Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)) continue;
                    Visit(item); continue;
                }
                if (new[] { ".pdb", ".log", ".tmp", ".pem", ".key" }.Contains(Path.GetExtension(item), StringComparer.OrdinalIgnoreCase) || Path.GetFileName(item) == ".env") continue;
                var relative = Path.GetRelativePath(source, item).Replace('\\', '/'); SafePath.Validate(relative);
                using var stream = File.OpenRead(item); var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                var target = Path.Combine(blobs, hash);
                if (File.Exists(target) && !Manifests.Matches(target, new(relative, new FileInfo(item).Length, hash)).GetAwaiter().GetResult())
                    throw new CryptographicException("An existing distribution blob is corrupt. Restore it before publishing.");
                if (!File.Exists(target))
                {
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(item, temporary); File.Move(temporary, target, false);
                }
                files.Add(new(relative, new FileInfo(item).Length, hash));
            }
        }
        Visit(source);
        var manifest = new ReleaseManifest(1, args[3], args[4], args[5], long.Parse(args[6]), DateTimeOffset.UtcNow, args[7], files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            args.Length > 10 ? File.ReadAllText(args[10]) : "Welcome to Brennor's Call.", args.Length > 11 ? File.ReadAllText(args[11]) : "");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[9]));
        if (key.KeySize != 256) throw new CryptographicException("Use an ECDSA P-256 signing key.");
        var currentPath = Path.Combine(output, args[4], args[3] + ".json");
        if (File.Exists(currentPath))
        {
            var current = Manifests.Verify(File.ReadAllBytes(currentPath), new Dictionary<string,string> { [args[8]] = key.ExportSubjectPublicKeyInfoPem() }, args[3], args[4]);
            if (manifest.Sequence <= current.Sequence) throw new IOException("Release sequence must increase. Live cannot be overwritten by an older build.");
        }
        var envelope = Manifests.Sign(manifest, args[8], key);
        var versioned = Path.Combine(output, args[4], args[3] + "-" + args[6] + ".json");
        if (File.Exists(versioned)) throw new IOException("This sequence is already published. Increment it; releases are immutable.");
        AtomicFile.Write(versioned, envelope);
        AtomicFile.Write(Path.Combine(output, args[4], args[3] + ".json"), envelope);
        Console.WriteLine($"Published {manifest.Product} {manifest.Version}, {manifest.Channel}, sequence {manifest.Sequence}, {files.Count} files, {files.Sum(f => f.Size)} bytes."); break;
    }
    default: throw new ArgumentException("Unknown release command.");
}
