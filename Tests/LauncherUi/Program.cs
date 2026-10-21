using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using BrennorsCall.Launcher;
using BrennorsCall.Launcher.Desktop;

internal static class Preview
{
    [STAThread] private static void Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        using var http = new HttpClient();
        var model = new LauncherViewModel(http, new DeploymentConfig(), Path.GetFullPath(args[1]));
        var window = new MainWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000, Top = -2000 };
        window.Show(); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(args[0])) encoder.Save(output);
        Console.WriteLine("Rendered launcher WPF window."); window.Close();
        // Exercise actual two-way Settings bindings while account/channel data changes.
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = new ChannelEndpoint("http://127.0.0.1:1", "http://127.0.0.1:1", "127.0.0.1:7777");
        var config = new DeploymentConfig { AllowLoopbackHttp = true, Channels = new() { ["live"] = endpoint, ["testing"] = endpoint }, TrustedKeys = new() { ["fixture"] = signingKey.ExportSubjectPublicKeyInfoPem() } };
        using var fixtureHttp = new HttpClient(new AccountUiFixture());
        var accountModel = new LauncherViewModel(fixtureHttp, config, Path.GetFullPath(args[1]) + "-accounts") { LoginName = "ui_test", TabIndex = 2 };
        accountModel.Settings.RememberAccounts = false;
        var settingsWindow = new MainWindow { DataContext = accountModel, Left = -2000, Top = -2000 }; settingsWindow.Show(); settingsWindow.UpdateLayout();
        var login = accountModel.Login("fixture-password-only", false);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (login.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); login.GetAwaiter().GetResult(); settingsWindow.UpdateLayout();
        if (accountModel.Settings.Channel != "live" || !accountModel.AvailableChannels.Contains("testing") || accountModel.SelectedCharacter is null)
            throw new InvalidOperationException("Signing in changed the selected channel or failed to select the character.");
        File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]) + "-accounts", "accounts.dpapi.json"), "{broken");
        var corruptProfile = new LauncherViewModel(fixtureHttp, config, Path.GetFullPath(args[1]) + "-accounts");
        corruptProfile.Initialize().GetAwaiter().GetResult();
        if (!corruptProfile.Idle) throw new InvalidOperationException("Corrupt saved accounts left the launcher busy.");
        File.Delete(Path.Combine(Path.GetFullPath(args[1]) + "-accounts", "accounts.dpapi.json"));
        Console.WriteLine("PASS WPF sign-in, character selection and channel binding stability.");
        // Unit-level UI flow: private files wait for login, then automatic installation reaches Play.
        var game = System.Text.Encoding.UTF8.GetBytes("UI fixture executable; never launched");
        var manifest = new ReleaseManifest(1, "game", "live", "0.2.2", 1, DateTimeOffset.UtcNow, "game.exe",
            [new("game.exe", game.Length, Convert.ToHexStringLower(SHA256.HashData(game)))]);
        var signed = Manifests.Sign(manifest, "fixture", signingKey);
        using var privateHttp = new HttpClient(new AccountUiFixture(signed, game));
        var privateConfig = new DeploymentConfig { AllowLoopbackHttp = true, TrustedKeys = config.TrustedKeys, Channels = new() { ["live"] = endpoint with { PrivateDownloads = true } } };
        var privateProfile = Path.GetFullPath(args[1]) + "-private-" + Guid.NewGuid().ToString("N");
        new SettingsStore(privateProfile).Save(new LauncherSettings { GameDirectory = Path.Combine(privateProfile, "game"), RememberAccounts = false });
        var privateModel = new LauncherViewModel(privateHttp, privateConfig, privateProfile) { LoginName = "ui_test" };
        var privateWindow = new MainWindow { DataContext = privateModel, Left = -2000, Top = -2000 }; privateWindow.Show();
        void Pump(Task task)
        {
            var wait = new DispatcherFrame(); var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            tick.Tick += (_, _) => { if (task.IsCompleted) { tick.Stop(); wait.Continue = false; } };
            tick.Start(); Dispatcher.PushFrame(wait); task.GetAwaiter().GetResult();
        }
        Pump(privateModel.Initialize());
        if (File.Exists(Path.Combine(privateModel.Settings.GameDirectory, "active.json")) || privateModel.TabIndex != 1 || !privateModel.Idle)
            throw new InvalidOperationException("Private installation did not wait for account sign-in.");
        Pump(privateModel.Login("fixture-password-only", false));
        if (privateModel.PrimaryLabel != "PLAY" || !File.Exists(Path.Combine(privateModel.Settings.GameDirectory, "active.json")))
            throw new InvalidOperationException("Private login did not automatically install a verified game: " + privateModel.Status);
        Console.WriteLine("PASS WPF private installation waits for login and automatically reaches Play after authenticated download.");
        privateWindow.Close(); settingsWindow.Close(); app.Shutdown();
    }
}

// Unit fixture only. Real authentication and packaged game acceptance use the live backend.
internal sealed class AccountUiFixture(byte[]? manifest = null, byte[]? game = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "release-assets.githubusercontent.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(game!) });
        if (request.RequestUri!.AbsolutePath.EndsWith("/game.json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest!) });
        object payload = request.RequestUri!.AbsolutePath switch
        {
            "/v1/auth/login" => new { token = "ui-fixture-session", expiresIn = 7200 },
            "/v1/characters" => new[] { new CharacterChoice(Guid.NewGuid(), "Wayfarer", 1) },
            "/v1/launcher/channels" => new[] { "live", "testing" },
            "/health" => new { status = "ready" },
            "/live/download" => new { url = "https://release-assets.githubusercontent.com/fixture?temporary=fixture" },
            _ => throw new InvalidOperationException("Unexpected UI fixture request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload, Manifests.Json), System.Text.Encoding.UTF8, "application/json") });
    }
}
